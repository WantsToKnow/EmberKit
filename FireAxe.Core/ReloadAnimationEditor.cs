using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace FireAxe;

public sealed record ReloadAnimationInfo(string Model, int Sequence, int Animation, string Name, int Frames, double Fps, double[] EventFrames, string? Unsupported, WeaponAnimationKind Kind = WeaponAnimationKind.Reload)
{
    public double Seconds => Frames > 1 && Fps > 0 ? (Frames - 1) / Fps : 0;
}
public sealed record ReloadAnimationAnalysis(string SourcePath, string SourceHash, string Title, IReadOnlyList<ReloadAnimationInfo> Clips, WeaponAnimationRecipe? Recipe = null);
public sealed record ReloadAnimationCut(ReloadAnimationInfo Clip, int NewFrames, double ActualCutSeconds, int RemovedEvents)
{
    public double NewSeconds => (NewFrames - 1) / Clip.Fps;
}

public static class ReloadAnimationEditor
{
    private static bool ViewModel(string path) => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("models/v_models/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("models/weapons/v_", StringComparison.OrdinalIgnoreCase));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public static Task<ReloadAnimationAnalysis> AnalyzeAsync(string sourcePath, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(sourcePath);
        var hash = Hash(path);
        using var package = new Package(); package.Read(path);
        var entries = package.Entries ?? throw new InvalidDataException("VPK 没有文件目录。");
        var clips = new List<ReloadAnimationInfo>();
        foreach (var entry in entries.Values.SelectMany(entries => entries))
        {
            token.ThrowIfCancellationRequested();
            var modelPath = entry.GetFullPath().Replace('\\', '/');
            if (!ViewModel(modelPath)) continue;
            package.ReadEntry(entry, out byte[] modelBytes);
            foreach (var clip in new StudioReloadModel(modelBytes).Inspect())
                clips.Add(new(modelPath, clip.Sequence, clip.Animation, clip.Name, clip.Frames, clip.Fps, clip.EventFrames, clip.Unsupported, clip.Kind));
        }
        if (clips.Count == 0) throw new NotSupportedException("未找到可识别的第一人称换弹 / 切枪动画。");
        if (Hash(path) != hash) throw new IOException("读取期间源文件发生变化，请重新读取。");
        return new ReloadAnimationAnalysis(path, hash, VpkUtils.GetAddonInfo(package)?.Title ?? Path.GetFileNameWithoutExtension(path), clips, WeaponAnimationRecipe.Read(package));
    }, token);

    public static IReadOnlyList<ReloadAnimationCut> Preview(ReloadAnimationAnalysis analysis, double seconds, WeaponAnimationKind kind = WeaponAnimationKind.Reload)
        => Preview(analysis, new AnimationTrimStep(kind, seconds));

    public static IReadOnlyList<ReloadAnimationCut> Preview(ReloadAnimationAnalysis analysis, AnimationTrimStep step)
    {
        var seconds = step.Seconds;
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("裁尾秒数必须大于 0。");
        var changes = new List<ReloadAnimationCut>();
        foreach (var clip in analysis.Clips.Where(clip => clip.Kind == step.Kind && (step.Model is null
            || clip.Model.Equals(step.Model, StringComparison.OrdinalIgnoreCase) && clip.Animation == step.Animation)))
        {
            if (clip.Unsupported is { } reason) throw new NotSupportedException(reason);
            var amount = Math.Round(seconds * clip.Fps, MidpointRounding.AwayFromZero);
            if (amount > clip.Frames - 2) throw new ArgumentException($"「{clip.Name}」仅 {clip.Seconds:0.###} 秒，裁尾后需保留至少两帧。");
            var newFrames = clip.Frames - (int)amount;
            changes.Add(new(clip, newFrames, amount / clip.Fps,
                clip.EventFrames.Count(frame => frame > newFrames - 1 + 0.00001)));
        }
        if (changes.Count == 0) throw new NotSupportedException(step.Kind == WeaponAnimationKind.Draw ? "未找到对应的切枪动画。" : "未找到对应的换弹动画。");
        if (changes.All(change => change.NewFrames == change.Clip.Frames)) throw new ArgumentException("裁尾值不足一帧，请输入稍大的秒数。");
        return changes;
    }

    public static Task<string> ExportAsync(ReloadAnimationAnalysis analysis, double seconds, string outputDirectory, CancellationToken token = default)
        => ExportAsync(analysis, [new(WeaponAnimationKind.Reload, seconds)], outputDirectory, token);

    public static Task<string> ExportAsync(ReloadAnimationAnalysis analysis, IReadOnlyList<AnimationTrimStep> steps, string outputDirectory,
        CancellationToken token = default, ulong? sourceWorkshopId = null) => Task.Run(() =>
    {
        var recipe = new WeaponAnimationRecipe(analysis.Recipe?.WorkshopId ?? sourceWorkshopId, analysis.Recipe?.SourceHash ?? analysis.SourceHash,
            [.. analysis.Recipe?.Steps ?? [], .. steps]);
        if (steps.Count == 0 || !recipe.IsValid) throw new ArgumentException("动画裁剪参数无效或处理次数过多。");
        var working = analysis;
        var operations = new List<IReadOnlyList<ReloadAnimationCut>>();
        foreach (var step in steps)
        {
            var changes = Preview(working, step); operations.Add(changes);
            working = working with { Clips = working.Clips.Select(clip =>
            {
                var change = changes.FirstOrDefault(change => change.Clip.Model.Equals(clip.Model, StringComparison.OrdinalIgnoreCase) && change.Clip.Animation == clip.Animation);
                return change is null ? clip : clip with { Frames = change.NewFrames, EventFrames = clip.EventFrames.Where(frame => frame <= change.NewFrames - 1 + 0.00001).ToArray() };
            }).ToArray() };
        }
        if (Hash(analysis.SourcePath) != analysis.SourceHash) throw new IOException("源文件已改变，请重新读取动画。");
        Directory.CreateDirectory(outputDirectory);
        const string priorSuffix = @"(?:\s*\[(?:Reload cut [0-9.]+s|Draw cut [0-9.]+s|Animation trim)\])+$";
        var title = Regex.Replace(Regex.Replace(analysis.Title, priorSuffix, "", RegexOptions.IgnoreCase), @"[^\p{L}\p{N}_ -]", "_").Trim();
        if (title.Length > 72) title = title[..72];
        if (title.Length == 0) title = "Mod";
        var uniform = recipe.Steps.All(step => step.Model is null && step.Kind == recipe.Steps[0].Kind);
        var suffix = uniform ? (recipe.Steps[0].Kind == WeaponAnimationKind.Reload ? "Reload cut " : "Draw cut ")
            + recipe.Steps.Sum(step => step.Seconds).ToString("0.###", CultureInfo.InvariantCulture) + "s" : "Animation trim";
        var destination = Path.Combine(outputDirectory, title + "_" + suffix.Replace(' ', '_') + "_" + Guid.NewGuid().ToString("N")[..8] + ".vpk");
        var temporary = destination + ".tmp.vpk";
        try
        {
            using var source = new Package(); source.Read(analysis.SourcePath);
            var entries = source.Entries ?? throw new InvalidDataException("VPK 没有文件目录。");
            using var result = new Package();
            foreach (var entry in entries.Values.SelectMany(entries => entries))
            {
                token.ThrowIfCancellationRequested();
                var path = entry.GetFullPath().Replace('\\', '/');
                if (path.Equals(WeaponAnimationRecipe.EntryPath, StringComparison.OrdinalIgnoreCase)) continue;
                source.ReadEntry(entry, out byte[] bytes);
                foreach (var operation in operations)
                {
                    var edits = operation.Where(change => change.Clip.Model.Equals(path, StringComparison.OrdinalIgnoreCase)
                        && change.NewFrames != change.Clip.Frames).ToArray();
                    if (edits.Length == 0) continue;
                    var model = new StudioReloadModel(bytes);
                    bytes = model.Trim(model.Inspect(), edits.GroupBy(change => change.Clip.Animation)
                        .ToDictionary(group => group.Key, group => group.First().NewFrames), token);
                }
                if (path.Equals("addoninfo.txt", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
                    var text = reader.ReadToEnd();
                    var pattern = "(\\\"?addontitle\\\"?\\s+\\\")((?:\\\\.|[^\\\"])*)\\\"";
                    var replaced = false;
                    text = Regex.Replace(text, pattern, match =>
                    {
                        if (replaced) return match.Value;
                        replaced = true;
                        return match.Groups[1].Value + Regex.Replace(match.Groups[2].Value, priorSuffix, "", RegexOptions.IgnoreCase) + " [" + suffix + "]\"";
                    }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    bytes = Encoding.UTF8.GetBytes(text);
                }
                result.AddFile(path, bytes);
            }
            result.AddFile(WeaponAnimationRecipe.EntryPath, Encoding.UTF8.GetBytes(recipe.Serialize()));
            token.ThrowIfCancellationRequested(); WriteVersion1(result, temporary, token);
            using (var check = new Package())
            {
                check.Read(temporary);
                if (check.Version != 1 || check.HeaderSize != 12)
                    throw new IOException("生成的 VPK 不是求生之路 2 支持的版本 1，未发布副本。");
                check.VerifyFileChecksums(null);
                var names = check.Entries?.Values.SelectMany(items => items).Select(entry => entry.GetFullPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (names is null || !names.SetEquals(entries.Values.SelectMany(items => items).Select(entry => entry.GetFullPath()).Append(WeaponAnimationRecipe.EntryPath)))
                    throw new IOException("生成的 VPK 目录校验失败。");
            }
            if (Hash(analysis.SourcePath) != analysis.SourceHash) throw new IOException("源文件在生成期间改变，未发布副本。");
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    private static void WriteVersion1(Package package, string path, CancellationToken token)
    {
        // ValvePak 4 writes VPK 2 only. VPK 1 keeps the same tree/data offsets relative
        // to their section, but has a 12-byte header and no VPK 2 checksum sections.
        package.Write(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[28]; stream.ReadExactly(header);
        var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))
            + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x55AA1234
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 2 || size > stream.Length - 28)
            throw new IOException("VPK 打包布局异常，未发布副本。");
        var buffer = new byte[81920];
        for (long offset = 0; offset < size;)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, size - offset);
            stream.Position = 28 + offset; stream.ReadExactly(buffer.AsSpan(0, count));
            stream.Position = 12 + offset; stream.Write(buffer, 0, count);
            offset += count;
        }
        stream.SetLength(12 + size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        stream.Position = 0; stream.Write(header, 0, 12);
    }
}
