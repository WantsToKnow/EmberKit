using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckLocalVpkImport(string output)
    {
        var game = Path.Combine(output, "local-import-" + Guid.NewGuid().ToString("N")[..7]);
        var addons = Path.Combine(game, "left4dead2", "addons");
        var sources = Path.Combine(game, "external-downloads");
        Directory.CreateDirectory(addons); Directory.CreateDirectory(sources);
        var host = Path.Combine(game, "left4dead2.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), host);
        var rifle = Path.Combine(sources, "测试M4.vpk");
        var hunter = Path.Combine(sources, "hunter.vpk");
        var bad = Path.Combine(sources, "bad.vpk");
        WriteVpk(rifle, "M4 本地测试", "models/v_models/v_rif_m16a2.mdl");
        WriteVpk(hunter, "Hunter 本地测试", "models/infected/hunter.mdl");
        File.WriteAllText(bad, "not a VPK");
        var originalRifle = File.ReadAllBytes(rifle);
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        using var blocked = root.BlockAutoCheck();
        using var explorer = new AddonNodeExplorerViewModel(root);
        var window = new Window { Width = 1400, Height = 820, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show(); PumpFrames();
        var button = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "importLocalVpkButton");
        Check(button.IsEffectivelyVisible && ReferenceEquals(button.Command, explorer.ImportLocalVpkCommand),
            "the main tool pane exposes a visible local VPK import button independent of TXT import");
        using (explorer.ChooseLocalVpkFilesInteraction.RegisterHandler(context => context.SetOutput(new[] { rifle, bad, hunter })))
        {
            Complete(explorer.ImportLocalVpkCommand.Execute().ToTask()); PumpFrames();
        }
        Check(explorer.LocalVpkImportStatus.Contains("导入 2 个") && explorer.LocalVpkImportStatus.Contains("失败 1 个")
            && !explorer.IsImportingLocalVpk && root.GetDescendants().OfType<VpkAddon>().Count() == 2,
            "multi-file import keeps valid mods and reports an invalid VPK without leaving an empty record");
        var m4 = root.GetDescendants().OfType<VpkAddon>().Single(node => node.Inspection?.Title == "M4 本地测试");
        var infected = root.GetDescendants().OfType<VpkAddon>().Single(node => node.Inspection?.Title == "Hunter 本地测试");
        Check(File.ReadAllBytes(rifle).SequenceEqual(originalRifle) && File.Exists(hunter)
            && m4.ContentHash == Convert.ToHexString(SHA256.HashData(originalRifle)) && ModStorage.IsBodyName(m4.StorageFileName!),
            "external source files are preserved and imported bodies have verified SHA-256 identities and short filenames");
        Check(m4.IsEnabled && m4.CatalogTarget?.Id == "m16" && infected.CatalogTarget?.Id == "infected:hunter"
            && explorer.ModCatalog.IsOverview && explorer.SelectedVpk == infected
            && explorer.ModCatalog.Sections.Any(section => section.IsExpanded
                && section.Children.Any(target => target.IsExpanded && target.Mods.Any(model => model.Addon == infected))),
            "imported mods are enabled, classified and located in the inline category view immediately");
        Check(!m4.IsGameSynchronized && !File.Exists(Path.Combine(addons, ModStorage.EntryName(m4.ContentHash!))),
            "offline imports stay pending instead of automatically changing game entries");
        Complete(root.Storage.RefreshAsync());
        m4.IsEnabled = false; m4.CatalogTargetId = "m16";
        var duplicate = Complete(root.Storage.ImportLocalVpkAsync([rifle]));
        Check(duplicate.Imported.Length == 0 && duplicate.Existing.Single() == m4 && !m4.IsEnabled
            && m4.CatalogTargetId == "m16" && root.GetDescendants().OfType<VpkAddon>().Count() == 2,
            "importing identical content again preserves the existing enable choice and manual category without duplicate records");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "local-vpk-import-preview.png"));

        var console = new GameConsole(game, Path.Combine(game, "private-console"));
        var arguments = Uri.UnescapeDataString(console.PrepareLaunch().FileName);
        var port = int.Parse(Regex.Match(arguments, @"-netconport (\d+)").Groups[1].Value);
        var password = Regex.Match(arguments, @"-netconpassword ([A-F0-9]{64})").Groups[1].Value;
        using var running = Process.Start(new ProcessStartInfo(host, "/d /c ping -n 30 127.0.0.1 > nul")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            Pump(() => ModStorage.IsGameRunning(game));
            var loose = Path.Combine(addons, "M4-reload-5s.vpk");
            WriteVpk(loose, "M4 换弹 5 秒测试", "models/v_models/v_rif_m16a2.mdl");
            var looseBytes = File.ReadAllBytes(loose);
            var list = GamePathUtils.GetAddonListPath(game);
            var listBytes = File.ReadAllBytes(list);
            var live = Complete(root.Storage.ImportLocalVpkAsync([loose]));
            var imported = live.Imported.Single();
            var canonical = Path.Combine(addons, ModStorage.EntryName(imported.ContentHash!));
            Check(imported.IsEnabled && imported.CatalogTarget?.Id == "m16" && File.Exists(imported.VpkFilePath!)
                && !imported.IsGameSynchronized && root.Storage.Status.Contains("待应用"),
                "a running-game import is immediately installed in the manager and classified while marked pending application");
            Check(File.ReadAllBytes(list).SequenceEqual(listBytes) && File.ReadAllBytes(loose).SequenceEqual(looseBytes)
                && !File.Exists(canonical),
                "running-game import does not modify mounted loose files, the game list or game entries");
            using var server = new FakeGameConsole(game, port, password, Path.GetFileName(loose));
            Complete(root.Storage.ReloadInMainMenuAsync(console));
            Check(!File.Exists(loose) && File.Exists(canonical) && imported.IsGameSynchronized
                && File.ReadAllBytes(imported.VpkFilePath!).SequenceEqual(looseBytes)
                && server.Commands.Count(command => command == "update_addon_paths") == 1,
                "explicit main-menu apply retires the imported loose alias after unload, retains its body and confirms one canonical mount");
            Check(File.ReadAllBytes(rifle).SequenceEqual(originalRifle) && !m4.IsEnabled,
                "applying a live import preserves external originals and the disabled previous appearance");
        }
        finally { if (!running.HasExited) running.Kill(entireProcessTree: true); running.WaitForExit(); }
        Check(!Directory.EnumerateFiles(root.Storage.TemporaryPath).Any(), "successful, duplicate and failed local imports clean their temporary copies");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        CheckThrows<OperationCanceledException>(() => Complete(root.Storage.ImportLocalVpkAsync([rifle], token: cancelled.Token)),
            "a cancelled local import does not create a mod or alter the existing choice");
        window.Close(); Complete(root.DisposeAsync().AsTask());
    }
}
