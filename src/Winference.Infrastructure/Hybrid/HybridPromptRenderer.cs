using System.Text;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Hybrid;

/// <summary>Chat template renderers for hybrid GPU + NPU execution.</summary>
internal static class HybridPromptRenderer
{
    public static string Render(string modelType, IReadOnlyList<ChatMessage> messages, string sessionDate)
    {
        Validate(messages);
        var type = (modelType ?? string.Empty).ToLowerInvariant();
        if (type.Contains("qwen")) return RenderQwen(messages);
        if (type.Contains("phi")) return RenderPhi(messages);
        if (type.Contains("llama") || type.Contains("mistral") || type.Contains("deepseek"))
            return RenderLlama(messages, sessionDate);

        // Universal fallback to ChatML for unclassified models
        return RenderQwen(messages);
    }

    public static string RenderLlama(IReadOnlyList<ChatMessage> messages, string sessionDate)
    {
        Validate(messages);
        var first = messages[0].Role == "system" ? 1 : 0;
        var prompt = new StringBuilder("<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n");
        prompt.Append("Cutting Knowledge Date: December 2023\nToday Date: ").Append(sessionDate).Append("\n\n");
        if (first == 1) prompt.Append(messages[0].Content.Trim());
        prompt.Append("<|eot_id|>");
        foreach (var message in messages.Skip(first))
            prompt.Append("<|start_header_id|>").Append(message.Role).Append("<|end_header_id|>\n\n")
                .Append(message.Content.Trim()).Append("<|eot_id|>");
        return Bounded(prompt.Append("<|start_header_id|>assistant<|end_header_id|>\n\n"));
    }

    public static string RenderPhi(IReadOnlyList<ChatMessage> messages)
    {
        Validate(messages);
        var prompt = new StringBuilder();
        string? previousRole = null;
        foreach (var message in messages)
        {
            if (message.Role == previousRole && message.Role is "user" or "system")
            {
                prompt.Length -= "<|end|>".Length;
                prompt.Append(message.Content).Append("<|end|>");
            }
            else
            {
                prompt.Append("<|").Append(message.Role).Append("|>").Append(message.Content).Append("<|end|>");
            }
            previousRole = message.Role;
        }
        return Bounded(prompt.Append("<|assistant|>"));
    }

    public static string RenderQwen(IReadOnlyList<ChatMessage> messages)
    {
        Validate(messages);
        var prompt = new StringBuilder();
        foreach (var message in messages)
            prompt.Append("<|im_start|>").Append(message.Role).Append('\n').Append(message.Content).Append("<|im_end|>\n");
        return Bounded(prompt.Append("<|im_start|>assistant\n"));
    }

    internal static void Validate(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count is < 1 or > 64) throw new RuntimeException("hybrid_context", "This conversation is too long. Start a new chat.");
        long length = 0;
        foreach (var message in messages)
        {
            if (message is null || message.Role is not ("system" or "user" or "assistant") || string.IsNullOrEmpty(message.Content))
                throw new RuntimeException("hybrid_message", "The conversation contains an empty or unsupported message.");
            if ((length += message.Content.Length) > 262144)
                throw new RuntimeException("hybrid_context", "This message is too large. Shorten it or start a new chat.");
            for (var index = 0; index < message.Content.Length; ++index)
            {
                if (!char.IsSurrogate(message.Content[index])) continue;
                if (!char.IsHighSurrogate(message.Content[index]) || index + 1 >= message.Content.Length || !char.IsLowSurrogate(message.Content[++index]))
                    throw new RuntimeException("hybrid_message", "The message contains invalid Unicode text.");
            }
        }
    }

    private static string Bounded(StringBuilder prompt) => prompt.Length <= 262144 ? prompt.ToString()
        : throw new RuntimeException("hybrid_context", "This message is too large. Shorten it or start a new chat.");
}
