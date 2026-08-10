using Winference.Core.Runtime;

namespace Winference.Core.Chat;

public sealed record ContextWindowResult(
    IReadOnlyList<ChatMessage> Messages, int ExcludedExchanges, int PromptTokens, bool IsExact);

/// <summary>
/// Keeps the newest contiguous complete exchanges that fit the selected profile.
/// The visible transcript and current draft are never changed by this policy.
/// </summary>
public static class ContextWindowPolicy
{
    public static async Task<ContextWindowResult> SelectAsync(
        IReadOnlyList<ChatMessage> history, string currentUser, int contextLength, int outputReserve,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<PromptTokenCount>> count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(count);
        if (string.IsNullOrWhiteSpace(currentUser))
            throw new RuntimeException("invalid_message", "Write a message before sending.");
        if (contextLength <= 1 || outputReserve <= 0 || outputReserve >= contextLength)
            throw new ArgumentOutOfRangeException(nameof(outputReserve));
        cancellationToken.ThrowIfCancellationRequested();

        var systems = new List<ChatMessage>();
        var exchanges = new List<(ChatMessage User, ChatMessage Assistant)>();
        ChatMessage? pendingUser = null;
        var conversationStarted = false;
        foreach (var message in history)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Content) ||
                message.Role is not ("system" or "user" or "assistant"))
                throw new RuntimeException("invalid_message", "The conversation contains an empty or unsupported message.");
            if (message.Role == "system")
            {
                if (conversationStarted)
                    throw new RuntimeException("invalid_message", "System instructions must precede the conversation.");
                systems.Add(message);
            }
            else
            {
                conversationStarted = true;
                if (message.Role == "user") pendingUser = message;
                else if (pendingUser is not null)
                {
                    exchanges.Add((pendingUser, message));
                    pendingUser = null;
                }
            }
        }

        var selected = new List<ChatMessage>(systems) { new("user", currentUser) };
        if (selected.Count > 64)
            throw ContextExceeded();
        var measured = await MeasureAsync(selected, count, cancellationToken).ConfigureAwait(false);
        if ((long)measured.Tokens + outputReserve > contextLength) throw ContextExceeded();
        var included = 0;
        for (var i = exchanges.Count - 1; i >= 0; --i)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count + 2 > 64) break;
            var candidate = new List<ChatMessage>(selected);
            candidate.Insert(systems.Count, exchanges[i].Assistant);
            candidate.Insert(systems.Count, exchanges[i].User);
            // RuntimeBroker's payload limit also remains enforced before any
            // runtime tokenization request. Trim history rather than dropping
            // the current message if only the extra exchange exceeds it.
            if (candidate.Sum(message => (long)System.Text.Encoding.UTF8.GetByteCount(message.Content)) > 262144) break;
            var candidateCount = await MeasureAsync(candidate, count, cancellationToken).ConfigureAwait(false);
            if ((long)candidateCount.Tokens + outputReserve > contextLength) break;
            selected = candidate;
            measured = candidateCount;
            ++included;
        }
        return new(selected.ToArray(), exchanges.Count - included, measured.Tokens, measured.IsExact);
    }

    private static async Task<PromptTokenCount> MeasureAsync(IReadOnlyList<ChatMessage> messages,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<PromptTokenCount>> count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await count(messages, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null || result.Tokens <= 0)
            throw new RuntimeException("tokenizer_count", "The model did not return a usable context count. Reload it and retry.");
        return result;
    }

    private static RuntimeException ContextExceeded() => new("context_limit",
        "This message and the reply reserve exceed the model's context limit. Shorten your message or start a new chat.");
}
