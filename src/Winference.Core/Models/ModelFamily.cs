namespace Winference.Core.Models;

/// <summary>A base-model family, independent of any one mode-specific artifact.</summary>
public sealed record ModelFamily(string Id, string DisplayName, string Publisher, string Description)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        if (Id.Length > 120 || Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')))
            throw new ArgumentException("A model family ID must use ASCII letters, digits, dots, dashes or underscores.", nameof(Id));
        if (DisplayName.Length > 200) throw new ArgumentException("A model family name is too long.", nameof(DisplayName));
        if (Publisher.Length > 200) throw new ArgumentException("A model publisher name is too long.", nameof(Publisher));
        if (Description.Length > 4_000) throw new ArgumentException("A model description is too long.", nameof(Description));
    }
}
