using System.Runtime.CompilerServices;
using Winference.Core.Chat;
using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Tests;

internal static class TestProfiles
{
    public static ModelProfile Create(string id = "model", ExecutionMode mode = ExecutionMode.NpuOnly) =>
        new("family-" + id, "profile-" + id, "artifact-" + id, "provider-" + id, mode);

    public static RuntimeSession Session(ModelProfile profile) =>
        new(Guid.NewGuid(), 0, profile, new Uri("http://127.0.0.1:5000"), "engine", "NPU", profile.ContextLength, "proof");
}

/// <summary>A scriptable runtime. Replies can be held open to exercise cancellation.</summary>
internal sealed class FakeBackend : IRuntimeBackend
{
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public Exception? StartFailure { get; set; }
    public Exception? GenerateFailure { get; set; }
    public bool HoldReply { get; set; }
    public GenerationRequest? LastRequest { get; private set; }
    public TaskCompletionSource ReplyStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeAvailability(true, "ready"));

    public Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        ++Starts;
        if (StartFailure is { } failure) throw failure;
        return Task.FromResult(TestProfiles.Session(profile));
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (GenerateFailure is { } failure) throw failure;
        yield return new(GenerationEventKind.TextDelta, Text: "hello");
        ReplyStarted.TrySetResult();
        if (HoldReply) await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return new(GenerationEventKind.Completed, FinishReason: "stop");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ++Stops;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryConversationStore : IConversationStore
{
    public int Saves { get; private set; }
    public ChatWorkspace? Saved { get; private set; }
    public Exception? SaveFailure { get; set; }

    public Task<ChatWorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatWorkspaceLoadResult(Saved ?? ChatWorkspace.Empty, null));

    public Task SaveAsync(ChatWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (SaveFailure is { } failure) return Task.FromException(failure);
        ++Saves;
        Saved = workspace;
        return Task.CompletedTask;
    }
}

internal sealed class TemporaryFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("winference-tests-").FullName;
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
