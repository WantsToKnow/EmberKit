using Avalonia.Controls;
using Avalonia.Interactivity;
using FireAxe.ViewModels;

namespace FireAxe.Views;

public partial class ModCategoryHomeView : UserControl
{
    public ModCategoryHomeView()
    {
        InitializeComponent();
    }

    private void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VpkAddonViewModel { Addon: { } addon } } && DataContext is ModCatalogViewModel catalog)
            catalog.OpenMod(addon);
    }
}
