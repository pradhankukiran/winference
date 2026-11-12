using System.Runtime.CompilerServices;
using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Core.Extensions;

/// <summary>
/// Standard chat completion result compatible with Microsoft.Extensions.AI patterns.
/// </summary>
public sealed record ClientChatCompletion(
    ChatMessage Message,
    string? FinishReason = null,
    GenerationUsage? Usage = null);

/// <summary>
/// Incremental streaming update chunk compatible with Microsoft.Extensions.AI patterns.
/// </summary>
public sealed record ClientStreamingUpdate(
    string? Text = null,
    string? Role = null,
    string? FinishReason = null,
    GenerationUsage? Usage = null);

/// <summary>
/// Optional configuration parameters for model completions.
/// </summary>
public sealed record ClientChatOptions(
    int? MaxOutputTokens = null,
    double? Temperature = null,
    IReadOnlyList<string>? StopSequences = null);

/// <summary>
/// Metadata describing an active AI chat client session.
/// </summary>
public sealed record ClientMetadata(
    string ProviderName,
    Uri? Endpoint,
    string? ModelId,
    string? Device,
    int ContextLength);

/// <summary>
/// Standardized interface compatible with Microsoft.Extensions.AI IChatClient,
/// enabling seamless interop with Semantic Kernel, AutoGen, and .NET AI tooling.
/// </summary>
public interface IWinferenceChatClient : IAsyncDisposable, IDisposable
{
    ClientMetadata Metadata { get; }

    Task<ClientChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        ClientChatOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ClientStreamingUpdate> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        ClientChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bridges an active Winference RuntimeBroker to the standardized chat client interface.
/// </summary>
public sealed class WinferenceChatClient : IWinferenceChatClient
{
    private readonly RuntimeBroker _broker;
    private readonly ModelProfile _profile;

    public WinferenceChatClient(RuntimeBroker broker, ModelProfile profile)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _profile.Validate();
    }

    public ClientMetadata Metadata
    {
        get
        {
            var session = _broker.Snapshot.Session;
            return new(
                ProviderName: session?.Engine ?? "Winference",
                Endpoint: session?.Endpoint,
                ModelId: _profile.ProviderModelId,
                Device: session?.Device ?? "Local Acceleration",
                ContextLength: _profile.ContextLength);
        }
    }

    public async Task<ClientChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        ClientChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var textBuilder = new System.Text.StringBuilder();
        GenerationUsage? usage = null;
        string? finishReason = null;

        var maxTokens = options?.MaxOutputTokens ?? _profile.MaxOutputTokens;
        await foreach (var item in _broker.GenerateAsync(messages, maxTokens, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == GenerationEventKind.TextDelta && item.Text is not null)
                textBuilder.Append(item.Text);

            if (item.Usage is not null)
                usage = item.Usage;

            if (item.FinishReason is not null)
                finishReason = item.FinishReason;

            if (item.Kind == GenerationEventKind.Failed)
                throw new RuntimeException("generation_failed", item.Error ?? "Inference failed.");
        }

        return new ClientChatCompletion(
            new ChatMessage("assistant", textBuilder.ToString()),
            finishReason ?? "completed",
            usage);
    }

    public async IAsyncEnumerable<ClientStreamingUpdate> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        ClientChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxTokens = options?.MaxOutputTokens ?? _profile.MaxOutputTokens;
        await foreach (var item in _broker.GenerateAsync(messages, maxTokens, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == GenerationEventKind.ReasoningDelta) continue;
            yield return new ClientStreamingUpdate(
                Text: item.Text,
                Role: item.Kind == GenerationEventKind.TextDelta ? "assistant" : null,
                FinishReason: item.FinishReason,
                Usage: item.Usage);
        }
    }

    public ValueTask DisposeAsync() => _broker.DisposeAsync();

    public void Dispose()
    {
        _broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
