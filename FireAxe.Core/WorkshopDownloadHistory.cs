using System.Text.Json;
using Serilog;

namespace FireAxe;

public enum WorkshopDownloadOutcome { Installed, Failed, Cancelled }
public sealed record WorkshopDownloadResult(ulong Id, string Title, WorkshopDownloadOutcome Outcome,
    string? StorageFileName = null, string? Error = null, DateTimeOffset? FinishedAt = null, bool HashCheckSkipped = false,
    bool RemovedFromHistory = false, string? RetainedFileName = null);

// Keep the latest completed attempt per workshop ID, independently of missing-node cleanup.
public sealed class WorkshopDownloadHistory(AddonRoot root)
{
    private readonly Dictionary<ulong, WorkshopDownloadResult> _records = new();
    private string? _loadedPath;
    private string HistoryPath => Path.Combine(root.Storage.BackupPath, ".manager", "download-results.json");
    public event Action? Changed;

    private void Load()
    {
        var path = HistoryPath;
        if (_loadedPath == path) return;
        _records.Clear();
        _loadedPath = path;
        try
        {
            if (File.Exists(path))
                foreach (var result in JsonSerializer.Deserialize<WorkshopDownloadResult[]>(File.ReadAllText(path)) ?? [])
                    if (result.Id > 0 && Enum.IsDefined(result.Outcome)) _records[result.Id] = result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Log.Warning(ex, "Cannot read workshop download results"); }
    }

    public void Record(WorkshopDownloadResult result)
    {
        if (result.Id == 0) throw new ArgumentException("工坊 ID 不能为 0。", nameof(result));
        Load();
        _records[result.Id] = result with { FinishedAt = result.FinishedAt ?? DateTimeOffset.Now, RemovedFromHistory = false };
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning(ex, "Cannot save workshop download results"); }
        Changed?.Invoke();
    }

    private void Save()
    {
        WorkshopBackup.EnsureRealDirectory(root.Storage.BackupPath);
        WorkshopBackup.EnsureRealDirectory(Path.GetDirectoryName(HistoryPath)!);
        var temporary = HistoryPath + ".tmp";
        if (new FileInfo(HistoryPath).LinkTarget is not null || new FileInfo(temporary).LinkTarget is not null)
            throw new IOException("下载结果文件不能是链接。");
        File.WriteAllText(temporary, JsonSerializer.Serialize(_records.Values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, HistoryPath, true);
    }

    public async Task RemoveAsync(ulong id)
    {
        root.ThrowIfInvalid();
        var result = GetResults().FirstOrDefault(result => result.Id == id);
        if (result is null) return;
        if (result.Outcome != WorkshopDownloadOutcome.Installed)
            foreach (var node in root.GetDescendants().OfType<WorkshopVpkAddon>()
                .Where(node => node.PublishedFileId == id && ModStorage.IsUninstalledDownload(node)
                    && node.DownloadCheckTask is not { IsCompleted: false }).ToArray())
                await node.DestroyAsync();
        Load();
        var previous = _records.GetValueOrDefault(id);
        // Keep cached titles for the mod list, without recreating a deleted history row
        // from an installed node. A new completed attempt makes the row visible again.
        _records[id] = result with { RemovedFromHistory = true };
        try
        {
            Save();
        }
        catch
        {
            if (previous is not null) _records[id] = previous;
            else _records.Remove(id);
            throw;
        }
        root.Save();
        Changed?.Invoke();
    }

    public WorkshopVpkAddon? FindInstalled(ulong id) => root.GetDescendants().OfType<WorkshopVpkAddon>()
        .FirstOrDefault(addon => addon.IsValid && addon.PublishedFileId == id && addon.VpkFilePath is { } path && File.Exists(path));

    public string? GetFilePath(WorkshopDownloadResult result)
    {
        if (result.RetainedFileName is { } retained && retained == Path.GetFileName(retained)
            && retained.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            var retainedPath = Path.Combine(root.Storage.TemporaryPath, retained);
            if (File.Exists(retainedPath)) return retainedPath;
        }
        if (FindInstalled(result.Id)?.VpkFilePath is { } path) return path;
        return result.StorageFileName is { } name && ModStorage.IsBodyName(name)
            ? Path.Combine(root.Storage.BackupPath, name) : null;
    }

    public string GetDisplayTitle(VpkAddon addon)
    {
        Load();
        var record = WorkshopManifest.KnownId(addon) is { } id ? _records.GetValueOrDefault(id) : null;
        record ??= addon.StorageFileName is { } name
            ? _records.Values.FirstOrDefault(result => string.Equals(result.StorageFileName, name, StringComparison.OrdinalIgnoreCase)) : null;
        var candidates = new[] { record?.Title, (addon as WorkshopVpkAddon)?.PublishedFileDetailsCache?.Title, addon.Inspection?.Title };
        return candidates.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title) && !title.Trim().All(char.IsAsciiDigit))?.Trim() ?? addon.Name;
    }

    public IReadOnlyList<WorkshopDownloadResult> GetResults()
    {
        Load();
        var results = new Dictionary<ulong, WorkshopDownloadResult>(_records);
        foreach (var addon in root.GetDescendants().OfType<WorkshopVpkAddon>())
            if (addon.IsValid && addon.PublishedFileId is { } id && id > 0 && addon.VpkFilePath is { } path && File.Exists(path)
                && !results.ContainsKey(id))
                results[id] = new(id, addon.Inspection?.Title ?? addon.PublishedFileDetailsCache?.Title ?? addon.Name,
                    WorkshopDownloadOutcome.Installed, addon.StorageFileName);
        return results.Values.Where(result => !result.RemovedFromHistory).OrderByDescending(result => result.FinishedAt).ThenBy(result => result.Id).ToArray();
    }
}
