using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;
using SteamDatabase.ValvePak;
using System.Text;

internal static partial class Program
{
    private static void CheckCategoryDashboard(string output)
    {
        var game = Path.Combine(output, "cat-" + Guid.NewGuid().ToString("N")[..6]);
        var addons = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(addons);
        File.WriteAllText(Path.Combine(game, "left4dead2.exe"), "fixture, never executed");
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            DownloadService = new FixtureDownloads([]) };
        var samples = new (string Name, string File)[]
        {
            ("AK-47 木质经典", "models/v_models/v_rif_ak47.mdl"), ("AK-47 战术黑", "models/v_models/v_rif_ak47.mdl"),
            ("M4A1 沙色涂装", "models/v_models/v_rif_m16a2.mdl"), ("SG552 城市行动", "models/v_models/v_rif_sg552.mdl"),
            ("Hunter 暗夜外观", "models/infected/hunter.mdl"), ("Tank 重装外观", "models/infected/hulk.mdl"),
            ("Nick 都市便服", "models/survivors/survivor_gambler.mdl"), ("Zoey 经典外套", "models/survivors/survivor_teenangst.mdl"),
            ("武士刀 黑曜", "models/weapons/melee/v_katana.mdl"), ("警棍 简约", "models/weapons/melee/v_tonfa.mdl"),
            ("医疗包 应急款", "models/weapons/melee/v_medkit.mdl"), ("肾上腺素", "models/weapons/melee/v_adrenaline.mdl"),
            ("夜色战役", "maps/c1_sample.bsp"), ("简洁 HUD", "resource/ui/demo.res"),
            ("安全屋音乐", "sound/music/safe/demo.wav"), ("曳光效果", "particles/demo.pcf"),
            ("手枪 沙色外观", "models/v_models/v_pist_pistol.mdl")
        };
        var mods = new List<LocalVpkAddon>();
        using (root.BlockAutoCheck())
            for (int i = 0; i < samples.Length; i++)
            {
                var node = AddonNode.Create<LocalVpkAddon>(root); node.Name = "demo-" + i; node.IsEnabled = i % 3 != 1;
                WriteVpk(node.VpkFilePath!, samples[i].Name, samples[i].File); mods.Add(node);
            }
        using (var package = new Package())
        {
            foreach (var resource in new[] { samples[0].File, "models/survivors/survivor_coach.mdl", "resource/ui/weapon.res", "scripts/demo.nut", "sound/music/demo.wav" })
                package.AddFile(resource, [1, 2, 3]);
            package.AddFile("addoninfo.txt", Encoding.UTF8.GetBytes($"\"AddonInfo\" {{ \"addontitle\" \"{samples[0].Name}\" \"addonauthor\" \"EmberKit 测试素材\" \"addondescription\" \"经典木质外观，附带图标、音效和脚本；列表只归入一个分类。\" }}"));
            using var bitmap = new SkiaSharp.SKBitmap(128, 88);
            using var canvas = new SkiaSharp.SKCanvas(bitmap);
            canvas.Clear(new SkiaSharp.SKColor(37, 63, 86));
            using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(177, 213, 234) };
            canvas.DrawRoundRect(22, 30, 84, 15, 3, 3, paint); canvas.DrawRect(48, 40, 14, 24, paint);
            using var image = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            package.AddFile("addonimage.jpg", image.ToArray());
            package.Write(mods[0].VpkFilePath!);
        }
        WriteVpk(mods[12].VpkFilePath!, samples[12].Name, samples[12].File, "models/v_models/v_rif_m16a2.mdl", "resource/ui/map.res", "sound/music/map.wav");
        Complete(Task.WhenAll(mods.Select(node => node.CheckVpkValidity())));
        Complete(root.Storage.RefreshAsync());
        using var model = new AddonNodeExplorerViewModel(root);
        var view = new AddonNodeExplorerView { ViewModel = model };
        var window = new Window { Width = 1600, Height = 1040, Content = view };
        window.Show(); PumpFrames();
        Pump(() => model.ModCatalog.Sections.Any(section => section.Id == "category:枪械 / 投掷" && section.Count == 5));
        Check(mods[0].Inspection!.Targets.Count >= 5 && mods[0].CatalogTarget?.Id == "ak47" && mods[12].CatalogTarget?.Id == "maps"
            && model.ModCatalog.Sections.Sum(section => section.Count) == mods.Count
            && model.ModCatalog.Categories.Where(category => category.ParentId is not null || category.Id is "unknown" or "pending").Sum(category => category.Count) == mods.Count,
            "bundled models, HUD, sounds and scripts have one browse category and one target per mod, while retaining all inspection evidence");
        var overview = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "categoryOverview");
        var tools = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "toolScrollViewer");
        var categories = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "categoryScrollViewer");
        Check(model.ModCatalog.IsOverview && overview.IsEffectivelyVisible && overview.Bounds.Width > window.Bounds.Width * 0.7
            && overview.Bounds.Height > window.Bounds.Height * 0.9 && !model.ShowModDetails
            && tools.TranslatePoint(default, window)!.Value.X < overview.TranslatePoint(default, window)!.Value.X
            && tools.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Visible
            && categories.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
            "tools and the full-height category workspace sit side by side with independent visible scrollbars");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "category-dashboard-preview.png"));
        var layout = window.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "workspaceLayout");
        var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(control => control.Name == "workspaceSplitter");
        var initialToolsWidth = layout.ColumnDefinitions[0].ActualWidth;
        var initialWorkspaceWidth = overview.Bounds.Width;
        Drag(splitter, 60);
        Check(layout.ColumnDefinitions[0].ActualWidth > initialToolsWidth + 30 && overview.Bounds.Width < initialWorkspaceWidth,
            "dragging the main divider reallocates width between the compact tools and category workspace");
        Drag(splitter, -60);
        window.Width = 1100; window.Height = 760; PumpFrames();
        var compactFolders = window.GetVisualDescendants().OfType<Expander>().Where(folder => folder.Name == "categoryFolder").ToArray();
        Check(compactFolders.Length >= 10 && compactFolders.All(folder => folder.Bounds.Width >= 500
            && folder.TranslatePoint(default, window) is { } position && position.X >= 0
            && position.X + folder.Bounds.Width <= window.Bounds.Width),
            "full-width category folders fit compact windows without horizontal clipping");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "category-dashboard-compact.png"));
        var startup = window.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "launchSettingsExpander");
        window.Height = 520; startup.IsExpanded = true; PumpFrames();
        var oldCategoriesOffset = categories.Offset;
        tools.Offset = new Vector(0, 100); PumpFrames();
        var toolsOffset = tools.Offset;
        categories.Offset = new Vector(0, 100); PumpFrames();
        Check(toolsOffset.Y > 0 && oldCategoriesOffset.Y == 0 && categories.Offset.Y > 0 && tools.Offset == toolsOffset,
            "each side scrolls independently without moving the other pane");
        tools.Offset = categories.Offset = default; startup.IsExpanded = false;
        window.Width = 1600; window.Height = 1040; PumpFrames();
        model.ModCatalog.CategorySearchText = "Hunter"; PumpFrames();
        Check(model.ModCatalog.Sections.Count == 1 && model.ModCatalog.Sections[0].Id == "category:感染者",
            "search locates and expands a matching infected folder");
        model.ModCatalog.CategorySearchText = "木质经典"; PumpFrames();
        Check(model.ModCatalog.Sections.Count == 1 && model.ModCatalog.Sections[0].Id == "category:枪械 / 投掷"
            && model.ModCatalog.Sections[0].VisibleChildren.Single().VisibleMods.Single().Addon == mods[0],
            "mod-title search reveals the matching category, object and inline mod");
        model.ModCatalog.CategorySearchText = ""; model.ModCatalog.CollapseSections(); PumpFrames();
        var guns = model.ModCatalog.Sections.Single(section => section.Id == "category:枪械 / 投掷");
        var infected = model.ModCatalog.Sections.Single(section => section.Id == "category:感染者");
        var ak = guns.Children.Single(target => target.Id == "ak47");
        var pistol = guns.Children.Single(target => target.Id == "pistol");
        var hunter = infected.Children.Single(target => target.Id == "infected:hunter");
        ClickHeader(CategoryFolder(guns.Id)); PumpFrames();
        ClickHeader(TargetFolder(ak.Id)); PumpFrames();
        ClickHeader(TargetFolder(pistol.Id)); PumpFrames();
        Check(guns.IsExpanded && ak.IsExpanded && pistol.IsExpanded && ak.VisibleMods.Count == 2 && pistol.VisibleMods.Count == 1
            && model.ModCatalog.IsOverview && window.OwnedWindows.Count == 0,
            "AK and pistol folders expand independently in the same category without navigation");
        ClickHeader(CategoryFolder(infected.Id)); PumpFrames();
        ClickHeader(TargetFolder(hunter.Id)); PumpFrames();
        Check(guns.IsExpanded && ak.IsExpanded && pistol.IsExpanded && infected.IsExpanded && hunter.IsExpanded
            && TargetFolder(ak.Id).IsEffectivelyVisible && TargetFolder(pistol.Id).IsEffectivelyVisible && TargetFolder(hunter.Id).IsEffectivelyVisible,
            "guns, pistols and infected folders can all remain open together");
        ClickHeader(CategoryFolder(guns.Id)); PumpFrames();
        Check(!guns.IsExpanded && ak.IsExpanded && pistol.IsExpanded && infected.IsExpanded && hunter.IsExpanded,
            "collapsing one category preserves its object states and leaves other categories open");
        ClickHeader(CategoryFolder(guns.Id)); PumpFrames();
        Check(TargetFolder(ak.Id).IsExpanded && TargetFolder(pistol.Id).IsExpanded, "reopening a category restores its previously expanded objects");
        var gunControl = CategoryFolder(guns.Id);
        var toggle = TargetFolder(ak.Id).GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.DataContext is VpkAddonViewModel item && item.Addon == mods[0]);
        var enabledBefore = mods[0].IsEnabled;
        Click(toggle);
        Pump(() => mods[0].IsEnabled != enabledBefore); PumpFrames();
        Check(model.ModCatalog.IsOverview && mods[0].IsEnabled != enabledBefore && ReferenceEquals(gunControl, CategoryFolder(guns.Id))
            && ak.IsExpanded && pistol.IsExpanded && hunter.IsExpanded,
            "inline switches update real mod state without rebuilding or collapsing open folders");
        var inlineItems = TargetFolder(ak.Id).GetVisualDescendants().OfType<AddonNodeEnableButton>().Select(control => (VpkAddonViewModel)control.DataContext!).ToArray();
        Pump(() => inlineItems.Single(item => item.Addon == mods[0]).Image is not null);
        Check(inlineItems.Single(item => item.Addon == mods[0]).ShouldShowImage && !inlineItems.Single(item => item.Addon == mods[1]).ShouldShowImage,
            "inline mods reuse available thumbnails and omit image placeholders when no thumbnail exists");
        categories.Offset = default; window.MouseMove(new Point(4, 4)); PumpFrames();
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "category-inline-preview.png"));
        window.Width = 1100; window.Height = 760; PumpFrames();
        categories.Offset = default; PumpFrames();
        Check(window.GetVisualDescendants().OfType<Border>().Where(control => control.Name == "inlineModCard" && control.IsEffectivelyVisible)
            .All(control => control.Bounds.Width > 450 && control.TranslatePoint(default, window) is { } point && point.X + control.Bounds.Width <= window.Bounds.Width),
            "expanded mod summaries and enable controls fit the compact window");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "category-inline-compact.png"));
        window.Width = 1600; window.Height = 1040; PumpFrames();
        var tagExpander = window.GetVisualDescendants().OfType<Expander>().Single(control => control.Header as string == "更改标签" && control.DataContext is VpkAddonViewModel item && item.Addon == mods[0]);
        tagExpander.IsExpanded = true; PumpFrames();
        var edit = window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "inlineCatalogTag" && control.DataContext is VpkAddonViewModel item && item.Addon == mods[0]);
        var evidence = mods[0].Inspection;
        edit.SelectedItem = VpkAddonViewModel.CatalogTags.Single(tag => tag.Id == "infected:hunter");
        Pump(() => model.ModCatalog.Sections.Single(section => section.Id == "category:枪械 / 投掷").Count == 4);
        Check(mods[0].CatalogTargetId == "infected:hunter" && ak.Mods.Count == 1 && hunter.Mods.Count == 2
            && model.ModCatalog.Sections.Single(section => section.Id == "category:感染者").Count == 3
            && model.ModCatalog.Sections.Sum(section => section.Count) == mods.Count && mods[0].Inspection == evidence
            && ak.IsExpanded && pistol.IsExpanded && hunter.IsExpanded && ReferenceEquals(gunControl, CategoryFolder(guns.Id)),
            "manual tag edits move a mod uniquely between folders while preserving other open objects and conflict evidence");
        var saved = AddonRoot.Deserialize(AddonRoot.Serialize(root.CreateSave()));
        var restored = new AddonRoot { DirectoryPath = Path.Combine(game, "saved-check"), TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        using (restored.BlockAutoCheck()) restored.LoadSave(saved);
        Check(restored.GetDescendants().OfType<VpkAddon>().Single(node => node.Id == mods[0].Id).CatalogTargetId == "infected:hunter",
            "manual category tags survive serialized library save and reload");
        Complete(restored.DisposeAsync().AsTask());
        Complete(mods[0].CheckVpkValidity());
        Check(mods[0].CatalogTarget?.Id == "infected:hunter" && ak.IsExpanded && pistol.IsExpanded && hunter.IsExpanded,
            "rescanning a VPK preserves its manual classification and independent folder states");
        mods[0].CatalogTargetId = null;
        Pump(() => model.ModCatalog.Sections.Single(section => section.Id == "category:枪械 / 投掷").Count == 5); PumpFrames();
        var details = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "inlineDetailsButton" && control.DataContext is VpkAddonViewModel item && item.Addon == mods[0]);
        Click(details);
        Pump(() => model.ModCatalog.IsBrowsing && model.SelectedVpk == mods[0] && !model.IsSearching && model.CurrentNodes.Contains(mods[0]));
        Check(model.ShowModDetails, "only the explicit detail button navigates to the full mod page and selects the requested mod");
        model.ModCatalog.ShowOverview(); PumpFrames();
        Check(guns.IsExpanded && ak.IsExpanded && pistol.IsExpanded && infected.IsExpanded && hunter.IsExpanded,
            "returning from full details retains every independently expanded category and object");
        var openCategory = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "openCategoryPageButton"
            && control.DataContext is ModCategorySection section && section.Id == guns.Id);
        Click(openCategory);
        Pump(() => model.ModCatalog.IsBrowsing && model.CurrentNodes.Count == 5 && !model.IsSearching);
        PumpFrames();
        Check(!overview.IsEffectivelyVisible && model.CurrentNodes.All(node => model.ModCatalog.Matches(node))
            && model.ModCatalog.Categories.Any(category => category.Id == "melee:tonfa" && category.Name.Contains("警棍")),
            "opening a category fills the mod workspace with its actual mods and retains readable target labels");
        var modWorkspace = window.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "modWorkspace");
        var targetSplitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(control => control.Name == "categoryPaneSplitter");
        var targetWidth = modWorkspace.ColumnDefinitions[0].ActualWidth;
        Drag(targetSplitter, 45);
        Check(modWorkspace.ColumnDefinitions[0].ActualWidth > targetWidth + 20,
            "the target list and middle mod list have their own adjustable divider");
        Drag(targetSplitter, -45);
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "category-mods-preview.png"));
        model.SelectNode(mods[0]); PumpFrames();
        Check(model.ShowModDetails, "mod details appear only after selecting a mod");
        model.ModCatalog.ShowOverview(); PumpFrames();
        Check(overview.IsEffectivelyVisible && !model.ShowModDetails, "returning to categories restores the full workspace");
        var apply = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "quickApplyModsButton");
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible
            && text.Text?.Contains("退出当前对局") == true), "the one-click apply button visibly explains that it leaves an active match");
        Click(apply);
        Pump(() => !model.IsApplyingMods && model.ModApplyStatus.Contains("Mod 已应用"));
        Check(window.OwnedWindows.Count == 0 && root.Storage.IsStarted && mods.All(node => File.Exists(node.VpkFilePath)),
            "one click applies mods offline without opening another page, prompting again or removing stored mods");
        window.Close(); Complete(root.DisposeAsync().AsTask());

        Expander CategoryFolder(string id) => window.GetVisualDescendants().OfType<Expander>()
            .Single(control => control.Name == "categoryFolder" && control.DataContext is ModCategorySection section && section.Id == id);
        Expander TargetFolder(string id) => window.GetVisualDescendants().OfType<Expander>()
            .Single(control => control.Name == "targetFolder" && control.DataContext is ModTargetSection target && target.Id == id);
        void ClickHeader(Expander folder) => Click(folder.GetVisualDescendants().OfType<ToggleButton>().First());
        void Click(Control button)
        {
            button.BringIntoView(); PumpFrames();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        }
        void Drag(GridSplitter divider, double delta)
        {
            var point = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left);
            var destination = new Point(point.X + delta, point.Y);
            window.MouseMove(destination); window.MouseUp(destination, MouseButton.Left); PumpFrames();
        }
    }
}
