using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.App.ViewModels;

/// <summary>Readable recovery UI when no trustworthy runtime descriptor exists.</summary>
internal sealed class UnavailableBackend : IRuntimeBackend
{
    public Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeAvailability(false, "Choose a model to continue."));
    public Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken) =>
        Task.FromException<RuntimeSession>(new RuntimeException("model_unavailable", "Choose a model to continue."));
    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new(GenerationEventKind.Failed, Error: "Choose a model to continue.");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
