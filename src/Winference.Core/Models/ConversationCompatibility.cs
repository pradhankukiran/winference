namespace Winference.Core.Models;

/// <summary>
/// An explicitly reviewed equivalence between mode-specific artifacts. A shared
/// display name is never enough to create this key.
/// </summary>
public sealed record ConversationCompatibility(
    string BaseCheckpoint,
    string CheckpointRevision,
    string TokenizerPolicy,
    string TemplatePolicy)
{
    public void Validate()
    {
        foreach (var value in new[] { BaseCheckpoint, CheckpointRevision, TokenizerPolicy, TemplatePolicy })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
                throw new ArgumentException("Conversation compatibility requires a bounded checkpoint, revision, tokenizer and template identity.");
    }
}
