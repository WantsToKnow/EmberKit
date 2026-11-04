using System;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace FireAxe.ViewModels;

public sealed class WorkshopTransferViewModel : ViewModelBase, IDisposable
{
    private readonly AddonRoot _root;
    private readonly IDisposable _invalidHandler;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    public WorkshopTransferViewModel(AddonRoot root, int tab = 0)
    {
        _root = root;
        SelectedTab = tab;
        if (GamePathUtils.Resolve(root.ParentSettings?.GamePath ?? root.GamePath, root.IsDirectoryPathSet ? root.DirectoryPath : null) is { } gamePath)
            root.GamePath = gamePath;
        root.PropertyChanged += OnRootPropertyChanged;
        _invalidHandler = root.RegisterInvalidHandler(() => CloseRequested?.Invoke());
        _root.NewDownloadItem += Downloads.Add;
        if (root.DownloadService is DownloadService service)
            foreach (var download in service.ActiveDownloadItems) Downloads.Add(download);
        var ready = this.WhenAnyValue(model => model.IsBusy).Select(busy => !busy);
        DownloadCommand = ReactiveCommand.CreateFromTask(() => RunAsync(token => PrepareDownloadsAsync(LinkText, false, token)), ready);
        ImportCommand = ReactiveCommand.CreateFromTask(() => RunAsync(token => PrepareDownloadsAsync(ImportText, true, token)), ready);
        LoadCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            var path = await ChooseTextFile.Handle(Unit.Default);
            if (path is null) return;
            if (new FileInfo(path).Length > WorkshopManifest.MaxTextLength) throw new FormatException("清单超过 2 MiB，请分批导入。");
            ImportText = await File.ReadAllTextAsync(path, token);
            var parsed = WorkshopManifest.Parse(ImportText);
            Status = $"已读取 {parsed.Entries.Length} 个工坊 ID；无效行 {parsed.InvalidLines}，重复行 {parsed.DuplicateLines}。点击「导入并下载」开始。";
        }), ready);
        ExportCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            var result = await WorkshopManifest.ExportAsync(_root.GetDescendants().ToArray(), _root.HttpClient, EnabledOnly,
                new Progress<string>(message => Status = message), token);
            token.ThrowIfCancellationRequested();
            ExportText = WorkshopManifest.Serialize(result.Entries, result.Skipped);
            Report = FormatSkips(result.Skipped);
            Status = $"可分享 {result.Entries.Length} 个作品，跳过 {result.Skipped.Length} 个。";
            if (result.Entries.Length == 0) return;
            var path = await SaveTextFile.Handle(Unit.Default);
            if (path is null) return;
            await File.WriteAllTextAsync(path, ExportText, new UTF8Encoding(false), token);
            Status = $"已保存 TXT：{result.Entries.Length} 个作品，跳过 {result.Skipped.Length} 个。";
        }), ready);
        ChooseGameCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async _ =>
        {
            if (await ChooseGameDirectory.Handle(Unit.Default) is { } path) GamePath = path;
        }), ready);
        OrganizeCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            var result = await _root.Storage.RefreshAsync(token);
            Report = string.Join("\n", result.Warnings);
            Status = result.Pending ? _root.Storage.Status : $"移动 {result.Moved} · 去重 {result.DuplicatesDeleted} · 修复入口 {result.RestoredLinks} · 找回 {result.DiscoveredMods} · 清理记录 {result.RemovedRecords}。" + _root.Storage.Status;
        }), ready);
        SyncGameCommand = OrganizeCommand;
        LaunchGameCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            var console = CreateGameConsole();
            if (ModStorage.IsGameRunning(console.GameDirectory)) throw new InvalidOperationException("游戏已经运行。请先检查连接；若不是通过助手启动，需要退出游戏后再用此按钮启动一次。");
            var feedback = new KillFeedbackSettings { Enabled = KillFeedback.Enabled, Scope = KillFeedback.Scope, AutoPistolEnabled = KillFeedback.AutoPistolEnabled };
            var result = await _root.Storage.RefreshAsync(token);
            if (result.Pending || result.Warnings.Length > 0) throw new IOException("本地整理尚未完成：" + _root.Storage.Status);
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(console.PrepareLaunch(feedback));
            GameConnectionStatus = feedback.RequiresClientModule
                ? "已按普通方式交给 Steam 启动；正在等待本次游戏连接并加载自产客户端反馈模块…"
                : "已交给 Steam 启动，请等游戏主菜单出现，再检查连接或应用 Mod。";
            Status = GameConnectionStatus;
            if (feedback.RequiresClientModule)
            {
                try { GameConnectionStatus = await console.AttachFeedbackAsync(token); }
                catch (OperationCanceledException) { GameConnectionStatus = "已取消等待反馈初始化；游戏保持运行，已发起的模块加载可能仍会完成。"; }
                catch (Exception ex) { GameConnectionStatus = "游戏已启动，但击杀反馈未就绪：" + ex.Message; }
                Status = GameConnectionStatus;
            }
        }), ready);
        CheckGameConnectionCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            GameConnectionStatus = "正在连接游戏…";
            await using var connection = await CreateGameConsole().ConnectAsync(token);
            var mounted = await connection.ReadMountedAddonsAsync(token);
            var feedback = KillFeedback.RequiresClientModule ? " " + await connection.ReadKillFeedbackAsync(token) : "";
            GameConnectionStatus = $"已连接游戏，并核对安装目录；当前挂载 {mounted.Count} 个 Mod。" + feedback + " 应用按钮会自动断开地图或服务器、返回主菜单。";
            Status = GameConnectionStatus;
        }), ready);
        ApplyInMainMenuCommand = ReactiveCommand.CreateFromTask(() => RunAsync(async token =>
        {
            SelectedTab = 5;
            GameConnectionStatus = "正在连接游戏并应用 Mod…";
            Status = await _root.Storage.ReloadInMainMenuAsync(CreateGameConsole(), token);
            GameConnectionStatus = "已收到游戏重新挂载的回执，可以进入新地图测试。";
        }), ready);
        ShowMainMenuReloadCommand = ReactiveCommand.Create(() => { SelectedTab = 5; });
        CancelCommand = ReactiveCommand.Create(() => _operation?.Cancel());
        ShowDownloadResultsCommand = ReactiveCommand.Create(() => { SelectedTab = 4; });
        OpenDownloadDirectoryCommand = ReactiveCommand.Create(() =>
        {
            if (Directory.Exists(DownloadDirectory)) Utils.ShowInFileExplorer(DownloadDirectory, true);
            else Status = "还没有已归档的文件。下载并校验完成后会保存在：" + DownloadDirectory;
        });
        RefreshDownloadResultsCommand = ReactiveCommand.Create(RefreshDownloadResults);
        _root.DownloadHistory.Changed += RefreshDownloadResults;
        RefreshDownloadResults();
    }

    public event Action? CloseRequested;
    public int SelectedTab { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool IsBusy { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string LinkText { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string ImportText { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string ExportText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Report { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Status { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "就绪。操作结果和跳过原因会显示在这里。";
    public bool EnabledOnly { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool EnableDownloads { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public bool IgnoreWorkshopHash { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public KillFeedbackSettings KillFeedback => _root.KillFeedback;
    public string GamePath { get => _root.GamePath; set => _root.GamePath = value; }
    public string? LibraryDirectoryPath => _root.IsDirectoryPathSet ? _root.DirectoryPath : null;
    public bool KeepSnapshot { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public ModStorage LocalStorage => _root.Storage;
    public DownloadItemListViewModel Downloads { get; } = new();
    public ObservableCollection<WorkshopDownloadResultViewModel> DownloadResults { get; } = new();
    public string DownloadDirectory => _root.Storage.BackupPath;
    public string DownloadResultsSummary { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string DownloadResultsButton { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "下载结果";
    public bool HasDownloadResults => DownloadResults.Count > 0;
    public string GameConnectionStatus { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "首次使用：通过助手启动游戏，即可建立控制台连接。普通启动的游戏需先退出一次。";
    public ReactiveCommand<Unit, Unit> LaunchGameCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckGameConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyInMainMenuCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMainMenuReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDownloadResultsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDownloadDirectoryCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshDownloadResultsCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ChooseGameCommand { get; }
    public ReactiveCommand<Unit, Unit> OrganizeCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncGameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public Interaction<Unit, string?> ChooseTextFile { get; } = new();
    public Interaction<Unit, string?> SaveTextFile { get; } = new();
    public Interaction<Unit, string?> ChooseGameDirectory { get; } = new();

    private GameConsole CreateGameConsole() => new(GamePathUtils.FromPath(GamePath)
        ?? GamePathUtils.FromPath(LibraryDirectoryPath) ?? throw new InvalidOperationException("请先在「本地存储」设置游戏目录。"));

    private async Task PrepareDownloadsAsync(string text, bool preserveState, CancellationToken token)
    {
        var ignoreWorkshopHash = IgnoreWorkshopHash;
        var parsed = WorkshopManifest.Parse(text);
        if (parsed.Entries.Length == 0) throw new FormatException("没有有效的工坊链接或 ID。每行填写一个作品，可直接导入本程序导出的 TXT。");
        Status = $"正在向 Steam 验证 {parsed.Entries.Length} 个作品或合集，并展开合集内容…";
        var entries = preserveState ? parsed.Entries : parsed.Entries.Select(entry => entry with { Enabled = EnableDownloads }).ToArray();
        var plan = await WorkshopManifest.ResolveDownloadsAsync(entries, _root.HttpClient, token);
        token.ThrowIfCancellationRequested();
        var queued = WorkshopManifest.QueueDownloads(_root, plan, ignoreWorkshopHash);
        Report = FormatSkips(plan.Skipped);
        Status = $"新建下载 {queued.Added.Length} 个；重试 {queued.Retried} 个；已有 {queued.AlreadyPresent} 个；跳过 {plan.Skipped.Length} 个。临时文件位于 firefox_temp；完成后自动归档和应用启用状态。"
            + (ignoreWorkshopHash ? " 本批下载忽略工坊 Hash，仍检查 VPK 格式；大小不符仅记录警告。" : "");
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        Report = "";
        _operation = new();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Status = "已取消后续操作；已完成的备份和链接会保留，已入队的下载可在下方单独取消。"; if (SelectedTab == 5) GameConnectionStatus = Status; }
        catch (Exception ex) { Status = "操作未完成：" + ex.Message; if (SelectedTab == 5) GameConnectionStatus = Status; }
        finally { _operation?.Dispose(); _operation = null; IsBusy = false; }
    }

    private static string FormatSkips(WorkshopListSkip[] skipped) => skipped.Length == 0 ? "没有跳过的作品。" : string.Join("\n", skipped.Select(item => $"{item.Name}：{item.Reason}"));

    private void RefreshDownloadResults()
    {
        if (_disposed) return;
        foreach (var item in DownloadResults) item.Dispose();
        DownloadResults.Clear();
        foreach (var result in _root.DownloadHistory.GetResults())
            DownloadResults.Add(new(result, _root.DownloadHistory, message => Status = message));
        var failures = DownloadResults.Count(result => result.HasError);
        DownloadResultsSummary = $"本地文件 {DownloadResults.Count(result => result.CanOpenFile)} · 失败 / 取消 {failures}。记录会保留，重开窗口也可查看。";
        DownloadResultsButton = failures > 0 ? $"查看下载结果（{failures} 项未成功）" : $"查看下载结果（{DownloadResults.Count}）";
        this.RaisePropertyChanged(nameof(HasDownloadResults));
        this.RaisePropertyChanged(nameof(DownloadDirectory));
    }

    private void OnRootPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AddonRoot.GamePath)) this.RaisePropertyChanged(nameof(GamePath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operation?.Cancel();
        _root.NewDownloadItem -= Downloads.Add;
        _root.DownloadHistory.Changed -= RefreshDownloadResults;
        foreach (var item in DownloadResults) item.Dispose();
        _root.PropertyChanged -= OnRootPropertyChanged;
        _invalidHandler.Dispose();
        Downloads.Dispose();
    }
}
