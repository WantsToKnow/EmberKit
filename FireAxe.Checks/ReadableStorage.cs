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
        Check(first.StorageFileName == "0.vpk" && second.StorageFileName == "1.vpk",
            "same-title different VPKs receive consecutive numeric IDs from zero");
        Check(File.ReadAllBytes(first.VpkFilePath!).SequenceEqual(firstBytes) && File.ReadAllBytes(second.VpkFilePath!).SequenceEqual(secondBytes)
            && new FileInfo(first.VpkFilePath!).LinkTarget is null && new FileInfo(second.VpkFilePath!).LinkTarget is null,
            "numbered files in modebackup are unchanged physical VPK bodies");
        var index = Path.Combine(root.Storage.BackupPath, ModBackupIndex.FileName);
        var importedIndex = File.ReadAllText(index).ReplaceLineEndings("\n");
        Check(importedIndex.Contains($"数字编号：0\n本体文件名：0.vpk\n可读名称：同名枪皮\nSHA-256：{first.ContentHash}\n备份本体：modebackup\\0.vpk")
            && importedIndex.Contains($"数字编号：1\n本体文件名：1.vpk\n可读名称：同名枪皮\nSHA-256：{second.ContentHash}"),
            "TXT immediately maps each numeric body ID to its title and actual complete hash");
        Check(!first.IsGameSynchronized && !File.Exists(Path.Combine(addons, ModStorage.EntryName(first.ContentHash!))),
            "numbered imports stay pending and do not automatically create game entries");
        Complete(root.Storage.RefreshAsync());
        var entry = Path.Combine(addons, ModStorage.EntryName(first.ContentHash!));
        Check(Path.GetFileName(entry).Length == 32 && new FileInfo(entry).LinkTarget == Path.Combine("modebackup", first.StorageFileName!)
            && FileSystemUtils.IsSameFile(entry, first.VpkFilePath!),
            "game entries retain short hash names and relative links to numbered bodies");
        Check(WorkshopManifest.KnownId(second) is null,
            "the numbered body and its recorded bank path cannot be mistaken for a Workshop ID");
        first.IsEnabled = false; first.CatalogTargetId = "m16"; first.Priority = 17;
        var duplicate = Complete(root.Storage.ImportLocalVpkAsync([firstSource]));
        Check(duplicate.Existing.Single() == first && !first.IsEnabled && first.Priority == 17 && first.CatalogTargetId == "m16"
            && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
            "TXT-backed deduplication reuses the exact body and preserves switches, priority and manual tags");
        var name = first.StorageFileName;
        Complete(root.Storage.RefreshAsync()); Complete(root.Storage.RefreshAsync());
        Check(first.StorageFileName == name && Directory.GetFiles(root.Storage.BackupPath, "*.vpk").Length == 2,
            "repeated explicit scans preserve assigned numeric IDs");
        root.Save(); var saved = root.CreateSave();
        Complete(root.DisposeAsync().AsTask());
        root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = scheduler };
        root.LoadSave(saved); root.Storage.CheckDeployment();
        first = root.GetDescendants().OfType<VpkAddon>().Single(node => node.ContentHash == Convert.ToHexString(SHA256.HashData(firstBytes)));
        Check(first.StorageFileName == name && !first.IsEnabled && first.Priority == 17 && first.CatalogTargetId == "m16" && File.Exists(first.VpkFilePath),
            "numeric IDs and all existing mod choices survive save and reopen");
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
            "legacy readable names remain loadable while traversal and invalid Windows filenames are rejected");
        var longGame = Path.Combine(output, "long-name-" + Guid.NewGuid().ToString("N")[..5]);
        var longAddons = Path.Combine(longGame, "left4dead2", "addons"); Directory.CreateDirectory(longAddons);
        var longSource = Path.Combine(longGame, "long.vpk"); var longTitle = new string('a', 99) + ".bb";
        WriteVpk(longSource, longTitle, "custom/probe.bin");
        root = new AddonRoot { DirectoryPath = longAddons, GamePath = longGame, TaskScheduler = scheduler };
        var longBody = Complete(root.Storage.ImportLocalVpkAsync([longSource])).Imported.Single();
        Check(longBody.StorageFileName == "0.vpk" && File.Exists(longBody.VpkFilePath)
            && File.ReadAllText(Path.Combine(root.Storage.BackupPath, ModBackupIndex.FileName)).Contains("可读名称：" + longTitle),
            "long titles stay complete in TXT while the physical filename remains numeric");
        Complete(root.DisposeAsync().AsTask());
        var migration = Path.Combine(output, "old-name-" + Guid.NewGuid().ToString("N")[..5]);
        var migrationAddons = Path.Combine(migration, "left4dead2", "addons"); var bank = Path.Combine(migrationAddons, "modebackup");
        const string oldName = "AK-47 - 旧中文名称.vpk";
        const string oldNumber = "m000123.vpk";
        Directory.CreateDirectory(bank);
        File.WriteAllBytes(Path.Combine(bank, oldName), firstBytes);
        File.WriteAllBytes(Path.Combine(bank, oldNumber), secondBytes);
        WriteVpk(Path.Combine(bank, "0.vpk"), "已有编号", "models/v_models/v_smg_mp5.mdl");
        var hash = Convert.ToHexString(SHA256.HashData(firstBytes)); var id = Guid.NewGuid();
        var secondHash = Convert.ToHexString(SHA256.HashData(secondBytes));
        root = new AddonRoot { DirectoryPath = migrationAddons, GamePath = migration, TaskScheduler = scheduler };
        root.LoadSave(new() { Nodes =
        [
            new LocalVpkAddonSave { Id = id, Name = "旧枪皮", ContentHash = hash,
                StorageFileName = oldName, IsEnabled = false, Priority = 23, CatalogTargetId = "m16" },
            new LocalVpkAddonSave { Id = Guid.NewGuid(), Name = "原工坊枪皮", ContentHash = secondHash,
                StorageFileName = oldNumber, IsEnabled = true, OriginalPaths = [Path.Combine("workshop", "3372750017.vpk")] }
        ] });
        root.Storage.CheckDeployment();
        Check(File.Exists(Path.Combine(bank, oldName)) && File.Exists(Path.Combine(bank, oldNumber)),
            "opening an old library leaves its legacy body names in place");
        Complete(root.Storage.RefreshAsync());
        var migrated = root.GetDescendants().OfType<VpkAddon>().Single(node => node.Id == id);
        var migratedNumber = root.GetDescendants().OfType<VpkAddon>().Single(node => node.ContentHash == secondHash);
        var migratedIndex = File.ReadAllText(Path.Combine(bank, ModBackupIndex.FileName)).ReplaceLineEndings("\n");
        Check(migrated.StorageFileName == "1.vpk" && migrated.ContentHash == hash
            && !migrated.IsEnabled && migrated.Priority == 23 && migrated.CatalogTargetId == "m16"
            && !File.Exists(Path.Combine(bank, oldName)) && File.ReadAllBytes(migrated.VpkFilePath!).SequenceEqual(firstBytes)
            && migratedIndex.Contains($"数字编号：1\n本体文件名：1.vpk\n可读名称：{Path.GetFileNameWithoutExtension(oldName)}\nSHA-256：{hash}")
            && migratedNumber.StorageFileName == "2.vpk" && File.Exists(Path.Combine(bank, "0.vpk"))
            && !File.Exists(Path.Combine(bank, oldNumber)) && WorkshopManifest.KnownId(migratedNumber) == 3372750017,
            "migration preserves the existing ID, former Chinese name, body bytes, choices and real Workshop provenance");
        root.Save(); Complete(root.DisposeAsync().AsTask());
        File.WriteAllText(Path.Combine(output, "readable-rollback-game.txt"), migration);
    }
}
