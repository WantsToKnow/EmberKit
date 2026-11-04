using FireAxe;

internal static partial class Program
{
    private static void CheckWorkshopCollections()
    {
        var steam = new SteamFixtureHandler([]);
        steam.Collections[500] = [42, 501, 730, 999];
        steam.Collections[501] = [43, 42, 500];
        steam.Collections[502] = [43, 44];
        using var client = new HttpClient(steam);
        var plan = Complete(WorkshopManifest.ResolveDownloadsAsync(
            [new(500, "Nested collection", false, 9), new(42, "Explicit item", true, 3), new(502, "Overlapping collection")], client, default));
        Check(plan.Items.Select(item => item.Entry.Id).Order().SequenceEqual(new ulong[] { 42, 43, 44 }),
            "collection links expand nested and overlapping collections into unique downloadable items");
        Check(steam.CollectionRequests == 3,
            "cyclic child collections are visited once instead of recursing forever");
        Check(plan.Items.Single(item => item.Entry.Id == 42).Entry is { Enabled: true, Priority: 3 }
            && plan.Items.Single(item => item.Entry.Id == 43).Entry is { Enabled: false, Priority: 9 },
            "collection children inherit enabled state and priority while explicit item settings take precedence");
        Check(plan.Skipped.Length == 2 && plan.Items.All(item => item.File.FileName.EndsWith(".vpk")),
            "wrong-game and unavailable collection children are reported without queuing collection preview images");
    }
}
