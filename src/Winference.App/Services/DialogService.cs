using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Winference.App.Services;

/// <summary>Confirmations and prompts. WinUI allows one dialog at a time, so requests take turns.</summary>
internal sealed class DialogService(Func<XamlRoot> root, DispatcherQueue dispatcher, AppLifetime lifetime)
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>Returns true only if the user chose the primary action.</summary>
    public async Task<bool> ConfirmAsync(string title, string[] lines, string primary,
        bool primaryIsDefault = false, (string Text, Uri Target)? link = null, string close = "Cancel")
    {
        var details = new StackPanel { Spacing = 12 };
        foreach (var line in lines) details.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        if (link is { } target) details.Children.Add(new HyperlinkButton { Content = target.Text, NavigateUri = target.Target, Padding = new(0) });
        var dialog = new ContentDialog
        {
            Title = title, Content = details, PrimaryButtonText = primary, CloseButtonText = close,
            DefaultButton = primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Explains why something cannot go ahead. There is nothing to confirm.</summary>
    public async Task InformAsync(string title, string[] lines)
    {
        var details = new StackPanel { Spacing = 12 };
        foreach (var line in lines) details.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        await ShowAsync(new ContentDialog { Title = title, Content = details, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close });
    }

    /// <summary>Returns the text entered, or null if the user cancelled.</summary>
    public async Task<string?> AskTextAsync(string title, string initial, string primary)
    {
        var input = new TextBox { Text = initial, MaxLength = 120, SelectionStart = 0, SelectionLength = initial.Length };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, "DialogTextInput");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, title);
        var dialog = new ContentDialog
        {
            Title = title, Content = input, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? input.Text : null;
    }

    /// <summary>The selected window is also the user's one-time permission to read it.</summary>
    public async Task<WindowReader.Target?> ChooseWindowAsync(IReadOnlyList<WindowReader.Target> windows)
    {
        if (windows.Count == 0)
        {
            await InformAsync("No open windows", ["Open the app you want to ask about and try again."]);
            return null;
        }
        var choices = new ComboBox { ItemsSource = windows, DisplayMemberPath = nameof(WindowReader.Target.Display), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Choose the window to read. Its text, including content outside the visible area, may be sent to your local model and saved with your conversation.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(choices);
        var dialog = new ContentDialog
        {
            Title = "Allow Winference to read a window?", Content = content,
            PrimaryButtonText = "Read window", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? choices.SelectedItem as WindowReader.Target : null;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _turn.WaitAsync();
        try
        {
            if (lifetime.IsClosing) return ContentDialogResult.None;
            dialog.XamlRoot = root();
            using var dismiss = lifetime.Token.Register(() => dispatcher.TryEnqueue(() => dialog.Hide()));
            return await dialog.ShowAsync();
        }
        finally { _turn.Release(); }
    }
}
