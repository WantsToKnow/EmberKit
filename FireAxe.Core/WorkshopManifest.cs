using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FireAxe;

public sealed record WorkshopListEntry(ulong Id, string Title, bool Enabled = true, int Priority = 0, WeaponAnimationRecipe? AnimationEdits = null)
{
    public string VariantKey => Id.ToString(CultureInfo.InvariantCulture) + ":" + AnimationEdits?.Serialize();
}
public sealed record WorkshopListParseResult(WorkshopListEntry[] Entries, int InvalidLines, int DuplicateLines);
public sealed record WorkshopListSkip(string Name, string Reason);
public sealed record WorkshopExportResult(WorkshopListEntry[] Entries, WorkshopListSkip[] Skipped);
public sealed record WorkshopDownloadPlan((WorkshopListEntry Entry, PublishedFileDetails File)[] Items, WorkshopListSkip[] Skipped);
public sealed record WorkshopQueueResult(WorkshopVpkAddon[] Added, int AlreadyPresent, int Retried = 0, int AnimationVariants = 0)
{
    public Task Completion { get; init; } = Task.CompletedTask;
}

public static class WorkshopManifest
{
    public const int MaxTextLength = 2 * 1024 * 1024;
    public const int MaxItems = 5000;
    private sealed record Candidate(string Name, string? Path, ulong? Id, bool Enabled, int Priority, WeaponAnimationRecipe? AnimationEdits);
    private static readonly Regex SearchLink = new(@"(?:sharedfiles|workshop)/filedetails/\?id=(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ContentHash = new(@"/ugc/\d+/([a-fA-F0-9]{40})/?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static WorkshopListParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextLength) throw new FormatException("清单超过 2 MiB，请分批导入。");
        var entries = new List<WorkshopListEntry>();
        var seen = new HashSet<string>();
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
            WeaponAnimationRecipe? recipe = null;
            var titleLength = parts.Length - 3;
            if (parts.Length > 4 && parts[^1].StartsWith("trim=", StringComparison.Ordinal))
            {
                recipe = WeaponAnimationRecipe.Parse(parts[^1][5..]);
                if (recipe is null || recipe.WorkshopId is { } origin && origin != id) { invalid++; continue; }
                recipe = recipe with { WorkshopId = id }; titleLength--;
            }
            var entry = new WorkshopListEntry(id, parts.Length > 3 ? string.Join(" ", parts.Skip(3).Take(titleLength)) : "工坊 " + id, enabled, priority, recipe);
            if (!seen.Add(entry.VariantKey)) { duplicates++; continue; }
            if (entries.Count >= MaxItems) throw new FormatException($"每份清单最多 {MaxItems} 个作品，请分批导入。");
            entries.Add(entry);
        }
        return new(entries.ToArray(), invalid, duplicates);
    }

    public static string Serialize(IEnumerable<WorkshopListEntry> entries, IEnumerable<WorkshopListSkip>? skipped = null)
    {
        var text = new StringBuilder("# FireAxe Workshop List v2\n# appid=550\n# 工坊链接<TAB>启用(1/0)<TAB>优先级<TAB>标题<TAB>trim=动画裁尾参数（可选，按记录顺序处理并保留原版）\n");
        foreach (var entry in entries.DistinctBy(entry => entry.VariantKey))
        {
            if (entry.Id == 0) throw new ArgumentException("工坊 ID 不能为 0。", nameof(entries));
            text.Append("https://steamcommunity.com/sharedfiles/filedetails/?id=").Append(entry.Id)
                .Append('\t').Append(entry.Enabled ? '1' : '0').Append('\t').Append(entry.Priority.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(Clean(entry.Title));
            if (entry.AnimationEdits is { } recipe)
            {
                if (!recipe.IsValid || recipe.WorkshopId is { } source && source != entry.Id) throw new ArgumentException("动画裁尾来源与工坊 ID 不一致。");
                text.Append("\ttrim=").Append((recipe with { WorkshopId = entry.Id }).Serialize());
            }
            text.Append('\n');
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
        if (addon.AnimationEdits is not null) return null; // A derived package is not the downloadable original.
        if (addon is WorkshopVpkAddon { PublishedFileId: > 0 } workshop) return workshop.PublishedFileId;
        if (ulong.TryParse(addon.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        if (!ModStorage.IsBankBodyPath(addon.VpkFilePath)
            && ulong.TryParse(Path.GetFileNameWithoutExtension(addon.VpkFilePath), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0) return id;
        var ids = addon.OriginalPaths.Where(path => !ModStorage.IsBankBodyPath(path))
            .Select(path => Regex.Match(Path.GetFileName(path), @"^(\d+)(?:_[a-z0-9]+)?\.vpk$", RegexOptions.IgnoreCase))
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
                return new Candidate(group.Key.Inspection?.Title ?? group.Key.Name, group.Key.VpkFilePath,
                    group.Key.AnimationEdits?.WorkshopId ?? KnownId(group.Key), enabled, priority, group.Key.AnimationEdits);
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
            if (candidate.AnimationEdits is not null && candidate.Id is null)
            {
                skipped.Add(new(candidate.Name, "本地改版没有可下载的原版工坊来源；裁尾参数已保存在 VPK 中，无法仅靠 TXT 分享原文件。"));
                continue;
            }
            if (candidate.Id is { } id && known.TryGetValue(id, out var result))
            {
                if (result.IsSucceeded && PublishedFileUtils.GetDownloadUri(result.Content) is not null)
                {
                    entries.Add(new(id, result.Content.Title, candidate.Enabled, candidate.Priority, candidate.AnimationEdits));
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
        return new(entries.GroupBy(entry => entry.VariantKey).Select(group => group.First() with { Enabled = group.Any(entry => entry.Enabled), Priority = group.Max(entry => entry.Priority) }).ToArray(), skipped.ToArray());
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
        var requested = entries.DistinctBy(entry => entry.VariantKey).ToArray();
        var details = (await PublishedFileUtils.GetPublishedFileDetailsBatchAsync(requested.Select(entry => entry.Id), client, cancellationToken).ConfigureAwait(false)).ToDictionary();
        var explicitEntries = requested.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var expanded = new List<WorkshopListEntry>();
        foreach (var entry in requested)
        {
            var result = details[entry.Id];
            // Steam's published-file response has no collection flag: collections usually point at a preview image.
            var children = result.IsSucceeded && PublishedFileUtils.GetDownloadUri(result.Content) is null
                ? await WorkshopCollectionUtils.GetWorkshopCollectionContentAsync(entry.Id, true, client, cancellationToken).ConfigureAwait(false)
                : null;
            if (children is null) expanded.Add(entry);
            else expanded.AddRange(children.Distinct().SelectMany(id => explicitEntries.GetValueOrDefault(id)
                ?? [new WorkshopListEntry(id, "工坊 " + id, entry.Enabled, entry.Priority, entry.AnimationEdits is null ? null : entry.AnimationEdits with { WorkshopId = id })]));
        }
        var expandedEntries = expanded.DistinctBy(entry => entry.VariantKey).ToArray();
        if (expandedEntries.Length > MaxItems) throw new FormatException($"合集展开后超过 {MaxItems} 个作品，请分批导入。");
        var missing = expandedEntries.Select(entry => entry.Id).Where(id => !details.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            foreach (var pair in await PublishedFileUtils.GetPublishedFileDetailsBatchAsync(missing, client, cancellationToken).ConfigureAwait(false))
                details[pair.Key] = pair.Value;
        var items = new List<(WorkshopListEntry, PublishedFileDetails)>();
        var skipped = new List<WorkshopListSkip>();
        foreach (var entry in expandedEntries)
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
        var scheduled = new List<(VpkAddon Original, WorkshopListEntry[] Variants, bool Download)>();
        int present = 0, retried = 0;
        using (root.BlockAutoCheck())
        {
            foreach (var group in plan.Items.GroupBy(item => item.Entry.Id))
            {
                var variants = group.Select(item => item.Entry).Where(entry => entry.AnimationEdits is not null).DistinctBy(entry => entry.VariantKey).ToArray();
                var entry = group.Select(item => item.Entry).FirstOrDefault(entry => entry.AnimationEdits is null)
                    ?? group.First().Entry with { Enabled = false, AnimationEdits = null };
                WorkshopVpkAddon addon;
                if (existing.TryGetValue(entry.Id, out var matches))
                {
                    if (matches.FirstOrDefault(node => node.VpkFilePath is { } path && File.Exists(path)) is { } installed)
                    {
                        present++;
                        if (variants.Length > 0) scheduled.Add((installed, variants, false));
                        continue;
                    }
                    var retry = matches.OfType<WorkshopVpkAddon>().FirstOrDefault();
                    if (matches.OfType<WorkshopVpkAddon>().FirstOrDefault(node => node.DownloadCheckTask is { IsCompleted: false }) is { } downloading)
                    {
                        present++;
                        if (variants.Length > 0) scheduled.Add((downloading, variants, false));
                        continue;
                    }
                    if (retry is null)
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
                scheduled.Add((addon, variants, true));
            }
        }
        var work = scheduled.Select(item => ProcessQueuedAsync(item.Original, item.Variants,
            item.Download ? ((WorkshopVpkAddon)item.Original).CheckDownloadAsync(ignoreWorkshopHash)
                : (item.Original as WorkshopVpkAddon)?.DownloadCheckTask ?? Task.CompletedTask, ignoreWorkshopHash)).ToArray();
        return new(added.ToArray(), present, retried, scheduled.Sum(item => item.Variants.Length)) { Completion = Task.WhenAll(work) };
    }

    private static async Task ProcessQueuedAsync(VpkAddon original, WorkshopListEntry[] variants, Task download, bool ignoreWorkshopHash)
    {
        await download;
        if (variants.Length == 0 || !original.IsValid || original.VpkFilePath is not { } source || !File.Exists(source)) return;
        var root = original.Root;
        var errors = new List<string>();
        var created = 0; var present = 0;
        foreach (var entry in variants)
        {
            string? output = null;
            try
            {
                original.ThrowIfInvalid();
                var recipe = entry.AnimationEdits!;
                if (root.GetDescendants().OfType<VpkAddon>().Any(node => node.AnimationEdits?.Serialize() == recipe.Serialize())) { present++; continue; }
                var analysis = await ReloadAnimationEditor.AnalyzeAsync(source, original.DestructionCancellationToken);
                if (!analysis.SourceHash.Equals(recipe.SourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("下载的原版与裁剪记录的版本不同，原版已保留，请用对应版本重新导出参数。");
                output = await ReloadAnimationEditor.ExportAsync(analysis, recipe.Steps, root.Storage.TemporaryPath,
                    original.DestructionCancellationToken, entry.Id);
                var imported = await root.Storage.ImportLocalVpkAsync([output], token: original.DestructionCancellationToken);
                var node = imported.Imported.FirstOrDefault() ?? imported.Existing.FirstOrDefault()
                    ?? throw new IOException(string.Join("\n", imported.Errors));
                if (imported.Imported.Length > 0)
                {
                    using var blocked = root.BlockAutoCheck();
                    node.IsEnabled = entry.Enabled; node.Priority = entry.Priority; node.CatalogTargetId = original.CatalogTargetId;
                    foreach (var dependency in original.DependentAddonIds) node.AddDependentAddon(dependency);
                    created++;
                }
                else present++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { errors.Add(entry.Title + "：" + ex.Message); }
            finally { if (output is not null && File.Exists(output)) File.Delete(output); }
        }
        if (!root.IsValid) return;
        try { root.Storage.CheckDeployment(); root.Save(); }
        catch (Exception ex) { errors.Add("入口检查：" + ex.Message); }
        if (!original.IsValid) return;
        root.DownloadHistory.Record(new(variants[0].Id, root.DownloadHistory.GetDisplayTitle(original),
            errors.Count == 0 ? WorkshopDownloadOutcome.Installed : WorkshopDownloadOutcome.Failed, original.StorageFileName,
            Error: errors.Count == 0 ? null : "原版已保留；裁尾处理未全部完成。\n" + string.Join("\n", errors), HashCheckSkipped: ignoreWorkshopHash,
            AnimationSummary: $"原版已保留；动画裁尾版新建 {created} 个，已有 {present} 个，失败 {errors.Count} 个。"));
    }

    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
