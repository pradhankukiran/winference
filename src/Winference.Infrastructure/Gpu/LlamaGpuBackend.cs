using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Winference.Core.Gpu;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Gpu;

/// <summary>
/// Runs a GGUF model on the AMD GPU in Winference's own GPU worker, which calls llama.cpp directly.
/// The app owns the private pipe; only the worker it started, under the same Windows user, may connect.
/// </summary>
public sealed class LlamaGpuBackend(
    GpuModelRegistration registration,
    GpuRuntimeInstallation runtime,
    string workerExecutable) : IRuntimeBackend
{
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private OwnedProcessSession? _owned;
    private FileStream? _modelLease;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Channel<GpuWorkerEvent>? _events;
    private Task? _reading;
    private RuntimeSession? _session;

    public string[] LastDiagnostics { get; private set; } = [];
    public GpuWorkerReady? Ready { get; private set; }
    /// <summary>The last reply's timing, including how much of the prompt was read again.</summary>
    public GpuWorkerEvent? LastUsage { get; private set; }

    public async Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "GPU chat requires native Windows.", "windows_required");
        if (profile != registration.Profile || profile.ExecutionMode != ExecutionMode.GpuOnly)
            return new(false, "This GPU model profile does not match the selected file.", "gpu_profile_mismatch");
        if (profile.ContextLength is < 256 or > 131072)
            return new(false, "This GPU build supports a context between 256 and 131,072 tokens.", "gpu_context");
        if (!File.Exists(workerExecutable))
            return new(false, "The GPU worker is missing. Restore the complete Winference application folder.", "gpu_worker_missing");
        if (!await GpuRuntimeAcquisition.VerifyAsync(runtime, cancellationToken).ConfigureAwait(false))
            return new(false, "The GPU runtime is missing or changed. Prepare the GPU component from Models before loading.", "gpu_runtime_missing");
        return await GpuModelService.VerifyAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new RuntimeException("windows_required", "GPU chat requires native Windows.");
        if (_owned is not null || _modelLease is not null)
            throw new RuntimeException("gpu_busy", "The GPU runtime is already starting or running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        try
        {
            // The same read handle spans verification and the session, so the file cannot change behind the check.
            _modelLease = new FileStream(registration.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var availability = await DiscoverAsync(profile, token).ConfigureAwait(false);
            if (!availability.IsAvailable)
                throw new RuntimeException(availability.FailureCode ?? "gpu_unavailable", availability.Detail);

            var sessionId = Guid.NewGuid();
            var pipeName = GpuWorkerProtocol.PipePrefix + sessionId.ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            var dataRoot = Directory.GetParent(runtime.RuntimeDirectory)?.Parent?.FullName
                ?? throw new RuntimeException("gpu_runtime_missing", "The GPU component location is invalid.");
            _owned = OwnedProcessSession.Start(Path.GetFullPath(workerExecutable),
                [GpuWorkerProtocol.WorkerFlag, pipeName, dataRoot, Path.GetFullPath(registration.ModelPath),
                 profile.ContextLength.ToString(CultureInfo.InvariantCulture)],
                runtime.RuntimeDirectory, CleanEnvironment());

            var connecting = _pipe.WaitForConnectionAsync(token);
            while (!connecting.IsCompleted)
            {
                if (_owned.HasExited) throw WorkerStopped();
                await Task.WhenAny(connecting, Task.Delay(100, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            await connecting.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var client) || client != _owned.ProcessId)
                throw new RuntimeException("gpu_ownership", "An unexpected program connected to the GPU engine. Loading was stopped.");

            _writer = new StreamWriter(_pipe, GpuWorkerProtocol.Encoding, 65536, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            _events = Channel.CreateUnbounded<GpuWorkerEvent>(new() { SingleReader = true, SingleWriter = true });
            _reading = ReadEventsAsync(new StreamReader(_pipe, GpuWorkerProtocol.Encoding, false, 65536, leaveOpen: true), _events.Writer);

            var first = await NextAsync(token).ConfigureAwait(false);
            if (first.Type == "error") throw WorkerError(first);
            if (first is not { Type: "ready", Ready: { } ready } || ready.ProcessId != _owned.ProcessId || ready.ContextLength != profile.ContextLength)
                throw new RuntimeException("gpu_ownership", "The GPU engine did not confirm the selected model.");
            Ready = ready;
            var proof = JsonSerializer.Serialize(new
            {
                sessionId, workerPid = _owned.ProcessId, workerStartUtc = _owned.StartedUtc, pipe = pipeName,
                engine = "llama.cpp " + ready.EngineVersion, runtimeTag = runtime.ReleaseTag,
                modelPath = registration.ModelPath, modelSha256 = registration.Sha256, ready.ModelDescription,
                ready.Device, ready.DeviceDescription, ready.DeviceMemoryBytes, ready.Layers,
                ready.ContextLength, ready.TrainedContextLength
            });
            _session = new RuntimeSession(sessionId, 0, profile, new Uri("net.pipe://localhost/" + pipeName),
                "llama.cpp " + runtime.ReleaseTag, "GPU · " + ready.DeviceDescription, profile.ContextLength, proof);
            return _session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new RuntimeException("gpu_startup_timeout", "The GPU model took too long to load. Check the driver and available memory, then retry.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The worker ends a reply when asked and confirms it, so Stop keeps the model loaded.</summary>
    public bool CancelsInPlace => true;

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequireSession(session, request.ProfileId);
        if (request.Messages.Any(message => message.HasImages))
            throw new RuntimeException("ImagesUnsupported", "Pictures need a vision model. This GPU model reads text only.");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var sent = false;
        var settled = false;
        try
        {
            await SendAsync(new GpuWorkerRequest("generate", Wire(request.Messages), request.MaxOutputTokens,
                request.Temperature, request.UseRemainingContext, id), cancellationToken).ConfigureAwait(false);
            sent = true;
            while (true)
            {
                var message = await NextAsync(cancellationToken).ConfigureAwait(false);
                switch (message.Type)
                {
                    case "text" when message.Text is not null:
                        yield return new(GenerationEventKind.TextDelta, message.Text);
                        break;
                    case "reasoning" when message.Text is not null:
                        yield return new(GenerationEventKind.ReasoningDelta, message.Text);
                        break;
                    case "usage":
                        LastUsage = message;
                        yield return new(GenerationEventKind.Usage, Usage: new(message.PromptTokens, message.CompletionTokens, message.TokensPerSecond));
                        break;
                    case "done":
                        settled = true;
                        yield return new(GenerationEventKind.Completed, FinishReason: message.Finish);
                        yield break;
                    case "cancelled":
                        settled = true;
                        yield return new(GenerationEventKind.Cancelled);
                        yield break;
                    case "error":
                        settled = true;
                        throw WorkerError(message);
                    default:
                        throw new RuntimeException("gpu_stream", "The GPU engine sent an unexpected reply.");
                }
            }
        }
        finally
        {
            try
            {
                // Cancelled or abandoned mid-reply: end it in the worker and wait for the confirmation,
                // so the next request never reads this reply's leftovers.
                if (sent && !settled) await CancelAndDrainAsync(id).ConfigureAwait(false);
            }
            finally { _requestGate.Release(); }
        }
    }

    private async Task CancelAndDrainAsync(string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await SendAsync(new GpuWorkerRequest("cancel", Id: id), deadline.Token).ConfigureAwait(false);
            while ((await NextAsync(deadline.Token).ConfigureAwait(false)).Type is not ("cancelled" or "done" or "error")) { }
        }
        catch (Exception error) when (error is OperationCanceledException or RuntimeException)
        {
            // Throwing replaces the cancellation, so the broker stops this worker instead of reusing it.
            throw new RuntimeException("gpu_cancel", "The GPU engine did not stop the reply in time. Load the model again.", error);
        }
    }

    public async Task<PromptTokenCount> CountPromptTokensAsync(RuntimeSession session,
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        RequireSession(session, session.Profile.ProfileId);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync(new GpuWorkerRequest("count", Wire(messages)), cancellationToken).ConfigureAwait(false);
            var reply = await NextAsync(cancellationToken).ConfigureAwait(false);
            if (reply.Type == "error") throw WorkerError(reply);
            if (reply is not { Type: "count", Tokens: > 0 and var tokens })
                throw new RuntimeException("gpu_context", "The GPU engine did not return a valid token count.");
            return new(tokens, true);
        }
        finally { _requestGate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session = null;
            if (_owned is { } owned)
            {
                // Ask the worker to finish, then end the job either way so no GPU work is left running.
                try
                {
                    if (_writer is not null && !owned.HasExited)
                    {
                        using var polite = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _writer.WriteLineAsync(GpuWorkerProtocol.Serialize(new GpuWorkerRequest("stop")).AsMemory(), polite.Token).ConfigureAwait(false);
                        await owned.WaitForExitAsync(polite.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                LastDiagnostics = owned.Diagnostics;
                await owned.StopAsync(cancellationToken).ConfigureAwait(false);
                owned.Dispose();
                _owned = null;
            }
            _writer?.Dispose(); _writer = null;
            _pipe?.Dispose(); _pipe = null;
            if (_reading is not null) { try { await _reading.ConfigureAwait(false); } catch (Exception) { } _reading = null; }
            _events = null;
            _modelLease?.Dispose(); _modelLease = null;
            Ready = null;
        }
        finally { _stopGate.Release(); }
    }

    private void RequireSession(RuntimeSession session, string profileId)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != registration.Profile ||
            profileId != registration.Profile.ProfileId || _owned is null || _owned.HasExited || _writer is null)
            throw new RuntimeException("gpu_session", "The selected GPU session changed. Reload the model before sending.");
    }

    private async Task SendAsync(GpuWorkerRequest request, CancellationToken token)
    {
        var writer = _writer ?? throw new RuntimeException("gpu_session", "The GPU engine has stopped. Load the model again.");
        try { await writer.WriteLineAsync(GpuWorkerProtocol.Serialize(request).AsMemory(), token).ConfigureAwait(false); }
        catch (IOException error) { throw new RuntimeException("gpu_session", "The GPU engine has stopped. Load the model again.", error); }
    }

    private async Task<GpuWorkerEvent> NextAsync(CancellationToken token)
    {
        var events = _events ?? throw new RuntimeException("gpu_session", "The GPU engine has stopped. Load the model again.");
        try { return await events.Reader.ReadAsync(token).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw WorkerStopped(); }
    }

    private static async Task ReadEventsAsync(StreamReader reader, ChannelWriter<GpuWorkerEvent> events)
    {
        try
        {
            using (reader)
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    await events.WriteAsync(GpuWorkerProtocol.Deserialize<GpuWorkerEvent>(line)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonException or ObjectDisposedException) { }
        finally { events.TryComplete(); }
    }

    private static GpuWorkerMessage[] Wire(IReadOnlyList<ChatMessage> messages) =>
        [.. messages.Select(message => new GpuWorkerMessage(message.Role, message.Content))];

    private static RuntimeException WorkerError(GpuWorkerEvent message) => new(
        message.Code is { Length: > 0 and <= 64 } code ? code : "gpu_request",
        message.Message is { Length: > 0 and <= 400 } text ? text : "The GPU engine could not finish this request. Load the model again.");

    private static RuntimeException WorkerStopped() =>
        new("gpu_startup", "The GPU engine stopped unexpectedly. Check the GPU driver and model file, then retry.");

    /// <summary>The app's environment without settings that change llama.cpp's behaviour behind the app's back.</summary>
    private static Dictionary<string, string> CleanEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(item => item.Key is string key && !key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) &&
                !key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("LEMONADE_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);
}
