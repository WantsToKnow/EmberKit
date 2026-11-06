using System.Security.Cryptography;
using FireAxe;

internal static partial class Program
{
    private static void CheckReadableStorage(string output)
    {
        var game = Path.Combine(output, "named-" + Guid.NewGuid().ToString("N")[..6]);
        var addons = Path.Combine(game, "left4dead2", "addons"); Directory.CreateDirectory(addons);
        var firstSource = Path.Combine(game, "first.vpk"); var secondSource = Path.Combine(game, "second.vpk");
        WriteVpk(firstSource, "同名枪皮", "models/v_models/v_rif_ak47.mdl");
        WriteVpk(secondSource, "同名枪皮", "models/v_models/v_rif_ak47.mdl", "sound/variant.wav");
        var firstBytes = File.ReadAllBytes(firstSource); var secondBytes = File.ReadAllBytes(secondSource);
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = scheduler };
        var imported = Complete(root.Storage.ImportLocalVpkAsync([firstSource, secondSource]));
        var first = imported.Imported[0]; var second = imported.Imported[1];
        Check(first.StorageFileName == "AK-47 - 同名枪皮.vpk" && second.StorageFileName == "AK-47 - 同名枪皮 (2).vpk",
            "readable body names describe the replaced object and title; same-title different content gets a numbered filename");
        Check(File.ReadAllBytes(first.VpkFilePath!).SequenceEqual(firstBytes) && File.ReadAllBytes(second.VpkFilePath!).SequenceEqual(secondBytes)
            && new FileInfo(first.VpkFilePath!).LinkTarget is null && new FileInfo(second.VpkFilePath!).LinkTarget is null,
            "readable files in modebackup are unchanged physical VPK bodies");
        var index = Path.Combine(root.Storage.BackupPath, ModBackupIndex.FileName);
        Check(File.ReadAllText(index).Contains("备份本体：" + Path.Combine("modebackup", first.StorageFileName!))
            && File.ReadAllText(index).Contains("SHA-256：" + first.ContentHash),
            "filename-hash pairs are written to TXT immediately on import, before game application");
        Check(!first.IsGameSynchronized && !File.Exists(Path.Combine(addons, ModStorage.EntryName(first.ContentHash!))),
            "readable naming preserves pending-only imports and does not automatically create game entries");
        Complete(root.Storage.RefreshAsync());
        var entry = Path.Combine(addons, ModStorage.EntryName(first.ContentHash!));
        Check(Path.GetFileName(entry).Length == 32 && new FileInfo(entry).LinkTarget == Path.Combine("modebackup", first.StorageFileName!)
            && FileSystemUtils.IsSameFile(entry, first.VpkFilePath!),
            "game entries retain short hash names and relative links even when their targets have Chinese descriptive names");
        first.IsEnabled = false; first.CatalogTargetId = "m16"; first.Priority = 17;
        var duplicate = Complete(root.Storage.ImportLocalVpkAsync([firstSource]));
        Check(duplicate.Existing.Single() == first && !first.IsEnabled && first.Priority == 17 && first.CatalogTargetId == "m16"
            && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
            "TXT-backed deduplication reuses the exact body and preserves switches, priority and manual tags");
        var name = first.StorageFileName;
        Complete(root.Storage.RefreshAsync()); Complete(root.Storage.RefreshAsync());
        Check(first.StorageFileName == name && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
            "repeated explicit scans preserve readable names without growing suffixes");
        root.Save(); var saved = root.CreateSave();
        Complete(root.DisposeAsync().AsTask());
        root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = scheduler };
        root.LoadSave(saved); root.Storage.CheckDeployment();
        first = root.GetDescendants().OfType<VpkAddon>().Single(node => node.ContentHash == Convert.ToHexString(SHA256.HashData(firstBytes)));
        Check(first.StorageFileName == name && !first.IsEnabled && first.Priority == 17 && first.CatalogTargetId == "m16" && File.Exists(first.VpkFilePath),
            "readable filenames and all existing mod choices survive save and reopen");
        Complete(root.DisposeAsync().AsTask());

        File.Delete(Path.Combine(addons, "modebackup", "organizer-history.jsonl"));
        File.Delete(Path.Combine(addons, AddonRoot.SaveFileName));
        root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = scheduler };
        var recovered = Complete(root.Storage.ImportLocalVpkAsync([firstSource]));
        Check(recovered.Errors.Length == 0 && recovered.Imported.Single().StorageFileName == name
            && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
            "TXT alone locates and verifies a duplicate body when library nodes and the JSON journal are unavailable");
        Complete(root.DisposeAsync().AsTask());

        var badGame = Path.Combine(output, "bad-index-" + Guid.NewGuid().ToString("N")[..5]);
        var badAddons = Path.Combine(badGame, "left4dead2", "addons"); var badBank = Path.Combine(badAddons, "modebackup");
        Directory.CreateDirectory(badBank);
        var goodBody = Path.Combine(badBank, "原文件.vpk"); File.WriteAllBytes(goodBody, firstBytes);
        File.WriteAllText(Path.Combine(badBank, ModBackupIndex.FileName),
            "SHA-256：" + Convert.ToHexString(SHA256.HashData(secondBytes)) + "\n备份本体：modebackup/原文件.vpk\n");
        root = new AddonRoot { DirectoryPath = badAddons, GamePath = badGame, TaskScheduler = scheduler };
        var refused = Complete(root.Storage.ImportLocalVpkAsync([secondSource]));
        Check(refused.Imported.Length == 0 && refused.Errors.Length == 1 && File.ReadAllBytes(goodBody).SequenceEqual(firstBytes)
            && File.ReadAllBytes(secondSource).SequenceEqual(secondBytes),
            "a false TXT hash cannot overwrite or delete either the existing body or the incoming original");
        Complete(root.DisposeAsync().AsTask());

        Check(ModStorage.IsBodyName("AK-47 - 中文枪皮.vpk") && !ModStorage.IsBodyName("../outside.vpk")
            && !ModStorage.IsBodyName("CON.vpk") && !ModStorage.IsBodyName("CON.backup.vpk")
            && !ModStorage.IsBodyName("bad:name.vpk") && !ModStorage.IsBodyName("bad .vpk"),
            "readable body validation accepts names with meaning while rejecting traversal and invalid Windows filenames");
        var longGame = Path.Combine(output, "long-name-" + Guid.NewGuid().ToString("N")[..5]);
        var longAddons = Path.Combine(longGame, "left4dead2", "addons"); Directory.CreateDirectory(longAddons);
        var longSource = Path.Combine(longGame, "long.vpk"); WriteVpk(longSource, new string('a', 99) + ".bb", "custom/probe.bin");
        root = new AddonRoot { DirectoryPath = longAddons, GamePath = longGame, TaskScheduler = scheduler };
        var longBody = Complete(root.Storage.ImportLocalVpkAsync([longSource])).Imported.Single();
        Check(longBody.StorageFileName == new string('a', 99) + ".vpk" && File.Exists(longBody.VpkFilePath),
            "long titles are shortened without leaving an invalid trailing dot or losing the saved file mapping");
        Complete(root.DisposeAsync().AsTask());
        var migration = Path.Combine(output, "old-name-" + Guid.NewGuid().ToString("N")[..5]);
        var migrationAddons = Path.Combine(migration, "left4dead2", "addons"); var bank = Path.Combine(migrationAddons, "modebackup");
        Directory.CreateDirectory(bank); File.WriteAllBytes(Path.Combine(bank, "m000123.vpk"), firstBytes);
        var hash = Convert.ToHexString(SHA256.HashData(firstBytes)); var id = Guid.NewGuid();
        root = new AddonRoot { DirectoryPath = migrationAddons, GamePath = migration, TaskScheduler = scheduler };
        root.LoadSave(new() { Nodes = [new LocalVpkAddonSave { Id = id, Name = "旧枪皮", ContentHash = hash,
            StorageFileName = "m000123.vpk", IsEnabled = false, Priority = 23, CatalogTargetId = "m16" }] });
        root.Storage.CheckDeployment();
        Check(File.Exists(Path.Combine(bank, "m000123.vpk")), "opening an old library does not rename or apply anything automatically");
        Complete(root.Storage.RefreshAsync());
        var migrated = root.GetDescendants().OfType<VpkAddon>().Single();
        Check(migrated.Id == id && migrated.StorageFileName == "AK-47 - 同名枪皮.vpk" && migrated.ContentHash == hash
            && !migrated.IsEnabled && migrated.Priority == 23 && migrated.CatalogTargetId == "m16"
            && !File.Exists(Path.Combine(bank, "m000123.vpk")) && File.ReadAllBytes(migrated.VpkFilePath!).SequenceEqual(firstBytes),
            "explicit migration renames legacy bodies without changing identity, bytes, enabled choices, priority or tags");
        root.Save(); Complete(root.DisposeAsync().AsTask());
        File.WriteAllText(Path.Combine(output, "readable-rollback-game.txt"), migration);
    }
}
