// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using Newtonsoft.Json;
using Serilog;
using System.Diagnostics.CodeAnalysis;

namespace FireAxe;

public class WorkshopVpkAddon : VpkAddon
{
    public const string MetaInfoFileName = ".workshop";
    internal static readonly JsonSerializerSettings s_metaInfoJsonSettings = new() { Formatting = Formatting.Indented };
    public static readonly object ForceUseWorkshopTitle = new();
    private ulong? _publishedFileId;
    private bool? _isAutoUpdate;
    private Task<GetPublishedFileDetailsResult>? _detailsTask;
    private IDownloadItem? _download;
    public override Type SaveType => typeof(WorkshopVpkAddonSave);
    public override bool IsDirectory => false;
    // Kept only to locate pre-v3 records during the one-time migration. No .workshop directories are created.
    public override string FileExtension => ".workshop";
    public override string? VpkFilePath
    {
        get
        {
            if (StoredVpkPath is { } path) return path;
            var metadata = Path.Combine(FullFilePath, MetaInfoFileName);
            try
            {
                if (File.Exists(metadata) && JsonConvert.DeserializeObject<WorkshopVpkMetaInfo>(File.ReadAllText(metadata)) is { } old)
                    return WorkshopBackup.Inside(FullFilePath, old.CurrentFile);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { Log.Warning(ex, "Cannot read old workshop metadata"); }
            return null;
        }
    }

    [DisallowNull]
    public ulong? PublishedFileId
    {
        get => _publishedFileId;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!NotifyAndSetIfChanged(ref _publishedFileId, value)) return;
            ClearCaches();
            Root.RequestSave = true;
            // Editing an ID is an explicit user download action. Loading saves sets the backing field.
            if (IsAutoCheckEnabled) _ = CheckDownloadAsync();
        }
    }
    public bool? IsAutoUpdate { get => _isAutoUpdate; set => NotifyAndSetIfChanged(ref _isAutoUpdate, value); }
    public bool IsAutoUpdateActually => false;
    public PublishedFileDetails? PublishedFileDetailsCache { get; private set => NotifyAndSetIfChanged(ref field, value); }
    public Task? DownloadCheckTask { get; private set => NotifyAndSetIfChanged(ref field, value); }
    public IDownloadItem? DownloadItem
    {
        get => _download;
        private set
        {
            if (NotifyAndSetIfChanged(ref _download, value) && value is not null) Root.NotifyDownloadItem(value);
        }
    }
    public bool RequestAutoSetName { get; set; }
    public bool RequestApplyTagsFromWorkshop { get; set; } = true;

    protected override void OnCreate()
    {
        base.OnCreate();
        IsEnabled = true;
    }

    public async Task<GetPublishedFileDetailsResult> GetPublishedFileDetailsAsync(CancellationToken cancellationToken = default)
    {
        if (PublishedFileId is not { } id) return new(null, GetPublishedFileDetailsResultStatus.InvalidPublishedFileId);
        _detailsTask ??= PublishedFileUtils.GetPublishedFileDetailsAsync(id, Root.HttpClient, DestructionCancellationToken);
        try
        {
            var result = await _detailsTask.WaitAsync(cancellationToken);
            if (result.IsSucceeded) PublishedFileDetailsCache = result.Content;
            return result;
        }
        finally { _detailsTask = null; }
    }
    public async Task<PublishedFileDetails?> GetPublishedFileDetailsAllowCacheAsync(CancellationToken cancellationToken = default)
    {
        if (PublishedFileDetailsCache is { } cache) return cache;
        var result = await GetPublishedFileDetailsAsync(cancellationToken);
        return result.IsSucceeded ? result.Content : null;
    }

    // Explicit download only. Startup, Check, rescan, image loading and sync never call this method.
    public Task CheckDownloadAsync(bool ignoreWorkshopHash = true)
    {
        if (DownloadCheckTask is { IsCompleted: false }) return DownloadCheckTask;
        if (PublishedFileId is not { } id) return Task.CompletedTask;
        var task = DownloadAsync(id, ignoreWorkshopHash);
        DownloadCheckTask = task;
        _ = task.ContinueWith(_ => { if (DownloadCheckTask == task) DownloadCheckTask = null; }, Root.TaskScheduler);
        return task;
    }
    private async Task DownloadAsync(ulong id, bool ignoreWorkshopHash)
    {
        var creator = this.GetValidTaskCreator();
        var token = DestructionCancellationToken;
        string? file = null, url = null;
        var title = Name;
        try
        {
            var result = await GetPublishedFileDetailsAsync(token);
            var details = result.IsSucceeded ? result.Content : null;
            if (details is null) throw new InvalidDataException("不是有效的求生之路 2 工坊作品。");
            title = details.Title;
            url = PublishedFileUtils.GetDownloadUri(details)?.AbsoluteUri ?? throw new InvalidDataException("Steam 没有提供可下载的 VPK 文件地址。");
            WorkshopBackup.EnsureRealDirectory(Root.Storage.TemporaryPath);
            file = Path.Combine(Root.Storage.TemporaryPath, id + "_" + Guid.NewGuid().ToString("N") + ".vpk");
            using (var download = Root.DownloadService.Download(url, file))
            {
                await creator.StartNew(self => self.DownloadItem = download);
                using var cancellation = token.Register(download.Cancel);
                await download.WaitAsync().WaitAsync(token);
                if (download.Status == DownloadStatus.Cancelled) throw new OperationCanceledException("下载已取消。");
                if (download.Status != DownloadStatus.Succeeded) throw download.Exception ?? new IOException("下载未成功。");
            }
            var actualSize = FileSystemUtils.GetContentLength(file);
            if (details.FileSize > 0 && actualSize != details.FileSize)
                Log.Warning("Workshop {WorkshopId} size differs from Steam metadata; keeping download. Expected {ExpectedSize}, actual {ActualSize}", id, details.FileSize, actualSize);
            var expected = new Uri(url).Segments.LastOrDefault()?.Trim('/');
            if (!ignoreWorkshopHash && expected is { Length: 40 } && expected.All(char.IsAsciiHexDigit))
            {
                await using var input = File.OpenRead(file);
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(input, token));
                if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"下载内容与 Steam 内容 Hash 不一致。预期 {expected}，实际 {actual}。");
            }
            WorkshopBackup.ValidateVpk(file);
            await creator.StartNew(self =>
            {
                if (self.RequestApplyTagsFromWorkshop && details.Tags is { } tags)
                    foreach (var tag in tags) self.AddTag(tag?.Tag);
                self.RequestApplyTagsFromWorkshop = false;
                self.RequestAutoSetName = false;
                self.InvalidateProblem<AddonDownloadFailedProblem>();
                self.InvalidateProblem<WorkshopVpkNotLoadedProblem>();
            });
            Task commit = Task.CompletedTask;
            await creator.StartNew(self => commit = self.Root.Storage.CommitDownloadAsync(self, file, token));
            await commit;
            await creator.StartNew(self => self.Check());
            await creator.StartNew(self => self.Root.DownloadHistory.Record(new(id, title,
                WorkshopDownloadOutcome.Installed, self.StorageFileName, HashCheckSkipped: ignoreWorkshopHash)));
        }
        catch (OperationCanceledException)
        {
            if (Root.IsValid)
                await Task.Factory.StartNew(() => Root.DownloadHistory.Record(new(id, title,
                    WorkshopDownloadOutcome.Cancelled, Error: "下载已取消。", HashCheckSkipped: ignoreWorkshopHash)), CancellationToken.None, TaskCreationOptions.None, Root.TaskScheduler);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explicit workshop download failed");
            await creator.StartNew(self =>
            {
                self.Root.DownloadHistory.Record(new(id, title, WorkshopDownloadOutcome.Failed, Error: ex.Message, HashCheckSkipped: ignoreWorkshopHash,
                    RetainedFileName: file is not null && File.Exists(file) ? Path.GetFileName(file) : null));
                self.SetProblem(new AddonDownloadFailedProblem(self) { Url = url ?? "", FilePath = file ?? "", Exception = ex });
            });
        }
        finally
        {
            await creator.StartNew(self => self.DownloadItem = null);
            // CommitDownloadAsync consumes successful downloads. Keep failed files for recovery.
            if (Directory.Exists(Root.Storage.TemporaryPath) && !Directory.EnumerateFileSystemEntries(Root.Storage.TemporaryPath).Any())
                try { Directory.Delete(Root.Storage.TemporaryPath); } catch (IOException) { }
        }
    }

    protected override void OnCheck(Action<Task> submit)
    {
        base.OnCheck(submit);
        SetProblem(VpkFilePath is null ? new WorkshopVpkNotLoadedProblem(this) : null);
    }
    protected override void OnClearCaches() { base.OnClearCaches(); PublishedFileDetailsCache = null; }
    public override async Task<string?> TryGetSuggestedNameAsync(object? arg = null, CancellationToken cancellationToken = default)
    {
        if (arg == ForceUseWorkshopTitle && (await GetPublishedFileDetailsAllowCacheAsync(cancellationToken))?.Title is { } title) return SanitizeName(title);
        return await base.TryGetSuggestedNameAsync(arg, cancellationToken);
    }
    protected override Task OnDestroyAsync()
    {
        _download?.Cancel();
        return TaskUtils.WhenAllIgnoreCanceled(new[] { base.OnDestroyAsync(), DownloadCheckTask ?? Task.CompletedTask, (Task?)_detailsTask ?? Task.CompletedTask });
    }
    protected override void OnCreateSave(AddonNodeSave value)
    {
        base.OnCreateSave(value);
        var save = (WorkshopVpkAddonSave)value;
        save.PublishedFileId = PublishedFileId;
        save.IsAutoUpdate = false;
        save.RequestAutoSetName = false;
        save.RequestApplyTagsFromWorkshop = RequestApplyTagsFromWorkshop;
    }
    protected override void OnLoadSave(AddonNodeSave value)
    {
        base.OnLoadSave(value);
        var save = (WorkshopVpkAddonSave)value;
        _publishedFileId = save.PublishedFileId;
        RequestApplyTagsFromWorkshop = save.RequestApplyTagsFromWorkshop;
    }
    // Compatibility for old cleanup menus; v3 never leaves redundant workshop versions.
    public DeleteRedundantVpkFilesReport RequestDeleteRedundantVpkFiles() => new();
    public sealed class DeleteRedundantVpkFilesReport
    {
        public IReadOnlyCollection<string> Files => [];
        public bool IsEmpty => true;
        public long TotalFileSize => 0;
        public void Execute() { }
        public static DeleteRedundantVpkFilesReport Combine(IEnumerable<DeleteRedundantVpkFilesReport> _) => new();
    }
}
