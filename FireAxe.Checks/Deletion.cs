using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
    private static void CheckDeletion(string output)
    {
        var directory = Path.Combine(output, "deletion-" + Guid.NewGuid().ToString("N")[..7]);
        Directory.CreateDirectory(directory);
        var root = new AddonRoot { DirectoryPath = directory, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        using var blocked = root.BlockAutoCheck();
        var group = AddonNode.Create<AddonGroup>(root); group.Name = "group";
        var child = AddonNode.Create<LocalVpkAddon>(root, group); child.Name = "child";
        Directory.CreateDirectory(Path.GetDirectoryName(child.VpkFilePath!)!);
        WriteVpk(child.VpkFilePath!, "Nested M4", "models/v_models/v_rifle.mdl");
        var duplicate = AddonNode.Create<LocalVpkAddon>(root); duplicate.Name = "duplicate";
        WriteVpk(duplicate.VpkFilePath!, "Duplicate selection", "models/v_models/v_rifle.mdl");
        using var explorer = new AddonNodeExplorerViewModel(root);
        var failures = new List<Exception>();
        using var observedErrors = explorer.DeleteCommand.ThrownExceptions.Subscribe(failures.Add);
        var operations = Array.Empty<(Task Task, string Name)>();
        using var confirmation = explorer.ConfirmDeleteInteraction.RegisterHandler(context => context.SetOutput(true));
        using var progress = explorer.ShowDeletionProgressInteraction.RegisterHandler(context =>
        {
            operations = context.Input.ToArray(); context.SetOutput(Unit.Default);
        });
        explorer.SelectNodes([group, child]);
        Exception? failure = null;
        try { Complete(explorer.DeleteCommand.Execute(true).ToTask()); }
        catch (Exception ex) { failure = ex; Console.WriteLine("Deletion regression: " + ex.Message); }
        Check(failure is null && failures.Count == 0, "deleting a selected parent and child does not read the invalid child's NodePath or fault the command");
        Complete(Task.WhenAll(operations.Select(operation => operation.Task)));
        Check(operations.Length == 1 && !group.IsValid && !child.IsValid,
            "a parent and selected descendants are deleted as one operation");
        explorer.SelectedNodes.Add(duplicate); explorer.SelectedNodes.Add(duplicate);
        Complete(explorer.DeleteCommand.Execute(true).ToTask()); Complete(Task.WhenAll(operations.Select(operation => operation.Task)));
        Check(operations.Length == 1 && !duplicate.IsValid && failures.Count == 0,
            "repeated references to one selected mod are deleted once without crashing");
        var failing = AddonNode.Create<FailingDeletionAddon>(root); failing.Name = "failure";
        explorer.SelectNode(failing);
        Complete(explorer.DeleteCommand.Execute(true).ToTask());
        Check(operations.Single().Task.IsFaulted && failing.IsValid && failures.Count == 0,
            "synchronous storage failures belong to deletion progress instead of crashing the reactive command");
        _ = operations.Single().Task.Exception;
        failing.Fail = false;

        var target = AddonNode.Create<LocalVpkAddon>(root); target.Name = "Delete only this M4"; target.CatalogTargetId = "m16";
        var other = AddonNode.Create<LocalVpkAddon>(root); other.Name = "Keep this M4"; other.CatalogTargetId = "m16";
        using var activation = explorer.Activator.Activate();
        var window = new Window { Width = 1400, Height = 800, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show(); PumpFrames();
        explorer.ModCatalog.LocateMod(target); explorer.SelectNode(other); PumpFrames();
        var confirmed = false; var requestedNames = ""; var confirmations = 0;
        using var rowConfirmation = explorer.ConfirmDeleteInteraction.RegisterHandler(context =>
        {
            requestedNames = explorer.DeletionNames; confirmations++; context.SetOutput(confirmed);
        });
        using var rowProgress = explorer.ShowDeletionProgressInteraction.RegisterHandler(context =>
        { operations = context.Input.ToArray(); context.SetOutput(Unit.Default); });
        var button = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "inlineDeleteModButton"
            && control.DataContext is VpkAddonViewModel model && model.Addon == target);
        Check(button.IsEffectivelyVisible && ReferenceEquals(button.Command, explorer.DeleteModCommand) && button.CommandParameter == target,
            "each inline mod has a direct delete button bound to that exact mod");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "deletion-inline-preview.png"));
        Click(); PumpFrames();
        Check(confirmations == 1 && requestedNames.Contains(target.Name) && !requestedNames.Contains(other.Name)
            && target.IsValid && explorer.SelectedNodes.SequenceEqual([other]) && explorer.ModCatalog.IsOverview,
            "row deletion confirms its own title and cancellation preserves other selections without opening details");
        confirmed = true; Click(); PumpFrames(); Complete(Task.WhenAll(operations.Select(operation => operation.Task)));
        Check(!target.IsValid && other.IsValid && explorer.SelectedNodes.SequenceEqual([other]) && explorer.ModCatalog.IsOverview,
            "confirmed row deletion removes only that mod and keeps the catalog and other selection open");
        var previous = confirmations;
        Complete(explorer.DeleteModCommand.Execute(target).ToTask());
        Check(confirmations == previous && other.IsValid, "deleting an already removed row is a harmless no-op");
        window.Close();
        Complete(root.DisposeAsync().AsTask());

        void Click()
        {
            button.BringIntoView(); PumpFrames();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        }
    }

    private sealed class FailingDeletionAddon : LocalVpkAddon
    {
        public bool Fail { get; set; } = true;
        protected override Task OnDestroyAsync() => Fail ? throw new IOException("Simulated deletion failure") : base.OnDestroyAsync();
    }
}
