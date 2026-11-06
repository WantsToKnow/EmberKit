using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using FireAxe.ViewModels;
using ReactiveUI.Avalonia;

namespace FireAxe.Views;

public partial class AdditionalFeaturesWindow : ReactiveWindow<AdditionalFeaturesViewModel>
{
    public AdditionalFeaturesWindow()
    {
        InitializeComponent();
        this.RegisterViewModelConnection((model, disposables) =>
        {
            model.CloseRequested += Close;
            Disposable.Create(() => model.CloseRequested -= Close).DisposeWith(disposables);
        });
        Closed += (_, _) => ViewModel?.Dispose();
    }
}
