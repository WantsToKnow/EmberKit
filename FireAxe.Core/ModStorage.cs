using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace FireAxe;

public sealed record ModStorageReport(int Moved, int DuplicatesDeleted, int RestoredLinks, int DiscoveredMods, int RemovedRecords, string[] Warnings, bool Pending = false);

// One physical store and one game entry per content hash. Workshop checks never download.
public sealed class ModStorage(AddonRoot root) : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _operation = new(1);
    private readonly Dictionary<string, (long Size, DateTime Time, string Hash)> _verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);
    private long _nextBodyId = 1;
    private FileSystemWatcher? _watcher;
    private Timer? _retry;
    private bool _working, _queued, _disposed, _pending;
    private long _stateVersion;
    private string? _listPath;
    private DateTime _listWriteTime;
    public Task? CurrentTask { get; private set; }
    public bool IsStarted { get; private set; }
    public bool UseSymbolicLinks
    {
        get;
        set
        {
            if (NotifyAndSetIfChanged(ref field, value)) { root.RequestSave = true; RequestSync(); }
        }
    }
    public HashSet<string> IgnoredHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string AddonsPath => Path.GetFullPath((GamePathUtils.FromPath(root.GamePath) ?? GamePathUtils.FromPath(root.DirectoryPath)) is { } game
        ? GamePathUtils.GetAddonsPath(game) : root.DirectoryPath);
    public string BackupPath => Path.Combine(AddonsPath, "modebackup");
    public string TemporaryPath => Path.Combine(AddonsPath, "firefox_temp");
    public string Status { get; private set => NotifyAndSetIfChanged(ref field, value); } = "等待本地检查";
    // L4D2's addon metadata truncates the absolute extensionless path at 127 bytes.
    // Complete SHA-256 identities stay in metadata and the catalog, separately from filenames.
    public static string EntryName(string hash) => "mod_" + hash[..24].ToLowerInvariant() + ".vpk";
    public static bool IsBodyName(string name) => name == Path.GetFileName(name)
        && name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
        && name.Length is >= 11 and <= 23 && name[0] == 'm' && name[1..^4].All(char.IsAsciiDigit);

    private void ReserveBodyIds(IEnumerable<string> recordedNames)
    {
        foreach (var name in recordedNames.Concat(Directory.EnumerateFileSystemEntries(BackupPath).Select(Path.GetFileName)))
            if (name is not null && IsBodyName(name) && long.TryParse(name[1..^4], out var id))
                _nextBodyId = Math.Max(_nextBodyId, checked(id + 1));
    }

    private string AllocateBodyPath()
    {
        while (true)
        {
            var path = Path.Combine(BackupPath, "m" + (_nextBodyId++).ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".vpk");
            if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget is null) return path;
        }
    }
    public static bool IsEntryName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return Path.GetExtension(path).Equals(".vpk", StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("mod_", StringComparison.OrdinalIgnoreCase)
            && name.Length is 28 or 68 && name[4..].All(char.IsAsciiHexDigit);
    }

    public static void ValidateEntryPath(string path)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(Path.ChangeExtension(Path.GetFullPath(path), null)) > 127)
            throw new IOException("VPK 加载入口的完整路径超过游戏的 127 字节限制，会被截断。请使用更短的英文游戏安装路径：" + path);
    }

    public static bool IsGameRunning(string? gameDirectory = null)
    {
        foreach (var name in new[] { "left4dead2", "left4dead2_d3d11" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    if (gameDirectory is null) return true;
                    try
                    {
                        if (string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), Path.TrimEndingDirectorySeparator(gameDirectory), StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private bool IsManagedGameRunning() => IsGameRunning(GamePathUtils.FromPath(root.GamePath) ?? GamePathUtils.FromPath(root.DirectoryPath));

    private void SetState(bool verified, string status)
    {
        Status = status;
        foreach (var node in root.GetDescendants())
        {
            node.IsGameSynchronized = verified;
            node.DeploymentStatus = status;
        }
    }

    public void RequestSync()
    {
        _stateVersion++;
        if (!IsStarted || _working || _disposed || !root.IsAutoCheckEnabled) return;
        SetState(false, "等待同步游戏入口…");
        if (_queued) return;
        _queued = true;
        CurrentTask = Task.Delay(180).ContinueWith(async _ =>
        {
            _queued = false;
            if (_disposed || !root.IsValid) return;
            try { await RefreshAsync(); }
            catch (Exception ex) { SetState(false, "同步失败：" + ex.Message); Log.Error(ex, "Mod storage sync failed"); }
        }, CancellationToken.None, TaskContinuationOptions.None, root.TaskScheduler).Unwrap();
    }

    public void Forget(AddonNode node)
    {
        if (!IsStarted || _working || !root.IsAutoCheckEnabled) return;
        if (IsManagedGameRunning() && node.GetSelfAndDescendantsByDfsPostorder().OfType<VpkAddon>().Any(vpk => !IsUninstalledDownload(vpk)))
            throw new InvalidOperationException("请退出游戏后删除已安装的 Mod；未安装的下载条目可以直接删除。");
        foreach (var vpk in node.GetSelfAndDescendantsByDfsPostorder().OfType<VpkAddon>())
            if (vpk.ContentHash is { } hash) IgnoredHashes.Add(hash);
        root.RequestSave = true;
        RequestSync();
    }

    public static bool IsUninstalledDownload(VpkAddon node) => node is WorkshopVpkAddon
        && node.ContentHash is null && node.StorageFileName is null
        && (node.VpkFilePath is not { } path || !File.Exists(path) && new FileInfo(path).LinkTarget is null);

    private void StartWatching()
    {
        if (_watcher is not null || _disposed) return;
        _watcher = new(AddonsPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        void Changed(string path)
        {
            if (path.StartsWith(BackupPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(TemporaryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith(".addonroot", StringComparison.OrdinalIgnoreCase)) return;
            if (_working) return;
            _ = Task.Factory.StartNew(RequestSync, CancellationToken.None, TaskCreationOptions.None, root.TaskScheduler);
        }
        _watcher.Created += (_, e) => Changed(e.FullPath);
        _watcher.Changed += (_, e) => Changed(e.FullPath);
        _watcher.Deleted += (_, e) => Changed(e.FullPath);
        _watcher.Renamed += (_, e) => Changed(e.FullPath);
        _watcher.Error += (_, _) => _pending = true;
        _watcher.EnableRaisingEvents = true;
        _retry = new(_ =>
        {
            if (_disposed || _working) return;
            var listChanged = _listPath is not null && File.GetLastWriteTimeUtc(_listPath) != _listWriteTime;
            if (listChanged || _pending && !IsManagedGameRunning())
                _ = Task.Factory.StartNew(RequestSync, CancellationToken.None, TaskCreationOptions.None, root.TaskScheduler);
        }, null, 3000, 3000);
    }

    public async Task<ModStorageReport> RefreshAsync(CancellationToken token = default)
    {
        root.ThrowIfInvalid();
        IsStarted = true;
        await _operation.WaitAsync(token);
        _working = true;
        var warnings = new List<string>();
        int moved = 0, duplicates = 0, repaired = 0, discovered = 0, removed = 0;
        try
        {
            WorkshopBackup.EnsureRealDirectory(AddonsPath);
            WorkshopBackup.EnsureRealDirectory(BackupPath);
            StartWatching();
            if (IsManagedGameRunning())
            {
                _pending = true;
                SetState(false, "游戏运行中：修改待应用。可点「主菜单应用」返回主菜单并刷新，或退出游戏后自动同步。");
                return new(0, 0, 0, 0, 0, [], true);
            }
            _pending = false;
            SetState(false, "正在核对本体与游戏入口…");
            using var blocked = root.BlockAutoCheck();
            var game = GamePathUtils.FromPath(root.GamePath) ?? GamePathUtils.FromPath(root.DirectoryPath);
            if (game is not null) root.GamePath = game;
            var listPath = game is null ? null : GamePathUtils.GetAddonListPath(game);
            if (listPath is not null && new FileInfo(listPath).LinkTarget is not null) throw new IOException("addonlist.txt 不能是文件链接。");
            var oldEntries = listPath is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ModOrganizer.ReadEntries(listPath);
            var bodies = _bodies;
            bodies.Clear();
            var knownBodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var originals = ModBackupIndex.ReadOriginalPaths(AddonsPath, warnings, knownBodies);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodes = root.GetDescendants().OfType<VpkAddon>().ToList();
            foreach (var node in nodes)
                if (node.StorageFileName is { } name && node.ContentHash is { } hash)
                {
                    if (knownBodies.TryGetValue(name, out var expected) && !expected.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("同一个短编号对应不同 Hash，未更改文件：" + name);
                    knownBodies[name] = hash;
                }
            ReserveBodyIds(knownBodies.Keys);
            // Persist a unique pre-migration snapshot before moving any bodies or removing old links.
            if (nodes.Any(node => node.ContentHash is null) || Directory.EnumerateFiles(BackupPath, "*.vpk").Any(path => !IsBodyName(Path.GetFileName(path)))
                || Directory.EnumerateDirectories(AddonsPath).Any(path => Path.GetFileName(path) is not "modebackup" and not "firefox_temp"))
                Snapshot(listPath);
            else if (Directory.EnumerateFiles(AddonsPath, "mod_*.vpk").Any(path => Path.GetFileNameWithoutExtension(path).Length == 68))
                Snapshot(listPath);

            void Remember(string source, string hash, string body)
            {
                if (originals.GetValueOrDefault(source) != hash || knownBodies.GetValueOrDefault(body) != hash)
                    AppendOriginal(Path.GetRelativePath(AddonsPath, source), hash, body);
                originals[source] = hash;
                knownBodies[body] = hash;
            }

            async Task<string> Store(string source)
            {
                WorkshopBackup.ValidateVpk(source);
                var info = new FileInfo(source);
                var resolved = info.LinkTarget is null ? source : info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("链接目标缺失：" + source);
                if (!resolved.StartsWith(AddonsPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("链接指向 addons 外部，未移动：" + source);
                var inBank = FileSystemUtils.IsSamePath(Path.GetDirectoryName(resolved)!, BackupPath);
                var hash = inBank ? await VerifyBody(resolved, token, knownBodies.GetValueOrDefault(Path.GetFileName(resolved)))
                    : await WorkshopBackup.HashAsync(resolved, token);
                ValidateEntryPath(Path.Combine(AddonsPath, EntryName(hash)));
                if (bodies.Keys.Any(existing => existing != hash && EntryName(existing) == EntryName(hash)))
                    throw new IOException("检测到加载入口 Hash 前缀碰撞，未覆盖文件。");
                if (!bodies.ContainsKey(hash))
                {
                    var legacy = Path.Combine(BackupPath, hash + ".vpk");
                    if (File.Exists(legacy) && !FileSystemUtils.IsSamePath(legacy, source)) await VerifyBody(legacy, token, hash);
                    var body = inBank && IsBodyName(Path.GetFileName(resolved)) ? resolved : AllocateBodyPath();
                    // Journal the full identity before a rename. A crash can recover it by hashing the bank.
                    Remember(source, hash, Path.GetFileName(body));
                    if (!FileSystemUtils.IsSamePath(source, body))
                    {
                        if (info.LinkTarget is not null) throw new IOException("外部旧链接本体尚未迁入备份目录：" + source);
                        File.Move(source, body);
                        try { await VerifyBody(body, token, hash); moved++; }
                        catch { if (!File.Exists(source)) File.Move(body, source); throw; }
                    }
                    bodies[hash] = body;
                }
                var storedBody = bodies[hash];
                var relative = Path.GetRelativePath(AddonsPath, source);
                Remember(source, hash, Path.GetFileName(storedBody));
                if (!FileSystemUtils.IsSamePath(source, storedBody) && (File.Exists(source) || info.LinkTarget is not null))
                {
                    // A duplicate is deleted only after the bank hash has been verified.
                    if (info.LinkTarget is null && !FileSystemUtils.IsSameFile(source, storedBody))
                    {
                        if (await WorkshopBackup.HashAsync(source, token) != hash) throw new IOException("文件在校验后变更，未删除：" + source);
                        duplicates++;
                    }
                    aliases.Add(source);
                }
                return hash;
            }

            foreach (var body in Directory.EnumerateFiles(BackupPath, "*.vpk").OrderBy(path => IsBodyName(Path.GetFileName(path)) ? 0 : 1).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                token.ThrowIfCancellationRequested();
                try { await Store(body); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { warnings.Add(Path.GetFileName(body) + "：" + ex.Message); }
            }

            foreach (var node in nodes.ToArray())
            {
                if (node.ContentHash is { } existing && bodies.TryGetValue(existing, out var existingBody))
                { node.StorageFileName = Path.GetFileName(existingBody); continue; }
                var path = node.VpkFilePath;
                try
                {
                    if (path is not null && File.Exists(path)) node.ContentHash = await Store(path);
                    else
                    {
                        var recorded = originals.Where(pair => FileSystemUtils.IsSamePath(pair.Key, node.FullFilePath)
                            || node is WorkshopVpkAddon workshop && workshop.PublishedFileId is { } id
                                && Path.GetFileName(pair.Key).StartsWith(id + "_", StringComparison.OrdinalIgnoreCase))
                            .Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase).Where(bodies.ContainsKey).ToArray();
                        if (recorded.Length == 1) node.ContentHash = recorded[0];
                    }
                    if (node.ContentHash is { } hash && bodies.TryGetValue(hash, out var storedBody))
                    {
                        node.StorageFileName = Path.GetFileName(storedBody);
                        node.OriginalPaths = node.OriginalPaths.Concat(originals.Where(pair => pair.Value == hash).Select(pair => Path.GetRelativePath(AddonsPath, pair.Key))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    else if (node is not WorkshopVpkAddon { DownloadCheckTask.IsCompleted: false })
                    { await node.DestroyAsync(); nodes.Remove(node); removed++; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { warnings.Add(node.Name + "：" + ex.Message); }
            }
            foreach (var source in ScanIncoming(AddonsPath))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (RemoveDanglingBankLink(source)) continue;
                    if (IsEntryName(source))
                    {
                        var prefix = Path.GetFileNameWithoutExtension(source)[4..];
                        var matches = bodies.Keys.Where(hash => hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length == 1 && FileSystemUtils.IsSameFile(source, bodies[matches[0]]))
                        {
                            if (!Path.GetFileName(source).Equals(EntryName(matches[0]), StringComparison.OrdinalIgnoreCase))
                            {
                                AppendOriginal(Path.GetRelativePath(AddonsPath, source), matches[0]);
                                originals[source] = matches[0];
                                aliases.Add(source);
                            }
                            continue;
                        }
                    }
                    var storedHash = await Store(source);
                    var relative = Path.GetRelativePath(AddonsPath, source);
                    var match = nodes.FirstOrDefault(node => node.OriginalPaths.Contains(relative, StringComparer.OrdinalIgnoreCase)
                        || node.Name == Path.GetFileNameWithoutExtension(source)
                        || node is WorkshopVpkAddon workshop && workshop.PublishedFileId is { } id
                            && (Path.GetFileNameWithoutExtension(source) == id.ToString() || Path.GetFileName(source).StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)));
                    if (match is not null)
                    {
                        if (match.ContentHash is { } previous && previous != storedHash) IgnoredHashes.Add(previous);
                        match.ContentHash = storedHash;
                        match.StorageFileName = Path.GetFileName(bodies[storedHash]);
                        match.OriginalPaths = match.OriginalPaths.Append(relative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { warnings.Add(Path.GetRelativePath(AddonsPath, source) + "：" + ex.Message); }
            }
            foreach (var pair in bodies)
            {
                if (IgnoredHashes.Contains(pair.Key) || nodes.Any(node => node.ContentHash == pair.Key)) continue;
                var node = AddonNode.Create<LocalVpkAddon>(root);
                node.Name = root.GetUniqueChildName(Path.GetFileNameWithoutExtension(pair.Value), true);
                node.ContentHash = pair.Key;
                node.StorageFileName = Path.GetFileName(pair.Value);
                node.OriginalPaths = originals.Where(original => original.Value == pair.Key).Select(original => Path.GetRelativePath(AddonsPath, original.Key)).ToArray();
                var oldAlias = "fa_mod_" + pair.Key[..24].ToLowerInvariant() + ".vpk";
                node.IsEnabled = oldEntries.GetValueOrDefault(EntryName(pair.Key), oldEntries.GetValueOrDefault(oldAlias,
                    node.OriginalPaths.Select(path => oldEntries.GetValueOrDefault(path)).FirstOrDefault(state => state is not null) ?? "1")) == "1";
                nodes.Add(node); discovered++;
            }
            foreach (var duplicateGroup in nodes.Where(node => node.ContentHash is not null).GroupBy(node => node.ContentHash).Where(group => group.Count() > 1).ToArray())
            {
                var keep = duplicateGroup.OrderByDescending(node => node is WorkshopVpkAddon).First();
                keep.IsEnabled = duplicateGroup.Any(node => node.IsEnabled);
                keep.OriginalPaths = duplicateGroup.SelectMany(node => node.OriginalPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var duplicate in duplicateGroup.Where(node => node != keep))
                {
                    foreach (var reference in root.GetDescendants().OfType<RefAddonNode>().Where(node => node.SourceAddonId == duplicate.Id)) reference.SourceAddonId = keep.Id;
                    await duplicate.DestroyAsync(); nodes.Remove(duplicate); removed++;
                }
            }
            // Save identities before deleting old path aliases. A crash can always recover from modebackup.
            root.Save();
            foreach (var alias in aliases)
            {
                var hash = originals[alias];
                using var bankGuard = new FileStream(bodies[hash], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (new FileInfo(alias).LinkTarget is null)
                {
                    using var sourceGuard = new FileStream(alias, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (await WorkshopBackup.HashAsync(alias, token) != hash) throw new IOException("旧入口内容在整理后发生变化，未删除：" + alias);
                    File.Delete(WorkshopBackup.Inside(AddonsPath, Path.GetRelativePath(AddonsPath, alias)));
                }
                else File.Delete(WorkshopBackup.Inside(AddonsPath, Path.GetRelativePath(AddonsPath, alias)));
            }
            CleanupDirectories();
            var appliedVersion = _stateVersion;
            repaired += ApplyEntries(bodies, listPath, out _);
            foreach (var node in nodes.Where(node => node.IsValid))
            {
                node.CheckFiles();
                _ = node.CheckVpkValidity();
            }
            await ModBackupIndex.WriteAsync(game ?? GamePathUtils.FromPath(AddonsPath) ?? throw new InvalidOperationException("请设置游戏目录。"), bodies, nodes, token);
            root.Save();
            if (warnings.Count > 0)
            {
                _pending = true;
                SetState(false, "本地同步未完成：" + warnings[0]);
            }
            else if (_stateVersion != appliedVersion)
            {
                _pending = true;
                SetState(false, "开关已再次改变，正在等待应用最新状态…");
            }
            else { SetState(true, "已同步并核验游戏入口；下次启动游戏加载。检查与重新识别不会下载 Mod。"); root.NotifySynchronized(); }
            return new(moved, duplicates, repaired, discovered, removed, [.. warnings]);
        }
        catch (Exception ex)
        {
            _pending = true;
            SetState(false, "同步失败：" + ex.Message);
            throw;
        }
        finally { _working = false; _operation.Release(); }
    }

    // Explicit reload only: no migration, deduplication, body moves or body deletion while the game runs.
    public async Task<string> ReloadInMainMenuAsync(GameConsole console, CancellationToken token = default)
    {
        root.ThrowIfInvalid();
        var game = GamePathUtils.FromPath(root.GamePath) ?? GamePathUtils.FromPath(root.DirectoryPath)
            ?? throw new InvalidOperationException("请先设置游戏目录。");
        if (!FileSystemUtils.IsSamePath(game, console.GameDirectory)) throw new InvalidOperationException("控制台与 Mod 库的游戏目录不一致。");
        await _operation.WaitAsync(token);
        _working = true;
        IsStarted = true;
        var recoveryFailed = false;
        try
        {
            using var blocked = root.BlockAutoCheck();
            SetState(false, "正在核验待应用的 Mod 本体…");
            WorkshopBackup.EnsureRealDirectory(AddonsPath);
            WorkshopBackup.EnsureRealDirectory(BackupPath);
            var allNodes = root.GetDescendants().OfType<VpkAddon>().ToArray();
            var nodes = allNodes.Where(node => !IsUninstalledDownload(node)).ToArray();
            var skippedDownloads = allNodes.Length - nodes.Length;
            var bodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                if (node.ContentHash is not { } hash || node.StorageFileName is not { } name || !IsBodyName(name))
                    throw new IOException("有尚未归档的 Mod。请等下载完成；旧库请先退出游戏，完成一次本地整理。");
                var body = WorkshopBackup.Inside(BackupPath, name);
                await VerifyBody(body, token, hash);
                bodies[hash] = body;
            }
            // Include retained bodies from a previous version too, so stale enabled entries are removed.
            foreach (var body in Directory.EnumerateFiles(BackupPath, "*.vpk"))
            {
                var hash = await VerifyBody(body, token);
                bodies.TryAdd(hash, body);
            }
            var listPath = GamePathUtils.GetAddonListPath(game);
            if (new FileInfo(listPath).LinkTarget is not null) throw new IOException("addonlist.txt 不能是文件链接。");
            await using var connection = await console.ConnectAsync(token);
            var unloaded = false;
            var remounted = false;
            try
            {
                SetState(false, "正在返回主菜单并断开服务器…");
                GameConsole.Connection.CheckCommand(await connection.ExecuteAsync("disconnect\ngameui_activate", token));
                SetState(false, "正在卸载旧 Mod…请留在主菜单，暂勿进入地图。");
                unloaded = true;
                // disconnect can finish on the following frame; wait for an actual unload receipt.
                for (int attempt = 0; ; attempt++)
                {
                    var reply = await connection.ExecuteAsync("unload_all_addons", token);
                    var stillConnected = reply.Contains("connected", StringComparison.OrdinalIgnoreCase);
                    if (!stillConnected) GameConsole.Connection.CheckCommand(reply);
                    var mountedBeforeApply = await connection.ReadMountedAddonsAsync(token);
                    if (!stillConnected && mountedBeforeApply.Count == 0) break;
                    if (attempt >= 15) throw new IOException("游戏仍在挂载附加内容，未修改入口。请等返回主菜单后重试。");
                    await Task.Delay(200, token);
                }

                token.ThrowIfCancellationRequested();
                SetState(false, "正在应用启用状态和 VPK 入口…");
                var appliedVersion = _stateVersion;
                ApplyEntries(bodies, listPath, out var entries);
                SetState(false, "正在让游戏重新挂载 Mod，并核对游戏回执…");
                GameConsole.Connection.CheckCommand(await connection.ExecuteAsync("update_addon_paths", token));
                var mountedAddons = await connection.ReadMountedAddonsAsync(token);
                foreach (var entry in entries)
                {
                    var mounted = mountedAddons.Contains(GameConsole.Connection.NormalizePath(Path.Combine(AddonsPath, entry.Key)));
                    if (mounted != (entry.Value == "1"))
                    {
                        var node = nodes.FirstOrDefault(node => node.ContentHash is { } hash && EntryName(hash) == entry.Key);
                        var title = node is null ? entry.Key : root.DownloadHistory.GetDisplayTitle(node);
                        throw new IOException($"「{title}」的挂载回执不符：应{(entry.Value == "1" ? "启用" : "禁用")}，游戏报告{(mounted ? "仍在挂载" : "未挂载")}。请重试；仍失败时重启游戏。");
                    }
                }
                var enabledPaths = entries.Where(entry => entry.Value == "1")
                    .Select(entry => GameConsole.Connection.NormalizePath(Path.Combine(AddonsPath, entry.Key))).ToArray();
                if (mountedAddons.Any(path => !enabledPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException("游戏还挂载了未归档的附加内容。请退出游戏，完成一次本地整理后再试。");
                var gameEntries = ModOrganizer.ReadEntries(listPath);
                // The engine may prune disabled files that no longer have a top-level entry.
                if (entries.Any(entry => gameEntries.GetValueOrDefault(entry.Key, "0") != entry.Value))
                    throw new IOException("游戏刷新后的启用清单与开关不一致，请稍后重试。");
                remounted = true;
                _bodies.Clear();
                foreach (var pair in bodies) _bodies.Add(pair.Key, pair.Value);
                await ModBackupIndex.WriteAsync(game, bodies, nodes, token);
                root.Save();
                _listWriteTime = File.GetLastWriteTimeUtc(listPath);
                if (_stateVersion != appliedVersion) throw new IOException("应用过程中开关再次改变，请再点一次「返回主菜单并应用 Mod」。");
                _pending = false;
                SetState(true, "游戏已确认重新挂载 Mod 入口。" + (skippedDownloads > 0 ? $"跳过 {skippedDownloads} 个未安装的下载条目。" : "")
                    + "请进入新地图测试；个别模型或材质仍有旧缓存时需重启游戏。");
                root.NotifySynchronized();
                return Status;
            }
            finally
            {
                // Cancellation and disk errors must not leave the game deliberately unloaded.
                if (unloaded && !remounted)
                    try { GameConsole.Connection.CheckCommand(await connection.ExecuteAsync("update_addon_paths", CancellationToken.None)); }
                    catch (Exception ex) { recoveryFailed = true; Log.Warning(ex, "Could not restore addon paths after an interrupted reload"); }
            }
        }
        catch (Exception ex)
        {
            _pending = true;
            SetState(false, (ex is OperationCanceledException ? "主菜单应用已取消，已尝试恢复游戏挂载。" : "主菜单应用未完成：" + ex.Message)
                + (recoveryFailed ? " 恢复挂载也未能确认，请退出并重启游戏。" : ""));
            throw;
        }
        finally { _working = false; _operation.Release(); }
    }

    private int ApplyEntries(Dictionary<string, string> bodies, string? listPath, out Dictionary<string, string> entries)
    {
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in root.Nodes.SelectMany(node => node.GetAllNodesEnabledInHierarchy()))
        {
            var vpk = node is RefAddonNode reference ? reference.ActualSourceAddon as VpkAddon : node as VpkAddon;
            if (vpk?.ContentHash is { } hash)
                priorities[hash] = Math.Max(priorities.GetValueOrDefault(hash, int.MinValue), node.PriorityInHierarchy);
        }
        entries = new(StringComparer.OrdinalIgnoreCase);
        int repaired = 0;
        foreach (var pair in bodies)
        {
            var entry = Path.Combine(AddonsPath, EntryName(pair.Key));
            ValidateEntryPath(entry);
            var exists = File.Exists(entry) || new FileInfo(entry).LinkTarget is not null;
            if (exists && !FileSystemUtils.IsSameFile(entry, pair.Value)) throw new IOException("游戏入口被其他文件占用：" + entry);
            if (priorities.ContainsKey(pair.Key))
            {
                var target = new FileInfo(entry).LinkTarget;
                if (!exists || (target is not null) != UseSymbolicLinks
                    || target is not null && !FileSystemUtils.IsSamePath(new FileInfo(entry).ResolveLinkTarget(true)!.FullName, pair.Value))
                {
                    var temporary = entry + ".fireaxe-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        if (UseSymbolicLinks) File.CreateSymbolicLink(temporary, Path.GetRelativePath(AddonsPath, pair.Value));
                        else FileSystemUtils.CreateHardLink(temporary, pair.Value);
                        if (!FileSystemUtils.IsSameFile(temporary, pair.Value)) throw new IOException("文件入口校验失败：" + entry);
                        File.Move(temporary, entry, overwrite: true);
                        repaired++;
                    }
                    finally { if (File.Exists(temporary) || new FileInfo(temporary).LinkTarget is not null) File.Delete(temporary); }
                }
                entries[EntryName(pair.Key)] = "1";
            }
            else
            {
                if (exists) File.Delete(entry);
                entries[EntryName(pair.Key)] = "0";
            }
        }
        if (listPath is not null)
        {
            var entryPriorities = priorities.ToDictionary(pair => EntryName(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            entries = entries.OrderByDescending(pair => entryPriorities.GetValueOrDefault(pair.Key, int.MinValue))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            ModOrganizer.WriteEntries(listPath, entries);
            var readBack = ModOrganizer.ReadEntries(listPath);
            foreach (var pair in entries)
                if (readBack.GetValueOrDefault(pair.Key) != pair.Value) throw new IOException("游戏清单回读校验失败：" + pair.Key);
            _listPath = listPath;
            _listWriteTime = File.GetLastWriteTimeUtc(listPath);
        }
        return repaired;
    }

    private async Task<string> VerifyBody(string path, CancellationToken token, string? expectedHash = null)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("modebackup 本体不能是链接。");
        var info = new FileInfo(path);
        if (!_verified.TryGetValue(path, out var cached) || cached.Size != info.Length || cached.Time != info.LastWriteTimeUtc)
        {
            WorkshopBackup.ValidateVpk(path);
            cached = (info.Length, info.LastWriteTimeUtc, await WorkshopBackup.HashAsync(path, token));
            _verified[path] = cached;
        }
        expectedHash ??= ModOrganizer.IsContentName(path) ? Path.GetFileNameWithoutExtension(path) : null;
        if (expectedHash is not null && !expectedHash.Equals(cached.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("本体 SHA-256 与记录不匹配，未覆盖或删除。");
        return cached.Hash;
    }

    private IEnumerable<string> ScanIncoming(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).ToArray())
        {
            if (Directory.Exists(path))
            {
                if (Path.GetFileName(path) is "modebackup" or "firefox_temp" or AddonRoot.AddonRootDirectoryName) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in ScanIncoming(path)) yield return file;
            }
            else if (path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    private void AppendOriginal(string relative, string hash, string? body = null)
    {
        using var output = new FileStream(Path.Combine(BackupPath, "organizer-history.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { source = relative, hash, body, alias = EntryName(hash), time = DateTime.UtcNow }) + "\n");
        output.Write(bytes); output.Flush(true);
    }

    private bool RemoveDanglingBankLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null || info.ResolveLinkTarget(true)?.FullName is not { } target) return false;
        if (target.StartsWith(@"\\?\", StringComparison.Ordinal)) target = target[4..];
        if (!string.Equals(Path.GetDirectoryName(target), BackupPath, StringComparison.OrdinalIgnoreCase) || !ModOrganizer.IsContentName(target)) return false;
        try { File.GetAttributes(target); return false; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            AppendOriginal(Path.GetRelativePath(AddonsPath, path), Path.GetFileNameWithoutExtension(target));
            File.Delete(WorkshopBackup.Inside(AddonsPath, Path.GetRelativePath(AddonsPath, path)));
            return true;
        }
    }

    private void Snapshot(string? listPath)
    {
        var directory = Path.Combine(BackupPath, ".manager", "backups");
        WorkshopBackup.EnsureRealDirectory(directory);
        var prefix = Path.Combine(directory, "before-unified-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(prefix + ".addonroot", AddonRoot.Serialize(root.CreateSave()));
        if (listPath is not null && File.Exists(listPath)) File.Copy(listPath, prefix + "-addonlist.txt");
    }

    private void CleanupDirectories()
    {
        foreach (var path in Directory.EnumerateDirectories(AddonsPath).ToArray())
        {
            if (Path.GetFileName(path) is "modebackup" or "firefox_temp") continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("发现目录链接，未移动：" + path);
            if (Directory.EnumerateFiles(path, "*.vpk", SearchOption.AllDirectories).Any()) continue;
            if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            else
            {
                // Preserve artwork and old metadata, outside the game scan path.
                var archive = Path.Combine(BackupPath, ".manager", "legacy");
                WorkshopBackup.EnsureRealDirectory(archive);
                var destination = WorkshopBackup.Inside(archive, Path.GetFileName(path) + "-" + Guid.NewGuid().ToString("N"));
                Directory.Move(WorkshopBackup.Inside(AddonsPath, Path.GetFileName(path)), destination);
            }
        }
    }

    public async Task CommitDownloadAsync(VpkAddon node, string file, CancellationToken token)
    {
        await _operation.WaitAsync(token);
        try
        {
            WorkshopBackup.ValidateVpk(file);
            var hash = await WorkshopBackup.HashAsync(file, token);
            token.ThrowIfCancellationRequested();
            node.ThrowIfInvalid();
            WorkshopBackup.EnsureRealDirectory(BackupPath);
            var knownBodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ModBackupIndex.ReadOriginalPaths(AddonsPath, [], knownBodies);
            ReserveBodyIds(knownBodies.Keys);
            var body = _bodies.GetValueOrDefault(hash)
                ?? knownBodies.Where(pair => pair.Value == hash).Select(pair => Path.Combine(BackupPath, pair.Key)).FirstOrDefault(File.Exists)
                ?? Path.Combine(BackupPath, hash + ".vpk");
            if (File.Exists(body)) { await VerifyBody(body, token, hash); token.ThrowIfCancellationRequested(); node.ThrowIfInvalid(); File.Delete(file); }
            else { token.ThrowIfCancellationRequested(); node.ThrowIfInvalid(); body = AllocateBodyPath(); File.Move(file, body); }
            _bodies[hash] = body;
            if (node.ContentHash is { } old && old != hash) IgnoredHashes.Add(old);
            IgnoredHashes.Remove(hash);
            AppendOriginal(Path.GetRelativePath(AddonsPath, file), hash, Path.GetFileName(body));
            node.ContentHash = hash;
            node.StorageFileName = IsBodyName(Path.GetFileName(body)) ? Path.GetFileName(body) : null;
            node.OriginalPaths = node.OriginalPaths.Append(Path.GetRelativePath(AddonsPath, file)).Distinct().ToArray();
            root.Save();
        }
        finally { _operation.Release(); }
        await RefreshAsync(token);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _retry?.Dispose();
    }

    public async Task StopAsync()
    {
        Dispose();
        await _operation.WaitAsync();
        _operation.Release();
    }
}
