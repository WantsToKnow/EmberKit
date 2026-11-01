using System.Net;
using System.Security.Cryptography;
using FireAxe;

internal static partial class Program
{
    private static void CheckLiveWorkshop(string output)
    {
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:7890"), UseProxy = true })
        { Timeout = TimeSpan.FromSeconds(12) };
        var service = new DownloadService(new() { Proxy = new WebProxy("http://127.0.0.1:7890") });
        var root = new AddonRoot
        {
            TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            DirectoryPath = Path.Combine(output, "live-steam-" + Guid.NewGuid().ToString("N")),
            HttpClient = client,
            DownloadService = service
        };
        var parsed = WorkshopManifest.Parse("https://steamcommunity.com/sharedfiles/filedetails/?id=1103089137\t0\t4\tBlue sight");
        var plan = Complete(WorkshopManifest.ResolveDownloadsAsync(parsed.Entries, client, default));
        Check(plan.Items.Length == 1 && plan.Items[0].File.Title == "Blue sight", "LIVE: official Steam API resolves an actual L4D2 item");
        var queued = WorkshopManifest.QueueDownloads(root, plan);
        var addon = queued.Added.Single();
        Pump(() => addon.Inspection is not null || addon.Problems.Any(problem => problem is AddonDownloadFailedProblem));
        if (addon.VpkFilePath is null)
        {
            var failure = addon.Problems.OfType<AddonDownloadFailedProblem>().FirstOrDefault();
            throw new Exception("Live Steam download failed: " + failure?.Exception?.Message);
        }
        Check(new FileInfo(addon.VpkFilePath).Length == 109181 && addon.Inspection is { Error: null }, "LIVE: existing download queue retrieves and validates the 109181-byte VPK");
        Check(Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(addon.VpkFilePath))) == "C5608BAE6133EFACDCA74CACC393BB3A2A4F051C", "LIVE: downloaded bytes match Steam CDN content hash");
        var exported = Complete(WorkshopManifest.ExportAsync(root.GetDescendants(), client, false, null, default));
        var text = WorkshopManifest.Serialize(exported.Entries, exported.Skipped);
        File.WriteAllText(Path.Combine(output, "live-steam-roundtrip.txt"), text);
        var restored = WorkshopManifest.Parse(text).Entries.Single();
        Check(restored.Id == 1103089137 && !restored.Enabled && restored.Priority == 4, "LIVE: exported TXT round-trips an actual Workshop download");
        Complete(root.DisposeAsync().AsTask());
        Complete(service.DisposeAsync().AsTask());
    }
}
