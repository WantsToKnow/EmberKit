using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FireAxe;
using FireAxe.ViewModels;

internal static partial class Program
{
    private static void CheckUnifiedStorage(string output)
    {
        var game = Path.Combine(output, "unified-" + Guid.NewGuid().ToString("N")[..8]);
        var addons = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(Path.Combine(addons, "workshop"));
        Directory.CreateDirectory(Path.Combine(addons, "工坊下载(3)", "旧皮肤.workshop"));
        var pistolPath = Path.Combine(addons, "workshop", "3806596801.vpk");
        var scarPath = Path.Combine(addons, "工坊下载(3)", "旧皮肤.workshop", "3372750017_1.vpk");
        WriteVpk(pistolPath, "Pistol test appearance", "models/v_models/v_pistola.mdl");
        WriteVpk(scarPath, "SCAR test appearance", "models/v_models/v_desert_rifle.mdl");
        File.WriteAllText(Path.Combine(addons, "工坊下载(3)", "旧皮肤.workshop", ".workshop"), JsonSerializer.Serialize(new { PublishedFileId = 3372750017UL, TimeUpdated = 1, CurrentFile = "3372750017_1.vpk" }));
        File.Copy(pistolPath, Path.Combine(addons, "same-bytes.vpk"));
        var obsoleteLink = Path.Combine(addons, "fa_mod_obsolete.vpk");
        File.CreateSymbolicLink(obsoleteLink, Path.Combine(addons, "modebackup", new string('0', 64) + ".vpk"));
        var list = Path.Combine(game, "left4dead2", "addonlist.txt");
        File.WriteAllText(list, "\"AddonList\" { \"workshop/3806596801.vpk\" \"0\" \"工坊下载(3)/旧皮肤.workshop/3372750017_1.vpk\" \"0\" }");
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext() };
        LocalVpkAddon pistol;
        WorkshopVpkAddon scar;
        using (root.BlockAutoCheck())
        {
            var workshop = AddonNode.Create<AddonGroup>(root);
            workshop.Name = "workshop";
            workshop.IsEnabled = true;
            pistol = AddonNode.Create<LocalVpkAddon>(root, workshop);
            pistol.Name = "3806596801";
            pistol.IsEnabled = true;
            var batch = AddonNode.Create<AddonGroup>(root);
            batch.Name = "工坊下载(3)";
            batch.IsEnabled = true;
            scar = AddonNode.Create<WorkshopVpkAddon>(root, batch);
            scar.Name = "旧皮肤";
            scar.PublishedFileId = 3372750017;
            scar.IsEnabled = true;
        }
        var result = Complete(root.Storage.RefreshAsync());
        Check(result.Warnings.Length == 0 && result.Moved == 2 && result.DuplicatesDeleted == 1, "all nested/workshop VPK bodies are consolidated and byte-identical duplicates removed");
        Check(new FileInfo(obsoleteLink).LinkTarget is null, "dangling legacy bank links are removed without recreating nonexistent bodies");
        Check(Directory.EnumerateDirectories(addons).Select(Path.GetFileName).SequenceEqual(["modebackup"]), "addons contains no old workshop/batch/manager directories");
        Check(root.GetDescendants().OfType<VpkAddon>().Count() == 2 && pistol.ContentHash is not null && scar.ContentHash is not null, "one persistent mod record per body, including the legacy workshop item");
        Check(new[] { pistol, (VpkAddon)scar }.All(node => node.StorageFileName is { } name && ModStorage.IsBodyName(name) && name.Length == 11)
            && pistol.StorageFileName != scar.StorageFileName, "bank bodies have distinct short ASCII IDs independently of their complete SHA-256 identities");
        string Entry(VpkAddon node) => Path.Combine(addons, ModStorage.EntryName(node.ContentHash!));
        Check(Path.GetFileName(Entry(pistol)).Length == 32, "loading entries use short ASCII hash prefixes instead of complete SHA-256 filenames");
        var actualGameAddons = @"F:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons";
        CheckThrows<IOException>(() => ModStorage.ValidateEntryPath(Path.Combine(actualGameAddons, "mod_" + pistol.ContentHash!.ToLowerInvariant() + ".vpk")),
            "the former real-world 72-character entry is rejected because its metadata path is truncated by L4D2");
        ModStorage.ValidateEntryPath(Path.Combine(actualGameAddons, ModStorage.EntryName(pistol.ContentHash!)));
        Check(true, "the short entry fits the actual game installation's metadata path limit");
        Check(new FileInfo(Entry(pistol)).LinkTarget == Path.Combine("modebackup", pistol.StorageFileName!)
            && FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!), "new game entries are relative symbolic links without opting in");
        Check(FileSystemUtils.GetContentLength(Entry(pistol)) == FileSystemUtils.GetContentLength(pistol.VpkFilePath!) && pistol.FileSize > 0, "GUI sizes follow symbolic links to the real VPK content");
        Check(ReadGameEntries(list).GetValueOrDefault(Path.GetFileName(Entry(pistol))) == "1" && ReadGameEntries(list).GetValueOrDefault(Path.GetFileName(Entry(scar))) == "1", "manager-enabled pistol and SCAR overwrite stale disabled game-list entries");
        Check(pistol.IsGameSynchronized && scar.IsGameSynchronized, "green status is published only after links and the game list verify");
        var text = File.ReadAllText(Path.Combine(root.Storage.BackupPath, ModBackupIndex.FileName));
        Check(text.Contains("Pistol test appearance") && text.Contains("SCAR test appearance") && text.Contains("3372750017") && text.Contains(pistol.ContentHash!), "human-readable TXT retains titles, purpose, source IDs and hash mappings");
        Check(text.Contains("独立 ID：" + Path.GetFileNameWithoutExtension(pistol.StorageFileName)) && text.Contains("SHA-256：" + pistol.ContentHash), "TXT explicitly maps each short body ID to its complete hash");
        Check(Directory.EnumerateFiles(root.BackupDirectoryPath, "before-unified-*.addonroot").Any(), "a unique metadata and game-list snapshot precedes migration");

        File.Delete(Entry(pistol));
        Check(File.Exists(pistol.VpkFilePath), "deleting a game symbolic link leaves the backup body intact");
        Complete(root.Storage.RefreshAsync());
        Check(FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!), "rescan repairs a deleted game entry directly from the hash-backed record");
        pistol.IsEnabled = false;
        Pump(() => !File.Exists(Entry(pistol)) && pistol.IsGameSynchronized);
        Check(ReadGameEntries(list)[Path.GetFileName(Entry(pistol))] == "0" && File.Exists(pistol.VpkFilePath), "disable automatically removes the game entry and persists state without a manual push");
        pistol.IsEnabled = true;
        Pump(() => File.Exists(Entry(pistol)) && pistol.IsGameSynchronized);
        Check(ReadGameEntries(list)[Path.GetFileName(Entry(pistol))] == "1", "enable automatically recreates the entry and verifies the game list");

        // Simulate an entry made by a previous version; the new runtime must only create symlinks.
        using (root.BlockAutoCheck())
        {
            File.Delete(Entry(pistol));
            FileSystemUtils.CreateHardLink(Entry(pistol), pistol.VpkFilePath!);
        }
        using (var locked = File.Open(Entry(pistol), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CheckThrows<IOException>(() => Complete(root.Storage.RefreshAsync()), "an occupied legacy entry reports failed conversion rather than falling back");
            Check(new FileInfo(Entry(pistol)).LinkTarget is null && File.Exists(pistol.VpkFilePath)
                && !Directory.EnumerateFiles(addons, "*.tmp").Any() && !pistol.IsGameSynchronized,
                "failed conversion retains the old entry and physical body, removes temporary links and does not report success");
        }
        var symbolic = Complete(root.Storage.RefreshAsync());
        Check(symbolic.Warnings.Length == 0 && symbolic.RestoredLinks == 1 && new FileInfo(Entry(pistol)).LinkTarget == Path.Combine("modebackup", pistol.StorageFileName!)
            && FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!), "rescan converts a legacy hard link to a relative symbolic link pointing to the same verified body");
        Check(!AddonRoot.Serialize(root.CreateSave()).Contains("UseSymbolicLinks") && FileSystemUtils.GetContentLength(Entry(pistol)) == pistol.FileSize, "link type is no longer a stored preference and GUI content sizes stay correct");
        var symbolicAgain = Complete(root.Storage.RefreshAsync());
        Check(symbolicAgain.RestoredLinks == 0 && symbolicAgain.DuplicatesDeleted == 0, "unchanged symbolic entries are retained without repeated deletion or conversion");
        pistol.IsEnabled = false;
        Pump(() => !File.Exists(Entry(pistol)) && pistol.IsGameSynchronized);
        pistol.IsEnabled = true;
        Pump(() => File.Exists(Entry(pistol)) && pistol.IsGameSynchronized);
        Check(new FileInfo(Entry(pistol)).LinkTarget is not null && File.Exists(pistol.VpkFilePath), "disable and reenable always retain symbolic links without removing or copying the body");
        var again = Complete(root.Storage.RefreshAsync());
        Check(again.Moved == 0 && again.DuplicatesDeleted == 0 && again.DiscoveredMods == 0 && Directory.GetFiles(addons, "*.vpk").Length == 2, "repeated scans keep exactly one entry per enabled body");
        var stableName = pistol.StorageFileName;
        var journal = Path.Combine(root.Storage.BackupPath, "organizer-history.jsonl");
        var journalLength = new FileInfo(journal).Length;
        Complete(root.Storage.RefreshAsync());
        Check(pistol.StorageFileName == stableName && new FileInfo(journal).Length == journalLength, "unchanged rescans preserve short IDs and do not append redundant mapping records");

        var oldBody = Path.Combine(root.Storage.BackupPath, pistol.ContentHash + ".vpk");
        File.Move(pistol.VpkFilePath!, oldBody);
        Complete(root.Storage.RefreshAsync());
        Check(!File.Exists(oldBody) && ModStorage.IsBodyName(Path.GetFileName(pistol.VpkFilePath!)) && FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!), "a legacy full-hash bank body migrates to a short ID without changing bytes, game entry or enabled state");

        pistol.Name = "中文手枪显示名称";
        Complete(root.Storage.RefreshAsync());
        Check(File.ReadAllText(Path.Combine(root.Storage.BackupPath, ModBackupIndex.FileName)).Contains("管理器显示名称：中文手枪显示名称"), "TXT keeps the manager's Chinese display name as well as the original VPK title");
        var obsoleteLongEntry = Path.Combine(addons, "mod_" + pistol.ContentHash!.ToLowerInvariant() + ".vpk");
        FileSystemUtils.CreateHardLink(obsoleteLongEntry, pistol.VpkFilePath!);
        Complete(root.Storage.RefreshAsync());
        Check(!File.Exists(obsoleteLongEntry) && File.Exists(Entry(pistol)) && FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!),
            "rescan migrates the v3.0 overlong hard link to the short entry without changing its body");
        File.WriteAllText(list, "\"AddonList\" { }");
        Pump(() => pistol.IsGameSynchronized && ReadGameEntries(list).GetValueOrDefault(Path.GetFileName(Entry(pistol))) == "1");
        Check(ReadGameEntries(list)[Path.GetFileName(Entry(scar))] == "1", "external game-list rewrites are detected and reconciled with manager switches");

        var http = new SteamFixtureHandler(File.ReadAllBytes(scar.VpkFilePath!));
        using var client = new HttpClient(http);
        var downloads = new FixtureDownloads(File.ReadAllBytes(scar.VpkFilePath!));
        root.HttpClient = client;
        root.DownloadService = downloads;
        root.Check();
        Pump(() => root.CheckTask is null);
        Check(http.MetadataRequests == 0 && !Directory.Exists(Path.Combine(addons, "3372750017.workshop")), "Check does not request workshop metadata or recreate canceled workshop downloads");
        root.Save();
        var legacySave = Newtonsoft.Json.Linq.JObject.Parse(AddonRoot.Serialize(root.CreateSave()));
        legacySave["UseSymbolicLinks"] = false;
        var saved = AddonRoot.Deserialize(legacySave.ToString());
        var pistolHash = pistol.ContentHash;
        Complete(root.DisposeAsync().AsTask());
        root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), HttpClient = client, DownloadService = downloads };
        root.LoadSave(saved);
        Complete(root.Storage.RefreshAsync());
        pistol = root.GetDescendants().OfType<LocalVpkAddon>().Single(node => node.ContentHash == pistolHash);
        Check(http.MetadataRequests == 0 && new FileInfo(Entry(pistol)).LinkTarget is not null
            && FileSystemUtils.IsSameFile(Entry(pistol), pistol.VpkFilePath!), "reopening a legacy library ignores its old hard-link preference and keeps symbolic entries without network calls");
        Check(pistol.StorageFileName is { } savedBodyName && ModStorage.IsBodyName(savedBodyName), "short body filenames survive save and reopen");

        var bodyPath = pistol.VpkFilePath!;
        Complete(pistol.DestroyAsync());
        Complete(root.Storage.RefreshAsync());
        Complete(root.Storage.RefreshAsync());
        Check(File.Exists(bodyPath) && !File.Exists(Path.Combine(addons, ModStorage.EntryName(pistolHash!))) && root.GetDescendants().OfType<VpkAddon>().All(node => node.ContentHash != pistolHash), "explicit remove-keep-file is remembered and the orphan body is not resurrected on rescan");

        var incoming = Path.Combine(addons, "workshop", "555.vpk");
        Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
        WriteVpk(incoming, "Steam incoming AK", "models/v_models/v_rif_ak47.mdl");
        Complete(root.Storage.RefreshAsync());
        Check(!Directory.Exists(Path.GetDirectoryName(incoming)) && root.GetDescendants().OfType<VpkAddon>().Any(node => node.OriginalPaths.Contains("workshop" + Path.DirectorySeparatorChar + "555.vpk")), "new Steam workshop bodies enter the same bank without leaving workshop links");

        // A native process in this fixture's game directory exercises the real per-installation guard.
        var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var gameHost = Path.Combine(game, "left4dead2.exe");
        File.Copy(host, gameHost, true);
        using (var runningGame = Process.Start(new ProcessStartInfo(gameHost, "/d /c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!)
        {
            Pump(() => ModStorage.IsGameRunning(game));
            var active = root.GetDescendants().OfType<VpkAddon>().First(node => node.IsEnabled);
            var entry = Entry(active);
            active.IsEnabled = false;
            var pending = Complete(root.Storage.RefreshAsync());
            Check(pending.Pending && !active.IsGameSynchronized && File.Exists(entry) && active.DeploymentStatus.Contains("游戏运行中"), "while the game runs, switches remain visibly pending and loaded files are not modified");
            WorkshopVpkAddon failedDownload;
            using (root.BlockAutoCheck())
            {
                failedDownload = AddonNode.Create<WorkshopVpkAddon>(root);
                failedDownload.Name = "pending_failed";
                failedDownload.PublishedFileId = 2908110509;
            }
            var failedItem = new AddonNodeListItemViewModel(failedDownload);
            Check(failedItem.TargetSummary == "未安装 · 可重试下载或删除", "empty download placeholders describe their uninstalled state instead of waiting for VPK recognition");
            Complete(failedDownload.DestroyWithFileAsync());
            Check(!failedDownload.IsValid && !root.GetDescendants().Contains(failedDownload) && File.Exists(entry),
                "an empty failed download can be deleted during gameplay without touching mounted mod files");
            CheckThrows<InvalidOperationException>(() => Complete(active.DestroyWithFileAsync()),
                "deleting an installed mod during gameplay remains blocked");
            Check(active.IsValid && File.Exists(active.VpkFilePath) && File.Exists(entry),
                "a rejected installed-mod deletion keeps its record, stored body and mounted entry");
            runningGame.Kill(entireProcessTree: true); runningGame.WaitForExit();
            Pump(() => active.IsGameSynchronized && !File.Exists(entry));
            Check(ReadGameEntries(list)[Path.GetFileName(entry)] == "0", "pending switches automatically apply after the game exits");
        }
        File.Delete(gameHost);

        var downloadFixture = Path.Combine(game, "download-fixture.vpk");
        WriteVpk(downloadFixture, "Explicit download test", "models/survivors/survivor_gambler.mdl");
        var downloadBytes = File.ReadAllBytes(downloadFixture);
        using var downloadClient = new HttpClient(new SteamFixtureHandler(downloadBytes));
        var explicitDownloads = new FixtureDownloads(downloadBytes);
        root.HttpClient = downloadClient;
        root.DownloadService = explicitDownloads;
        WorkshopVpkAddon downloaded;
        using (root.BlockAutoCheck())
        {
            downloaded = AddonNode.Create<WorkshopVpkAddon>(root);
            downloaded.Name = "777";
            downloaded.PublishedFileId = 777;
            downloaded.IsEnabled = true;
        }
        Complete(downloaded.CheckDownloadAsync());
        Check(downloaded.ContentHash is not null && downloaded.VpkFilePath!.StartsWith(root.Storage.BackupPath)
            && new FileInfo(Entry(downloaded)).LinkTarget == Path.Combine("modebackup", downloaded.StorageFileName!), "explicit download validates then commits to the single bank and automatically enables its symbolic link");
        Check(!Directory.Exists(root.Storage.TemporaryPath) && !Directory.Exists(Path.Combine(addons, "777.workshop")), "successful downloads leave neither a temporary directory nor workshop wrapper folders");
        var previousHash = downloaded.ContentHash;
        explicitDownloads.CorruptNext = true;
        Complete(downloaded.CheckDownloadAsync());
        Check(downloaded.ContentHash == previousHash && File.ReadAllBytes(downloaded.VpkFilePath!).SequenceEqual(downloadBytes)
            && downloaded.Problems.OfType<AddonDownloadFailedProblem>().Any(), "a corrupt redownload preserves the previous verified body and reports failure");
        Check(WorkshopManifest.KnownId(downloaded) == 777, "hash filenames retain workshop identity for TXT export");

        var orphan = root.GetDescendants().OfType<VpkAddon>().First(node => !node.IsEnabled);
        var orphanHash = orphan.ContentHash;
        var bodyToRemove = orphan.VpkFilePath!;
        File.Delete(bodyToRemove);
        Complete(root.Storage.RefreshAsync());
        Check(root.GetDescendants().OfType<VpkAddon>().All(node => node.ContentHash != orphanHash), "a record is pruned when both its body and its disabled game entry are truly absent");

        var foreign = Path.Combine(addons, "foreign.vpk");
        File.CreateSymbolicLink(foreign, downloadFixture);
        var skipped = Complete(root.Storage.RefreshAsync());
        Check(skipped.Warnings.Any(warning => warning.Contains("addons 外部")) && File.Exists(downloadFixture) && new FileInfo(foreign).LinkTarget is not null,
            "incoming links outside addons are reported and neither external data nor the link is deleted");
        File.Delete(foreign);

        var collision = Path.Combine(addons, "collision.vpk");
        WriteVpk(collision, "Source that must survive", "models/v_models/v_smg_mp5.mdl");
        var collisionHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(collision)));
        var corruptBody = Path.Combine(root.Storage.BackupPath, collisionHash + ".vpk");
        File.WriteAllText(corruptBody, "damaged body fixture");
        var protectedResult = Complete(root.Storage.RefreshAsync());
        Check(protectedResult.Warnings.Length > 0 && File.Exists(collision) && new FileInfo(collision).LinkTarget is null,
            "a corrupt existing bank file cannot cause the incoming good original to be removed");
        Complete(root.DisposeAsync().AsTask());
    }
}
