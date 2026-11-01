using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckLinkedSizesAndSelection(string output)
    {
        var directory = Path.Combine(output, "linked-sizes-" + Guid.NewGuid().ToString("N"));
        var root = new AddonRoot { DirectoryPath = directory, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        var body = Path.Combine(directory, "body.vpk");
        WriteVpk(body, "Linked AK appearance", "models/v_models/v_rif_ak47.mdl");
        var bytes = new FileInfo(body).Length;
        LocalVpkAddon local, chained, broken, empty;
        using (root.BlockAutoCheck())
        {
            local = AddonNode.Create<LocalVpkAddon>(root);
            local.Name = "1415525327";
            File.CreateSymbolicLink(local.FullFilePath, body);
            chained = AddonNode.Create<LocalVpkAddon>(root);
            chained.Name = "145079100";
            File.CreateSymbolicLink(chained.FullFilePath, local.FullFilePath);
            broken = AddonNode.Create<LocalVpkAddon>(root);
            broken.Name = "broken_link";
            File.CreateSymbolicLink(broken.FullFilePath, Path.Combine(directory, "missing.vpk"));
            empty = AddonNode.Create<LocalVpkAddon>(root);
            empty.Name = "empty";
            File.WriteAllBytes(empty.FullFilePath, []);
            foreach (var node in root.Nodes) node.CheckFiles();
        }
        Check(local.FileSize == bytes && chained.FileSize == bytes && FileSystemUtils.GetContentLength(body) == bytes, "ordinary VPKs, symbolic links and chained links all report the content length");
        Check(broken.FileSize is null && broken.Problems.OfType<AddonFileMissingProblem>().Any() && empty.FileSize == 0, "a broken link reports a missing file and unknown size, distinct from a real empty file");
        Complete(Task.WhenAll(local.CheckVpkValidity(), chained.CheckVpkValidity()));
        using var explorer = new AddonNodeExplorerViewModel(root) { ListItemViewKind = AddonNodeListItemViewKind.Grid };
        using var activation = explorer.Activator.Activate();
        var window = new Window { Width = 1300, Height = 750, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show();
        Pump(() => !explorer.IsSearching && explorer.CurrentNodes.Count == 4);
        PumpFrames();
        Check(window.GetVisualDescendants().OfType<ListBox>().Count(list => ReferenceEquals(list.ItemsSource, explorer.CurrentNodes)) == 1, "tile and grid layouts use one mod list and one selection model");
        var readableSize = new FireAxe.ValueConverters.ReadableBytesConverter().Convert(bytes, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string;
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == readableSize), "the actual list row renders the target size instead of 0 B");
        explorer.SelectNodes([local, chained, local]);
        PumpFrames();
        Check(explorer.SelectedNodes.Count == 2 && explorer.SelectedNodes.Distinct().Count() == 2, "two selected mods cannot become three entries through duplicate selection input");
        foreach (var kind in new[] { AddonNodeListItemViewKind.MediumTile, AddonNodeListItemViewKind.Grid, AddonNodeListItemViewKind.SmallTile, AddonNodeListItemViewKind.Grid })
        {
            explorer.ListItemViewKind = kind;
            PumpFrames();
        }
        explorer.SortMethod = AddonNodeSortMethod.FileSize;
        Pump(() => !explorer.IsSearching);
        PumpFrames();
        Check(explorer.SelectedNodes.Count == 2 && explorer.SelectedNodes.Distinct().Count() == 2, "changing layouts and refreshing a sorted list preserve two unique selections");
        explorer.SelectNode(local);
        var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsEffectivelyVisible && item.DataContext == chained);
        var point = row.TranslatePoint(new Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        PumpFrames();
        Check(explorer.SelectedNodes.Count == 1 && explorer.SelectedNodes[0] == chained, "clicking a row after a layout change leaves one selected mod and opens its details");
        WriteVpk(body, "A longer updated title for the linked AK appearance", "models/v_models/v_rif_ak47.mdl");
        local.CheckFiles();
        Check(local.FileSize == new FileInfo(body).Length && local.FileSize != bytes, "refreshing a linked mod updates its size after the target content changes");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "linked-size-selection-preview.png"));
        window.Close();
        Complete(root.DisposeAsync().AsTask());
    }
}
