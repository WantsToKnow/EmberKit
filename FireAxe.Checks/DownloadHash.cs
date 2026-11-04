using System.Security.Cryptography;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckDownloadHashOption(string output)
    {
        var directory = Path.Combine(output, "hash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var sample = Path.Combine(directory, "sample.vpk");
        WriteVpk(sample, "Hash mismatch sample", "models/v_models/v_rif_ak47.mdl");
        var data = File.ReadAllBytes(sample);
        using var client = new HttpClient(new SteamFixtureHandler(data) { MismatchHash = true });
        var downloads = new FixtureDownloads(data);
        var root = new AddonRoot { DirectoryPath = Path.Combine(directory, "game", "left4dead2", "addons"),
            TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), HttpClient = client, DownloadService = downloads };
        using var model = new WorkshopTransferViewModel(root) { LinkText = "47" };
        var window = new WorkshopTransferWindow { ViewModel = model };
        window.Show();
        PumpFrames();
        Check(model.IgnoreWorkshopHash && HashOption().IsChecked == true && model.EnableDownloads
            && window.GetVisualDescendants().OfType<CheckBox>().Any(box => box.IsEffectivelyVisible
                && box.Content as string == "下载完成后自动启用" && box.IsChecked == true),
            "link downloads default to skipping workshop hashes and enabling completed mods, with both options checked");
        HashOption().IsChecked = false;
        Check(!model.IgnoreWorkshopHash, "users can explicitly enable strict workshop hash checking");
        ((ICommand)model.DownloadCommand).Execute(null);
        Pump(() => !model.IsBusy && Result(47)?.Outcome == WorkshopDownloadOutcome.Failed);
        var addon = root.GetDescendants().OfType<WorkshopVpkAddon>().Single(node => node.PublishedFileId == 47);
        Complete(addon.DownloadCheckTask ?? Task.CompletedTask);
        Check(addon.VpkFilePath is null && Result(47)!.Error!.Contains("Hash 不一致") && !Result(47)!.HashCheckSkipped,
            "explicit strict checking rejects a valid same-size VPK with a different workshop SHA-1");

        HashOption().IsChecked = true;
        Check(model.IgnoreWorkshopHash, "the download checkbox updates the actual bypass option");
        model.SelectedTab = 1;
        PumpFrames();
        Check(HashOption().IsChecked == true, "TXT import shares the explicitly selected hash option");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "download-hash-option-preview.png"));
        using var importModel = new WorkshopTransferViewModel(root, 1)
        {
            ImportText = WorkshopManifest.Serialize([new(47, "Retry mismatch", true, 11)])
        };
        ((ICommand)importModel.ImportCommand).Execute(null);
        Pump(() => !importModel.IsBusy && Result(47)?.Outcome == WorkshopDownloadOutcome.Installed);
        Complete(addon.DownloadCheckTask ?? Task.CompletedTask);
        Check(root.GetDescendants().OfType<WorkshopVpkAddon>().Count(node => node.PublishedFileId == 47) == 1
            && addon.IsValid && addon.Priority == 11 && addon.IsEnabledInHierarchy && importModel.Status.Contains("重试 1 个"),
            "a fresh TXT import skips workshop SHA-1 by default, retries the failed node and preserves shared state without duplication");
        Check(File.ReadAllBytes(addon.VpkFilePath!).SequenceEqual(data) && addon.ContentHash == Convert.ToHexString(SHA256.HashData(data)),
            "ignoring workshop SHA-1 still commits the original valid bytes and computes their SHA-256 storage identity");
        var installed = new WorkshopDownloadHistory(root).GetResults().Single(result => result.Id == 47);
        Check(installed.HashCheckSkipped && installed.Outcome == WorkshopDownloadOutcome.Installed,
            "the installed result persists the explicit hash bypass across history reloads");
        model.SelectedTab = 4;
        PumpFrames();
        Check(model.DownloadResults.Single(row => row.WorkshopId == "工坊 ID：47").Detail.Contains("忽略工坊 Hash"),
            "download results visibly label an installation made with workshop hash checking disabled");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "download-hash-result-preview.png"));

        var body = addon.VpkFilePath;
        Complete(addon.CheckDownloadAsync(ignoreWorkshopHash: false));
        Check(Result(47)?.Outcome == WorkshopDownloadOutcome.Failed && !Result(47)!.HashCheckSkipped
            && addon.VpkFilePath == body && File.ReadAllBytes(body!).SequenceEqual(data),
            "explicit strict checking still applies to a redownload and preserves the installed body on rejection");
        Complete(addon.CheckDownloadAsync());
        Check(Result(47) is { Outcome: WorkshopDownloadOutcome.Installed, HashCheckSkipped: true } && addon.VpkFilePath == body,
            "direct download retries also skip workshop hashes by default and keep the archived body");

        model.SelectedTab = 0;
        model.LinkText = "48";
        downloads.InvalidVpkNext = true;
        ((ICommand)model.DownloadCommand).Execute(null);
        Pump(() => !model.IsBusy && Result(48)?.Outcome == WorkshopDownloadOutcome.Failed);
        Check(Result(48)!.HashCheckSkipped && !Result(48)!.Error!.Contains("大小") && !Result(48)!.Error!.Contains("Hash 不一致")
            && root.DownloadHistory.FindInstalled(48) is null,
            "bypass still rejects an invalid VPK whose size exactly matches Steam metadata");
        model.LinkText = "49";
        downloads.CorruptNext = true;
        ((ICommand)model.DownloadCommand).Execute(null);
        Pump(() => !model.IsBusy && Result(49)?.Outcome == WorkshopDownloadOutcome.Failed);
        Check(Result(49)!.HashCheckSkipped && Result(49)!.Error!.Contains("大小") && root.DownloadHistory.FindInstalled(49) is null,
            "bypass still rejects a download whose size differs from Steam metadata");
        using (var reopened = new WorkshopTransferViewModel(root))
            Check(reopened.IgnoreWorkshopHash && reopened.EnableDownloads,
                "reopening the dialog restores the defaults of ignoring workshop hashes and enabling completed mods");
        window.Close();
        Complete(root.DisposeAsync().AsTask());

        WorkshopDownloadResult? Result(ulong id) => root.DownloadHistory.GetResults().FirstOrDefault(result => result.Id == id);
        CheckBox HashOption() => window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.IsEffectivelyVisible
            && box.Content is string content && content.StartsWith("忽略工坊 Hash"));
    }
}
