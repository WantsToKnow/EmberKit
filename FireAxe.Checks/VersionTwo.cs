using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;

internal static partial class Program
{
    private static void CheckVersionTwo(string output, AddonRoot root, AddonNodeExplorerViewModel explorer)
    {
        var catalog = explorer.ModCatalog;
        catalog.CollapseAll();
        Check(catalog.Categories.All(category => category.ParentId is null), "collapse all hides target rows");
        var guns = catalog.Categories.Single(category => category.Id == "category:枪械 / 投掷");
        guns.Toggle();
        Check(catalog.Categories.Any(category => category.Id == "ak47"), "individual category expands");
        catalog.Categories.Single(category => category.Id == guns.Id).Toggle();
        Check(!catalog.Categories.Any(category => category.Id == "ak47"), "individual category collapses");
        root.Nodes[0].IsEnabled = !root.Nodes[0].IsEnabled;
        var settle = DateTime.UtcNow;
        Pump(() => DateTime.UtcNow - settle > TimeSpan.FromMilliseconds(250));
        Check(!catalog.Categories.Any(category => category.Id == "ak47"), "category collapse survives live catalog refresh");
        catalog.OverlapGroups.Single().Show();
        Check(catalog.Categories.Any(category => category.Id == "m16") && catalog.SelectedCategory?.Id == "m16", "overlap reminder expands a collapsed category");
        catalog.ExpandAll();
        catalog.SelectedCategory = catalog.Categories.First();

        var original = new[] { new WorkshopListEntry(1103089137, "中文\t标题\n换行", true, 12), new WorkshopListEntry(1187196624, "Disabled", false, -3) };
        var serialized = WorkshopManifest.Serialize(original, [new("私人 Mod", "没有工坊来源")]);
        var parsed = WorkshopManifest.Parse(serialized);
        Check(parsed.Entries.Length == 2 && parsed.Entries[0].Priority == 12 && !parsed.Entries[1].Enabled && parsed.Entries[1].Priority == -3, "TXT round-trip preserves IDs, enabled states and priorities");
        Check(!serialized.Contains("中文\t标题\n换行"), "TXT titles cannot inject extra records");
        var mixed = WorkshopManifest.Parse("1103089137\n1103089137\n0\nhttps://evil.test/steamcommunity.com/sharedfiles/filedetails/?id=42\nhttps://steamcommunity.com.evil.test/sharedfiles/filedetails/?id=43\n123\tx\t0");
        Check(mixed.Entries.Length == 1 && mixed.DuplicateLines == 1 && mixed.InvalidLines == 4, "TXT parser rejects invalid IDs, spoofed hosts and invalid states");
        CheckThrows<FormatException>(() => WorkshopManifest.Parse("# appid=730\n123"), "wrong-game TXT header is rejected");

        var testDirectory = Path.Combine(output, "sharing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDirectory);
        var dataFile = Path.Combine(testDirectory, "sample.vpk");
        WriteVpk(dataFile, "Workshop sample", "models/v_models/v_rif_ak47.mdl");
        var data = File.ReadAllBytes(dataFile);
        var fake = new SteamFixtureHandler(data);
        using var client = new HttpClient(fake);
        var batch = Complete(PublishedFileUtils.GetPublishedFileDetailsBatchAsync(Enumerable.Range(1, 101).Select(id => (ulong)id), client, default));
        Check(batch.Count == 101 && fake.MetadataRequests == 2 && batch.Values.All(result => result.IsSucceeded), "Steam metadata is batched and mapped by ID");
        var plan = Complete(WorkshopManifest.ResolveDownloadsAsync([new(10, "Valid"), new(730, "Wrong game"), new(999, "Missing"), new(888, "No VPK")], client, default));
        Check(plan.Items.Length == 1 && plan.Skipped.Length == 3, "import skips wrong-game, deleted and non-VPK workshop items");

        var downloads = new FixtureDownloads(data);
        var sharingRoot = new AddonRoot { TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DirectoryPath = Path.Combine(testDirectory, "game", "left4dead2", "addons"), HttpClient = client, DownloadService = downloads };
        LocalVpkAddon numeric, renamed;
        using (sharingRoot.BlockAutoCheck())
        {
            numeric = AddonNode.Create<LocalVpkAddon>(sharingRoot);
            numeric.Name = "10";
            numeric.IsEnabled = true;
            numeric.Priority = 9;
            File.Copy(dataFile, numeric.VpkFilePath!);
            renamed = AddonNode.Create<LocalVpkAddon>(sharingRoot);
            renamed.Name = "renamed_mod";
            File.CreateSymbolicLink(renamed.VpkFilePath!, dataFile);
        }
        Complete(Task.WhenAll(numeric.CheckVpkValidity(), renamed.CheckVpkValidity()));
        var exported = Complete(WorkshopManifest.ExportAsync(sharingRoot.GetDescendants(), client, false, null, default));
        Check(exported.Entries.Any(entry => entry.Id == 10 && entry.Priority == 9) && exported.Entries.Any(entry => entry.Id == 42), "export resolves numeric IDs and matches a renamed file link using the target length and content hash");
        Check(!WorkshopManifest.Serialize(exported.Entries).Contains(testDirectory), "shared TXT contains no local filesystem paths");
        fake.DenySearch = true;
        var denied = Complete(WorkshopManifest.ExportAsync([renamed], client, false, null, default));
        Check(denied.Entries.Length == 0 && denied.Skipped.Single().Reason.Contains("搜索访问失败"), "blocked Steam search reports unconfirmed source instead of guessing");
        fake.DenySearch = false;

        var queuedPlan = Complete(WorkshopManifest.ResolveDownloadsAsync([new(10, "Already present"), new(44, "Download", true, 7)], client, default));
        var queued = WorkshopManifest.QueueDownloads(sharingRoot, queuedPlan);
        Check(queued.AlreadyPresent == 1 && queued.Added.Length == 1 && queued.Added[0].IsEnabledInHierarchy && queued.Added[0].Priority == 7, "TXT import deduplicates existing IDs and restores enabled state");
        Pump(() => queued.Added[0].Inspection is not null);
        Check(File.ReadAllBytes(queued.Added[0].VpkFilePath!).SequenceEqual(data) && queued.Added[0].Inspection!.Targets.Any(match => match.Target.Id == "ak47"), "existing downloader installs and immediately classifies imported VPK");
        Check(queued.Added[0].Group is null && queued.Added[0].Name == "44" && Path.GetDirectoryName(queued.Added[0].VpkFilePath) == sharingRoot.Storage.BackupPath, "downloads use the shared hash body store without a workshop directory");
        var corrupt = new WorkshopListEntry(45, "Bad download");
        downloads.CorruptNext = true;
        var corruptPlan = Complete(WorkshopManifest.ResolveDownloadsAsync([corrupt], client, default));
        var corruptAddon = WorkshopManifest.QueueDownloads(sharingRoot, corruptPlan).Added.Single();
        Pump(() => corruptAddon.Problems.Any(problem => problem is AddonDownloadFailedProblem));
        Check(corruptAddon.VpkFilePath is null, "invalid downloaded bytes are not accepted as an installed VPK");
        CheckDownloadResults(output, sharingRoot);
        CheckDownloadHashOption(output);
        CheckClipboardDownloads(output);

        using (var model = new WorkshopTransferViewModel(sharingRoot, 2))
        {
            var transfer = new WorkshopTransferWindow { DataContext = model };
            transfer.Show();
            PumpFrames();
            using (var frame = transfer.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "workshop-sharing-preview.png"));
            model.SelectedTab = 3;
            PumpFrames();
            using (var frame = transfer.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "workshop-backup-preview.png"));
            Check(transfer.Bounds.Width >= 760 && model.KeepSnapshot, "sharing and backup GUI renders with independent snapshot enabled");
            model.SelectedTab = 3;
            PumpFrames();
            using (var frame = transfer.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "organizer-preview.png"));
            Check(transfer.GetVisualDescendants().OfType<Button>().Any(button => ReferenceEquals(button.Command, model.OrganizeCommand) && button.IsEffectivelyVisible), "unified local storage is visible on tab 3");
            Check(!transfer.GetVisualDescendants().OfType<CheckBox>().Any(box => box.IsEffectivelyVisible)
                && transfer.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("游戏入口统一使用") == true),
                "local storage explains the single symbolic-link policy without exposing a link-type option");
            transfer.Close();
        }
        Complete(sharingRoot.DisposeAsync().AsTask());
    }

    private static T Complete<T>(Task<T> task) { Pump(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void CheckThrows<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, description); return; }
        throw new Exception("FAIL: " + description);
    }

    private sealed class SteamFixtureHandler(byte[] data) : HttpMessageHandler
    {
        public int MetadataRequests;
        public int CollectionRequests;
        public Dictionary<ulong, ulong[]> Collections { get; } = [];
        public bool DenySearch;
        public bool MismatchHash;
        public bool MismatchSize;
        private readonly string _sha1 = Convert.ToHexString(SHA1.HashData(data));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return DenySearch ? new(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK) { Content = new StringContent("https://steamcommunity.com/sharedfiles/filedetails/?id=42") };
            var body = await request.Content!.ReadAsStringAsync(token);
            var ids = body.Split('&').Select(field => field.Split('=', 2)).Where(pair => WebUtility.UrlDecode(pair[0]).StartsWith("publishedfileids["))
                .Select(pair => ulong.Parse(pair[1])).Reverse().ToArray();
            if (request.RequestUri!.AbsolutePath.Contains("GetCollectionDetails"))
            {
                CollectionRequests++;
                var collections = ids.Select(id => new
                {
                    publishedfileid = id.ToString(), result = Collections.ContainsKey(id) ? 1 : 9,
                    children = Collections.GetValueOrDefault(id, []).Select(child => new
                    { publishedfileid = child.ToString(), filetype = Collections.ContainsKey(child) ? 2 : 0 })
                });
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response = new { result = 1, collectiondetails = collections } }), Encoding.UTF8, "application/json") };
            }
            MetadataRequests++;
            var details = ids.Select(id => new
            {
                publishedfileid = id.ToString(), result = id == 999 ? 9 : 1, consumer_app_id = id == 730 ? 730 : 550,
                title = "Workshop sample " + id, filename = Collections.ContainsKey(id) ? "collection_preview.jpg" : id == 888 ? "collection" : "sample.vpk", file_size = data.Length + (MismatchSize ? 1234 : 0),
                file_url = id == 888 ? "" : "https://cdn.steamusercontent.com/ugc/123/" + (MismatchHash ? new string('0', 40) : _sha1) + "/", preview_url = "", time_updated = 1
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response = new { publishedfiledetails = details } }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FixtureDownloads(byte[] data) : IDownloadService
    {
        public int DownloadCount;
        public bool CorruptNext;
        public bool InvalidVpkNext;
        public Task? CompletionGate;
        public IDownloadItem Download(string url, string filePath)
        {
            DownloadCount++;
            var bytes = CorruptNext ? new byte[] { 1, 2, 3 } : InvalidVpkNext ? new byte[data.Length] : data;
            CorruptNext = false;
            InvalidVpkNext = false;
            File.WriteAllBytes(filePath, bytes);
            return new FixtureDownload(url, filePath, bytes.Length, CompletionGate);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureDownload(string url, string filePath, int length, Task? completionGate = null) : IDownloadItem
    {
        public string Url => url;
        public string FilePath => filePath;
        public long DownloadedBytes => length;
        public long TotalBytes => length;
        public double BytesPerSecondSpeed => 0;
        public DownloadStatus Status => DownloadStatus.Succeeded;
        public Exception Exception => new InvalidOperationException();
        public void Pause() { }
        public void Resume() { }
        public void Cancel() { }
        public void Wait() => WaitAsync().GetAwaiter().GetResult();
        public Task WaitAsync() => completionGate ?? Task.CompletedTask;
        public void Dispose() { }
    }
}
