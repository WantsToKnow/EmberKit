using System.Reactive;
using FireAxe.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System.Reactive.Disposables.Fluent;

namespace FireAxe.Views;

public partial class WorkshopTransferWindow : ReactiveWindow<WorkshopTransferViewModel>
{
    public WorkshopTransferWindow()
    {
        InitializeComponent();
        this.RegisterViewModelConnection((model, disposables) =>
        {
            model.CloseRequested += Close;
            System.Reactive.Disposables.Disposable.Create(() => model.CloseRequested -= Close).DisposeWith(disposables);
            model.ChooseTextFile.RegisterHandler(async context => context.SetOutput(await CommonMessageBoxes.ChooseFile(this,
                new ChooseFileOptions { Title = "导入工坊 TXT 清单", FilePatterns = ["*.txt"] }))).DisposeWith(disposables);
            model.SaveTextFile.RegisterHandler(async context => context.SetOutput(await CommonMessageBoxes.SaveFile(this,
                new SaveFileOptions { Title = "导出工坊 TXT 清单", FilePatterns = ["*.txt"], DefaultFileExtension = ".txt", SuggestedFileName = "求生之路2-Mod清单.txt" }))).DisposeWith(disposables);
            model.ChooseGameDirectory.RegisterHandler(async context => context.SetOutput(await CommonMessageBoxes.ChooseDirectory(this,
                new ChooseDirectoryOptions { Title = "选择求生之路 2 游戏根目录", StartDirectoryPath = model.GamePath }))).DisposeWith(disposables);
        });
        Closed += (_, _) => ViewModel?.Dispose();
    }
}
