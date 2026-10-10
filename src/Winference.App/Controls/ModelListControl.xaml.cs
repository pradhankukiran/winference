using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Winference.App.ViewModels;
using Winference.Core.Models;

namespace Winference.App.Controls;

/// <summary>The model list, shared by the Models page and the chat picker so both behave identically.</summary>
public sealed partial class ModelListControl : UserControl
{
    private bool _applyingFilter;

    public ModelLibraryViewModel Library { get; }

    /// <summary>A row's main button was used, so a host such as the chat flyout can close.</summary>
    public event EventHandler? ActionInvoked;

    public ModelListControl(ModelLibraryViewModel library, bool compact)
    {
        Library = library;
        InitializeComponent();
        var row = (DataTemplate)Resources[compact ? "CompactRowTemplate" : "FullRowTemplate"];
        OnThisPcList.ItemTemplate = row;
        if (compact)
        {
            // The picker sits in a flyout, which is already a surface of its own.
            OnThisPcGroup.Style = null;
            AvailableGroup.Style = null;
        }
        AvailableList.ItemTemplate = row;
        ApplyFilter();
        Loaded += (_, _) => Library.PropertyChanged += Library_PropertyChanged;
        Unloaded += (_, _) => Library.PropertyChanged -= Library_PropertyChanged;
    }

    private void Library_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModelLibraryViewModel.Filter)) ApplyFilter();
    }

    private void ApplyFilter()
    {
        _applyingFilter = true;
        FilterBar.SelectedItem = Library.Filter switch
        {
            ModelFilter.Npu => NpuFilter,
            ModelFilter.GpuNpu => GpuNpuFilter,
            ModelFilter.Gpu => GpuFilter,
            _ => AllFilter
        };
        _applyingFilter = false;
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_applyingFilter) return;
        Library.Filter = ReferenceEquals(sender.SelectedItem, NpuFilter) ? ModelFilter.Npu
            : ReferenceEquals(sender.SelectedItem, GpuNpuFilter) ? ModelFilter.GpuNpu
            : ReferenceEquals(sender.SelectedItem, GpuFilter) ? ModelFilter.Gpu
            : ModelFilter.All;
    }

    private async void Action_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ModelEntryViewModel { CanAct: true } entry }) return;
        ActionInvoked?.Invoke(this, EventArgs.Empty);
        await Library.PerformAsync(entry);
    }

    private void More_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ModelEntryViewModel entry } target) return;
        var menu = new MenuFlyout();
        var details = new MenuFlyoutItem { Text = "Details" };
        details.Click += (_, _) => ShowDetails(entry, target);
        AutomationProperties.SetAutomationId(details, "ModelDetails");
        menu.Items.Add(details);
        if (entry.CanRemove)
        {
            var remove = new MenuFlyoutItem { Text = "Remove" };
            remove.Click += async (_, _) => await Library.RemoveAsync(entry);
            AutomationProperties.SetName(remove, entry.RemoveAutomationName);
            AutomationProperties.SetAutomationId(remove, "RemoveModel");
            menu.Items.Add(remove);
        }
        menu.ShowAt(target);
    }

    private static void ShowDetails(ModelEntryViewModel entry, FrameworkElement target)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = entry.Publisher, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        content.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = entry.DetailsText, FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        new Flyout
        {
            Content = new ScrollViewer
            {
                Content = content, MaxHeight = 360, MaxWidth = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        }.ShowAt(target);
    }
}
