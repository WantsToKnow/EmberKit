using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using ReactiveUI;

namespace FireAxe.ViewModels;

public sealed class WorkshopDownloadResultViewModel : IDisposable
{
    public WorkshopDownloadResultViewModel(WorkshopDownloadResult result, WorkshopDownloadHistory history, Action<string>? reportError = null)
    {
        Title = string.IsNullOrWhiteSpace(result.Title) ? "工坊 " + result.Id : result.Title;
        WorkshopId = "工坊 ID：" + result.Id;
        FilePath = history.GetFilePath(result);
        CanOpenFile = FilePath is { } path && File.Exists(path);
        HasError = result.Outcome != WorkshopDownloadOutcome.Installed;
        ResultText = result.Outcome switch
        {
            WorkshopDownloadOutcome.Failed => "失败",
            WorkshopDownloadOutcome.Cancelled => "已取消",
            _ when !CanOpenFile => "文件已移除",
            _ when history.FindInstalled(result.Id) is { } addon => addon.IsEnabledInHierarchy ? "已安装 · 已启用" : "已安装 · 已禁用",
            _ => "已下载"
        };
        Detail = result.Error ?? (result.FinishedAt is { } finished ? "完成于 " + finished.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "已在本地的工坊 Mod");
        if (result.HashCheckSkipped) Detail += " · 本次下载已选择忽略工坊 Hash 校验";
        if (result.AnimationSummary is { } summary) Detail += " · " + summary;
        PathText = FilePath ?? "未安装，没有已归档的 VPK。";
        StatusBrush = HasError ? Brushes.Orange : CanOpenFile ? Brushes.LightGreen : Brushes.Gray;
        OpenFileLocationCommand = ReactiveCommand.Create(() =>
        {
            if (FilePath is { } file && File.Exists(file)) Utils.ShowInFileExplorer(file);
        }, Observable.Return(CanOpenFile));
        RemoveText = HasError && !CanOpenFile ? "删除失败项" : "移除记录";
        RemoveRecordCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            try { await history.RemoveAsync(result.Id); }
            catch (Exception ex) { reportError?.Invoke("移除记录未完成：" + ex.Message); }
        });
    }

    public string Title { get; }
    public string WorkshopId { get; }
    public string ResultText { get; }
    public string Detail { get; }
    public string? FilePath { get; }
    public string PathText { get; }
    public bool CanOpenFile { get; }
    public bool HasError { get; }
    public IBrush StatusBrush { get; }
    public string RemoveText { get; }
    public ReactiveCommand<Unit, Unit> OpenFileLocationCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveRecordCommand { get; }

    public void Dispose()
    {
        OpenFileLocationCommand.Dispose();
        RemoveRecordCommand.Dispose();
    }
}
