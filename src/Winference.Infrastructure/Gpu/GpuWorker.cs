using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Gpu;

/// <summary>
/// Entry point of the GPU worker: Winference.exe started again by the app, inside the app's
/// kill-on-close job. It loads one model on the GPU and answers the app over a private pipe.
/// A crash in the engine ends only this process.
/// </summary>
public static class GpuWorker
{
    /// <summary>Arguments: --gpu-worker &lt;pipe name&gt; &lt;data root&gt; &lt;model path&gt; &lt;context length&gt;.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5 || args[0] != GpuWorkerProtocol.WorkerFlag ||
            !GpuWorkerProtocol.IsPipeName(args[1]) || !Path.IsPathFullyQualified(args[2]) || !Path.IsPathFullyQualified(args[3]) ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var contextLength) ||
            contextLength is < 256 or > 131072) return 2;

        await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(30_000); }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException) { return 3; }
        using var reader = new StreamReader(pipe, GpuWorkerProtocol.Encoding, false, 65536, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, GpuWorkerProtocol.Encoding, 65536, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        var writeLock = new object();
        void Send(GpuWorkerEvent message)
        {
            var line = GpuWorkerProtocol.Serialize(message);
            lock (writeLock) writer.WriteLine(line);
        }

        var runtime = new GpuRuntimeAcquisition(args[2]).ExpectedInstallation;
        var leases = new List<FileStream>();
        LlamaEngine? engine = null;
        try
        {
            // Hold the engine files open for reading, then prove they are the pinned release,
            // so nothing can replace them between the check and the load.
            if (Directory.Exists(runtime.RuntimeDirectory))
                foreach (var file in Directory.EnumerateFiles(runtime.RuntimeDirectory, "*.dll"))
                    leases.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            if (!await GpuRuntimeAcquisition.VerifyAsync(runtime))
                throw new RuntimeException("gpu_runtime_missing", "The GPU component is missing or changed. Repair it from Models.");
            if (!File.Exists(args[3]))
                throw new RuntimeException("gpu_model_missing", "The model file is missing. Choose its new location in Models.");
            engine = LlamaEngine.Load(runtime.RuntimeDirectory, args[3], contextLength);
            Send(new("ready", Ready: engine.Ready));
        }
        catch (Exception error)
        {
            try { Report(error, Send, "gpu_worker_startup", "The GPU engine could not start. Check the AMD graphics driver and retry."); }
            catch (IOException) { }
            engine?.Dispose();
            foreach (var lease in leases) lease.Dispose();
            return 4;
        }

        // Requests are read on their own task, so "stop" is seen even while a reply is being written.
        var requests = Channel.CreateUnbounded<GpuWorkerRequest>(new() { SingleReader = true, SingleWriter = true });
        var stopping = 0;
        var reading = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var request = GpuWorkerProtocol.Deserialize<GpuWorkerRequest>(line);
                    if (request.Op == "stop") { Volatile.Write(ref stopping, 1); break; }
                    await requests.Writer.WriteAsync(request);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or ObjectDisposedException) { }
            finally
            {
                // The app closed the pipe or asked to stop: finish the current request and exit.
                Volatile.Write(ref stopping, 1);
                requests.Writer.TryComplete();
            }
        });

        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync())
            {
                try
                {
                    switch (request.Op)
                    {
                        case "count":
                            Send(new("count", Tokens: engine.Count(request.Messages ?? [])));
                            break;
                        case "generate":
                            engine.Generate(request, () => Volatile.Read(ref stopping) == 1, Send);
                            break;
                        default:
                            throw new RuntimeException("gpu_request", "The GPU engine received an unknown request.");
                    }
                }
                catch (IOException) { break; }
                catch (Exception error)
                {
                    try { Report(error, Send, "gpu_request", "The GPU engine could not finish this reply. Load the model again."); }
                    catch (IOException) { break; }
                }
            }
        }
        finally
        {
            engine.Dispose();
            foreach (var lease in leases) lease.Dispose();
        }
        await reading;
        return 0;
    }

    private static void Report(Exception error, Action<GpuWorkerEvent> send, string code, string message)
    {
        Console.Error.WriteLine(error is RuntimeException known ? known.Code : error.GetType().Name);
        send(error is RuntimeException runtime ? new("error", Code: runtime.Code, Message: runtime.Message)
            : new("error", Code: code, Message: message));
    }
}
