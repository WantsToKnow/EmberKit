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
        var directory = Path.Combine(output, "hash-option-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sample = Path.Combine(directory, "sample.vpk");
        WriteVpk(sample, "Hash mismatch sample", "models/v_models/v_rif_ak47.mdl");
        var data = File.ReadAllBytes(sample);
        using var client = new HttpClient(new SteamFixtureHandler(data) { MismatchHash = true });
        var downloads = new FixtureDownloads(data);
        var root = new AddonRoot { DirectoryPath = Path.Combine(directory, "game", "left4dead2", "addons"),
            TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), HttpClient = client, DownloadService = downloads };
        using var model = new WorkshopTransferViewModel(root) { LinkText = "47", EnableDownloads = true };
        var window = new WorkshopTransferWindow { ViewModel = model };
        window.Show();
        PumpFrames();
        Check(!model.IgnoreWorkshopHash && HashOption().IsChecked == false,
            "workshop hash bypass is off by default and visible in link downloads");
        ((ICommand)model.DownloadCommand).Execute(null);
        Pump(() => !model.IsBusy && Result(47)?.Outcome == WorkshopDownloadOutcome.Failed);
        var addon = root.GetDescendants().OfType<WorkshopVpkAddon>().Single(node => node.PublishedFileId == 47);
        Complete(addon.DownloadCheckTask ?? Task.CompletedTask);
        Check(addon.VpkFilePath is null && Result(47)!.Error!.Contains("Hash 不一致") && !Result(47)!.HashCheckSkipped,
            "a valid same-size VPK with a different workshop SHA-1 is rejected by default");

        HashOption().IsChecked = true;
        Check(model.IgnoreWorkshopHash, "the download checkbox updates the actual bypass option");
        model.SelectedTab = 1;
        PumpFrames();
        Check(HashOption().IsChecked == true, "TXT import shares the explicitly selected hash option");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "download-hash-option-preview.png"));
        model.ImportText = WorkshopManifest.Serialize([new(47, "Retry mismatch", true, 11)]);
        ((ICommand)model.ImportCommand).Execute(null);
        Pump(() => !model.IsBusy && Result(47)?.Outcome == WorkshopDownloadOutcome.Installed);
        Complete(addon.DownloadCheckTask ?? Task.CompletedTask);
        Check(root.GetDescendants().OfType<WorkshopVpkAddon>().Count(node => node.PublishedFileId == 47) == 1
            && addon.IsValid && addon.Priority == 11 && addon.IsEnabledInHierarchy && model.Status.Contains("重试 1 个"),
            "TXT import retries the failed node with the chosen option instead of skipping it or duplicating it");
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
        Complete(addon.CheckDownloadAsync());
        Check(Result(47)?.Outcome == WorkshopDownloadOutcome.Failed && !Result(47)!.HashCheckSkipped
            && addon.VpkFilePath == body && File.ReadAllBytes(body!).SequenceEqual(data),
            "bypass does not leak into a later default download, and a rejected redownload preserves the installed body");

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
            Check(!reopened.IgnoreWorkshopHash, "reopening the dialog restores default hash checking");
        window.Close();
        Complete(root.DisposeAsync().AsTask());

        WorkshopDownloadResult? Result(ulong id) => root.DownloadHistory.GetResults().FirstOrDefault(result => result.Id == id);
        CheckBox HashOption() => window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.IsEffectivelyVisible
            && box.Content is string content && content.StartsWith("忽略工坊 Hash"));
    }
}
