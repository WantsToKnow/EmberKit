using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FireAxe;

public sealed record WorkshopListEntry(ulong Id, string Title, bool Enabled = true, int Priority = 0);
public sealed record WorkshopListParseResult(WorkshopListEntry[] Entries, int InvalidLines, int DuplicateLines);
public sealed record WorkshopListSkip(string Name, string Reason);
public sealed record WorkshopExportResult(WorkshopListEntry[] Entries, WorkshopListSkip[] Skipped);
public sealed record WorkshopDownloadPlan((WorkshopListEntry Entry, PublishedFileDetails File)[] Items, WorkshopListSkip[] Skipped);
public sealed record WorkshopQueueResult(WorkshopVpkAddon[] Added, int AlreadyPresent, int Retried = 0);

public static class WorkshopManifest
{
    public const int MaxTextLength = 2 * 1024 * 1024;
    public const int MaxItems = 5000;
    private sealed record Candidate(string Name, string? Path, ulong? Id, bool Enabled, int Priority);
    private static readonly Regex SearchLink = new(@"(?:sharedfiles|workshop)/filedetails/\?id=(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ContentHash = new(@"/ugc/\d+/([a-fA-F0-9]{40})/?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static WorkshopListParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextLength) throw new FormatException("清单超过 2 MiB，请分批导入。");
        var entries = new List<WorkshopListEntry>();
        var seen = new HashSet<ulong>();
        int invalid = 0, duplicates = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("# appid=", StringComparison.OrdinalIgnoreCase) && line != "# appid=550")
                    throw new FormatException("这份清单不属于求生之路 2（AppID 550）。");
                continue;
            }
            var parts = line.Split('\t');
            bool enabled = true;
            int priority = 0;
            if (!PublishedFileUtils.TryParsePublishedFileId(parts[0], out var id) ||
                parts.Length > 1 && parts[1] is not ("0" or "1") ||
                parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority))
            {
                invalid++;
                continue;
            }
            if (parts.Length > 1) enabled = parts[1] == "1";
            if (!seen.Add(id)) { duplicates++; continue; }
            if (entries.Count >= MaxItems) throw new FormatException($"每份清单最多 {MaxItems} 个作品，请分批导入。");
            entries.Add(new(id, parts.Length > 3 ? string.Join(" ", parts.Skip(3)) : "工坊 " + id, enabled, priority));
        }
        return new(entries.ToArray(), invalid, duplicates);
    }

    public static string Serialize(IEnumerable<WorkshopListEntry> entries, IEnumerable<WorkshopListSkip>? skipped = null)
    {
        var text = new StringBuilder("# FireAxe Workshop List v1\n# appid=550\n# 工坊链接<TAB>启用(1/0)<TAB>优先级<TAB>标题\n");
        foreach (var entry in entries.DistinctBy(entry => entry.Id))
        {
            if (entry.Id == 0) throw new ArgumentException("工坊 ID 不能为 0。", nameof(entries));
            text.Append("https://steamcommunity.com/sharedfiles/filedetails/?id=").Append(entry.Id)
                .Append('\t').Append(entry.Enabled ? '1' : '0').Append('\t').Append(entry.Priority.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(Clean(entry.Title)).Append('\n');
        }
        foreach (var item in skipped ?? []) text.Append("# 已跳过：").Append(Clean(item.Name)).Append(" — ").Append(Clean(item.Reason)).Append('\n');
        return text.ToString();
    }

    public static VpkAddon? ActualVpk(AddonNode node) => node.IsValid ? node switch
    {
        VpkAddon vpk => vpk,
        RefAddonNode reference => reference.ActualSourceAddon as VpkAddon,
        _ => null
    } : null;

    public static ulong? KnownId(VpkAddon addon)
    {
        if (addon is WorkshopVpkAddon { PublishedFileId: > 0 } workshop) return workshop.PublishedFileId;
        if (ulong.TryParse(addon.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        if (ulong.TryParse(Path.GetFileNameWithoutExtension(addon.VpkFilePath), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0) return id;
        var ids = addon.OriginalPaths.Select(path => Regex.Match(Path.GetFileName(path), @"^(\d+)(?:_[a-z0-9]+)?\.vpk$", RegexOptions.IgnoreCase))
            .Where(match => match.Success && ulong.TryParse(match.Groups[1].Value, out _)).Select(match => ulong.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Where(value => value > 0).Distinct().ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    public static async Task<WorkshopExportResult> ExportAsync(IEnumerable<AddonNode> nodes, HttpClient client, bool enabledOnly,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var candidates = nodes.Where(node => node.IsValid).Select(node => (Node: node, Vpk: ActualVpk(node))).Where(pair => pair.Vpk is not null)
            .GroupBy(pair => pair.Vpk!).Select(group =>
            {
                var enabled = group.Any(pair => pair.Node.IsEnabledInHierarchy);
                var priority = group.Where(pair => pair.Node.IsEnabledInHierarchy).Select(pair => pair.Node.PriorityInHierarchy).DefaultIfEmpty(group.Key.PriorityInHierarchy).Max();
                return new Candidate(group.Key.Inspection?.Title ?? group.Key.Name, group.Key.VpkFilePath, KnownId(group.Key), enabled, priority);
            }).Where(candidate => !enabledOnly || candidate.Enabled).ToArray();
        if (candidates.Length > MaxItems) throw new InvalidOperationException($"模组超过 {MaxItems} 个，请分批导出。");
        progress?.Report($"正在验证 {candidates.Length} 个模组的工坊来源…");
        var known = await PublishedFileUtils.GetPublishedFileDetailsBatchAsync(candidates.Where(candidate => candidate.Id.HasValue).Select(candidate => candidate.Id!.Value), client, cancellationToken).ConfigureAwait(false);
        var entries = new List<WorkshopListEntry>();
        var skipped = new List<WorkshopListSkip>();
        var searches = new Dictionary<string, PublishedFileDetails[]>(StringComparer.OrdinalIgnoreCase);
        bool searchUnavailable = false;
        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            progress?.Report($"正在匹配 {index + 1}/{candidates.Length}：{candidate.Name}");
            if (candidate.Id is { } id && known.TryGetValue(id, out var result))
            {
                if (result.IsSucceeded && PublishedFileUtils.GetDownloadUri(result.Content) is not null)
                {
                    entries.Add(new(id, result.Content.Title, candidate.Enabled, candidate.Priority));
                    continue;
                }
                if (result.Status == GetPublishedFileDetailsResultStatus.Failed)
                {
                    skipped.Add(new(candidate.Name, "Steam 验证失败，请检查网络或代理后重试"));
                    continue;
                }
            }
            if (candidate.Path is null || !File.Exists(candidate.Path))
            {
                skipped.Add(new(candidate.Name, "本地文件缺失，且没有可用的工坊来源"));
                continue;
            }
            if (searchUnavailable)
            {
                skipped.Add(new(candidate.Name, "Steam 搜索暂不可访问，无法确认来源"));
                continue;
            }
            try
            {
                if (!searches.TryGetValue(candidate.Name, out var found))
                {
                    found = await SearchByTitleAsync(candidate.Name, client, cancellationToken).ConfigureAwait(false);
                    searches[candidate.Name] = found;
                }
                await using var stream = File.OpenRead(candidate.Path);
                var length = stream.Length;
                var possible = found.Where(file => file.FileSize == length && PublishedFileUtils.GetDownloadUri(file) is not null && ContentHash.IsMatch(file.FileUrl)).ToArray();
                PublishedFileDetails? match = null;
                if (possible.Length > 0)
                {
                    var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    match = possible.FirstOrDefault(file => ContentHash.Match(file.FileUrl).Groups[1].Value.Equals(sha1, StringComparison.OrdinalIgnoreCase));
                }
                if (match is null) skipped.Add(new(candidate.Name, "未找到内容一致的工坊作品，先跳过"));
                else entries.Add(new(match.PublishedFileId, match.Title, candidate.Enabled, candidate.Priority));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { searchUnavailable = true; skipped.Add(new(candidate.Name, "Steam 搜索访问失败，无法确认来源")); }
            catch (OperationCanceledException) { searchUnavailable = true; skipped.Add(new(candidate.Name, "Steam 搜索超时，无法确认来源")); }
            catch (IOException) { skipped.Add(new(candidate.Name, "无法读取本地文件进行核对")); }
        }
        return new(entries.GroupBy(entry => entry.Id).Select(group => group.First() with { Enabled = group.Any(entry => entry.Enabled), Priority = group.Max(entry => entry.Priority) }).ToArray(), skipped.ToArray());
    }

    private static async Task<PublishedFileDetails[]> SearchByTitleAsync(string title, HttpClient client, CancellationToken cancellationToken)
    {
        // ponytail: inspect the first 24 search results; add pagination if renamed archives commonly miss a match.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var uri = "https://steamcommunity.com/workshop/browse/?appid=550&browsesort=textsearch&searchtext=" + Uri.EscapeDataString(title);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 FireAxe/0.7.3");
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var ids = SearchLink.Matches(html).Select(match => ulong.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Take(24).ToArray();
        var details = await PublishedFileUtils.GetPublishedFileDetailsBatchAsync(ids, client, cancellationToken).ConfigureAwait(false);
        return details.Values.Where(result => result.IsSucceeded).Select(result => result.Content).ToArray();
    }

    public static async Task<WorkshopDownloadPlan> ResolveDownloadsAsync(IEnumerable<WorkshopListEntry> entries, HttpClient client, CancellationToken cancellationToken)
    {
        var requested = entries.DistinctBy(entry => entry.Id).ToArray();
        var details = await PublishedFileUtils.GetPublishedFileDetailsBatchAsync(requested.Select(entry => entry.Id), client, cancellationToken).ConfigureAwait(false);
        var items = new List<(WorkshopListEntry, PublishedFileDetails)>();
        var skipped = new List<WorkshopListSkip>();
        foreach (var entry in requested)
        {
            var result = details[entry.Id];
            if (result.IsSucceeded && PublishedFileUtils.GetDownloadUri(result.Content) is not null) items.Add((entry, result.Content));
            else skipped.Add(new(entry.Title, result.Status == GetPublishedFileDetailsResultStatus.Failed ? "Steam 验证失败，请检查网络或代理" : "作品不可用、不属于求生之路 2，或没有可直接下载的 VPK"));
        }
        return new(items.ToArray(), skipped.ToArray());
    }

    public static WorkshopQueueResult QueueDownloads(AddonRoot root, WorkshopDownloadPlan plan, bool ignoreWorkshopHash = true)
    {
        root.ThrowIfInvalid();
        var existing = root.GetDescendants().OfType<VpkAddon>().Where(addon => addon.IsValid)
            .Where(addon => KnownId(addon).HasValue).GroupBy(addon => KnownId(addon)!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var added = new List<WorkshopVpkAddon>();
        var scheduled = new List<WorkshopVpkAddon>();
        var seen = new HashSet<ulong>();
        int present = 0, retried = 0;
        using (root.BlockAutoCheck())
        {
            foreach (var (entry, _) in plan.Items)
            {
                if (!seen.Add(entry.Id)) continue;
                WorkshopVpkAddon addon;
                if (existing.TryGetValue(entry.Id, out var matches))
                {
                    var retry = matches.OfType<WorkshopVpkAddon>().FirstOrDefault();
                    if (retry is null || matches.Any(node => node.VpkFilePath is { } path && File.Exists(path))
                        || matches.OfType<WorkshopVpkAddon>().Any(node => node.DownloadCheckTask is { IsCompleted: false }))
                    { present++; continue; }
                    addon = retry;
                    retried++;
                }
                else
                {
                    addon = AddonNode.Create<WorkshopVpkAddon>(root);
                    addon.Name = root.GetUniqueChildName(entry.Id.ToString(CultureInfo.InvariantCulture));
                    addon.PublishedFileId = entry.Id;
                    added.Add(addon);
                }
                addon.IsEnabled = entry.Enabled;
                addon.Priority = entry.Priority;
                scheduled.Add(addon);
            }
        }
        foreach (var addon in scheduled) _ = addon.CheckDownloadAsync(ignoreWorkshopHash);
        return new(added.ToArray(), present, retried);
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
