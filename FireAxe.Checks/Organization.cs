using System.Text.RegularExpressions;
internal static partial class Program
{
    private static Dictionary<string, string> ReadGameEntries(string path)
    {
        return Regex.Matches(File.ReadAllText(path), "\"([^\"]+)\"\\s+\"([01])\"")
            .ToDictionary(match => match.Groups[1].Value.Replace("\\\\", "\\").Replace('\\', '/'), match => match.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
    }
}
