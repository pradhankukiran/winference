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
}
