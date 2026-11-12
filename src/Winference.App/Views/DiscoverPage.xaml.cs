using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Winference.App.ViewModels;
using Winference.Core.Discover;
using Winference.Core.Models;

namespace Winference.App.Views;

public sealed partial class DiscoverPage : Page
{
    public DiscoverViewModel ViewModel { get; }

    public DiscoverPage(DiscoverViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs args) => _ = ViewModel.OpenAsync();

    private void Refresh_Click(object sender, RoutedEventArgs args) => _ = ViewModel.RefreshAsync();

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ViewModel.Query = sender.Text;

    private void RunsOn_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.RunsOn = ReferenceEquals(sender.SelectedItem, RunsOnNpu) ? ModelFilter.Npu
            : ReferenceEquals(sender.SelectedItem, RunsOnGpuNpu) ? ModelFilter.GpuNpu
            : ReferenceEquals(sender.SelectedItem, RunsOnGpu) ? ModelFilter.Gpu
            : ModelFilter.All;

    private void Need_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton toggle) return;
        var capability = ReferenceEquals(toggle, NeedThinking) ? ModelCapabilities.Reasoning
            : ReferenceEquals(toggle, NeedPictures) ? ModelCapabilities.Vision
            : ReferenceEquals(toggle, NeedTools) ? ModelCapabilities.Tools
            : ModelCapabilities.Coding;
        ViewModel.SetNeed(capability, toggle.IsChecked == true);
    }

    private void Family_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is DiscoverFamilyViewModel family) ViewModel.Selected = family;
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs args) => ViewModel.Selected = null;

    private async void Action_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: DiscoverVariantViewModel { Entry.CanAct: true } variant })
            await ViewModel.Library.PerformAsync(variant.Entry);
    }
}
