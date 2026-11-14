using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winference.App.ViewModels;

namespace Winference.App.Views;

public sealed partial class DeveloperPage : Page
{
    public DeveloperViewModel ViewModel { get; }

    public DeveloperPage(DeveloperViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs args)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ViewModel.Address);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void ForgetApp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: AppPermissionItem item }) ViewModel.Forget(item);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs args) => ViewModel.ClearLog();
}
