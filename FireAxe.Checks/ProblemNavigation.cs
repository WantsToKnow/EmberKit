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
    private static void CheckProblemNavigation(string output)
    {
        var game = Path.Combine(output, "issue-" + Guid.NewGuid().ToString("N")[..6]);
        var directory = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(game, "left4dead2.exe"), "fixture, never executed");
        var core = new AddonRoot { DirectoryPath = directory, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        LocalVpkAddon map, resources, gunA, gunB;
        using (core.BlockAutoCheck())
        {
            map = Add("map-a", "Campaign A", true, true);
            resources = Add("map-resources", "Campaign A Resources", true, false);
            gunA = Add("weapon-a", "AK 木质外观", false, false);
            gunB = Add("weapon-b", "AK 战术外观", false, false);
            gunA.Priority = gunB.Priority = 10;
        }
        Complete(Task.WhenAll(new[] { map, resources, gunA, gunB }.Select(node => node.CheckVpkValidity())));
        var result = Complete(core.CheckVpkAddonConflictsAsync());
        Check(resources.CatalogTarget?.Id == "maps" && resources.Inspection!.Targets.Any(target => target.Target.Id == "ak47"),
            "campaign metadata keeps asset-only parts in maps while preserving weapon resource evidence");
        Check(result.HasConflict && result.MapAdvisories.ToHashSet().SetEquals([map, resources])
            && !result.NeedsAttention(map) && !result.NeedsAttention(resources)
            && result.NeedsAttention(gunA) && result.NeedsAttention(gunB)
            && !map.Problems.OfType<VpkAddonConflictProblem>().Any() && !resources.Problems.OfType<VpkAddonConflictProblem>().Any(),
            "map asset sharing is advisory, while weapon conflicts at a different priority remain actionable");
        Check(map.ConflictingFilesWithAddonIds.Count > 0 && resources.ConflictingFilesWithAddonIds.Count > 0,
            "map advisory resource paths remain available in the detailed conflict inspector");
        WritePack(resources, "Campaign A Resources", true, true);
        result = Complete(core.CheckVpkAddonConflictsAsync());
        Check(!result.NeedsAttention(map) && !result.NeedsAttention(resources), "identical bundled map definitions do not become false map-version errors");
        WritePack(resources, "Campaign A Resources", true, true, [9, 8, 7]);
        result = Complete(core.CheckVpkAddonConflictsAsync());
        Check(result.NeedsAttention(map) && result.NeedsAttention(resources), "different versions of the same BSP still require attention");
        WritePack(resources, "Campaign A Resources", true, false);
        Complete(resources.CheckVpkValidity());
        Complete(core.CheckVpkAddonConflictsAsync());
        core.Save();
        Complete(core.DisposeAsync().AsTask());

        using var client = new HttpClient(new SteamFixtureHandler([]));
        using var settings = new AppSettings((App)Application.Current!)
        {
            LastOpenDirectory = null, GamePath = game, IsAutoDetectWorkshopItemLinkInClipboard = false, IsFileBackupEnabled = false
        };
        using var downloads = new DownloadItemListViewModel();
        var manager = new AppWindowManager(settings, downloads, client);
        using var main = new MainWindowViewModel(settings, manager, new FixtureDownloads([]), client, downloads);
        var window = manager.CreateMainWindow(main);
        window.Width = 1500; window.Height = 760;
        window.Show();
        Complete(main.OpenDirectoryAsync(directory));
        var root = main.AddonRoot!;
        if (root.CheckTask is { } openingCheck) Complete(openingCheck);
        Complete(root.CheckVpkAddonConflictsAsync());
        var explorer = main.AddonNodeExplorerViewModel!;
        Pump(() => explorer.ModCatalog.Sections.Any(section => section.Id == "category:枪械 / 投掷" && section.Count == 2)
            && explorer.ModCatalog.Sections.Any(section => section.Id == "category:地图 / 战役" && section.Count == 2));
        Check(explorer.ModCatalog.OverlapGroups.Single().Status.Contains("已安装 2"),
            "map parts are excluded from ordinary weapon replacement warnings");
        var target = root.GetDescendants().OfType<VpkAddon>().Single(node => node.Name == "weapon-b");
        manager.OpenProblemListWindow(); PumpFrames();
        var issues = window.OwnedWindows.OfType<AddonProblemListWindow>().Single();
        var issueModel = issues.ViewModel!;
        Pump(() => issueModel.MapAdvisoryViewModels.Count == 2
            && issueModel.ProblemViewModels.Any(problem => problem.ProblemType == typeof(VpkAddonConflictProblem)));
        Check(issues.Owner == window && issues.IsVisible && window.IsEnabled && issues.Width <= 480,
            "the problem list is a compact owned nonmodal tool window and leaves the workbench interactive");
        using (var frame = issues.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "problem-tool-preview.png"));
        var group = issues.GetVisualDescendants().OfType<Expander>().Single(control => control.DataContext is AddonProblemListViewModel.ProblemViewModel problem
            && problem.ProblemType == typeof(VpkAddonConflictProblem));
        group.IsExpanded = true; PumpFrames();
        var reference = issues.GetVisualDescendants().OfType<AddonRefView>().First(control => control.IsEffectivelyVisible
            && control.ViewModel?.Addon == target);
        Check(reference.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "AK 战术外观"),
            "problem links display the mod title instead of its internal numeric filename");
        explorer.ModCatalog.CategorySearchText = "missing";
        explorer.SearchText = "missing";
        explorer.ModCatalog.ShowAll(); PumpFrames();
        Click(issues, reference.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "jumpButton"));
        PumpFrames();
        var row = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "inlineModCard"
            && control.DataContext is VpkAddonViewModel model && model.Addon == target);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "categoryScrollViewer");
        var point = row.TranslatePoint(default, scroll)!.Value;
        Check(explorer.ModCatalog.IsOverview && explorer.ModCatalog.CategorySearchText.Length == 0 && explorer.SearchText.Length == 0
            && explorer.SelectedVpk == target && row.IsEffectivelyVisible && row.Classes.Contains("located")
            && point.Y >= -1 && point.Y + row.Bounds.Height <= scroll.Bounds.Height + 1
            && issues.IsVisible && window.IsEnabled,
            "one problem click expands the exact category/object, scrolls and highlights the mod without closing the problem window");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "problem-location-preview.png"));
        using (var frame = issues.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "problem-tool-preview.png"));
        var toggle = row.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        var originalPosition = issues.Position;
        issues.Position = new PixelPoint(window.Position.X + (int)((window.Bounds.Width - issues.Width) * window.RenderScaling), window.Position.Y + 42);
        PumpFrames();
        var workbench = window.GetVisualDescendants().OfType<AddonNodeExplorerView>().Single();
        var togglePoint = toggle.TranslatePoint(new Point(toggle.Bounds.Width, 0), window)!.Value;
        Check(workbench.Margin.Right >= issues.Width && window.Position.X + togglePoint.X * window.RenderScaling < issues.Position.X,
            "the floating problem list reserves space beside the workbench so Mod switches remain uncovered");
        issues.Position = originalPosition; PumpFrames();
        Click(window, toggle); Pump(() => !target.IsEnabled);
        issueModel.Check();
        Pump(() => root.CheckTask is null || root.CheckTask.IsCompleted);
        Complete(root.CheckVpkAddonConflictsAsync()); PumpFrames();
        Check(issues.IsVisible && issueModel.MapAdvisoryViewModels.Count == 2
            && !root.GetDescendants().SelectMany(node => node.Problems).OfType<VpkAddonConflictProblem>().Any(),
            "manual adjustment and recheck clear the weapon problem while the floating list and map advice remain available");
        issues.Close();
        Check(workbench.Margin.Right == 0, "closing the problem tool restores the full workbench width");
        window.Close(); main.CloseDirectory();
        Pump(() => !root.IsValid);

        LocalVpkAddon Add(string name, string title, bool campaign, bool bsp)
        {
            var node = AddonNode.Create<LocalVpkAddon>(core); node.Name = name; node.IsEnabled = true;
            WritePack(node, title, campaign, bsp); return node;
        }
        void WritePack(VpkAddon node, string title, bool campaign, bool bsp, byte[]? mapBytes = null)
        {
            using var package = new Package();
            package.AddFile("models/v_models/v_rif_ak47.mdl", [4, 5, 6]);
            package.AddFile("materials/shared/sample.vmt", [1, 2, 3]);
            if (bsp) package.AddFile("maps/campaign_a.bsp", mapBytes ?? [1, 2, 3]);
            package.AddFile("addoninfo.txt", Encoding.UTF8.GetBytes($"\"AddonInfo\" {{ \"addontitle\" \"{title}\" \"addonContent_Campaign\" \"{(campaign ? 1 : 0)}\" }}"));
            package.Write(node.VpkFilePath!);
        }
        void Click(Window surface, Control control)
        {
            control.BringIntoView(); PumpFrames();
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), surface)!.Value;
            surface.MouseDown(point, MouseButton.Left); surface.MouseUp(point, MouseButton.Left);
        }
    }
}
