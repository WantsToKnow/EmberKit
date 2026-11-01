using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckDownloadResults(string output, AddonRoot root)
    {
        Pump(() => root.DownloadHistory.GetResults().Any(result => result.Id == 44 && result.FinishedAt is not null));
        var success = root.DownloadHistory.GetResults().Single(result => result.Id == 44);
        var failed = root.DownloadHistory.GetResults().Single(result => result.Id == 45);
        Check(success.Outcome == WorkshopDownloadOutcome.Installed && File.Exists(root.DownloadHistory.GetFilePath(success)),
            "download results are successful only after validation and archive commit, and point at the actual VPK");
        Check(failed.Outcome == WorkshopDownloadOutcome.Failed && !string.IsNullOrWhiteSpace(failed.Error)
            && root.DownloadHistory.GetFilePath(failed) is null, "rejected downloaded bytes leave a visible failure reason without claiming an installed path");
        var loaded = new WorkshopDownloadHistory(root).GetResults();
        Check(loaded.Single(result => result.Id == 45).Error == failed.Error
            && loaded.Single(result => result.Id == 44).StorageFileName == success.StorageFileName,
            "completed success and failure records survive reloading history from disk");
        Check(root.DownloadHistory.GetFilePath(new(999, "Untrusted saved name", WorkshopDownloadOutcome.Installed, "../../outside.vpk")) is null,
            "download result paths cannot escape the physical backup directory");

        using var model = new WorkshopTransferViewModel(root, 4);
        var window = new WorkshopTransferWindow { ViewModel = model };
        window.Show();
        PumpFrames();
        var goodRow = model.DownloadResults.Single(result => result.WorkshopId == "工坊 ID：44");
        var badRow = model.DownloadResults.Single(result => result.WorkshopId == "工坊 ID：45");
        Check(goodRow.CanOpenFile && ((ICommand)goodRow.OpenFileLocationCommand).CanExecute(null)
            && !badRow.CanOpenFile && !((ICommand)badRow.OpenFileLocationCommand).CanExecute(null),
            "file-location actions are available for real archived files, and disabled for rejected downloads");
        Check(badRow.Detail.Contains(failed.Error!) && window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == badRow.Detail)
            && window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == goodRow.FilePath),
            "the results tab renders the specific failure and final VPK path");
        root.DownloadHistory.Record(new(46, "取消的任务", WorkshopDownloadOutcome.Cancelled, Error: "下载已取消。"));
        PumpFrames();
        Check(model.DownloadResults.Any(result => result.WorkshopId == "工坊 ID：46" && result.ResultText == "已取消"),
            "an open results tab updates immediately when a task finishes or is cancelled");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "download-results-preview.png"));
        window.Close();
        using var reopened = new WorkshopTransferViewModel(root, 4);
        Check(reopened.DownloadResults.Any(result => result.WorkshopId == "工坊 ID：45" && result.HasError),
            "closing and reopening the workshop window does not hide a failed result");

        var removalWindow = new WorkshopTransferWindow { ViewModel = reopened };
        removalWindow.Show(); PumpFrames();
        void ClickRemove(ulong id)
        {
            var row = reopened.DownloadResults.Single(result => result.WorkshopId == "工坊 ID：" + id);
            var button = removalWindow.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && button.DataContext == row && button.Command == row.RemoveRecordCommand);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), removalWindow)!.Value;
            removalWindow.MouseDown(point, MouseButton.Left); removalWindow.MouseUp(point, MouseButton.Left);
            Pump(() => reopened.DownloadResults.All(result => result.WorkshopId != row.WorkshopId));
        }
        ClickRemove(45);
        Check(root.GetDescendants().OfType<WorkshopVpkAddon>().All(node => node.PublishedFileId != 45)
            && new WorkshopDownloadHistory(root).GetResults().All(result => result.Id != 45),
            "clicking delete on a failed result removes its empty node and persistent history");
        var installed = root.DownloadHistory.FindInstalled(44)!;
        var body = installed.VpkFilePath!;
        var title = root.DownloadHistory.GetDisplayTitle(installed);
        ClickRemove(44);
        Check(installed.IsValid && File.Exists(body) && root.DownloadHistory.GetDisplayTitle(installed) == title
            && new WorkshopDownloadHistory(root).GetResults().All(result => result.Id != 44),
            "removing a successful history row retains the mod and cached title without recreating the row from the installed file");
        ClickRemove(46);
        Check(new WorkshopDownloadHistory(root).GetResults().All(result => result.Id != 46),
            "cancelled download history can also be removed persistently");
        root.DownloadHistory.Record(success);
        PumpFrames();
        Check(reopened.DownloadResults.Any(result => result.WorkshopId == "工坊 ID：44"),
            "a subsequent completed attempt makes a removed history item visible again");
        using (var frame = removalWindow.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "download-results-removal-preview.png"));
        removalWindow.Close();
    }
}
