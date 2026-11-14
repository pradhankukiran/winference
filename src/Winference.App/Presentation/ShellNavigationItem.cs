namespace Winference.App.Presentation;

public enum ShellPage { Chat, Discover, Models, Developer, Settings }

/// <summary>One destination pinned to the bottom of the sidebar.</summary>
public sealed class ShellNavigationItem(ShellPage page, string label, string glyph, string automationId)
{
    public ShellPage Page { get; } = page;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public string AutomationId { get; } = automationId;

    // ListViewItem exposes the item's ToString() as its automation name.
    public override string ToString() => Label;
}
