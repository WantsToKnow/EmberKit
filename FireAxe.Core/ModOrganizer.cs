using ValveKeyValue;
namespace FireAxe;

public static class ModOrganizer
{
    public const string BackupDirectory = "modebackup";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    internal static string? GetGameEntry(string game, string source)
    {
        var target = new FileInfo(source).ResolveLinkTarget(true)?.FullName ?? source;
        return IsContentName(target) ? GameFileName(target) : null;
    }
    internal static bool IsContentName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 64 && name.All(char.IsAsciiHexDigit);
    }
    internal static string GameFileName(string target) => ModStorage.EntryName(Path.GetFileNameWithoutExtension(target));
    internal static Dictionary<string, string> ReadEntries(string path)
    {
        var result = new Dictionary<string, string>(PathComparer);
        if (!File.Exists(path)) return result;
        using var input = File.OpenRead(path);
        foreach (var entry in KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(input))
            result[entry.Name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)] = entry.Value?.ToString() ?? "0";
        return result;
    }
    internal static void WriteEntries(string path, Dictionary<string, string> entries)
    {
        var temporary = path + ".fireaxe-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Serialize(output, new KVObject("AddonList", entries.Select(entry => new KVObject(entry.Key, entry.Value))));
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
