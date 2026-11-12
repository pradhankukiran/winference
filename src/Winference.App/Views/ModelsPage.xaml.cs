using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winference.App.Controls;
using Winference.App.ViewModels;

namespace Winference.App.Views;

public sealed partial class ModelsPage : Page
{
    public ModelLibraryViewModel Library { get; }

    public ModelsPage(ModelLibraryViewModel library)
    {
        Library = library;
        InitializeComponent();
        ListHost.Content = new ModelListControl(library, compact: false, installedOnly: true);
    }

    /// <summary>The user wants to find more models.</summary>
    public event EventHandler? DiscoverRequested;

    private void Discover_Click(object sender, RoutedEventArgs args) => DiscoverRequested?.Invoke(this, EventArgs.Empty);

    private void Page_Loaded(object sender, RoutedEventArgs args) => _ = Library.RefreshAsync();

    // The chat picker shares this list and has no search box of its own.
    private void Page_Unloaded(object sender, RoutedEventArgs args) => ModelSearch.Text = string.Empty;

    private void Refresh_Click(object sender, RoutedEventArgs args) => _ = Library.RefreshAsync();
    private async void Add_Click(object sender, RoutedEventArgs args) => await Library.AddAsync();
    private void ModelSearch_TextChanged(object sender, TextChangedEventArgs args) => Library.Query = ModelSearch.Text;
}
