using System.Globalization;
using System.Buffers.Binary;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;
using SteamDatabase.ValvePak;

internal static partial class Program
{
    private static void CheckReloadEditor(string output)
    {
        var directory = Path.Combine(output, "reload-edit-" + Guid.NewGuid().ToString("N")[..7]);
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.vpk");
        var model = ReloadModelFixture();
        Write(original, model, "Original M4");
        var before = File.ReadAllBytes(original);
        var analysis = Complete(ReloadAnimationEditor.AnalyzeAsync(original));
        Check(analysis.Clips.Count == 2 && analysis.Clips[0].Seconds == 3 && analysis.Clips[1].Seconds == 3.75,
            "opt-in analysis reads independent base/layer reload durations from the selected VPK");
        var half = ReloadAnimationEditor.Preview(analysis, 0.5);
        Check(half[0].NewFrames == 76 && half[1].NewFrames == 79 && half.All(change => change.ActualCutSeconds == 0.5)
            && half[1].RemovedEvents == 1, "decimal seconds are converted per animation FPS and preview clipped tail events");
        var rounded = ReloadAnimationEditor.Preview(analysis, 0.017);
        Check(Math.Abs(rounded[0].ActualCutSeconds - 1.0 / 30) < 0.000001 && rounded[1].ActualCutSeconds == 0,
            "sub-frame decimal requests report the real whole-frame cut instead of promising continuous precision");
        CheckThrows<ArgumentException>(() => ReloadAnimationEditor.Preview(analysis, double.NaN), "nonfinite cut values are rejected");
        CheckThrows<ArgumentException>(() => ReloadAnimationEditor.Preview(analysis, 100), "a cut cannot remove the entire shortest reload sequence");
        var exports = Path.Combine(directory, "exports");
        var generated = Complete(ReloadAnimationEditor.ExportAsync(analysis, 0.5, exports));
        var generatedBytes = File.ReadAllBytes(generated);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(generatedBytes) == 0x55AA1234
            && BinaryPrimitives.ReadUInt32LittleEndian(generatedBytes.AsSpan(4)) == 1,
            "exported VPK bytes declare version 1 so L4D2 does not reject the archive as Unknown version 2");
        var after = Complete(ReloadAnimationEditor.AnalyzeAsync(generated));
        Check(after.Clips[0].Frames == 76 && after.Clips[1].Frames == 79 && after.Title.Contains("Reload cut 0.5s")
            && after.Clips[1].EventFrames.Length == 2 && Math.Abs(after.Clips[1].EventFrames[0] - 10) < 0.00001
            && Math.Abs(after.Clips[1].EventFrames[1] - 50) < 0.00001,
            "export trims only the tail, removes out-of-range events and preserves retained event frame numbers");
        using (var package = new Package())
        {
            package.Read(generated);
            package.ReadEntry(package.FindEntry("materials/probe.vtf")!, out byte[] texture);
            package.ReadEntry(package.FindEntry("models/v_models/v_rifle.mdl")!, out byte[] edited);
            var name = BitConverter.ToInt32(edited, 1080 + 76) + 1080;
            Check(texture.SequenceEqual(new byte[] { 4, 5, 6 }) && Encoding.UTF8.GetString(edited, name, 12) == "named_reload"
                && edited.AsSpan(8, 4).SequenceEqual(model.AsSpan(8, 4)),
                "textures and geometry checksums survive and moved named-event pointers remain valid");
        }
        foreach (var seconds in new[] { 1.0333333333, 2.0333333333, 1.7 })
            _ = Complete(ReloadAnimationEditor.ExportAsync(analysis, seconds, exports));
        Check(File.ReadAllBytes(original).SequenceEqual(before) && Directory.GetFiles(exports, "*.vpk").Length == 4,
            "section-boundary and long cuts produce validated copies while the source remains byte-identical");
        var vpk1Output = Complete(ReloadAnimationEditor.ExportAsync(Complete(ReloadAnimationEditor.AnalyzeAsync(generated)), 0.2, exports));
        using (var converted = new Package())
        {
            converted.Read(vpk1Output); converted.ReadEntry(converted.FindEntry("materials/probe.vtf")!, out byte[] texture);
            Check(converted.Version == 1 && converted.HeaderSize == 12 && texture.SequenceEqual(new byte[] { 4, 5, 6 })
                && File.ReadAllBytes(generated).SequenceEqual(generatedBytes),
                "an actual VPK 1 input stays VPK 1 after a further cut with correct data offsets and its source unchanged");
        }
        var version53 = Path.Combine(directory, "mdl53.vpk");
        var mdl53 = (byte[])model.Clone(); BitConverter.GetBytes(53).CopyTo(mdl53, 4); Write(version53, mdl53, "MDL53");
        var supported53 = Complete(ReloadAnimationEditor.AnalyzeAsync(version53));
        _ = Complete(ReloadAnimationEditor.ExportAsync(supported53, 0.3, exports));
        Check(supported53.Clips.All(clip => clip.Unsupported is null), "the validated MDL53 common animation layout is supported");
        var unsupportedFile = Path.Combine(directory, "external-ani.vpk");
        var external = (byte[])model.Clone(); BitConverter.GetBytes(1).CopyTo(external, 1536);
        Write(unsupportedFile, external, "External animation");
        var unsupported = Complete(ReloadAnimationEditor.AnalyzeAsync(unsupportedFile));
        Check(unsupported.Clips.Any(clip => clip.Unsupported?.Contains("外置") == true), "external animation blocks are clearly reported as unsupported");
        CheckThrows<NotSupportedException>(() => ReloadAnimationEditor.Preview(unsupported, 0.2), "unsupported layouts cannot be exported as apparently successful edits");
        var stale = Path.Combine(directory, "stale.vpk"); File.WriteAllBytes(stale, before);
        var staleAnalysis = Complete(ReloadAnimationEditor.AnalyzeAsync(stale)); File.AppendAllText(stale, "changed");
        CheckThrows<IOException>(() => Complete(ReloadAnimationEditor.ExportAsync(staleAnalysis, 0.2, exports)),
            "a source changed after inspection must be re-read before export");

        var game = Path.Combine(directory, "game");
        var library = Path.Combine(game, "left4dead2", "addons"); Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(game, "left4dead2.exe"), "test marker; never executed");
        var root = new AddonRoot { DirectoryPath = library, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        using var blocked = root.BlockAutoCheck();
        LocalVpkAddon first, second;
        using (root.BlockAutoCheck())
        {
            first = AddonNode.Create<LocalVpkAddon>(root); first.Name = "first"; first.IsEnabled = true; Write(first.VpkFilePath!, model, "First M4");
            var group = AddonNode.Create<AddonGroup>(root); group.Name = "nested";
            second = AddonNode.Create<LocalVpkAddon>(root, group); second.Name = "second"; second.IsEnabled = false;
            second.CatalogTargetId = "ak47"; Write(second.VpkFilePath!, mdl53, "Second M4");
        }
        Complete(Task.WhenAll(first.CheckVpkValidity(), second.CheckVpkValidity()));
        using var explorer = new AddonNodeExplorerViewModel(root);
        var window = new Window { Width = 1400, Height = 820, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show(); PumpFrames();
        Complete(explorer.OpenAdditionalFeaturesCommand.Execute().ToTask()); PumpFrames();
        var extras = window.OwnedWindows.OfType<AdditionalFeaturesWindow>().Single();
        var features = extras.ViewModel!;
        Check(features.Rows.Count == 0 && !features.HasRows
            && !window.GetVisualDescendants().OfType<TextBox>().Any(control => control.Name == "reloadCutTextBox"),
            "the small additional-features entry opens no reload analysis or permanent main-page duration controls by default");
        Complete(features.AnalyzeCommand.Execute().ToTask());
        Check(features.Rows.Count == 0 && features.Status.Contains("勾选 Mod 左侧"), "reading reload animations requires an explicitly selected mod");
        foreach (var section in explorer.ModCatalog.Sections)
        {
            section.IsExpanded = section.Count > 0;
            foreach (var target in section.Children) target.IsExpanded = target.Count > 0;
        }
        PumpFrames();
        SelectInline(first); SelectInline(second); PumpFrames();
        Check(explorer.SelectedNodes.Count == 2 && SelectionFor(first).IsChecked == true && SelectionFor(second).IsChecked == true,
            "visible selection checkboxes support direct multi-selection across objects and nested groups without Ctrl");
        Check(first.IsEnabled && !second.IsEnabled && explorer.ModCatalog.IsOverview && !explorer.ShowModDetails,
            "selecting mods for additional features does not change enable switches or navigate away from the catalog");
        Check(features.SelectedModNames.Contains("First M4") && features.SelectedModNames.Contains("Second M4")
            && features.SelectionText.Contains("2") && features.Rows.Count == 0,
            "the additional-features window names every selected mod before reading any animations");
        Check(window.GetVisualDescendants().OfType<Border>().Count(control => control.Name == "inlineModCard" && control.Classes.Contains("selected")) == 2,
            "selected inline mods have a visible highlighted border in addition to a checked box");
        first.CatalogTargetId = "ak47"; PumpFrames(); PumpFrames();
        Check(explorer.SelectedNodes.Count == 2 && SelectionFor(first).IsChecked == true && SelectionFor(second).IsChecked == true,
            "catalog refreshes preserve selected nested-group mods and restore their selection markers after moving categories");
        window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "categoryScrollViewer").Offset = default;
        window.MouseMove(new Point(4, 4)); PumpFrames();
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "reload-selection-catalog-preview.png"));
        using (var frame = extras.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "reload-selection-preview.png"));
        SelectInline(first); PumpFrames();
        Check(explorer.SelectedNodes.Count == 1 && !features.SelectedModNames.Contains("First M4") && features.SelectedModNames.Contains("Second M4"),
            "unchecking a mod updates the actual processing selection and visible names immediately");
        Complete(features.ClearSelectionCommand.Execute().ToTask()); PumpFrames();
        Check(explorer.SelectedNodes.Count == 0 && SelectionFor(second).IsChecked == false && features.SelectedModNames.Contains("尚未选择"),
            "clear selection removes all checkmarks without toggling game enable choices");
        explorer.SelectNodes([first]); PumpFrames();
        Check(SelectionFor(first).IsChecked == true && SelectionFor(second).IsChecked == false,
            "selection made through the shared full-list model is reflected by inline checkboxes");
        SelectInline(second); Complete(features.AnalyzeCommand.Execute().ToTask()); PumpFrames();
        Check(features.Rows.Count == 2 && features.HasRows && features.Rows.All(row => row.CutText == "0"),
            "only a deliberate read displays selected mods, durations and independent decimal cut fields");
        features.Rows[0].CutText = "-0.5"; features.Rows[1].CutText = "-1.7";
        Check(features.Rows[0].PreviewText.Contains("裁后") && features.Rows[1].PreviewText.Contains("尾段事件"),
            "different batch rows preview independent requested cuts and affected events");
        Complete(features.GenerateCommand.Execute().ToTask()); PumpFrames();
        Check(features.Rows.All(row => File.Exists(row.OutputPath)) && first.IsEnabled && !second.IsEnabled,
            "batch editing creates separate outputs without changing original enable choices");
        using (var frame = extras.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "additional-features-preview.png"));
        features.Rows[1].CutText = "0.5";
        Check(features.Rows[1].OutputPath == "" && features.Rows[1].PreviewText.Contains("负数"),
            "positive extension values are rejected and edited input cannot accidentally import an older output");
        Check(!features.ReplaceAndEnable, "automatic replacement is an explicit opt-in and does not change default copy generation");
        var replaceCheck = extras.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "replaceEditedModsCheckBox");
        replaceCheck.IsChecked = true; PumpFrames();
        features.Rows[0].CutText = "-0.4"; features.Rows[1].CutText = "-0.3";
        Check(features.ReplaceAndEnable && features.GenerateButtonText == "处理并替换启用",
            "the replacement checkbox binds to the batch processing action");
        PumpFrames();
        using (var frame = extras.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "reload-auto-replace-preview.png"));
        Complete(features.GenerateCommand.Execute().ToTask()); PumpFrames();
        var editedNodes = root.GetDescendants().OfType<VpkAddon>().Where(node => node.AnimationEdits is not null).ToArray();
        Check(editedNodes.Length == 2 && editedNodes.All(node => node.IsEnabled && File.Exists(node.VpkFilePath!))
            && first.IsValid && second.IsValid && !first.IsEnabled && !second.IsEnabled && File.Exists(first.VpkFilePath!) && File.Exists(second.VpkFilePath!),
            "opted-in editing enables new versions while retaining disabled originals that have not yet been archived by manual application");
        Check(features.Rows.All(row => row.ReplacementApplied && !row.CanEdit && row.Result.Contains("旧版已保留"))
            && !features.HasImportableOutputs && explorer.SelectedNodes.Count == 2,
            "successfully replaced rows require a deliberate re-read and the newly enabled mods remain selected without another import click");
        extras.Close(); window.Close(); Complete(root.DisposeAsync().AsTask());

        CheckBox SelectionFor(VpkAddon addon) => window.GetVisualDescendants().OfType<CheckBox>().Single(control =>
            control.Name == "inlineSelectModCheckBox" && control.DataContext is VpkAddonViewModel model && model.Addon == addon);
        void SelectInline(VpkAddon addon)
        {
            var control = SelectionFor(addon); control.BringIntoView(); PumpFrames();
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        }
    }

    private static void Write(string path, byte[] model, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var package = new Package(); package.AddFile("models/v_models/v_rifle.mdl", model);
        package.AddFile("materials/probe.vtf", [4, 5, 6]);
        package.AddFile("addoninfo.txt", Encoding.UTF8.GetBytes($"\"AddonInfo\" {{ \"addontitle\" \"{title}\" \"addonauthor\" \"Fixture Author\" }}"));
        package.Write(path);
    }

    private static byte[] ReloadModelFixture(bool includeDraw = false)
    {
        using var stream = new MemoryStream(); stream.SetLength(includeDraw ? 4096 : 2048);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void I(int offset, int value) { stream.Position = offset; writer.Write(value); }
        void F(int offset, float value) { stream.Position = offset; writer.Write(value); }
        int Text(string value) { stream.Position = stream.Length; var offset = (int)stream.Position; writer.Write(Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); return offset; }
        I(0, 0x54534449); I(4, 49); I(8, 123456); I(156, 1); I(160, 1280); I(1284, -1); F(1280 + 56, 1);
        var clipCount = includeDraw ? 3 : 2; var sequences = includeDraw ? 2048 : 512;
        I(180, clipCount); I(184, 256); I(188, clipCount); I(192, sequences);
        var namedEvent = Text("named_reload");
        for (var index = 0; index < clipCount; index++)
        {
            var anim = 256 + index * 100; var seq = sequences + index * 212; var table = 1536 + index * 64;
            I(anim, -anim); F(anim + 8, index == 1 ? 24 : 30); I(anim + 12, index == 1 ? 4 : 0); I(anim + 16, 91);
            I(anim + 80, table - anim); I(anim + 84, 30);
            I(seq, -seq); I(seq + 4, Text(index == 0 ? "reload" : index == 1 ? "reload_layer" : "draw") - seq);
            I(seq + 8, Text(index == 0 ? "ACT_VM_RELOAD" : index == 1 ? "ACT_VM_RELOAD_LAYER" : "ACT_VM_DRAW") - seq);
            I(seq + 56, 1); var blend = (int)stream.Length; stream.Position = blend; writer.Write((short)index); I(seq + 60, blend - seq);
            if (index == 1)
            {
                I(seq + 24, 3); I(seq + 28, 1000 - seq);
                var frames = new[] { 80, 10, 50 };
                for (var e = 0; e < 3; e++)
                { var location = 1000 + e * 80; F(location, frames[e] / 90f); I(location + 4, 5004); I(location + 76, namedEvent - location); }
            }
            for (var section = 0; section < 5; section++)
            {
                var first = Math.Min(90, section * 30); var count = Math.Min(30, 91 - first);
                stream.Position = stream.Length; var block = (int)stream.Position;
                writer.Write((byte)0); writer.Write((byte)(index == 0 ? 4 : 20)); writer.Write((short)0);
                writer.Write((short)6); writer.Write((short)0); writer.Write((short)0);
                writer.Write((byte)count); writer.Write((byte)count);
                for (var frame = 0; frame < count; frame++) writer.Write((short)((first + frame) * 2));
                I(table + section * 8, 0); I(table + section * 8 + 4, block - anim);
            }
        }
        I(76, (int)stream.Length); return stream.ToArray();
    }
}
