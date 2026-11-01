using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckProminentLaunch(Window window, AddonNodeExplorerViewModel explorer)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "launchGameButton");
        Check(button.IsEffectivelyVisible && button.Bounds.Width >= 220 && button.Bounds.Height >= 68
            && ReferenceEquals(button.Command, explorer.LaunchGameCommand),
            "the main window exposes a large launch button bound to the assistant launch action");
        var requests = 0;
        // Intercept the UI action: this check must never discover or launch an installed game.
        using var launch = explorer.LaunchGameInteraction.RegisterHandler(context =>
        {
            requests++;
            context.SetOutput(System.Reactive.Unit.Default);
        });
        var point = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Pump(() => requests == 1);
        Check(requests == 1 && !window.OwnedWindows.Any(),
            "one main-window click dispatches the assistant launch action; the check intercepts it without starting a game");
    }

    private static void CheckMainMenuReload(string output)
    {
        var game = Path.Combine(output, "reload-" + Guid.NewGuid().ToString("N")[..8]);
        var addons = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(addons);
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        LocalVpkAddon oldMod, newMod;
        using (root.BlockAutoCheck())
        {
            oldMod = AddonNode.Create<LocalVpkAddon>(root); oldMod.Name = "old-pistol"; oldMod.IsEnabled = true;
            newMod = AddonNode.Create<LocalVpkAddon>(root); newMod.Name = "new-mp5"; newMod.IsEnabled = false;
            WriteVpk(oldMod.VpkFilePath!, "旧手枪 [测试数据]", "models/v_models/v_pistola.mdl");
            WriteVpk(newMod.VpkFilePath!, "新 MP5 [测试数据]", "models/v_models/v_smg_mp5.mdl");
        }
        Complete(root.Storage.RefreshAsync());
        WorkshopVpkAddon failedDownload;
        using (root.BlockAutoCheck())
        {
            failedDownload = AddonNode.Create<WorkshopVpkAddon>(root);
            failedDownload.Name = "pending_failed";
            failedDownload.PublishedFileId = 2908110509;
            failedDownload.IsEnabled = true;
        }
        root.DownloadHistory.Record(new(2908110509, "失败的下载 [测试样本]", WorkshopDownloadOutcome.Failed, Error: "不是有效的求生之路 2 工坊作品。"));
        var oldEntry = Path.Combine(addons, ModStorage.EntryName(oldMod.ContentHash!));
        var newEntry = Path.Combine(addons, ModStorage.EntryName(newMod.ContentHash!));
        var before = Directory.GetFiles(root.Storage.BackupPath, "*.vpk").ToDictionary(file => Path.GetFileName(file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        var console = new GameConsole(game, Path.Combine(game, "private-settings"));
        var launch = console.PrepareLaunch(); // Inspect the URI, never start Steam or the game.
        var arguments = Uri.UnescapeDataString(launch.FileName);
        var port = int.Parse(Regex.Match(arguments, @"-netconport (\d+)").Groups[1].Value);
        var password = Regex.Match(arguments, @"-netconpassword ([A-F0-9]{64})").Groups[1].Value;
        Check(launch.UseShellExecute && arguments.StartsWith("steam://run/550//") && password.Length == 64,
            "managed launch prepares a Steam URI with a random console port and password, without starting a game in tests");
        Check(!Directory.EnumerateFiles(addons, "*.json", SearchOption.TopDirectoryOnly).Any(),
            "console credentials stay outside the game addon and backup folders");
        using var server = new FakeGameConsole(game, port, password, Path.GetFileName(oldEntry));
        using (root.BlockAutoCheck())
        {
            Complete(CheckNativeMountReadback());
            async Task CheckNativeMountReadback()
            {
                await using var connection = await console.ConnectAsync();
                var directories = await connection.ReadPathsAsync(CancellationToken.None);
                var mounted = await connection.ReadMountedAddonsAsync(CancellationToken.None);
                Check(!directories.Contains(".vpk", StringComparison.OrdinalIgnoreCase)
                    && mounted.SetEquals([GameConsole.Connection.NormalizePath(oldEntry)]),
                    "native directory-only path replies and VPK load-order replies are separate; official pak01 archives are excluded");
            }
            server.Commands.Clear();
            oldMod.IsEnabled = false;
            newMod.IsEnabled = true;
            server.WrongDirectory = true;
            CheckThrows<IOException>(() => Complete(root.Storage.ReloadInMainMenuAsync(console)),
                "a console serving another game directory is rejected before any deployment");
            Check(File.Exists(oldEntry) && !File.Exists(newEntry) && !server.Commands.Contains("disconnect"),
                "wrong-directory replies neither disconnect the game nor change VPK entries");
            server.WrongDirectory = false;
            server.RejectUnload = true;
            CheckThrows<IOException>(() => Complete(root.Storage.ReloadInMainMenuAsync(console)),
                "a rejected unload is not treated as a successful main-menu transition");
            Check(File.Exists(oldEntry) && !File.Exists(newEntry) && server.Commands.Contains("update_addon_paths") && !newMod.IsGameSynchronized,
                "an unload failure preserves the previous entries, attempts remount recovery, and stays unsynchronized");
            server.RejectUnload = false;
            server.Commands.Clear();
            var status = Complete(root.Storage.ReloadInMainMenuAsync(console));
            var commands = server.Commands.ToArray();
            Check(Array.IndexOf(commands, "disconnect") < Array.IndexOf(commands, "unload_all_addons")
                && Array.IndexOf(commands, "unload_all_addons") < Array.IndexOf(commands, "update_addon_paths"),
                "reload explicitly disconnects before unloading and applies entries before remounting");
            Check(!File.Exists(oldEntry) && FileSystemUtils.IsSameFile(newEntry, newMod.VpkFilePath!)
                && new FileInfo(newEntry).LinkTarget == Path.Combine("modebackup", newMod.StorageFileName!),
                "main-menu reload always creates relative symbolic VPK entries without a link-format setting");
            Check(oldMod.IsGameSynchronized && newMod.IsGameSynchronized && status.Contains("游戏已确认"),
                "success requires the native VPK load-order receipt to contain enabled entries and exclude disabled ones");
            Check(failedDownload.IsValid && failedDownload.ContentHash is null && status.Contains("跳过 1 个未安装"),
                "an uninstalled failed download does not block main-menu deployment or create a game entry");
            Check(before.All(pair => File.Exists(Path.Combine(root.Storage.BackupPath, pair.Key!))
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root.Storage.BackupPath, pair.Key!)))) == pair.Value),
                "all stored VPK filenames and SHA-256 contents survive main-menu reload unchanged");

            oldMod.IsEnabled = true;
            newMod.IsEnabled = false;
            server.IgnoreMount = true;
            CheckThrows<IOException>(() => Complete(root.Storage.ReloadInMainMenuAsync(console)),
                "a completed command without the requested VPK in game load order is a failure");
            Check(!oldMod.IsGameSynchronized && root.Storage.Status.Contains("回执") && server.Commands.Count(command => command == "update_addon_paths") >= 3,
                "a bad mount receipt cannot publish green success and triggers recovery");
            server.IgnoreMount = false;
            Complete(root.Storage.ReloadInMainMenuAsync(console));
            Check(File.Exists(oldEntry) && !File.Exists(newEntry) && oldMod.IsGameSynchronized,
                "retry recovers from a failed receipt using the latest enabled state");

            using var interrupted = new CancellationTokenSource();
            server.AfterUnload = interrupted.Cancel;
            var updatesBeforeCancel = server.Commands.Count(command => command == "update_addon_paths");
            CheckThrows<OperationCanceledException>(() => Complete(root.Storage.ReloadInMainMenuAsync(console, interrupted.Token)),
                "cancelling after unload interrupts the deployment rather than reporting success");
            Check(server.Commands.Count(command => command == "update_addon_paths") == updatesBeforeCancel + 1
                && File.Exists(oldEntry) && !File.Exists(newEntry),
                "cancelling after unload still restores addon paths and preserves pre-deployment entries");
            server.AfterUnload = null;

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            CheckThrows<OperationCanceledException>(() => Complete(root.Storage.ReloadInMainMenuAsync(console, cancelled.Token)),
                "cancelled reload does not start a game transaction");
            using var model = new WorkshopTransferViewModel(root, 5);
            var window = new WorkshopTransferWindow { ViewModel = model };
            window.Show(); PumpFrames();
            Check(window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "applyMainMenuButton"
                    && button.Content as string == "返回主菜单并应用 Mod" && button.Bounds.Width >= 320 && button.Bounds.Height >= 60
                    && ReferenceEquals(button.Command, model.ApplyInMainMenuCommand))
                && window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("会断开当前地图或服务器") == true)
                && ((ICommand)model.ApplyInMainMenuCommand).CanExecute(null),
                "the reload tab exposes a large return-and-apply button bound to the existing action and explains its disconnect behavior");
            using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "main-menu-reload-preview.png"));
            window.Close();
        }
        Complete(root.DisposeAsync().AsTask());
    }

    private sealed class FakeGameConsole : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        public readonly ConcurrentQueue<string> Commands = new();
        public bool WrongDirectory, RejectUnload, IgnoreMount;
        public Action? AfterUnload;

        public FakeGameConsole(string game, int port, string password, string initialEntry)
        {
            _listener = new(IPAddress.Loopback, port);
            _listener.Start();
            _task = Task.Run(async () =>
            {
                var mounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { initialEntry };
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        using var reader = new StreamReader(client.GetStream());
                        await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        var authenticated = false;
                        while (await reader.ReadLineAsync(_stop.Token) is { } command)
                        {
                            if (command == "PASS " + password) { authenticated = true; continue; }
                            if (!authenticated) break;
                            if (command.StartsWith("echo ")) { await writer.WriteLineAsync(command[5..]); continue; }
                            Commands.Enqueue(command);
                            switch (command)
                            {
                                case "path":
                                    // Actual L4D2 `path` output omits every mounted VPK archive.
                                    await writer.WriteLineAsync("---------------\nPath ID:             File Path:");
                                    await writer.WriteLineAsync("GAME                 \"" + (WrongDirectory ? "X:/OtherGame/left4dead2/" : Path.Combine(game, "left4dead2") + Path.DirectorySeparatorChar) + "\"");
                                    break;
                                case "show_addon_load_order":
                                    foreach (var entry in mounted) await writer.WriteLineAsync(Path.Combine(game, "left4dead2", "addons", entry).ToLowerInvariant());
                                    foreach (var folder in new[] { "update", "left4dead2_dlc3", "left4dead2" })
                                        await writer.WriteLineAsync(Path.Combine(game, folder, "pak01_dir.vpk").ToLowerInvariant());
                                    break;
                                case "unload_all_addons":
                                    if (RejectUnload) await writer.WriteLineAsync("Can't unload addons while connected to a server");
                                    else mounted.Clear();
                                    AfterUnload?.Invoke();
                                    break;
                                case "update_addon_paths":
                                    if (!IgnoreMount) mounted = ReadGameEntries(GamePathUtils.GetAddonListPath(game))
                                        .Where(pair => pair.Value == "1").Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                                    // Model the engine removing entries for missing disabled VPKs.
                                    if (!IgnoreMount) File.WriteAllText(GamePathUtils.GetAddonListPath(game),
                                        "\"AddonList\" { " + string.Join(" ", mounted.Select(entry => $"\"{entry}\" \"1\"")) + " }");
                                    break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _task.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
