using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Setup;

namespace Winference.App.Views;

public sealed partial class SettingsPage : Page
{
    private const string NpuName = "NPU · FastFlowLM";
    private const string HybridName = "GPU + NPU · AMD Ryzen AI";
    private const string GpuName = "GPU · llama.cpp";

    private bool _applyingAppearance;

    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenDataFolderRequested;
    public event EventHandler? OpenLogsRequested;
    public event EventHandler<ElementTheme>? AppearanceChanged;
    public event EventHandler<int>? IdleUnloadChanged;
    public event EventHandler<bool>? LoadOnStartChanged;
    public event EventHandler<bool>? ToolsEnabledChanged;

    public ObservableCollection<RuntimeStatusItem> Runtimes { get; } =
    [
        new(NpuName, "Checking…", "Checking…", string.Empty),
        new(HybridName, "Checking…", "Checking…", string.Empty),
        new(GpuName, "Checking…", "Checking…", string.Empty)
    ];

    public SettingsPage(string dataFolder)
    {
        InitializeComponent();
        DataFolderText.Text = dataFolder;
    }

    /// <summary>Reflects a saved preference without raising it back as a user change.</summary>
    public void SetAppearance(ElementTheme theme)
    {
        _applyingAppearance = true;
        AppearanceChoices.SelectedIndex = theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        _applyingAppearance = false;
    }

    private static readonly int[] IdleMinutes = [5, 15, 60, 0];

    /// <summary>Reflects saved choices without raising them back as user changes.</summary>
    public void SetModelBehavior(int idleMinutes, bool loadOnStart)
    {
        _applyingAppearance = true;
        var index = Array.IndexOf(IdleMinutes, idleMinutes);
        IdleChoices.SelectedIndex = index >= 0 ? index : 1;
        LoadOnStartSwitch.IsOn = loadOnStart;
        _applyingAppearance = false;
    }

    private void IdleChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_applyingAppearance && IdleChoices.SelectedIndex >= 0)
            IdleUnloadChanged?.Invoke(this, IdleMinutes[IdleChoices.SelectedIndex]);
    }

    private Winference.App.ViewModels.MediaAddonsViewModel? _addons;

    public void AttachAddons(Winference.App.ViewModels.MediaAddonsViewModel addons)
    {
        _addons = addons;
        AddonList.ItemsSource = addons.Items;
        _applyingAppearance = true;
        VoiceChoices.Items.Clear();
        foreach (var (_, label) in addons.Voices) VoiceChoices.Items.Add(label);
        _applyingAppearance = false;
        addons.Changed += (_, _) => RefreshVoice();
        RefreshVoice();
    }

    private void RefreshVoice()
    {
        if (_addons is null) return;
        _applyingAppearance = true;
        VoiceChoices.SelectedIndex = Math.Max(0, _addons.Voices.ToList().FindIndex(voice => voice.Id == _addons.Voice));
        VoiceChoices.IsEnabled = _addons.HasVoiceAddon;
        VoiceHint.Text = _addons.HasVoiceAddon ? "Kokoro voices. Replies are read with this voice." : "Get Kokoro above to choose a natural voice. Until then Windows' own voice is used.";
        _applyingAppearance = false;
    }

    private void VoiceChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_applyingAppearance || _addons is null || VoiceChoices.SelectedIndex < 0) return;
        _addons.SetVoice(_addons.Voices[VoiceChoices.SelectedIndex].Id);
    }

    private async void Addon_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: Winference.App.ViewModels.MediaAddonItem item }) await item.ActAsync();
    }

    public event EventHandler<string>? InstructionsChanged;
    public event EventHandler<string>? ResponseStyleChanged;
    private string _savedInstructions = string.Empty;

    /// <summary>Reflects saved chat preferences without raising them back as changes.</summary>
    public void SetChatPreferences(string instructions, string style)
    {
        _applyingAppearance = true;
        _savedInstructions = instructions;
        InstructionsBox.Text = instructions;
        StyleChoices.Items.Clear();
        foreach (var option in Winference.Core.Runtime.ResponseStyles.All) StyleChoices.Items.Add(option.Label);
        var index = Winference.Core.Runtime.ResponseStyles.All.ToList().FindIndex(option => option.Id == style);
        StyleChoices.SelectedIndex = index >= 0 ? index : 1;
        StyleHint.Text = Winference.Core.Runtime.ResponseStyles.All[StyleChoices.SelectedIndex].Description;
        _applyingAppearance = false;
    }

    private void StyleChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (StyleChoices.SelectedIndex < 0) return;
        var option = Winference.Core.Runtime.ResponseStyles.All[StyleChoices.SelectedIndex];
        StyleHint.Text = option.Description;
        if (!_applyingAppearance) ResponseStyleChanged?.Invoke(this, option.Id);
    }

    private void InstructionsBox_LostFocus(object sender, RoutedEventArgs args)
    {
        var text = InstructionsBox.Text.Trim();
        if (text == _savedInstructions) return;
        _savedInstructions = text;
        InstructionsChanged?.Invoke(this, text);
    }

    public void SetToolsEnabled(bool enabled)
    {
        _applyingAppearance = true;
        ToolsSwitch.IsOn = enabled;
        _applyingAppearance = false;
    }

    private void Tools_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_applyingAppearance) ToolsEnabledChanged?.Invoke(this, ToolsSwitch.IsOn);
    }

    private void LoadOnStart_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_applyingAppearance) LoadOnStartChanged?.Invoke(this, LoadOnStartSwitch.IsOn);
    }

    public void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy;
        if (busy) PageError.IsOpen = false;
    }

    public void Show(SetupInspection inspection, GpuRuntimeStatus gpu, HybridRuntimeStatus hybrid)
    {
        PageError.IsOpen = false;
        ProcessorText.Text = inspection.Processor;
        GraphicsText.Text = inspection.Graphics;
        NeuralProcessorText.Text = inspection.NeuralProcessor;
        MemoryText.Text = inspection.MemoryBytes > 0
            ? $"{inspection.MemoryBytes / 1_073_741_824d:0.#} GB"
            : "Unknown";

        var radeon = inspection.Graphics.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        Runtimes[0] = Runtime(NpuName, inspection.RuntimeStatus,
            inspection.NpuDetected ? inspection.DriverStatus : "No supported NPU",
            inspection.NpuDetected && inspection.RuntimeInstalled);
        Runtimes[1] = Runtime(HybridName, hybrid.Status,
            hybrid.Installation is { } hybridRuntime ? $"Runtime {hybridRuntime.ReleaseTag}" : "Runtime not installed",
            inspection.NpuDetected && radeon && hybrid.IsInstalled);
        Runtimes[2] = Runtime(GpuName, gpu.Status,
            gpu.Installation is { } gpuRuntime ? $"Runtime {gpuRuntime.ReleaseTag}" : "Runtime not installed",
            radeon && gpu.IsInstalled);
    }

    public void ShowError(string message)
    {
        SetBusy(false);
        PageError.Message = message;
        PageError.IsOpen = true;
    }

    private static RuntimeStatusItem Runtime(string name, string detail, string meta, bool isReady) =>
        new(name, string.IsNullOrWhiteSpace(detail) ? "Status unavailable." : detail,
            isReady ? "Ready" : "Not ready", meta ?? string.Empty);

    private void Page_Loaded(object sender, RoutedEventArgs args) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void Refresh_Click(object sender, RoutedEventArgs args) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OpenDataFolder_Click(object sender, RoutedEventArgs args) => OpenDataFolderRequested?.Invoke(this, EventArgs.Empty);
    private void OpenLogs_Click(object sender, RoutedEventArgs args) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);

    private void AppearanceChoices_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // The selector can report -1 while its template loads; that is not a user choice.
        if (_applyingAppearance || sender is not ComboBox { SelectedIndex: >= 0 } choices) return;
        AppearanceChanged?.Invoke(this, choices.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        });
    }
}

public sealed class RuntimeStatusItem(string name, string detail, string status, string meta)
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public string Status { get; } = status;
    public string Meta { get; } = meta;
}
