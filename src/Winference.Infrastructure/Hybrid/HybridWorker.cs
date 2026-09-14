using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Hybrid;

/// <summary>
/// Private, one-model worker. Uses the C API in the unchanged AMD 1.7.0 payload.
/// It runs before WinUI initialization in a separate owned app process. Explicit
/// absolute-path loading avoids binding GenAI to Windows' older ONNX Runtime.
/// </summary>
public static class HybridWorker
{
    public const string ProtocolVersion = "winference-hybrid-1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5 || args[0] != "--hybrid-worker" ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535 ||
            !Guid.TryParseExact(args[4], "N", out var session)) return 2;
        var secret = Environment.GetEnvironmentVariable("WINFERENCE_HYBRID_TOKEN");
        if (secret is not { Length: 64 } || secret.Any(value => !Uri.IsHexDigit(value))) return 2;
        var installation = new HybridRuntimeAcquisition(args[2]).ExpectedInstallation;
        if (!await HybridRuntimeAcquisition.VerifyAsync(installation)) return 3;
        var registration = (await new HybridModelService(args[2]).ReadRegisteredAsync())
            .SingleOrDefault(model => string.Equals(model.ModelDirectory, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase));
        if (registration is null) return 3;
        using var lease = await HybridModelService.OpenVerifiedReadLeaseAsync(registration);
        var sessionDate = DateTime.UtcNow.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        // Give the native library a session-owned working directory using instant zero-cost hard links
        // without duplicating gigabytes of model weights to disk (falls back to copy across volumes).
        var workingModel = Path.Combine(Environment.CurrentDirectory, "model");
        if (Directory.Exists(workingModel)) return 3;
        Directory.CreateDirectory(workingModel);
        foreach (var file in registration.ModelFiles)
        {
            var destination = Path.Combine(workingModel, file.Name);
            var source = Path.Combine(registration.ModelDirectory, file.Name);
            if (!OperatingSystem.IsWindows() || !CreateHardLink(destination, source, IntPtr.Zero))
                File.Copy(source, destination, overwrite: false);
        }
        using (await HybridModelService.OpenVerifiedReadLeaseAsync(registration with { ModelDirectory = workingModel })) { }
        var componentLeases = Directory.EnumerateFiles(installation.RuntimeDirectory)
            .Select(file => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
        IntPtr model = IntPtr.Zero, tokenizer = IntPtr.Zero;
        var initialized = false;
        try
        {
            LoadNative(installation.RuntimeDirectory);
            initialized = true;
            Oga.Check(Oga.OgaCreateModel(workingModel, out model));
            Oga.Check(Oga.OgaCreateTokenizer(model, out tokenizer));
            using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(workingModel, "genai_config.json")));
            var modelConfiguration = configuration.RootElement.GetProperty("model");
            var modelType = modelConfiguration.GetProperty("type").GetString()!;
            var eos = EndTokenIds(modelConfiguration);
            Oga.Check(Oga.OgaModelGetDeviceType(model, out var devicePointer));
            var device = Marshal.PtrToStringUTF8(devicePointer);
            Oga.OgaDestroyString(devicePointer);
            if (device != "RyzenAI")
                throw new RuntimeException("hybrid_native_device", "The native runtime did not select the reviewed AMD hybrid provider.");
            var modules = NativeModules();
            foreach (var name in new[] { "onnxruntime.dll", "onnxruntime-genai.dll", "onnxruntime_providers_ryzenai.dll" })
                if (!modules.TryGetValue(name, out var path) || !SamePath(path, Path.Combine(installation.RuntimeDirectory, name)))
                    throw new RuntimeException("hybrid_native_identity", "The selected native hybrid libraries could not be confirmed.");
            var health = new
            {
                status = "ok", protocol = ProtocolVersion, session = session.ToString("N"), pid = Environment.ProcessId,
                model_path = workingModel, source_model_path = registration.ModelDirectory, model_id = registration.ModelId,
                model_sha256 = registration.Sha256, execution_mode = "hybrid", provider = "RyzenAI",
                native_device = device, context_length = registration.ContextLength,
                model_type = modelType, template_date = sessionDate,
                runtime_archive_sha256 = installation.ArchiveSha256, modules
            };
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "ready", pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow }));
            try
            {
                var stopping = false;
                while (!stopping)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    await using var stream = client.GetStream();
                    var streaming = false;
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var request = await ReadRequestAsync(stream, deadline.Token);
                        if (request.Authorization != "Bearer " + secret)
                        { await WriteJsonAsync(stream, 401, new { error = "Unauthorized" }); continue; }
                        if (request.Method == "GET" && request.Path == "/health")
                            await WriteJsonAsync(stream, 200, health);
                        else if (request.Method == "POST" && request.Path == "/shutdown")
                        { await WriteJsonAsync(stream, 200, new { status = "stopping" }); stopping = true; }
                        else if (request.Method == "POST" && request.Path is "/v1/chat/completions" or "/v1/tokenize")
                        {
                            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 16 });
                            var body = document.RootElement;
                            var tokenize = request.Path == "/v1/tokenize";
                            if (body.GetProperty("model").GetString() != registration.Profile.ProviderModelId || (!tokenize && !body.GetProperty("stream").GetBoolean()))
                                throw new RuntimeException("hybrid_request", "The selected hybrid request does not match this worker.");
                            var messages = body.GetProperty("messages").EnumerateArray()
                                .Select(item => new ChatMessage(item.GetProperty("role").GetString()!, item.GetProperty("content").GetString()!)).ToArray();
                            var prompt = RenderPrompt(workingModel, modelType, messages, sessionDate, tokenizer);
                            var maximum = body.GetProperty("max_tokens").GetInt32();
                            var temperature = body.TryGetProperty("temperature", out var requested) && requested.TryGetDouble(out var value) && value is >= 0 and <= 2 ? value : 0.7;
                            if (maximum is < 1 or > HybridModelService.MaximumOutputTokens)
                                throw new RuntimeException("hybrid_context", "The reply limit exceeds this hybrid profile.");
                            Oga.Check(Oga.OgaCreateSequences(out var sequences));
                            try
                            {
                                Oga.Check(Oga.OgaTokenizerEncode(tokenizer, prompt, sequences));
                                var count = checked((int)Oga.OgaSequencesGetSequenceCount(sequences, 0));
                                if (tokenize)
                                {
                                    await WriteJsonAsync(stream, 200, new { prompt_tokens = count, context_length = registration.ContextLength,
                                        session = session.ToString("N"), model_id = registration.ModelId });
                                    continue;
                                }
                                // Counting must also return an over-budget prompt's
                                // real count so the caller can remove old complete
                                // exchanges. Only generation allocates that context.
                                if ((long)count + maximum > registration.ContextLength)
                                    throw new RuntimeException("hybrid_context", "This conversation and reply exceed the hybrid context limit.");
                                if (count != body.GetProperty("expected_prompt_tokens").GetInt32())
                                    throw new RuntimeException("hybrid_tokenizer", "The native tokenizer differs from the reviewed profile.");
                                await WriteHeaderAsync(stream, 200, "text/event-stream; charset=utf-8");
                                streaming = true;
                                await GenerateAsync(stream, model, tokenizer, sequences, count, maximum, eos, temperature);
                            }
                            finally { Oga.OgaDestroySequences(sequences); }
                        }
                        else await WriteJsonAsync(stream, 404, new { error = "Unknown private endpoint" });
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
                    { /* The broker kills the owned worker when a request is cancelled. */ }
                    catch (Exception error) when (error is RuntimeException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
                    {
                        Console.Error.WriteLine(error is RuntimeException failure ? failure.Code : "hybrid_request");
                        try
                        {
                            if (streaming) await WriteEventAsync(stream, new { error = "Hybrid generation failed. Reload the model and retry." });
                            else await WriteJsonAsync(stream, 400, new { error = error is RuntimeException refusal ? refusal.Code : "hybrid_request" });
                        }
                        catch (IOException) { }
                    }
                }
            }
            finally { listener.Stop(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is RuntimeException failure ? failure.Code : "hybrid_worker_startup");
            return 4;
        }
        finally
        {
            if (tokenizer != IntPtr.Zero) Oga.OgaDestroyTokenizer(tokenizer);
            if (model != IntPtr.Zero) Oga.OgaDestroyModel(model);
            if (initialized) Oga.OgaShutdown();
            foreach (var file in componentLeases) file.Dispose();
            RemoveWorkingCopy(workingModel);
        }
    }

    /// <summary>
    /// Render with the export's own chat template through onnxruntime-genai.
    /// The two built-in renderers stay as a fallback for exports without one.
    /// </summary>
    private static string RenderPrompt(string modelDirectory, string modelType, IReadOnlyList<ChatMessage> messages,
        string sessionDate, IntPtr tokenizer)
    {
        HybridPromptRenderer.Validate(messages);
        var template = ReadChatTemplate(modelDirectory);
        var payload = JsonSerializer.Serialize(messages.Select(message => new { role = message.Role, content = message.Content }));
        IntPtr result = Oga.OgaTokenizerApplyChatTemplate(tokenizer, template, payload, "", true, out var rendered);
        if (result == IntPtr.Zero && rendered != IntPtr.Zero)
        {
            try
            {
                var prompt = Marshal.PtrToStringUTF8(rendered);
                if (!string.IsNullOrEmpty(prompt))
                {
                    if (prompt.Length > 262144)
                        throw new RuntimeException("hybrid_context", "This message is too large. Shorten it or start a new chat.");
                    return prompt;
                }
            }
            finally { Oga.OgaDestroyString(rendered); }
        }
        else if (result != IntPtr.Zero) Oga.OgaDestroyResult(result);
        return HybridPromptRenderer.Render(modelType, messages, sessionDate);
    }

    private static string ReadChatTemplate(string directory)
    {
        var path = Path.Combine(directory, "chat_template.jinja");
        if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= 262144) return File.ReadAllText(path);
        path = Path.Combine(directory, "tokenizer_config.json");
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 4 * 1024 * 1024) return "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("chat_template", out var template)) return "";
            if (template.ValueKind == JsonValueKind.String) return template.GetString() ?? "";
            if (template.ValueKind == JsonValueKind.Array)
                foreach (var item in template.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("template", out var text) &&
                        text.ValueKind == JsonValueKind.String) return text.GetString() ?? "";
        }
        catch (JsonException) { }
        return "";
    }

    private static HashSet<int> EndTokenIds(JsonElement model)
    {
        var ids = new HashSet<int>();
        if (!model.TryGetProperty("eos_token_id", out var eos)) return ids;
        if (eos.ValueKind == JsonValueKind.Number && eos.TryGetInt32(out var single)) ids.Add(single);
        else if (eos.ValueKind == JsonValueKind.Array)
            foreach (var item in eos.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var id)) ids.Add(id);
        return ids;
    }

    private static async Task GenerateAsync(Stream stream, IntPtr model, IntPtr tokenizer, IntPtr sequences, int promptTokens, int maximum, IReadOnlySet<int> eos, double temperature)
    {
        IntPtr parameters = IntPtr.Zero, generator = IntPtr.Zero, decoder = IntPtr.Zero;
        try
        {
            Oga.Check(Oga.OgaCreateGeneratorParams(model, out parameters));
            Oga.Check(Oga.OgaGeneratorParamsSetSearchNumber(parameters, "max_length", promptTokens + maximum));
            Oga.Check(Oga.OgaGeneratorParamsSetSearchBool(parameters, "do_sample", true));
            Oga.Check(Oga.OgaGeneratorParamsSetSearchNumber(parameters, "random_seed", 1));
            Oga.Check(Oga.OgaGeneratorParamsSetSearchNumber(parameters, "temperature", Math.Max(0.01, temperature)));
            Oga.Check(Oga.OgaCreateGenerator(model, parameters, out generator));
            Oga.Check(Oga.OgaCreateTokenizerStream(tokenizer, out decoder));
            var clock = Stopwatch.StartNew();
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "prefill", pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow, promptTokens }));
            Oga.Check(Oga.OgaGenerator_AppendTokens(generator, Oga.OgaSequencesGetSequenceData(sequences, 0), (nuint)promptTokens));
            var generated = 0;
            var first = TimeSpan.Zero;
            var reason = "stop";
            while (!Oga.OgaGenerator_IsDone(generator))
            {
                Oga.Check(Oga.OgaGenerator_GenerateNextToken(generator));
                // Finished sequences can retain their previous last element.
                // Decode only tokens returned for this generation step.
                Oga.Check(Oga.OgaGenerator_GetNextTokens(generator, out var tokens, out var tokenCount));
                if (tokenCount == 0 && Oga.OgaGenerator_IsDone(generator)) break;
                if (tokenCount != 1 || tokens == IntPtr.Zero)
                    throw new RuntimeException("hybrid_native_tokens", "The native runtime returned an unsupported token batch.");
                var token = Marshal.ReadInt32(tokens);
                generated++;
                if (generated == 1)
                {
                    first = clock.Elapsed;
                    Console.WriteLine(JsonSerializer.Serialize(new { phase = "decode", pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow, firstTokenMs = first.TotalMilliseconds }));
                }
                if (eos.Contains(token)) break;
                Oga.Check(Oga.OgaTokenizerStreamDecode(decoder, token, out var text));
                var delta = Marshal.PtrToStringUTF8(text);
                if (!string.IsNullOrEmpty(delta)) await WriteEventAsync(stream, new { choices = new[] { new { index = 0, delta = new { content = delta } } } });
                if (generated >= maximum) { reason = "length"; break; }
            }
            await WriteEventAsync(stream, new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } });
            await WriteEventAsync(stream, new { choices = Array.Empty<object>(), usage = new { prompt_tokens = promptTokens, completion_tokens = generated, total_tokens = promptTokens + generated } });
            await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray());
            await stream.FlushAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "complete", pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow, promptTokens, completionTokens = generated, elapsedMs = clock.Elapsed.TotalMilliseconds }));
        }
        finally
        {
            if (decoder != IntPtr.Zero) Oga.OgaDestroyTokenizerStream(decoder);
            if (generator != IntPtr.Zero) Oga.OgaDestroyGenerator(generator);
            if (parameters != IntPtr.Zero) Oga.OgaDestroyGeneratorParams(parameters);
        }
    }

    private static void LoadNative(string directory)
    {
        // Load ORT before GenAI's static imports can bind to the OS copy.
        var ort = Oga.LoadLibraryEx(Path.Combine(directory, "onnxruntime.dll"), IntPtr.Zero, 0x1100);
        var genai = ort == IntPtr.Zero ? IntPtr.Zero : Oga.LoadLibraryEx(Path.Combine(directory, "onnxruntime-genai.dll"), IntPtr.Zero, 0x1100);
        if (genai == IntPtr.Zero || !Oga.SetDllDirectory(directory))
            throw new RuntimeException("hybrid_native_load", "The native AMD runtime could not be loaded.");
        NativeLibrary.SetDllImportResolver(typeof(Oga).Assembly, (name, _, _) => name == "winference-genai" ? genai : IntPtr.Zero);
    }

    private static Dictionary<string, string> NativeModules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Where(module => module.ModuleName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(module => module.ModuleName, module => module.FileName, StringComparer.OrdinalIgnoreCase);
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    internal static void RemoveWorkingCopy(string directory)
    {
        // This session owns the copy. Delete only its top-level regular files;
        // unexpected folders and links are preserved and never traversed.
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(path);
        }
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    private sealed record Request(string Method, string Path, string? Authorization, byte[] Body);
    private static async Task<Request> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var header = new MemoryStream();
        var single = new byte[1];
        var last = 0;
        while (last != 0x0d0a0d0a)
        {
            if (header.Length >= 16384 || await stream.ReadAsync(single, token) != 1) throw new IOException("Invalid private HTTP header.");
            header.WriteByte(single[0]); last = (last << 8) | single[0];
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1") throw new IOException("Invalid private HTTP request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new IOException("Invalid private HTTP field.");
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new IOException("Private requests require Content-Length.");
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var value) && (!int.TryParse(value, out length) || length is < 0 or > 262144))
            throw new IOException("Invalid private HTTP body length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return new(first[0], first[1], headers.GetValueOrDefault("Authorization"), body);
    }
    private static Task WriteHeaderAsync(Stream stream, int status, string type) => stream.WriteAsync(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n")).AsTask();
    private static async Task WriteJsonAsync(Stream stream, int status, object value)
    { await WriteHeaderAsync(stream, status, "application/json"); await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json)); await stream.FlushAsync(); }
    private static async Task WriteEventAsync(Stream stream, object value)
    { await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(value, Json) + "\n\n")); await stream.FlushAsync(); }
}
