using System.Security.Cryptography;
using SteamDatabase.ValvePak;
namespace FireAxe;
public static class WorkshopBackup
{
    internal static readonly SemaphoreSlim Operation = new(1);
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static bool IsVpk(string path) => path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);
    internal static void ValidateVpk(string path) { using var package = new Package(); package.Read(path); }

    internal static bool IsOwnLink(string source, string expectedTarget)
    {
        var info = new FileInfo(source);
        if (info.LinkTarget is not { } target) return false;
        var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(source)!, target));
        return FileSystemUtils.IsSamePath(resolved, expectedTarget);
    }

    internal static string Inside(string directory, string name)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(directory, name));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件路径超出指定目录。");
        return result;
    }

    internal static void EnsureRealDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("目录本身是链接，请先检查路径：" + path);
        Directory.CreateDirectory(path);
    }
}
