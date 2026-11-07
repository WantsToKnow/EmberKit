using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace FireAxe;

public static class ModBackupIndex
{
    public const string FileName = "mod-index.txt";

    private static Dictionary<string, (string Hash, string? ReadableName)> ReadStoredBodies(string addons, List<string> warnings)
    {
        var result = new Dictionary<string, (string Hash, string? ReadableName)>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(addons, ModOrganizer.BackupDirectory, FileName);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("TXT 对照表是文件链接，未读取。");
        if (!File.Exists(path)) return result;
        string? hash = null, readableName = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("可读名称：", StringComparison.Ordinal)) readableName = line[5..].Trim();
            else if (line.StartsWith("SHA-256：", StringComparison.Ordinal))
            {
                var value = line[8..].Trim();
                hash = ModOrganizer.IsContentName(value + ".vpk") ? value.ToUpperInvariant() : null;
            }
            else if (line.StartsWith("备份本体：", StringComparison.Ordinal))
            {
                var relative = line[5..].Trim().Replace('/', Path.DirectorySeparatorChar);
                var name = Path.GetFileName(relative);
                if (hash is not null)
                {
                    if (Path.GetDirectoryName(relative) != ModOrganizer.BackupDirectory || !ModStorage.IsBodyName(name))
                        warnings.Add("TXT 中的本体文件名无效，已跳过：" + relative);
                    else if (result.TryGetValue(name, out var previous) && previous.Hash != hash)
                        warnings.Add("TXT 中同名本体对应不同 Hash，已保留首条记录：" + name);
                    else result[name] = (hash, string.IsNullOrWhiteSpace(readableName) ? previous.ReadableName : readableName);
                }
                hash = null;
                readableName = null;
            }
        }
        return result;
    }

    internal static string? PreviousReadableName(IEnumerable<string> paths) => Path.GetFileNameWithoutExtension(paths
        .Where(ModStorage.IsBankBodyPath).Select(Path.GetFileName).FirstOrDefault(name => name is not null && ModStorage.IsReadableBodyName(name)));

    private static IEnumerable<string> IdentityLines(string name, string readableName, string hash)
    {
        if (ModStorage.IsNumberedBodyName(name)) yield return "数字编号：" + Path.GetFileNameWithoutExtension(name);
        yield return "本体文件名：" + name;
        yield return "可读名称：" + readableName.ReplaceLineEndings(" ").Trim();
        yield return "SHA-256：" + hash;
        yield return "备份本体：" + Path.Combine(ModOrganizer.BackupDirectory, name);
    }

    internal static async Task RecordBodyAsync(string addons, string name, string hash, string displayName, CancellationToken token)
    {
        var recorded = ReadStoredBodies(addons, []).GetValueOrDefault(name);
        if (recorded.Hash == hash && recorded.ReadableName is not null) return;
        var backup = Path.Combine(addons, ModOrganizer.BackupDirectory);
        using var package = new Package();
        package.Read(Path.Combine(backup, name));
        var title = VpkUtils.GetAddonInfo(package)?.Title;
        var readableName = ModStorage.IsReadableBodyName(name) ? Path.GetFileNameWithoutExtension(name)
            : string.IsNullOrWhiteSpace(title) ? displayName : title;
        var path = Path.Combine(backup, FileName);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, "EmberKit Mod 数字编号—名称—Hash 对照表\n\n", new UTF8Encoding(false), token);
        await File.AppendAllLinesAsync(path, IdentityLines(name, readableName, hash).Append(""), new UTF8Encoding(false), token);
    }

    internal static Dictionary<string, string> ReadOriginalPaths(string addons, List<string> warnings, Dictionary<string, string>? storedHashes = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (storedHashes is not null)
            foreach (var pair in ReadStoredBodies(addons, warnings)) storedHashes[pair.Key] = pair.Value.Hash;
        var journal = Path.Combine(addons, ModOrganizer.BackupDirectory, "organizer-history.jsonl");
        if (new FileInfo(journal).LinkTarget is not null) throw new IOException("整理记录是文件链接，未执行恢复。");
        if (!File.Exists(journal)) return result;
        foreach (var line in File.ReadLines(journal))
        {
            try
            {
                using var record = JsonDocument.Parse(line);
                var source = record.RootElement.GetProperty("source").GetString();
                var hash = record.RootElement.GetProperty("hash").GetString()?.ToUpperInvariant();
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(hash) || !ModOrganizer.IsContentName(hash + ".vpk"))
                    throw new InvalidDataException("原路径或 Hash 不完整");
                result[WorkshopBackup.Inside(addons, source)] = hash;
                if (storedHashes is not null && record.RootElement.TryGetProperty("body", out var value) && value.GetString() is { } body && ModStorage.IsBodyName(body))
                {
                    if (storedHashes.TryGetValue(body, out var previous) && !previous.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("本体文件名对应不同 Hash：" + body);
                    storedHashes[body] = hash;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            { warnings.Add("已跳过一条无效整理记录：" + ex.Message); }
        }
        return result;
    }

    public static Task<string> WriteAsync(string gamePath, CancellationToken token = default) => WriteAsync(gamePath, null, null, token);

    internal static async Task<string> WriteAsync(string gamePath, IReadOnlyDictionary<string, string>? bodies, IEnumerable<VpkAddon>? nodes, CancellationToken token)
    {
        var addons = Path.GetFullPath(GamePathUtils.GetAddonsPath(gamePath));
        var backup = Path.Combine(addons, ModOrganizer.BackupDirectory);
        WorkshopBackup.EnsureRealDirectory(backup);
        var warnings = new List<string>();
        var originals = ReadOriginalPaths(addons, warnings);
        var recorded = ReadStoredBodies(addons, warnings);
        var entries = ModOrganizer.ReadEntries(GamePathUtils.GetAddonListPath(gamePath));
        var lines = new List<string> { "EmberKit Mod 数字编号—名称—Hash 对照表", "按数字编号查找可读名称、完整 SHA-256 和备份本体位置。", "导入时按本表查找重复候选，再核验实际内容。路径以游戏 addons 为基准。", "游戏入口使用短名相对符号链接，资源管理器通常显示 0 KB。", "此文件随入库和手动应用更新。.addonroot 与 organizer-history.jsonl 保留恢复记录。", "" };
        if (bodies is null)
        {
            var recovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(backup, "*.vpk"))
            {
                if (new FileInfo(path).LinkTarget is not null) { warnings.Add("跳过链接本体：" + Path.GetFileName(path)); continue; }
                WorkshopBackup.ValidateVpk(path);
                recovered[await WorkshopBackup.HashAsync(path, token)] = path;
            }
            bodies = recovered;
        }
        foreach (var pair in bodies.OrderBy(pair => Path.GetFileName(pair.Value).Length).ThenBy(pair => Path.GetFileName(pair.Value), StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var hash = pair.Key;
            var body = pair.Value;
            var name = Path.GetFileName(body);
            var node = nodes?.FirstOrDefault(node => node.ContentHash == hash);
            var paths = originals.Where(pair => pair.Value == hash).Select(pair => Path.GetRelativePath(addons, pair.Key)).Order().ToArray();
            var ids = paths.Where(path => !ModStorage.IsBankBodyPath(path))
                .Select(path => Regex.Match(Path.GetFileName(path), @"^(\d+)(?:_[a-z0-9]+)?\.vpk$", RegexOptions.IgnoreCase))
                .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
            if (node is WorkshopVpkAddon { PublishedFileId: { } knownId }) ids = ids.Append(knownId.ToString()).Distinct().ToArray();
            var alias = ModStorage.EntryName(hash);
            string? title = null;
            var targets = "未识别 / 其他";
            long? length = null;
            try
            {
                if (new FileInfo(body).LinkTarget is not null) throw new IOException("备份本体是文件链接");
                length = FileSystemUtils.GetContentLength(body);
                using var package = new Package();
                package.Read(body);
                title = VpkUtils.GetAddonInfo(package)?.Title?.ReplaceLineEndings(" ");
                var matches = ModTargetClassifier.Classify(package.Entries!.Values.SelectMany(files => files).Select(file => file.GetFullPath()));
                if (matches.Length > 0) targets = string.Join("、", matches.Select(match => match.Target.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { title = "读取失败：" + ex.Message.ReplaceLineEndings(" "); }
            var existing = recorded.GetValueOrDefault(name);
            var readableName = (existing.Hash == hash ? existing.ReadableName : null) ?? PreviousReadableName(paths)
                ?? (ModStorage.IsReadableBodyName(name) ? Path.GetFileNameWithoutExtension(name) : null)
                ?? (string.IsNullOrWhiteSpace(title) ? node?.Name ?? name : title);
            lines.Add("========================================");
            lines.AddRange(IdentityLines(name, readableName, hash));
            lines.AddRange(["管理器显示名称：" + (node?.Name.ReplaceLineEndings(" ") ?? readableName),
                "Mod 名称 / 原始标题：" + (title ?? "未提供标题"), "替换对象 / 作用：" + targets,
                "文件编号 / 工坊 ID：" + (ids.Length == 0 ? "未记录" : string.Join("、", ids)),
                "本体大小：" + (length.HasValue ? (length.Value / 1024d / 1024d).ToString("F2") + " MiB" : "无法读取"),
                "游戏加载入口：" + alias,
                "游戏清单状态：" + (entries.GetValueOrDefault(alias) == "1" ? "启用" : "禁用 / 未写入清单"),
                "入口类型：" + (new FileInfo(Path.Combine(addons, alias)).LinkTarget is not null ? "符号链接" : File.Exists(Path.Combine(addons, alias)) ? "硬链接 / 文件入口" : "未启用，入口不存在"),
                "加载入口存在：" + (File.Exists(Path.Combine(addons, alias)) ? "是" : "否"), "原路径："]);
            lines.AddRange(paths.Length == 0 ? ["  未记录；可按上面的 Mod 名称和替换对象辨认。"] : paths.Select(path => "  " + path));
            lines.Add("");
        }
        lines.AddRange(warnings);
        var output = Path.Combine(backup, FileName);
        if (new FileInfo(output).LinkTarget is not null) throw new IOException("TXT 对照表是文件链接，未覆盖。");
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(false), token);
            File.Move(temporary, output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return output;
    }
}
