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
}
