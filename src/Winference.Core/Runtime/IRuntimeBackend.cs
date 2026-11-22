using Winference.Core.Models;

namespace Winference.Core.Runtime;

/// <summary>
/// The backend proves model/device/endpoint identity before returning a session.
/// StopAsync must settle its owned inference work, including a cancelled start.
/// It must not stop or unload a user's independently running runtime.
/// </summary>
public interface IRuntimeBackend
{
    Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken);
    Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken);
    IAsyncEnumerable<GenerationEvent> GenerateAsync(
        RuntimeSession session, GenerationRequest request, CancellationToken cancellationToken);
    Task<PromptTokenCount> CountPromptTokensAsync(RuntimeSession session,
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PromptTokenCount.Estimate(messages));
    }
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True when a cancelled GenerateAsync settles its own request before it throws
    /// OperationCanceledException, so the model stays loaded and ready for the next message.
    /// When false, a cancelled reply stops the backend, because the abandoned request cannot be proven settled.
    /// </summary>
    bool CancelsInPlace => false;
}
