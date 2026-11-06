using System.Text.Json;
using System.Text.Json.Serialization;
using SteamDatabase.ValvePak;

namespace FireAxe;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WeaponAnimationKind { Reload, Draw }

public sealed record AnimationTrimStep(WeaponAnimationKind Kind, double Seconds, string? Model = null, int? Animation = null);

public sealed record WeaponAnimationRecipe(ulong? WorkshopId, string SourceHash, AnimationTrimStep[] Steps)
{
    internal const string EntryPath = "emberkit/animation-trim.json";
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsValid => (WorkshopId is null or > 0) && SourceHash is { Length: 64 } && SourceHash.All(char.IsAsciiHexDigit)
        && Steps is { Length: > 0 and <= 256 } && Steps.All(step => step is not null && Enum.IsDefined(step.Kind)
            && double.IsFinite(step.Seconds) && step.Seconds > 0 && (step.Model is null) == (step.Animation is null)
            && (step.Model is null || step.Model.Length <= 512 && step.Model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && step.Animation >= 0));
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string Summary => string.Join(" · ", Steps.GroupBy(step => step.Kind).Select(group =>
    {
        var name = group.Key == WeaponAnimationKind.Reload ? "换弹" : "切枪";
        var uniform = group.Where(step => step.Model is null).Sum(step => step.Seconds);
        var sequences = group.Where(step => step.Model is not null).GroupBy(step => (step.Model, step.Animation)).Select(items => uniform + items.Sum(step => step.Seconds)).ToArray();
        string Seconds(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (sequences.Length == 0) return name + "累计裁尾 " + Seconds(uniform) + " s";
        var range = Math.Abs(sequences.Max() - sequences.Min()) < .0005 ? Seconds(sequences[0]) : Seconds(sequences.Min()) + "～" + Seconds(sequences.Max());
        return name + "按序列裁尾 " + range + " s";
    }));
    public static WeaponAnimationRecipe? Parse(string text)
    {
        if (text.Length > 65536) return null;
        try
        {
            var recipe = JsonSerializer.Deserialize<WeaponAnimationRecipe>(text);
            return recipe?.IsValid == true ? recipe : null;
        }
        catch (JsonException) { return null; }
    }
    internal static WeaponAnimationRecipe? Read(Package package)
    {
        if (package.FindEntry(EntryPath) is not { TotalLength: <= 65536 } entry) return null;
        try
        {
            package.ReadEntry(entry, out byte[] bytes, true);
            return Parse(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }
    }
    public string Serialize() => JsonSerializer.Serialize(this);
}
