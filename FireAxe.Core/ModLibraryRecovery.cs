namespace FireAxe;

public sealed record ModRecoveryReport(int RestoredLinks, int DiscoveredMods, int RemovedRecords, string[] Warnings);

public static class ModLibraryRecovery
{
    public static async Task<ModRecoveryReport> RefreshAsync(AddonRoot root, CancellationToken token = default)
    {
        var result = await root.Storage.RefreshAsync(token);
        return new(result.RestoredLinks, result.DiscoveredMods, result.RemovedRecords, result.Pending ? [root.Storage.Status] : result.Warnings);
    }
}
