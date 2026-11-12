using Winference.Core.Models;

namespace Winference.Core.Runtime;

public enum RuntimeState { Idle, Starting, Ready, Generating, Stopping, Error }

public sealed record RuntimeAvailability(bool IsAvailable, string Detail, string? FailureCode = null);

/// <summary>The backend's identity/ownership proof. A health response alone is insufficient.</summary>
public sealed record RuntimeSession(
    Guid SessionId,
    long Revision,
    ModelProfile Profile,
    Uri Endpoint,
    string Engine,
    string Device,
    int EffectiveContextLength,
    string OwnershipProof);

public sealed record RuntimeSnapshot(RuntimeState State, string Detail, RuntimeSession? Session, long Revision);

/// <param name="Images">Pictures sent with a user message. Only models with <see cref="ModelProfile.SupportsImages"/> accept them.</param>
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ChatImage>? Images = null)
{
    public bool HasImages => Images is { Count: > 0 };
}

/// <summary>An encoded picture (JPEG or PNG) for a vision model.</summary>
public sealed record ChatImage(string MediaType, byte[] Data)
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxPerMessage = 1;

    /// <summary>
    /// Conservative prompt cost of one image. Vision encoders vary (roughly 256 to 1,280 tokens),
    /// and text tokenizers can't count them, so this is added on top of the text count.
    /// </summary>
    public const int TokenBudget = 1280;

    public string DataUrl => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>A rendered-prompt count. Estimates must never be presented as exact tokenizer results.</summary>
public sealed record PromptTokenCount(int Tokens, bool IsExact)
{
    /// <summary>
    /// Conservative transport-side estimate for adapters without a qualified counting API.
    /// One token per UTF-8 byte plus 256 template tokens and 64 tokens per message
    /// deliberately overestimates ordinary byte-BPE text. This is not a proof for
    /// an arbitrary template; the selected runtime must still enforce its context.
    /// </summary>
    public static PromptTokenCount Estimate(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var tokens = 256L;
        foreach (var message in messages)
        {
            if (message is null || message.Role is not ("system" or "user" or "assistant") ||
                string.IsNullOrWhiteSpace(message.Content))
                throw new RuntimeException("invalid_message", "The conversation contains an empty or unsupported message.");
            tokens += System.Text.Encoding.UTF8.GetByteCount(message.Content) + 64L + (message.Images?.Count ?? 0) * (long)ChatImage.TokenBudget;
        }
        return new(checked((int)tokens), false);
    }
}

public sealed record GenerationRequest(
    Guid RequestId,
    long SessionRevision,
    string ProfileId,
    IReadOnlyList<ChatMessage> Messages,
    int MaxOutputTokens,
    double Temperature = ResponseStyles.DefaultTemperature,
    bool UseRemainingContext = false);

/// <summary>Named sampling temperatures offered as response styles.</summary>
public static class ResponseStyles
{
    public const double DefaultTemperature = 0.7;
    public static IReadOnlyList<(string Id, string Label, string Description, double Temperature)> All { get; } =
    [
        ("precise", "Precise", "Focused, consistent answers. Good for facts, code and documents.", 0.2),
        ("balanced", "Balanced", "The default mix of accuracy and variety.", DefaultTemperature),
        ("creative", "Creative", "More varied wording and ideas. Good for writing and brainstorming.", 1.0),
    ];

    public static double TemperatureFor(string? id) =>
        All.FirstOrDefault(style => style.Id == id) is { Id: not null } style ? style.Temperature : DefaultTemperature;
}

/// <summary>ReasoningDelta is a thinking model's working, sent before its answer. It is never part of the answer text.</summary>
public enum GenerationEventKind { TextDelta, Usage, Completed, Cancelled, Failed, ReasoningDelta }

public sealed record GenerationUsage(int? PromptTokens = null, int? CompletionTokens = null, double? DecodeTokensPerSecond = null);

public sealed record GenerationEvent(
    GenerationEventKind Kind,
    string? Text = null,
    GenerationUsage? Usage = null,
    string? Error = null,
    string? FinishReason = null)
{
    public bool IsTerminal => Kind is GenerationEventKind.Completed or GenerationEventKind.Cancelled or GenerationEventKind.Failed;
}

/// <summary>Code and Message are suitable for the UI. They must not contain conversation text.</summary>
public sealed class RuntimeException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
