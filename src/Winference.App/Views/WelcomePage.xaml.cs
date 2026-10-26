using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winference.App.ViewModels;

namespace Winference.App.Views;

/// <summary>First run: one recommendation and one button, or a plain explanation if this PC can't run models.</summary>
public sealed partial class WelcomePage : Page, INotifyPropertyChanged
{
    public ModelLibraryViewModel Library { get; }
    public bool ShowGetButton => Library.HasRecommendation && !Library.IsDownloading;

    public event EventHandler? AllModelsRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public WelcomePage(ModelLibraryViewModel library)
    {
        Library = library;
        InitializeComponent();
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ModelLibraryViewModel.HasRecommendation) or nameof(ModelLibraryViewModel.IsDownloading))
                PropertyChanged?.Invoke(this, new(nameof(ShowGetButton)));
        };
    }

    private void Page_Loaded(object sender, RoutedEventArgs args) => _ = Library.RefreshAsync();
    private async void Recommended_Click(object sender, RoutedEventArgs args) => await Library.ActOnRecommendationAsync();
    private void Cancel_Click(object sender, RoutedEventArgs args) => Library.CancelDownload();
    private void CheckAgain_Click(object sender, RoutedEventArgs args) => _ = Library.RefreshAsync();
    private async void UseFile_Click(object sender, RoutedEventArgs args) => await Library.AddAsync();
    private void AllModels_Click(object sender, RoutedEventArgs args) => AllModelsRequested?.Invoke(this, EventArgs.Empty);
}
