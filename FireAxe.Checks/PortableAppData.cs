using Avalonia;
using FireAxe;

internal static partial class Program
{
    private static void CheckPortableAppData(string output)
    {
        var previousDirectory = Environment.CurrentDirectory;
        var unrelatedDirectory = Path.Combine(output, "unrelated-working-directory");
        var game = Path.Combine(output, "portable-game");
        Directory.CreateDirectory(unrelatedDirectory);
        Directory.CreateDirectory(Path.Combine(game, "left4dead2", "addons"));
        Environment.CurrentDirectory = unrelatedDirectory;
        try
        {
            var app = (App)Application.Current!;
            var expected = Path.Combine(AppContext.BaseDirectory, "data");
            Check(FileSystemUtils.IsSamePath(app.DataDirectoryPath, expected),
                "portable app state is anchored to the executable directory even when launched from elsewhere");
            using var settings = new AppSettings(app);
            settings.GamePath = game;
            settings.Save();
            Check(settings.SettingsFilePath == Path.Combine(expected, "Settings.json") && File.Exists(settings.SettingsFilePath),
                "application settings are saved beside the executable instead of the user's Documents folder");
            using var reloaded = new AppSettings(app);
            Check(FileSystemUtils.IsSamePath(reloaded.GamePath, game) && reloaded.CustomVpkAddonConflictIgnoringFilesDirectoryPath.StartsWith(expected + Path.DirectorySeparatorChar),
                "saved settings reload locally and custom conflict rules also stay inside portable data");
            Check(AppDataPaths.LogDirectory.StartsWith(expected + Path.DirectorySeparatorChar)
                && AppDataPaths.CrashDirectory.StartsWith(expected + Path.DirectorySeparatorChar),
                "logs and crash-report temporary files remain inside the software data directory");
            var console = new GameConsole(game);
            var launch = console.PrepareLaunch(); // Prepare only; never launch a real game.
            Check(Directory.EnumerateFiles(AppDataPaths.GameConsoleDirectory, "*.json").Any()
                && launch.FileName.StartsWith("steam://run/550//")
                && !Directory.EnumerateFileSystemEntries(unrelatedDirectory).Any(),
                "game connection settings use portable data without creating files in the launch directory");
            Check(CommonVpkAddonConflictIgnoringFiles.HalfLife2.Count > 0,
                "bundled conflict rules are found independently of the launch directory");
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }
}
