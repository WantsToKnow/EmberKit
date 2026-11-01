using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace FireAxe;

public static class ModBackupIndex
{
    public const string FileName = "mod-index.txt";

    internal static Dictionary<string, string> ReadOriginalPaths(string addons, List<string> warnings, Dictionary<string, string>? storedHashes = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var journal = Path.Combine(addons, ModOrganizer.BackupDirectory, "organizer-history.jsonl");
        if (new FileInfo(journal).LinkTarget is not null) throw new IOException("整理记录是文件链接，未执行恢复。");
        if (!File.Exists(journal)) return result;
        foreach (var line in File.ReadLines(journal))
        {
            try
            {
                using var record = JsonDocument.Parse(line);
                var source = record.RootElement.GetProperty("source").GetString();
                var hash = record.RootElement.GetProperty("hash").GetString();
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(hash) || !ModOrganizer.IsContentName(hash + ".vpk"))
                    throw new InvalidDataException("原路径或 Hash 不完整");
                result[WorkshopBackup.Inside(addons, source)] = hash;
                if (storedHashes is not null && record.RootElement.TryGetProperty("body", out var value) && value.GetString() is { } body && ModStorage.IsBodyName(body))
                {
                    if (storedHashes.TryGetValue(body, out var previous) && !previous.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("短编号对应不同 Hash：" + body);
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
        var entries = ModOrganizer.ReadEntries(GamePathUtils.GetAddonListPath(gamePath));
        var lines = new List<string> { "FireAxe Mod 备份对照表", "说明：m000001 等短编号用于辨认文件；完整 SHA-256 用于内容校验和去重，与编号独立保存。", "路径以游戏 addons 为基准。文件编号取自原 VPK 文件名，未联网核验工坊来源。", "本表自动记录短编号、完整 Hash、原始标题、管理器显示名称（含中文原名，如有）、作用和来源。", "游戏入口统一使用指向 modebackup 本体的相对符号链接，不额外复制 VPK；资源管理器通常显示 0 KB。", "此文件随本地同步自动更新；.addonroot 与 organizer-history.jsonl 保留短编号和 Hash 映射。", "" };
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
        foreach (var pair in bodies.OrderBy(pair => Path.GetFileName(pair.Value), StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var hash = pair.Key;
            var body = pair.Value;
            var node = nodes?.FirstOrDefault(node => node.ContentHash == hash);
            var paths = originals.Where(pair => pair.Value == hash).Select(pair => Path.GetRelativePath(addons, pair.Key)).Order().ToArray();
            var ids = paths.Select(path => Regex.Match(Path.GetFileName(path), @"^(\d+)(?:_[a-z0-9]+)?\.vpk$", RegexOptions.IgnoreCase))
                .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
            if (node is WorkshopVpkAddon { PublishedFileId: { } knownId }) ids = ids.Append(knownId.ToString()).Distinct().ToArray();
            var alias = ModStorage.EntryName(hash);
            string title = "无法读取标题", targets = "未识别 / 其他";
            long? length = null;
            try
            {
                if (new FileInfo(body).LinkTarget is not null) throw new IOException("备份本体是文件链接");
                length = FileSystemUtils.GetContentLength(body);
                using var package = new Package();
                package.Read(body);
                title = VpkUtils.GetAddonInfo(package)?.Title?.ReplaceLineEndings(" ") ?? "未提供标题";
                var matches = ModTargetClassifier.Classify(package.Entries!.Values.SelectMany(files => files).Select(file => file.GetFullPath()));
                if (matches.Length > 0) targets = string.Join("、", matches.Select(match => match.Target.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { title = "读取失败：" + ex.Message.ReplaceLineEndings(" "); }
            lines.AddRange(["========================================", "独立 ID：" + Path.GetFileNameWithoutExtension(body),
                "管理器显示名称：" + (node?.Name.ReplaceLineEndings(" ") ?? title), "Mod 名称 / 原始标题：" + title, "替换对象 / 作用：" + targets,
                "文件编号 / 工坊 ID：" + (ids.Length == 0 ? "未记录" : string.Join("、", ids)),
                "本体大小：" + (length.HasValue ? (length.Value / 1024d / 1024d).ToString("F2") + " MiB" : "无法读取"),
                "SHA-256：" + hash, "备份本体：" + Path.GetRelativePath(addons, body), "游戏加载入口：" + alias,
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
