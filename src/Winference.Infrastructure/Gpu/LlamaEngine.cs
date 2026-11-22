using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Gpu;

/// <summary>
/// One GGUF model loaded on the AMD GPU through llama.cpp, with Winference's own reply loop:
/// chat template, tokens, prompt reuse between turns, sampling and stopping.
/// Runs only inside the GPU worker process. Not thread-safe: one request at a time.
/// </summary>
internal sealed unsafe class LlamaEngine : IDisposable
{
    private const int DecodeType = 1; // LLAMA_PROCESS_TYPE_DECODE
    private readonly IntPtr _devices;
    private readonly IntPtr _model;
    private readonly IntPtr _context;
    private readonly IntPtr _vocab;
    private readonly IntPtr _batch;
    private readonly byte* _template;
    private readonly int _contextLength;
    private readonly int _batchSize;
    /// <summary>The tokens already in the engine's memory, in order. A new prompt that starts the same way skips them.</summary>
    private readonly List<int> _cached = [];
    private bool _disposed;

    public GpuWorkerReady Ready { get; }

    private LlamaEngine(IntPtr devices, IntPtr model, IntPtr context, IntPtr vocab, IntPtr batch, byte* template,
        int contextLength, GpuWorkerReady ready)
    {
        _devices = devices; _model = model; _context = context; _vocab = vocab; _batch = batch;
        _template = template; _contextLength = contextLength; Ready = ready;
        _batchSize = (int)LlamaNative.BatchSize(context);
    }

    public static LlamaEngine Load(string runtimeDirectory, string modelPath, int contextLength)
    {
        LlamaNative.Load(runtimeDirectory);
        LlamaNative.LogSet(&Log, null);
        fixed (byte* directory = Utf8(runtimeDirectory)) LlamaNative.BackendLoadAllFromPath(directory);
        LlamaNative.BackendInit();

        var (device, description, memory) = FindAmdGpu();
        var devices = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(devices, 0, device);
        Marshal.WriteIntPtr(devices, IntPtr.Size, IntPtr.Zero);
        IntPtr model = IntPtr.Zero, context = IntPtr.Zero;
        try
        {
            var modelParams = LlamaNative.ModelDefaultParams();
            modelParams.Devices = devices;
            modelParams.GpuLayers = -1; // every layer on the GPU
            modelParams.SplitMode = LlamaNative.SplitModeNone;
            modelParams.MainGpu = 0;
            fixed (byte* path = Utf8(modelPath)) model = LlamaNative.ModelLoadFromFile(path, modelParams);
            if (model == IntPtr.Zero)
                throw new RuntimeException("gpu_model_load", "The GPU could not load this model. It may need more memory than the GPU has, or use an unsupported format.");

            var contextParams = LlamaNative.ContextDefaultParams();
            contextParams.ContextLength = (uint)contextLength;
            contextParams.MaxSequences = 1;
            contextParams.NoPerf = 1;
            context = LlamaNative.InitFromModel(model, contextParams);
            if (context == IntPtr.Zero)
                throw new RuntimeException("gpu_context_create", "The GPU does not have enough memory for this conversation length. Choose a shorter context.");
            if (LlamaNative.ContextSize(context) < contextLength)
                throw new RuntimeException("gpu_context_create", "The GPU engine could not reserve the selected conversation length.");

            var template = LlamaNative.ModelChatTemplate(model, null);
            if (template is null)
                throw new RuntimeException("gpu_template", "This model file has no chat format, so it cannot be used for chat.");
            var batch = LlamaNative.BatchInit(context);
            if (batch == IntPtr.Zero) throw new RuntimeException("gpu_context_create", "The GPU engine could not prepare its input buffer.");

            var nameBuffer = new byte[256];
            int length;
            fixed (byte* name = nameBuffer) length = LlamaNative.ModelDescription(model, name, (nuint)nameBuffer.Length);
            var ready = new GpuWorkerReady(Environment.ProcessId, LlamaNative.Text(LlamaNative.Version()) ?? "unknown",
                "Vulkan", description, memory, LlamaNative.ModelLayerCount(model), contextLength,
                LlamaNative.ModelTrainedContext(model), Encoding.UTF8.GetString(nameBuffer, 0, Math.Clamp(length, 0, nameBuffer.Length)));
            return new LlamaEngine(devices, model, context, LlamaNative.ModelGetVocab(model), batch, template, contextLength, ready);
        }
        catch
        {
            if (context != IntPtr.Zero) LlamaNative.Free(context);
            if (model != IntPtr.Zero) LlamaNative.ModelFree(model);
            Marshal.FreeHGlobal(devices);
            throw;
        }
    }

    /// <summary>The exact number of tokens the model reads for these messages.</summary>
    public int Count(IReadOnlyList<GpuWorkerMessage> messages) => Tokenize(Render(messages)).Length;

    /// <summary>Writes a reply token by token. Stops at the model's end, the limit, or a cancel request.</summary>
    public void Generate(GpuWorkerRequest request, Func<bool> cancelled, Action<GpuWorkerEvent> send)
    {
        var rendered = Render(request.Messages ?? []);
        var prompt = Tokenize(rendered);
        if (request.MaxTokens < 1 || (long)prompt.Length + request.MaxTokens > _contextLength)
            throw new RuntimeException("context_exceeded", "This conversation and its reply do not fit the selected context. Start a new chat or shorten the message.");
        var limit = request.UseRemaining ? _contextLength - prompt.Length : request.MaxTokens;

        var started = Stopwatch.StartNew();
        var reused = Reuse(prompt);
        for (var start = reused; start < prompt.Length; start += _batchSize)
        {
            if (cancelled()) { send(new("cancelled")); return; }
            var count = Math.Min(_batchSize, prompt.Length - start);
            Decode(prompt.AsSpan(start, count), start, outputLast: start + count == prompt.Length);
        }
        var promptSeconds = started.Elapsed.TotalSeconds;

        var sampler = CreateSampler(request.Temperature);
        try
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var splitter = new ReasoningSplitter(startInReasoning: EndsWithOpenThink(rendered));
            var piece = new byte[256];
            var characters = new char[512];
            var generated = 0;
            var finish = "length";
            Stopwatch? writing = null;
            while (generated < limit)
            {
                if (cancelled()) { send(new("cancelled")); return; }
                var token = LlamaNative.SamplerSample(sampler, _context, -1);
                if (LlamaNative.VocabIsEndOfGeneration(_vocab, token) != 0) { finish = "stop"; break; }
                writing ??= Stopwatch.StartNew();
                generated++;
                var bytes = Piece(token, ref piece);
                var chars = decoder.GetChars(piece, 0, bytes, characters, 0, flush: false);
                Send(splitter.Push(new string(characters, 0, chars)), send);
                if (generated == limit || _cached.Count + 1 > _contextLength) break;
                Decode([token], _cached.Count, outputLast: true);
            }
            var tail = decoder.GetChars([], 0, 0, characters, 0, flush: true);
            if (tail > 0) Send(splitter.Push(new string(characters, 0, tail)), send);
            Send(splitter.Flush(), send);
            double? speed = writing is { } clock && generated > 1 && clock.Elapsed.TotalSeconds > 0
                ? (generated - 1) / clock.Elapsed.TotalSeconds : null;
            send(new("usage", PromptTokens: prompt.Length, CompletionTokens: generated, TokensPerSecond: speed,
                NewPromptTokens: prompt.Length - reused, PromptSeconds: promptSeconds));
            send(new("done", Finish: finish));
        }
        finally { LlamaNative.SamplerFree(sampler); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LlamaNative.BatchFree(_batch);
        LlamaNative.Free(_context);
        LlamaNative.ModelFree(_model);
        Marshal.FreeHGlobal(_devices);
        LlamaNative.BackendFree();
    }

    /// <summary>Keeps the longest shared start of the last conversation in memory and returns its length.</summary>
    private int Reuse(int[] prompt)
    {
        var shared = 0;
        while (shared < _cached.Count && shared < prompt.Length && _cached[shared] == prompt[shared]) shared++;
        // The last prompt token is always read again, so the model has a fresh next-word guess.
        if (shared == prompt.Length) shared--;
        var memory = LlamaNative.GetMemory(_context);
        if (shared < _cached.Count && LlamaNative.MemorySequenceRemove(memory, 0, shared, -1) == 0)
        {
            // Some model types cannot drop only the end of their memory. Start over for them.
            LlamaNative.MemoryClear(memory, 1);
            shared = 0;
        }
        _cached.RemoveRange(shared, _cached.Count - shared);
        return shared;
    }

    private void Decode(ReadOnlySpan<int> tokens, int firstPosition, bool outputLast)
    {
        LlamaNative.BatchClear(_batch);
        for (var i = 0; i < tokens.Length; i++)
        {
            var index = LlamaNative.BatchAddToken(_batch, 0, tokens[i]);
            if (index < 0) throw new RuntimeException("gpu_decode", "The GPU engine could not accept this input.");
            var position = firstPosition + i;
            LlamaNative.BatchSetPosition(_batch, index, &position);
            if (outputLast && i == tokens.Length - 1) LlamaNative.BatchSetOutputLogits(_batch, index, 1);
        }
        var result = LlamaNative.Process(_context, DecodeType, _batch);
        if (result != 0)
        {
            // The memory may now hold part of this input, so it cannot be trusted for reuse.
            LlamaNative.MemoryClear(LlamaNative.GetMemory(_context), 1);
            _cached.Clear();
            throw new RuntimeException("gpu_decode", result == 1
                ? "The GPU ran out of room for this conversation. Start a new chat or choose a shorter context."
                : "The GPU engine stopped while reading the conversation. Load the model again.");
        }
        foreach (var token in tokens) _cached.Add(token);
    }

    private string Render(IReadOnlyList<GpuWorkerMessage> messages)
    {
        if (messages.Count == 0) throw new RuntimeException("invalid_message", "Write a message before sending.");
        var strings = new List<IntPtr>(messages.Count * 2);
        var chat = new LlamaChatMessage[messages.Count];
        try
        {
            var total = 0L;
            for (var i = 0; i < messages.Count; i++)
            {
                var role = Marshal.StringToCoTaskMemUTF8(messages[i].Role);
                var content = Marshal.StringToCoTaskMemUTF8(messages[i].Content);
                strings.Add(role); strings.Add(content);
                chat[i] = new LlamaChatMessage { Role = role, Content = content };
                total += Encoding.UTF8.GetByteCount(messages[i].Content) + 64;
            }
            var buffer = new byte[checked((int)Math.Min(int.MaxValue / 2, total * 2 + 1024))];
            fixed (LlamaChatMessage* list = chat)
            {
                var length = Apply(list, chat.Length, buffer);
                if (length > buffer.Length)
                {
                    buffer = new byte[length];
                    length = Apply(list, chat.Length, buffer);
                }
                if (length < 0 || length > buffer.Length)
                    throw new RuntimeException("gpu_template", "This model's chat format is not supported by the GPU engine yet.");
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
        }
        finally { foreach (var item in strings) Marshal.FreeCoTaskMem(item); }
    }

    private int Apply(LlamaChatMessage* list, int count, byte[] buffer)
    {
        fixed (byte* output = buffer) return LlamaNative.ChatApplyTemplate(_template, list, (nuint)count, 1, output, buffer.Length);
    }

    private int[] Tokenize(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int[] tokens;
        fixed (byte* input = bytes)
        {
            // The template writes the model's special markers as text, so they are parsed, not added again.
            var needed = -LlamaNative.Tokenize(_vocab, input, bytes.Length, null, 0, 0, 1);
            if (needed <= 0) throw new RuntimeException("gpu_tokenize", "The GPU engine could not read this message.");
            tokens = new int[needed];
            fixed (int* output = tokens)
                if (LlamaNative.Tokenize(_vocab, input, bytes.Length, output, tokens.Length, 0, 1) != needed)
                    throw new RuntimeException("gpu_tokenize", "The GPU engine could not read this message.");
        }
        if (LlamaNative.VocabAddsBos(_vocab) != 0)
        {
            var bos = LlamaNative.VocabBos(_vocab);
            if (tokens[0] != bos) tokens = [bos, .. tokens];
        }
        return tokens;
    }

    private int Piece(int token, ref byte[] buffer)
    {
        int length;
        fixed (byte* output = buffer) length = LlamaNative.TokenToPiece(_vocab, token, output, buffer.Length, 0, 0);
        if (length < 0)
        {
            buffer = new byte[-length];
            fixed (byte* output = buffer) length = LlamaNative.TokenToPiece(_vocab, token, output, buffer.Length, 0, 0);
        }
        if (length < 0) throw new RuntimeException("gpu_decode", "The GPU engine wrote an unreadable word.");
        return length;
    }

    /// <summary>Top-k 40, top-p 0.95, min-p 0.05, then temperature. A temperature near zero always takes the likeliest word.</summary>
    private static IntPtr CreateSampler(double temperature)
    {
        var chain = LlamaNative.SamplerChainInit(1);
        if (temperature <= 0.01)
        {
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerGreedy());
            return chain;
        }
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerTopK(40));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerTopP(0.95f, 1));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerMinP(0.05f, 1));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerTemperature((float)temperature));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerDistribution(LlamaNative.DefaultSeed));
        return chain;
    }

    private static void Send(IReadOnlyList<(bool IsReasoning, string Text)> pieces, Action<GpuWorkerEvent> send)
    {
        foreach (var (isReasoning, text) in pieces) send(new(isReasoning ? "reasoning" : "text", Text: text));
    }

    /// <summary>Some templates open the model's thinking themselves, so the reply starts inside it.</summary>
    private static bool EndsWithOpenThink(string prompt)
    {
        var trimmed = prompt.TrimEnd();
        return trimmed.EndsWith("<think>", StringComparison.Ordinal);
    }

    /// <summary>Picks the AMD Radeon GPU that the Vulkan driver reports.</summary>
    private static (IntPtr Device, string Description, long? Memory) FindAmdGpu()
    {
        var count = (int)LlamaNative.DeviceCount();
        for (var i = 0; i < count; i++)
        {
            var device = LlamaNative.DeviceGet((nuint)i);
            var type = LlamaNative.DeviceType(device);
            if (type is not (LlamaNative.DeviceTypeGpu or LlamaNative.DeviceTypeIntegratedGpu)) continue;
            if (LlamaNative.Text(LlamaNative.RegistryName(LlamaNative.DeviceBackendRegistry(device))) != "Vulkan") continue;
            var description = LlamaNative.Text(LlamaNative.DeviceDescription(device)) ?? string.Empty;
            if (!description.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
                !description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) continue;
            nuint free, total;
            LlamaNative.DeviceMemory(device, &free, &total);
            return (device, description, total > 0 ? (long)total : null);
        }
        throw new RuntimeException("gpu_device", "The Vulkan driver did not report an AMD Radeon GPU. Update the AMD graphics driver and retry.");
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static int _lastLogLevel;

    /// <summary>Passes only warnings and errors to the worker's error output, which the app keeps for diagnostics.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Log(int level, byte* text, void* userData)
    {
        if (level != 5) _lastLogLevel = level; // 5 continues the previous message
        if (_lastLogLevel < LlamaNative.LogLevelWarn || text is null) return;
        try { Console.Error.Write(LlamaNative.Text(text)); }
        catch (IOException) { }
    }
}
