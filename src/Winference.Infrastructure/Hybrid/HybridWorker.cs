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
}
