using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckClipboardDownloads(string output)
    {
        var directory = Path.Combine(output, "clipboard-" + Guid.NewGuid().ToString("N"));
        var addons = Path.Combine(directory, "game", "left4dead2", "addons");
        Directory.CreateDirectory(addons);
        var sample = Path.Combine(directory, "sample.vpk");
        WriteVpk(sample, "Clipboard sample", "models/v_models/v_smg_mp5.mdl");
        var data = File.ReadAllBytes(sample);
        var steam = new SteamFixtureHandler(data) { MismatchHash = true };
        using var client = new HttpClient(steam);
        using var settings = new AppSettings((App)Application.Current!)
        {
            LastOpenDirectory = null, GamePath = "", IsAutoDetectWorkshopItemLinkInClipboard = false,
            IsFileBackupEnabled = false
        };
        using var downloads = new DownloadItemListViewModel();
        var manager = new AppWindowManager(settings, downloads, client);
        using var main = new MainWindowViewModel(settings, manager, new FixtureDownloads(data), client, downloads);
        var window = manager.CreateMainWindow(main);
        window.Show();
        Complete(main.OpenDirectoryAsync(addons));
        var root = main.AddonRoot!;
        var accept = false;
        var prompts = 0;
        using var prompt = main.ShowAutoDetectWorkshopItemLinkDialogInteraction.RegisterHandler(context =>
        {
            prompts++;
            context.SetOutput(accept);
        });
        settings.IsAutoDetectWorkshopItemLinkInClipboard = true;
        Complete(window.Clipboard!.SetTextAsync("https://steamcommunity.com/sharedfiles/filedetails/?id=50"));
        main.CheckClipboard();
        Pump(() => prompts == 1);
        Check(!root.GetDescendants().Any() && !window.OwnedWindows.OfType<WorkshopTransferWindow>().Any(),
            "declining a clipboard link creates no mod or download window");

        accept = true;
        const string link = "https://steamcommunity.com/sharedfiles/filedetails/?id=51";
        Complete(window.Clipboard.SetTextAsync(link));
        main.CheckClipboard();
        Pump(() => window.OwnedWindows.OfType<WorkshopTransferWindow>().Any());
        var transfer = window.OwnedWindows.OfType<WorkshopTransferWindow>().Single();
        var model = transfer.ViewModel!;
        Check(model.SelectedTab == 0 && model.LinkText == link && model.IgnoreWorkshopHash
            && !root.GetDescendants().Any() && steam.MetadataRequests == 0,
            "accepting a clipboard link opens the new link-download page with the copied URL and default hash option, without creating a legacy mod");
        main.CheckClipboard();
        PumpFrames();
        Check(prompts == 2 && window.OwnedWindows.OfType<WorkshopTransferWindow>().Count() == 1,
            "an unchanged clipboard link does not reopen the download window or repeat the prompt");

        var queued = 0;
        root.NewDownloadItem += _ => queued++;
        ((ICommand)model.DownloadCommand).Execute(null);
        Pump(() => !model.IsBusy && root.DownloadHistory.FindInstalled(51) is not null);
        Check(queued == 1 && root.DownloadHistory.GetResults().Single().HashCheckSkipped
            && model.DownloadResults.Single().CanOpenFile,
            "clipboard downloads use the shared progress queue, archive and visible result history with hash comparison skipped");
        transfer.Close();
        window.Close();
        main.CloseDirectory();
        Pump(() => !root.IsValid);
    }
}
