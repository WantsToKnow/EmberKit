using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FireAxe.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FireAxe.Views;

public partial class ModCategoryHomeView : UserControl
{
    private ModCatalogViewModel? _catalog;
    private bool _attached;
    private int _locateVersion;
    public ModCategoryHomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConnectCatalog();
        AttachedToVisualTree += (_, _) => { _attached = true; ConnectCatalog(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; ConnectCatalog(); };
    }

    private void ConnectCatalog()
    {
        if (_catalog is not null) _catalog.LocateRequested -= ScrollToMod;
        _catalog = _attached ? DataContext as ModCatalogViewModel : null;
        if (_catalog is not null) _catalog.LocateRequested += ScrollToMod;
    }

    private async void ScrollToMod(VpkAddon addon)
    {
        var version = ++_locateVersion;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        BringIntoView();
        // The folder height animates, so align once more after it has settled.
        await Task.Delay(220);
        BringIntoView();

        void BringIntoView()
        {
            if (!_attached || version != _locateVersion) return;
            var row = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(row => row.Name == "inlineModCard"
                && row.DataContext is VpkAddonViewModel model && model.Addon == addon);
            row?.BringIntoView();
            row?.Focus();
        }
    }

    private void PrepareTagEditor(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Flyout: Flyout { Content: Control editor } } button)
            editor.DataContext = button.DataContext;
    }

    private void SelectMod(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: VpkAddonViewModel { Addon: { } addon } } checkBox
            && DataContext is ModCatalogViewModel catalog)
            catalog.SelectMod(addon, checkBox.IsChecked == true);
    }

    private void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VpkAddonViewModel { Addon: { } addon } } && DataContext is ModCatalogViewModel catalog)
            catalog.OpenMod(addon);
    }
}
