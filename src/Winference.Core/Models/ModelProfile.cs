namespace Winference.Core.Models;

/// <summary>A mode-specific artifact and its qualified inference settings.</summary>
public sealed record ModelProfile(
    string ModelFamilyId,
    string ProfileId,
    string ArtifactId,
    string ProviderModelId,
    ExecutionMode ExecutionMode,
    int ContextLength = 2048,
    int MaxOutputTokens = 256,
    ConversationCompatibility? ConversationCompatibility = null,
    bool SupportsImages = false)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelFamilyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderModelId);
        ConversationCompatibility?.Validate();
        if (!Enum.IsDefined(ExecutionMode))
            throw new ArgumentOutOfRangeException(nameof(ExecutionMode));
        if (ContextLength <= 1)
            throw new ArgumentOutOfRangeException(nameof(ContextLength));
        if (MaxOutputTokens <= 0 || MaxOutputTokens >= ContextLength)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
    }
}
