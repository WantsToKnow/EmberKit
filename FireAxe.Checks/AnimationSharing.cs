using System.Security.Cryptography;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;
using SteamDatabase.ValvePak;

internal static partial class Program
{
    private static void CheckAnimationSharing(string output)
    {
        var directory = Path.Combine(output, "anim-share-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "original.vpk"); Write(source, ReloadModelFixture(includeDraw: true), "Animation fixture");
        var bytes = File.ReadAllBytes(source);
        var analysis = Complete(ReloadAnimationEditor.AnalyzeAsync(source));
        Check(analysis.Clips.Count(clip => clip.Kind == WeaponAnimationKind.Draw) == 1 && analysis.Clips.Count(clip => clip.Kind == WeaponAnimationKind.Reload) == 2,
            "analysis separates draw animations from independent reload base and layer sequences");
        var firstSteps = new AnimationTrimStep[] { new(WeaponAnimationKind.Reload, 0.017), new(WeaponAnimationKind.Draw, 0.033) };
        var first = Complete(ReloadAnimationEditor.ExportAsync(analysis, firstSteps, Path.Combine(directory, "edited"), sourceWorkshopId: 42));
        var firstAnalysis = Complete(ReloadAnimationEditor.AnalyzeAsync(first));
        var twice = Complete(ReloadAnimationEditor.ExportAsync(firstAnalysis, [new(WeaponAnimationKind.Reload, 0.017)], Path.Combine(directory, "edited")));
        var result = Complete(ReloadAnimationEditor.AnalyzeAsync(twice));
        Check(result.Clips.Single(clip => clip.Name == "reload").Frames == 89
            && result.Clips.Single(clip => clip.Name == "reload_layer").Frames == 91 && result.Clips.Single(clip => clip.Kind == WeaponAnimationKind.Draw).Frames == 90,
            "two small reload cuts retain their individual frame rounding while draw trimming touches only draw");
        var recipe = result.Recipe!;
        Check(recipe.WorkshopId == 42 && recipe.SourceHash == Convert.ToHexString(SHA256.HashData(bytes))
            && recipe.Steps.Length == 3 && recipe.Steps[2].Kind == WeaponAnimationKind.Reload && recipe.Steps[2].Seconds == 0.017,
            "derived packages retain the original workshop source and the complete ordered editing recipe across multiple operations");
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "draw and reload editing leave the original archive unchanged");
        var placeholder = ReloadModelFixture(includeDraw: true);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(placeholder.AsSpan(256 + 16), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(placeholder.AsSpan(256 + 8), 0);
        var placeholderPath = Path.Combine(directory, "placeholder.vpk"); Write(placeholderPath, placeholder, "Placeholder");
        var placeholderAnalysis = Complete(ReloadAnimationEditor.AnalyzeAsync(placeholderPath));
        Check(placeholderAnalysis.Clips.Single(clip => clip.Name == "reload").Unsupported is not null
            && ReloadAnimationEditor.Preview(placeholderAnalysis, .1, WeaponAnimationKind.Draw).Count == 1,
            "a zero-frame placeholder does not hide independent, supported draw sequences");
        var blended = ReloadModelFixture(includeDraw: true);
        var blendOffset = blended.Length; Array.Resize(ref blended, blended.Length + 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blended.AsSpan(76), blended.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blended.AsSpan(2048 + 56), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blended.AsSpan(2048 + 60), blendOffset - 2048);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(blended.AsSpan(blendOffset), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(blended.AsSpan(blendOffset + 2), 1);
        var blendedPath = Path.Combine(directory, "blended.vpk"); Write(blendedPath, blended, "Blended");
        var blendedAnalysis = Complete(ReloadAnimationEditor.AnalyzeAsync(blendedPath));
        Check(blendedAnalysis.Clips.Where(clip => clip.Kind == WeaponAnimationKind.Reload).All(clip => clip.Unsupported is not null)
            && ReloadAnimationEditor.Preview(blendedAnalysis, .1, WeaponAnimationKind.Draw).Count == 1,
            "mixed sequences and animations shared with their layers are skipped without blocking independent draws");
        var shared = ReloadModelFixture(includeDraw: true);
        var drawSequence = 2048 + 2 * 212;
        var drawBlend = drawSequence + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(shared.AsSpan(drawSequence + 60));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(shared.AsSpan(drawBlend), 0);
        var sharedPath = Path.Combine(directory, "shared.vpk"); Write(sharedPath, shared, "Shared");
        Check(Complete(ReloadAnimationEditor.AnalyzeAsync(sharedPath)).Clips.Where(clip => clip.Animation == 0).All(clip => clip.Unsupported is not null),
            "one animation shared by reload and draw cannot be cut through either independent field");
        var single = Complete(ReloadAnimationEditor.ExportAsync(analysis,
            [new(WeaponAnimationKind.Draw, 0.1, "models/v_models/v_rifle.mdl", 2)], Path.Combine(directory, "edited"), sourceWorkshopId: 42));
        var exact = Complete(ReloadAnimationEditor.AnalyzeAsync(single));
        Check(exact.Clips.Single(clip => clip.Kind == WeaponAnimationKind.Draw).Frames == 88 && exact.Clips.Where(clip => clip.Kind == WeaponAnimationKind.Reload).All(clip => clip.Frames == 91),
            "a per-sequence recipe can reproduce automatic tail cuts without changing unrelated animation variants");

        var entries = new[] { new WorkshopListEntry(42, "Original", false, 2), new WorkshopListEntry(42, "Edited", true, 7, recipe), new WorkshopListEntry(42, "Draw edit", false, 3, exact.Recipe) };
        var serialized = WorkshopManifest.Serialize(entries);
        var parsed = WorkshopManifest.Parse(serialized);
        Check(parsed.Entries.Length == 3 && parsed.Entries[1].AnimationEdits!.Serialize() == recipe.Serialize()
            && parsed.Entries[2].AnimationEdits!.Steps.Single().Animation == 2 && parsed.InvalidLines == 0,
            "TXT preserves original and multiple derived variants of one workshop ID, including ordered draw and reload data");
        Check(WorkshopManifest.Parse(serialized + serialized).DuplicateLines == 3
            && WorkshopManifest.Parse("42\t1\t0\tBad\ttrim={}").InvalidLines == 1,
            "TXT deduplicates identical recipes while rejecting malformed animation parameters");

        var game = Path.Combine(directory, "game");
        var addons = Path.Combine(game, "left4dead2", "addons"); Directory.CreateDirectory(addons);
        File.WriteAllText(Path.Combine(game, "left4dead2.exe"), "fixture; never executed");
        using var client = new HttpClient(new SteamFixtureHandler(bytes));
        var downloads = new FixtureDownloads(bytes);
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, HttpClient = client,
            DownloadService = downloads, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        var plan = Complete(WorkshopManifest.ResolveDownloadsAsync(parsed.Entries, client, default));
        var queued = WorkshopManifest.QueueDownloads(root, plan);
        Complete(queued.Completion);
        var original = root.GetDescendants().OfType<WorkshopVpkAddon>().Single();
        var variants = root.GetDescendants().OfType<LocalVpkAddon>().Where(node => node.AnimationEdits is not null).ToArray();
        Check(downloads.DownloadCount == 1 && queued.AnimationVariants == 2 && variants.Length == 2
            && File.ReadAllBytes(original.VpkFilePath!).SequenceEqual(bytes) && !original.IsEnabled,
            "TXT import downloads the original once then creates both edited variants while retaining the original body");
        var imported = variants.Single(node => node.AnimationEdits!.Steps.Length == 3);
        Check(imported.IsEnabled && imported.Priority == 7 && !variants.Single(node => node != imported).IsEnabled
            && Complete(ReloadAnimationEditor.AnalyzeAsync(imported.VpkFilePath!)).Clips.Select(clip => clip.Frames).SequenceEqual(result.Clips.Select(clip => clip.Frames)),
            "import restores each variant's enable state and priority and reproduces sequential frame-accurate trimming");
        var exported = Complete(WorkshopManifest.ExportAsync(root.GetDescendants(), client, false, null, default));
        Check(exported.Entries.Length == 3 && exported.Entries.Count(entry => entry.AnimationEdits is not null) == 2 && exported.Skipped.Length == 0,
            "export resolves edited local packages back to their original workshop link without guessing by changed content hashes");
        imported.IsEnabled = false;
        var again = WorkshopManifest.QueueDownloads(root, plan); Complete(again.Completion);
        Check(downloads.DownloadCount == 1 && root.GetDescendants().OfType<VpkAddon>().Count() == 3 && !imported.IsEnabled,
            "reimporting existing recipes reuses both original and edited bodies and preserves the user's later switch choice");
        var bad = recipe with { SourceHash = new string('0', 64) };
        var badPlan = Complete(WorkshopManifest.ResolveDownloadsAsync([new(42, "Version mismatch", true, AnimationEdits: bad)], client, default));
        Complete(WorkshopManifest.QueueDownloads(root, badPlan).Completion);
        Check(File.Exists(original.VpkFilePath!) && root.DownloadHistory.GetResults().Single().Error?.Contains("原版与裁剪记录的版本不同") == true,
            "a changed source version reports a recipe failure while keeping the successfully downloaded original");

        var pendingGame = Path.Combine(directory, "pending-game");
        var pendingAddons = Path.Combine(pendingGame, "left4dead2", "addons"); Directory.CreateDirectory(pendingAddons);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingDownloads = new FixtureDownloads(bytes) { CompletionGate = gate.Task };
        var pendingRoot = new AddonRoot { DirectoryPath = pendingAddons, GamePath = pendingGame, HttpClient = client,
            DownloadService = pendingDownloads, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        var originalPlan = Complete(WorkshopManifest.ResolveDownloadsAsync([entries[0]], client, default));
        var originalQueue = WorkshopManifest.QueueDownloads(pendingRoot, originalPlan);
        var variantPlan = Complete(WorkshopManifest.ResolveDownloadsAsync([entries[1]], client, default));
        var variantQueue = WorkshopManifest.QueueDownloads(pendingRoot, variantPlan);
        gate.SetResult(); Complete(originalQueue.Completion); Complete(variantQueue.Completion);
        Check(pendingDownloads.DownloadCount == 1 && pendingRoot.GetDescendants().OfType<VpkAddon>().Count() == 2
            && pendingRoot.GetDescendants().OfType<VpkAddon>().Single(node => node.AnimationEdits is not null).IsEnabled,
            "importing a recipe while its original is still downloading waits for that download and creates the enabled variant");
        Complete(pendingRoot.DisposeAsync().AsTask());

        using var explorer = new AddonNodeExplorerViewModel(root);
        explorer.SelectNode(original);
        using var features = new AdditionalFeaturesViewModel(explorer, Path.Combine(directory, "ui-edits"));
        Complete(features.AnalyzeCommand.Execute().ToTask());
        var row = features.Rows.Single(); row.CutText = "-0.2"; row.DrawCutText = "-0.1";
        Check(row.CanEditDraw && row.CanEditReload && row.CurrentDuration.Contains("切枪")
            && row.PreviewText.Contains("换弹裁后") && row.PreviewText.Contains("切枪裁后"),
            "additional features shows separate draw and reload input fields, durations and previews on demand");
        var window = new AdditionalFeaturesWindow { ViewModel = features }; window.Show(); PumpFrames();
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "animation-sharing-preview.png"));
        window.Close();
        root.Save(); var saved = AddonRoot.LoadFileFromDirectory(addons)!;
        Complete(root.DisposeAsync().AsTask());
        var restored = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        using (restored.BlockAutoCheck()) restored.LoadSave(saved);
        Check(restored.GetDescendants().OfType<VpkAddon>().Count(node => node.AnimationEdits is not null) == 2,
            "animation source and ordered cut data survive a library metadata save and reload");
        Complete(restored.DisposeAsync().AsTask());
    }
}
