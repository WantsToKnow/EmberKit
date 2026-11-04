using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;
using ReactiveUI.Avalonia;
using SteamDatabase.ValvePak;

internal static partial class Program
{
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--hold-test-process"]) { Thread.Sleep(30000); return; }
        if (args is ["--inspect-console", var consoleGame])
        {
            // Explicit read-only diagnostic: never disconnect, unload, mount or write game files.
            var mounted = Task.Run(async () =>
            {
                await using var connection = await new GameConsole(consoleGame).ConnectAsync();
                return await connection.ReadMountedAddonsAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { GameDirectory = consoleGame,
                MountedCount = mounted.Count, MountedAddons = mounted.Order(StringComparer.OrdinalIgnoreCase).ToArray() }));
            return;
        }
        if (args is ["--write-index", var indexedGame])
        {
            Console.WriteLine(ModBackupIndex.WriteAsync(indexedGame).GetAwaiter().GetResult());
            return;
        }
        if (args is ["--recover-library", var recoverDirectory])
        {
            if (System.Diagnostics.Process.GetProcessesByName("FireAxe").Length != 0
                || System.Diagnostics.Process.GetProcessesByName("left4dead2").Length != 0) throw new InvalidOperationException("请先关闭游戏和管理器。");
            var recoveryRoot = new AddonRoot { DirectoryPath = recoverDirectory, TaskScheduler = TaskScheduler.Default };
            using (recoveryRoot.BlockAutoCheck()) recoveryRoot.LoadSave(AddonRoot.LoadFileFromDirectory(recoverDirectory)!);
            var result = ModLibraryRecovery.RefreshAsync(recoveryRoot).GetAwaiter().GetResult();
            recoveryRoot.Save();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            recoveryRoot.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }
        if (args is ["--inspect-pair", var firstPath, var secondPath])
        {
            using var firstPackage = new Package();
            using var secondPackage = new Package();
            firstPackage.Read(firstPath);
            secondPackage.Read(secondPath);
            var firstFiles = firstPackage.Entries!.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath());
            var secondFiles = secondPackage.Entries!.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath());
            var overlapping = firstFiles.Intersect(secondFiles, StringComparer.OrdinalIgnoreCase).Where(file => !file.StartsWith("addoninfo", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                first = new { path = firstPath, bytes = FileSystemUtils.GetContentLength(firstPath) },
                second = new { path = secondPath, bytes = FileSystemUtils.GetContentLength(secondPath) },
                sharedResourceCount = overlapping.Length, sharedResources = overlapping.Take(15).ToArray()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args is ["--inspect-vpk", var inspectPath])
        {
            using var package = new Package();
            package.Read(inspectPath);
            var resources = package.Entries!.Values.SelectMany(entries => entries).Select(entry => entry.GetFullPath()).ToArray();
            Console.WriteLine($"Valid VPK directory; resources: {resources.Length}");
            foreach (var match in ModTargetClassifier.Classify(resources)) Console.WriteLine(match.Target.Name + ": " + string.Join(", ", match.Files));
            foreach (var resource in resources.Where(path => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).Take(12)) Console.WriteLine(resource);
            return;
        }
        Check(Targets(@"MODELS\V_MODELS\V_RIF_AK47.MDL").Contains("ak47"), "case and slash normalization");
        Check(Targets("models/w_models/weapons/w_rifle_m16a2.mdl").SetEquals(["m16"]), "M4 appearance is categorized by its M16 replacement slot");
        Check(Targets("models/survivors/survivor_gambler.mdl").Contains("survivor:nick"), "Nick replacement");
        Check(Targets("models/survivors/survivor_teenangst.mdl").Contains("survivor:zoey"), "Zoey replacement");
        Check(Targets("models/infected/hulk.mdl").Contains("infected:tank"), "Tank replacement");
        Check(Targets("models/v_models/v_rif_ak47.mdl", "models/survivors/survivor_coach.mdl").Count == 2, "multi-target package");
        Check(Targets("materials/models/props/ak47_poster.vtf", "addoninfo.txt").Count == 0, "unrelated resource names do not imply a weapon replacement");
        Check(!Targets("materials/models/weapons/rifle/ak47/skin.vtf").Contains("m16"), "generic rifle directories do not falsely imply M16");
        Check(ModTargetClassifier.Classify(["maps/c1_demo.bsp", "resource/ui/demo.res"]).All(match => !match.Target.WarnOnOverlap), "maps and general HUD files are not exclusive replacement slots");

        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "FireAxe-Checks"));
        Directory.CreateDirectory(output);
        var fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new() { UseHeadlessDrawing = false }).UseReactiveUI().SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        var root = new AddonRoot { TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DirectoryPath = fixtures };
        LocalVpkAddon ak1, ak2, m16, nick, other, broken;
        RefAddonNode reference;
        using (root.BlockAutoCheck())
        {
            ak1 = Add("1103089137", "AK-47 · 木质经典 [测试样本]", "models/v_models/v_rif_ak47.mdl");
            ak2 = Add("1187196624", "AK-47 · 战术黑 [测试样本]", "models/v_models/v_rif_ak47.mdl");
            m16 = Add("1233370671", "M4A1 · 替换 M16 [测试样本]", "models/v_models/v_rif_m16a2.mdl");
            var group = AddonNode.Create<AddonGroup>(root);
            group.Name = "人物分组";
            nick = Add("1299064696", "Nick 人物外观 [测试样本]", "models/survivors/survivor_gambler.mdl", group);
            other = Add("1334419479", "未知资源 [测试样本]", "materials/custom/test.vtf");
            broken = AddonNode.Create<LocalVpkAddon>(root);
            broken.Name = "bad_vpk";
            File.WriteAllText(broken.VpkFilePath!, "deliberately invalid vpk");
            reference = AddonNode.Create<RefAddonNode>(root);
            reference.Name = "AK-47 的引用";
            reference.SourceAddonId = ak1.Id;
            reference.IsEnabled = true;
        }
        var initialRead = Task.WhenAll(new[] { ak1, ak2, m16, nick, other, broken }.Select(addon => addon.CheckVpkValidity()));
        Pump(() => initialRead.IsCompleted);
        initialRead.GetAwaiter().GetResult();
        Check(ak1.Inspection?.Targets.Single().Target.Id == "ak47", "real VPK directory parsing");
        Check(ak1.Inspection?.Title?.StartsWith("AK-47") == true, "addoninfo title is read for numeric filenames");
        Check(broken.Inspection?.Error is not null, "corrupt VPK is shown as an error");

        using var explorer = new AddonNodeExplorerViewModel(root) { ListItemViewKind = AddonNodeListItemViewKind.Grid };
        using var activation = explorer.Activator.Activate();
        var catalog = explorer.ModCatalog;
        Check(catalog.Categories.Single(category => category.Id == "ak47").Count == 2, "references do not double-count installed mods");
        Check(catalog.HasEnabledOverlap, "two active AK replacements are flagged");
        Check(catalog.Categories.Single(category => category.Id == "unknown").Count == 1, "unknown resources are explicit");
        Check(catalog.Categories.Single(category => category.Id == "pending").Count == 1, "failed reads are separate from unknown resources");
        var emptyPistol = catalog.Categories.Single(category => category.Name == "普通手枪");
        Check(emptyPistol.Count == 0, "an unfilled replacement slot remains visible as 普通手枪 0");
        catalog.SelectedCategory = emptyPistol;
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 0);
        Check(explorer.CurrentNodes.Count == 0, "selecting an empty replacement category shows an empty result");

        catalog.SelectedCategory = catalog.Categories.Single(category => category.Id == "ak47");
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count > 0);
        Check(explorer.CurrentNodes.All(node => ModCatalogViewModel.GetVpk(node)?.Inspection?.Targets.Any(match => match.Target.Id == "ak47") == true), "AK category filters the actual list");
        catalog.SelectedCategory = catalog.Categories.Single(category => category.Id == "survivor:nick");
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 1);
        Check(explorer.CurrentNodes.Single() == nick, "category filtering includes nested groups");

        catalog.SelectedCategory = catalog.Categories.First();
        explorer.SearchText = "战术黑";
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 1 && explorer.CurrentNodes[0] == ak2);
        Check(explorer.CurrentNodes.Single() == ak2, "search matches displayed addoninfo titles, not just numeric filenames");
        explorer.SearchText = "";
        catalog.OnlyOverlaps = true;
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 3);
        Check(explorer.CurrentNodes.All(node => ModCatalogViewModel.GetVpk(node) is { } vpk && (vpk == ak1 || vpk == ak2)), "overlap-only filter excludes unrelated weapons and characters");
        catalog.OnlyOverlaps = false;

        ak2.IsEnabled = false;
        Pump(() => !catalog.HasEnabledOverlap);
        Check(catalog.OverlapGroups.Single().Status.Contains("已启用 1"), "disabling a mod updates the warning immediately");
        ak1.IsEnabled = false;
        ak2.IsEnabled = true;
        Pump(() => catalog.HasEnabledOverlap);
        Check(catalog.HasEnabledOverlap, "enabled references count even if the source is disabled");
        reference.IsEnabled = false;
        Pump(() => !catalog.HasEnabledOverlap);

        var conflicting = AddonConflictUtils.CheckVpkConflictsAsync([ak1, ak2]);
        Pump(() => conflicting.IsCompleted);
        Check(!conflicting.GetAwaiter().GetResult().HasConflict, "disabled files do not create active file conflicts");
        ak1.IsEnabled = true;
        var activeConflicts = AddonConflictUtils.CheckVpkConflictsAsync([ak1, ak2]);
        Pump(() => activeConflicts.IsCompleted);
        Check(activeConflicts.GetAwaiter().GetResult().HasConflict, "existing file conflict detection still works");

        catalog.SelectedCategory = catalog.Categories.First();
        var window = new Window { Title = "FireAxe 分类增强版 · 测试样本", Width = 1400, Height = 820, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show();
        Pump(() => catalog.HasEnabledOverlap);
        PumpFrames();
        CheckProminentLaunch(window, explorer);
        SaveFrame("classified-preview.png");
        var overlapButton = window.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is ModOverlapGroup);
        Check(overlapButton.Command is not null, "overlap reminder has a bound GUI action");
        overlapButton.Command!.Execute(null);
        Pump(() => catalog.SelectedCategory?.Id == "ak47" && !explorer.IsSearching && explorer.CurrentNodes.Count == 3);
        Check(explorer.CurrentNodes.All(catalog.Matches), "clicking a reminder opens the matching weapon category");
        catalog.SelectedCategory = catalog.Categories.First();
        explorer.SelectNode(ak1);
        PumpFrames();
        SaveFrame("classified-details.png");
        Check(explorer.SelectedVpk == ak1, "detail panel follows the selected mod");
        CheckFileLocationMenu(window, explorer, ak1, nick, ak2);
        window.Width = 1100;
        window.Height = 680;
        PumpFrames();
        var compactLaunch = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "launchGameButton");
        var compactOrigin = compactLaunch.TranslatePoint(default, window)!.Value;
        Check(compactLaunch.IsEffectivelyVisible && compactOrigin.X >= 0 && compactOrigin.X + compactLaunch.Bounds.Width <= window.Bounds.Width,
            "the prominent launch entry remains fully visible at the minimum window width");
        SaveFrame("classified-compact.png");
        window.Close();

        WriteVpk(ak2.VpkFilePath!, "更新为 M16 [测试样本]", "models/v_models/v_rif_m16a2.mdl");
        var reread = ak2.CheckVpkValidity();
        Pump(() => reread.IsCompleted);
        reread.GetAwaiter().GetResult();
        Pump(() => catalog.Categories.Single(category => category.Id == "ak47").Count == 1);
        Check(ak2.Inspection?.Targets.Single().Target.Id == "m16", "changed VPK is reclassified without a stale cache");
        CheckVersionTwo(output, root, explorer);
        CheckUnifiedStorage(output);
        CheckMainMenuReload(output);
        CheckReadableModControls(output);
        CheckLinkedSizesAndSelection(output);
        CheckPathReuse(output);
        CheckPortableAppData(output);
        CheckKillFeedback(output);
        if (args.Contains("--live")) CheckLiveWorkshop(output);
        var dispose = root.DisposeAsync().AsTask();
        Pump(() => dispose.IsCompleted);
        dispose.GetAwaiter().GetResult();
        Check(!root.IsValid, "library with multiple mods closes cleanly");
        Console.WriteLine($"PASS: {_checks} checks; previews: {output}");

        LocalVpkAddon Add(string fileName, string title, string resource, AddonGroup? group = null)
        {
            var addon = AddonNode.Create<LocalVpkAddon>(root, group);
            addon.Name = fileName;
            addon.IsEnabled = true;
            Directory.CreateDirectory(Path.GetDirectoryName(addon.VpkFilePath!)!);
            WriteVpk(addon.VpkFilePath!, title, resource);
            return addon;
        }
        void SaveFrame(string name)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame");
            frame.Save(Path.Combine(output, name));
        }
    }

    private static HashSet<string> Targets(params string[] paths) => ModTargetClassifier.Classify(paths).Select(match => match.Target.Id).ToHashSet();

    private static void CheckFileLocationMenu(Window window, AddonNodeExplorerViewModel explorer, AddonNode first, AddonNode filtered, AddonNode searchTarget)
    {
        var command = (System.Windows.Input.ICommand)explorer.OpenFileLocationCommand;
        Check(command.CanExecute(null), "file location is available for a selected local mod");
        var catalog = explorer.ModCatalog;
        catalog.SelectedCategory = catalog.Categories.Single(category => category.Id == "survivor:nick");
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 1);
        PumpFrames();
        Check(explorer.SelectedNodes.All(explorer.CurrentNodes.Contains), "category filtering removes hidden selections");
        var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsEffectivelyVisible && item.DataContext == filtered);
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Right);
        window.MouseUp(point, Avalonia.Input.MouseButton.Right);
        PumpFrames();
        Check(command.CanExecute(null), "right-clicking a mod after category filtering enables file location");
        var menu = window.GetVisualDescendants().OfType<Border>().Select(border => border.ContextMenu).First(menu => menu is not null)!;
        var item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开文件位置"));
        Check(item.IsEnabled && ReferenceEquals(item.Command, explorer.OpenFileLocationCommand), "file-location context menu follows the selected row");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(Path.GetDirectoryName(explorer.AddonRoot.DirectoryPath)!, "file-location-menu-preview.png"));
        menu.Close();
        explorer.Refresh();
        Pump(() => !explorer.IsSearching);
        PumpFrames();
        Check(explorer.SelectedNode == filtered && command.CanExecute(null), "refreshing a category preserves its visible selection");
        catalog.SelectedCategory = catalog.Categories.First();
        explorer.SelectNode(first);
        PumpFrames();
        explorer.SearchText = searchTarget.Name;
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 1 && explorer.CurrentNodes[0] == searchTarget);
        Check(explorer.SelectedNodes.All(explorer.CurrentNodes.Contains), "text search removes hidden selections");
        explorer.SearchText = "";
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Contains(first));
        explorer.SelectNode(first);
        PumpFrames();
    }

    private static void WriteVpk(string filePath, string title, string resource)
    {
        using var package = new Package();
        package.AddFile(resource, [1, 2, 3]);
        package.AddFile("addoninfo.txt", Encoding.UTF8.GetBytes($"\"AddonInfo\" {{ \"addontitle\" \"{title}\" \"addonversion\" \"1\" }}"));
        package.Write(filePath);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            using var tick = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            Dispatcher.UIThread.MainLoop(tick.Token);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Dispatcher check timed out");
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static void PumpFrames()
    {
        var start = DateTime.UtcNow;
        Pump(() => DateTime.UtcNow - start > TimeSpan.FromMilliseconds(450));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
    }
}
