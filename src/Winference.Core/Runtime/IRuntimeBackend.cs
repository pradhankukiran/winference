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
}
