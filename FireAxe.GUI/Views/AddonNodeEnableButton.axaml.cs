// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using Avalonia.Input;
using FireAxe.ViewModels;
using ReactiveUI.Avalonia;

namespace FireAxe.Views;

public partial class AddonNodeEnableButton : ReactiveUserControl<AddonNodeSimpleViewModel>
{
    public AddonNodeEnableButton()
    {
        InitializeComponent();

        DoubleTapped += AddonNodeEnableButton_DoubleTapped;
    }

    private void AddonNodeEnableButton_DoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }
}
