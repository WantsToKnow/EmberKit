using System.Diagnostics;
using FireAxe;

internal static partial class Program
{
    private static void CheckReloadReplacement(string output)
    {
        foreach (var mode in new[] { "offline", "locked", "running" })
        {
            var game = Path.Combine(output, "replace-" + mode + "-" + Guid.NewGuid().ToString("N")[..7]);
            var addons = Path.Combine(game, "left4dead2", "addons");
            Directory.CreateDirectory(addons);
            var host = Path.Combine(game, "left4dead2.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), host);
            var source = Path.Combine(game, "original.vpk"); Write(source, ReloadModelFixture(), "Original " + mode);
            var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
            using var blocked = root.BlockAutoCheck();
            Process? running = null;
            FileStream? occupied = null;
            try
            {
                var original = Complete(root.Storage.ImportLocalVpkAsync([source])).Imported.Single();
                Complete(root.Storage.RefreshAsync());
                var oldBody = original.VpkFilePath!; var oldHash = original.ContentHash!;
                original.CatalogTargetId = "ak47"; original.Priority = 73;
                var reference = AddonNode.Create<RefAddonNode>(root); reference.Name = "reference"; reference.SourceAddonId = original.Id;
                var dependency = AddonNode.Create<AddonGroup>(root); dependency.Name = "required"; dependency.IsEnabled = true;
                var consumer = AddonNode.Create<AddonGroup>(root); consumer.Name = "consumer";
                original.AddDependentAddon(dependency.Id); consumer.AddDependentAddon(original.Id);
                var analysis = Complete(ReloadAnimationEditor.AnalyzeAsync(oldBody));
                var edited = Complete(ReloadAnimationEditor.ExportAsync(analysis, 0.5, Path.Combine(game, "edited")));
                var invalid = Path.Combine(game, "invalid.vpk"); File.WriteAllText(invalid, "invalid");
                CheckThrows<IOException>(() => Complete(root.Storage.ReplaceEditedModAsync(original, oldHash, invalid)),
                    mode + ": failed import never removes the original");
                Check(original.IsValid && original.IsEnabled && File.Exists(oldBody), mode + ": original survives failed replacement unchanged");

                VpkAddon? existing = null;
                if (mode == "offline")
                {
                    existing = Complete(root.Storage.ImportLocalVpkAsync([edited])).Imported.Single(); existing.IsEnabled = false;
                }
                if (mode == "locked") occupied = new FileStream(oldBody, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (mode == "running")
                {
                    running = Process.Start(new ProcessStartInfo(host, "/d /c ping -n 30 127.0.0.1 > nul")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
                    Pump(() => ModStorage.IsGameRunning(game));
                }
                var list = GamePathUtils.GetAddonListPath(game); var beforeList = File.ReadAllBytes(list);
                var result = Complete(root.Storage.ReplaceEditedModAsync(original, oldHash, edited));
                Check(result.Mod.IsEnabled && !result.Mod.IsGameSynchronized && File.Exists(result.Mod.VpkFilePath!) && (existing is null || result.Mod == existing),
                    mode + ": edited version is enabled but pending application, with the old file retained");
                Check(result.Mod.CatalogTargetId == "ak47" && result.Mod.Priority == 73
                    && result.Mod.ContainsDependentAddon(dependency.Id) && consumer.ContainsDependentAddon(result.Mod.Id)
                    && !consumer.ContainsDependentAddon(original.Id) && reference.ActualSourceAddon == result.Mod,
                    mode + ": manual category, priority, dependencies and references follow the replacement");
                Check(original.IsValid && !original.IsEnabled && File.Exists(oldBody)
                    && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
                    mode + ": replacing an edit preserves the disabled old record and original body without deleting anything");
                {
                    if (mode == "running")
                        Check(File.ReadAllBytes(list).SequenceEqual(beforeList)
                            && !File.Exists(Path.Combine(addons, ModStorage.EntryName(result.Mod.ContentHash!))) && !result.Mod.IsGameSynchronized,
                            "running replacement leaves mounted entries and addonlist untouched until explicit application");
                    occupied?.Dispose(); occupied = null;
                    if (running is not null) { if (!running.HasExited) running.Kill(entireProcessTree: true); running.WaitForExit(); }
                    Complete(root.Storage.RefreshAsync());
                    Check(original.IsValid && !original.IsEnabled && File.Exists(oldBody) && result.Mod.IsEnabled,
                        mode + ": a later sync does not schedule or perform deferred deletion of the retained original");
                }
            }
            finally
            {
                occupied?.Dispose();
                if (running is not null) { if (!running.HasExited) running.Kill(entireProcessTree: true); running.WaitForExit(); running.Dispose(); }
                Complete(root.DisposeAsync().AsTask());
            }
        }
    }
}
