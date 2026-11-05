using Avalonia.Controls;
using Avalonia.Interactivity;
using FireAxe.ViewModels;

namespace FireAxe.Views;

public partial class ModCategoryHomeView : UserControl
{
    public ModCategoryHomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateColumns();
        SizeChanged += (_, _) => UpdateColumns();
    }

    private void UpdateColumns()
    {
        if (DataContext is ModCatalogViewModel catalog)
            catalog.Columns = Bounds.Width >= 1380 ? 4 : Bounds.Width >= 900 ? 3 : 2;
    }

    private void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VpkAddonViewModel { Addon: { } addon } } && DataContext is ModCatalogViewModel catalog)
            catalog.OpenMod(addon);
    }
}
