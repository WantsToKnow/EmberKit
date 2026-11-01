using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckReadableModControls(string output)
    {
        var game = Path.Combine(output, "readable-" + Guid.NewGuid().ToString("N")[..8]);
        var addons = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(addons);
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game,
            TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        LocalVpkAddon pistol, grouped;
        using (root.BlockAutoCheck())
        {
            pistol = AddonNode.Create<LocalVpkAddon>(root);
            pistol.Name = "3806596801"; pistol.IsEnabled = true;
            WriteVpk(pistol.VpkFilePath!, "", "models/v_models/v_pistola.mdl");
            var group = AddonNode.Create<AddonGroup>(root); group.Name = "已禁用的测试分组"; group.IsEnabled = false;
            grouped = AddonNode.Create<LocalVpkAddon>(root, group); grouped.Name = "3803712629"; grouped.IsEnabled = true;
            Directory.CreateDirectory(Path.GetDirectoryName(grouped.VpkFilePath!)!);
            WriteVpk(grouped.VpkFilePath!, "分组内 MP5 [测试样本]", "models/v_models/v_smg_mp5.mdl");
        }
        Complete(root.Storage.RefreshAsync());
        root.DownloadHistory.Record(new(3806596801, "中文标题 · 普通手枪外观 [测试样本]", WorkshopDownloadOutcome.Installed, pistol.StorageFileName));
        Check(root.DownloadHistory.GetDisplayTitle(pistol).Contains("中文标题") && pistol.Name == "3806596801",
            "numeric-named mods reuse the stored download title without renaming their identity or files");
        using (root.BlockAutoCheck())
        {
            CheckThrows<InvalidOperationException>(() => Complete(root.Storage.ReloadInMainMenuAsync(new GameConsole(game, Path.Combine(game, "no-console-settings")))) ,
                "a missing console connection leaves a visible pending state for control checks");
            using var explorer = new AddonNodeExplorerViewModel(root) { ListItemViewKind = AddonNodeListItemViewKind.Grid };
            using var activation = explorer.Activator.Activate();
            var window = new Window { Width = 1400, Height = 850, Content = new AddonNodeExplorerView { DataContext = explorer } };
            window.Show(); PumpFrames();
            ToggleSwitch Toggle(AddonNode node) => window.GetVisualDescendants().OfType<ToggleSwitch>()
                .First(control => control.IsEffectivelyVisible && control.DataContext is AddonNodeSimpleViewModel model && model.Addon == node);
            AddonNodeEnableButton EnableControl(AddonNode node) => window.GetVisualDescendants().OfType<AddonNodeEnableButton>()
                .First(control => control.IsEffectivelyVisible && control.ViewModel?.Addon == node);
            bool HasVisibleText(Control parent, string text) => parent.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.IsEffectivelyVisible && block.Text == text);
            void Click(ToggleSwitch control)
            {
                var point = control.TranslatePoint(new Point(18, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); PumpFrames();
            }
            Check(Toggle(pistol).IsChecked == true && HasVisibleText(Toggle(pistol), "已启用")
                && HasVisibleText(EnableControl(pistol), "待应用") && Toggle(pistol).IsEnabled,
                "an enabled pending mod shows its checked switch and separate pending label, and stays editable");
            Click(Toggle(pistol));
            Check(!pistol.IsEnabled && Toggle(pistol).IsChecked == false && HasVisibleText(Toggle(pistol), "已禁用")
                && HasVisibleText(EnableControl(pistol), "待应用")
                && ToolTip.GetTip(Toggle(pistol)) is string hint && hint.Contains("点击启用"),
                "clicking a pending mod disables it immediately and the tooltip describes the next action");
            Check(EnableControl(pistol).ViewModel!.EnableState == AddonNodeEnableState.Disabled,
                "pending deployment no longer changes a disabled mod into an enabled-suppressed state");
            Click(Toggle(pistol));
            Check(pistol.IsEnabled && HasVisibleText(Toggle(pistol), "已启用") && HasVisibleText(EnableControl(pistol), "待应用"),
                "a pending mod can be reenabled without a game restart or waiting for synchronization");
            explorer.GotoGroup(grouped.Group);
            PumpFrames();
            Check(Toggle(grouped).IsChecked == true && HasVisibleText(EnableControl(grouped), "上级分组已禁用"),
                "a disabled parent group is explained separately from the child's own enabled switch");
            explorer.GotoGroup(null); explorer.SearchText = "中文标题";
            Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 1);
            Check(explorer.CurrentNodes[0] == pistol, "Chinese titles from download history are searchable in the main mod list");
            explorer.SelectNode(pistol); PumpFrames();
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == "中文标题 · 普通手枪外观 [测试样本]")
                && window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == "中文标题 · 普通手枪外观 [测试样本]")
                && window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == "工坊 ID：3806596801"),
                "the row and detail panel display the human-readable title and retain the workshop ID");
            root.DownloadHistory.Record(new(3806596801, "中文标题 · 更新后的手枪说明 [测试样本]", WorkshopDownloadOutcome.Installed, pistol.StorageFileName));
            PumpFrames();
            Check(window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.IsEffectivelyVisible && text.Text?.Contains("更新后的手枪说明") == true),
                "new download-title records refresh an already open detail panel");
            window.MouseMove(new Point(2, 2)); PumpFrames();
            using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "readable-mod-controls-preview.png"));
            window.Close();
        }
        Complete(root.DisposeAsync().AsTask());
    }
}
