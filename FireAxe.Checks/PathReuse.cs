using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private sealed class RememberedGamePath(string settingsFile) : IAddonRootParentSettings
    {
        public string GamePath
        {
            get => File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
            set => File.WriteAllText(settingsFile, value);
        }
        public bool IsAutoUpdateWorkshopItem => false;
        public VpkAddonConflictCheckSettings VpkAddonConflictCheckSettings => VpkAddonConflictCheckSettings.Default;
    }

    private static void CheckPathReuse(string output)
    {
        var fixture = Path.Combine(output, "path-reuse-" + Guid.NewGuid().ToString("N"));
        var firstGame = Path.Combine(fixture, "first-game");
        var secondGame = Path.Combine(fixture, "second-game");
        var firstAddons = Path.Combine(firstGame, "left4dead2", "addons");
        var secondWorkshop = Path.Combine(secondGame, "left4dead2", "addons", "workshop");
        Directory.CreateDirectory(Path.Combine(firstAddons, "workshop", "nested"));
        Directory.CreateDirectory(secondWorkshop);
        var exampleFile = Path.Combine(firstAddons, "workshop", "nested", "readme.txt");
        File.WriteAllText(exampleFile, "path inference sample");
        Check(GamePathUtils.FromPath(firstAddons + Path.DirectorySeparatorChar) == firstGame
            && GamePathUtils.FromPath(Path.Combine(firstGame, "left4dead2")) == firstGame
            && GamePathUtils.FromPath(exampleFile) == firstGame, "game root is inferred from addons with trailing slash, content directory and a nested file");
        Check(GamePathUtils.FromPath("addons") is null && GamePathUtils.FromPath(" ") is null, "inference rejects relative and empty paths");
        var settingsFile = Path.Combine(fixture, "saved-game-path.txt");
        var settings = new RememberedGamePath(settingsFile);
        var root = new AddonRoot { DirectoryPath = firstAddons, ParentSettings = settings, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        using var organizer = new WorkshopTransferViewModel(root, 3);
        using var backup = new WorkshopTransferViewModel(root, 3);
        Check(organizer.GamePath == firstGame && backup.GamePath == firstGame && settings.GamePath == firstGame,
            "organizer and backup prefill and persist the game root inferred from the existing library");
        var window = new WorkshopTransferWindow { DataContext = organizer };
        window.Show();
        PumpFrames();
        Check(window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.Text == firstGame), "organizer game directory textbox is filled before any action");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "organizer-autofill-preview.png"));
        Check(CommonMessageBoxes.GetStartDirectory(window) == firstAddons && CommonMessageBoxes.GetStartDirectory(window, secondWorkshop) == secondWorkshop,
            "pickers default to the current library and respect an explicit game/workshop starting directory");
        using (organizer.ChooseGameDirectory.RegisterHandler(context => context.SetOutput(secondWorkshop)))
        {
            ((System.Windows.Input.ICommand)organizer.ChooseGameCommand).Execute(null);
            Pump(() => !organizer.IsBusy && organizer.GamePath == secondGame);
        }
        Check(root.GamePath == secondGame && backup.GamePath == secondGame && settings.GamePath == secondGame,
            "choosing a nested game directory updates all open windows and the saved setting immediately");
        root.GamePath = firstGame;
        Check(organizer.GamePath == firstGame && backup.GamePath == firstGame, "changes arriving from the shared game setting refresh every directory field");
        organizer.GamePath = secondGame;
        var reopenedSettings = new RememberedGamePath(settingsFile);
        var reopenedRoot = new AddonRoot { DirectoryPath = firstAddons, ParentSettings = reopenedSettings, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        using (var reopened = new WorkshopTransferViewModel(reopenedRoot, 3))
            Check(reopened.GamePath == secondGame, "a reopened library prefers the saved game installation over a different library's inferred path");
        settings.GamePath = Path.Combine(fixture, "missing-game");
        var recoveredRoot = new AddonRoot { DirectoryPath = firstAddons, ParentSettings = settings, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        using (var recovered = new WorkshopTransferViewModel(recoveredRoot, 3))
            Check(recovered.GamePath == firstGame && settings.GamePath == firstGame, "a stale saved path is repaired using the current library location");
        organizer.GamePath = "F";
        Check(settings.GamePath == firstGame, "incomplete typed input does not overwrite the last valid saved game directory");
        Check(!Directory.EnumerateFiles(Path.Combine(firstAddons, "modebackup"), "*.vpk").Any() && !File.Exists(Path.Combine(firstGame, "left4dead2", "addonlist.txt")),
            "directory autofill does not start organization or edit the game loading list");
        window.Close();
        Complete(root.DisposeAsync().AsTask());
        Complete(reopenedRoot.DisposeAsync().AsTask());
        Complete(recoveredRoot.DisposeAsync().AsTask());
    }
}
