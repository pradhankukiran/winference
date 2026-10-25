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
}
