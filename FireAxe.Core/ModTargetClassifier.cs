namespace FireAxe;

public sealed record ModTarget(string Id, string Category, string Name, bool WarnOnOverlap = true);

public sealed record ModTargetMatch(ModTarget Target, IReadOnlyList<string> Files);

public sealed record ModInspection(string? Title, IReadOnlyList<ModTargetMatch> Targets, string? Error = null)
{
    // Browsing has one home; all matches remain available for conflict detection and evidence.
    public ModTarget? PrimaryTarget => Targets.OrderBy(match => match.Target.Id == "maps" ? 0 : match.Target.WarnOnOverlap ? 1 : 2)
        .ThenBy(match => match.Files.Any(path => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) ? 0
            : match.Files.Any(path => path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
        .ThenBy(match => match.Target.Id, StringComparer.Ordinal).FirstOrDefault()?.Target;

    public string Summary => Error ?? (Targets.Count == 0 ? "未识别 / 其他" : string.Join(" · ", Targets.Select(match => match.Target.Name)));
    public string Evidence => Error ?? (Targets.Count == 0 ? "没有找到已知的游戏资源路径，可继续使用原有标签手动标记。" :
        string.Join("\n\n", Targets.Select(match => $"{match.Target.Category} / {match.Target.Name}\n" + string.Join("\n", match.Files))));
}

/// <summary>Classifies the game resources being replaced, independently of the archive's name.</summary>
public static class ModTargetClassifier
{
    private sealed record Rule(ModTarget Target, string[] Tokens);

    // These are Source resource names, not the names of the cosmetic weapons in Workshop titles.
    private static readonly Rule[] Rules =
    [
        Weapon("ak47", "AK-47", "ak47", "rif_ak47", "rifle_ak47"),
        Weapon("m16", "M16 / M4", "m16a2", "rif_m16a2", "rifle_m16a2", "weapon_rifle"),
        Weapon("scar", "SCAR 三连发", "desert_rifle", "rif_desert", "rifle_desert"),
        Weapon("sg552", "SG552", "sg552", "rif_sg552", "rifle_sg552"),
        Weapon("m60", "M60", "m60", "mach_m60", "rif_m60", "rifle_m60"),
        Weapon("smg", "UZI 冲锋枪", "smg", "smg_uzi", "uzi"),
        Weapon("smg_silenced", "消音冲锋枪", "smg_silenced", "smg_mac10", "mac10"),
        Weapon("mp5", "MP5", "mp5", "smg_mp5"),
        Weapon("pumpshotgun", "泵动霰弹枪", "pumpshotgun", "shotgun_pump", "shotgun"),
        Weapon("shotgun_chrome", "铁喷 / Chrome", "shotgun_chrome", "shotgun_chrome", "chrome_shotgun"),
        Weapon("autoshotgun", "连喷 / Auto Shotgun", "autoshotgun", "auto_shotgun", "shotgun_auto"),
        Weapon("shotgun_spas", "SPAS 连喷", "shotgun_spas", "spas", "spas12"),
        Weapon("hunting_rifle", "木狙 / Hunting Rifle", "hunting_rifle", "snip_hunting", "sniper_mini14", "sniper"),
        Weapon("sniper_military", "军狙 / G3SG1", "sniper_military", "snip_military", "military_sniper"),
        Weapon("sniper_awp", "AWP", "sniper_awp", "snip_awp", "awp"),
        Weapon("sniper_scout", "Scout", "sniper_scout", "snip_scout", "scout"),
        Weapon("pistol", "普通手枪", "pistol", "pist_pistol", "pist_a", "pist_b", "w_pistol_a", "w_pistol_b"),
        Weapon("pistol_magnum", "马格南 / 沙鹰", "pistol_magnum", "pist_magnum", "pist_deagle", "magnum", "desert_eagle"),
        Weapon("grenade_launcher", "榴弹发射器", "grenade_launcher", "grenadelauncher"),
        Weapon("chainsaw", "电锯", "chainsaw"),
        Weapon("molotov", "燃烧瓶", "molotov", "eq_molotov"),
        Weapon("pipe_bomb", "土制炸弹", "pipe_bomb", "pipebomb", "eq_pipebomb"),
        Weapon("vomitjar", "胆汁罐", "vomitjar", "bile_flask", "eq_bile_flask"),
        Survivor("nick", "Nick / 尼克", "gambler", "nick"),
        Survivor("rochelle", "Rochelle / 罗雪儿", "producer", "rochelle"),
        Survivor("coach", "Coach / 教练", "coach"),
        Survivor("ellis", "Ellis / 艾利斯", "mechanic", "ellis"),
        Survivor("bill", "Bill / 比尔", "namvet", "bill"),
        Survivor("zoey", "Zoey / 佐伊", "teenangst", "zoey"),
        Survivor("francis", "Francis / 弗朗西斯", "biker", "francis"),
        Survivor("louis", "Louis / 路易斯", "manager", "louis"),
        Infected("tank", "Tank / 坦克", "hulk", "tank"),
        Infected("witch", "Witch / 女巫", "witch", "witch_bride"),
        Infected("smoker", "Smoker / 舌头", "smoker"),
        Infected("boomer", "Boomer / 胖子", "boomer", "boomette"),
        Infected("hunter", "Hunter / 猎人", "hunter"),
        Infected("charger", "Charger / 牛", "charger"),
        Infected("spitter", "Spitter / 口水", "spitter"),
        Infected("jockey", "Jockey / 猴子", "jockey"),
        Item("first_aid_kit", "医疗包", "first_aid_kit", "firstaidkit", "medkit", "eq_medkit"),
        Item("pain_pills", "止痛药", "pain_pills", "painpills", "pills", "eq_painpills"),
        Item("adrenaline", "肾上腺素", "adrenaline", "eq_adrenaline"),
        Item("defibrillator", "电击器", "defibrillator", "eq_defibrillator"),
        .. new[] { "fireaxe", "frying_pan", "machete", "baseball_bat", "crowbar", "cricket_bat", "tonfa", "katana", "electric_guitar", "knife", "golfclub", "pitchfork", "shovel" }
            .Select(name => new Rule(new("melee:" + name, "近战", name), [name, "v_" + name, "w_" + name]))
    ];

    public static IReadOnlyList<ModTarget> Targets { get; } = Array.AsReadOnly(Rules.Select(rule => rule.Target).Concat(new ModTarget[]
    {
        new("maps", "地图 / 战役", "地图 / 战役", false), new("ui", "界面", "HUD / 界面", false),
        new("music", "声音", "音乐", false), new("sounds", "声音", "其他音效", false),
        new("scripts", "脚本", "脚本 / 玩法", false), new("particles", "特效", "粒子特效", false),
        new("common_infected", "感染者", "其他感染者", false), new("unknown", "未识别 / 其他", "未识别 / 其他", false)
    }).Distinct().ToArray());

    public static ModTargetMatch[] Classify(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var matches = new Dictionary<ModTarget, List<string>>();
        foreach (var rawPath in filePaths)
        {
            var path = rawPath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            var parts = path.Split('/');
            parts[^1] = parts[^1].Split('.')[0];
            var matched = false;
            foreach (var rule in Rules)
            {
                if (!IsResourcePath(path, rule.Target.Category) ||
                    !(parts.Any(part => rule.Tokens.Contains(part, StringComparer.Ordinal)) || rule.Target.Id == "m16" && path.StartsWith("sound/weapons/rifle/")))
                    continue;
                Add(rule.Target, path);
                matched = true;
            }
            if (!matched && GeneralTarget(path) is { } general)
                Add(general, path);
        }
        return matches.Select(pair => new ModTargetMatch(pair.Key, pair.Value)).OrderBy(match => match.Target.Category)
            .ThenBy(match => match.Target.Name, StringComparer.Ordinal).ToArray();

        void Add(ModTarget target, string path)
        {
            if (!matches.TryGetValue(target, out var evidence))
                matches[target] = evidence = [];
            // ponytail: retain five evidence paths per target; the original conflict checker retains full file conflicts.
            if (evidence.Count < 5 && !evidence.Contains(path))
                evidence.Add(path);
        }
    }

    private static Rule Weapon(string id, string name, params string[] tokens) => ResourceRule(id, "枪械 / 投掷", name, tokens);
    private static Rule Item(string id, string name, params string[] tokens) => ResourceRule(id, "补给", name, tokens);
    private static Rule ResourceRule(string id, string category, string name, string[] tokens) => new(new(id, category, name),
        tokens.Concat(tokens.Select(token => "v_" + token)).Concat(tokens.Select(token => "w_" + token))
            .Concat(new[] { "weapon_" + id }).ToArray());
    private static Rule Survivor(string id, string name, params string[] tokens) => new(new("survivor:" + id, "人物", name),
        tokens.Concat(tokens.Select(token => "survivor_" + token)).Concat(tokens.Select(token => "v_arms_" + token)).ToArray());
    private static Rule Infected(string id, string name, params string[] tokens) => new(new("infected:" + id, "感染者", name), tokens);

    private static bool IsResourcePath(string path, string category) => category switch
    {
        "人物" => path.StartsWith("models/survivors/") || path.StartsWith("materials/models/survivors/") ||
            path.StartsWith("models/weapons/arms/") || path.StartsWith("models/v_models/arms/") ||
            path.StartsWith("materials/models/weapons/arms/") || path.StartsWith("sound/player/survivor/voice/"),
        "感染者" => path.StartsWith("models/infected/") || path.StartsWith("materials/models/infected/") || path.StartsWith("sound/player/"),
        _ => path.StartsWith("models/v_models/") || path.StartsWith("models/w_models/") ||
            path.StartsWith("models/weapons/") || path.StartsWith("materials/models/v_models/") ||
            path.StartsWith("materials/models/w_models/") || path.StartsWith("materials/models/weapons/") ||
            path.StartsWith("sound/weapons/") || path.StartsWith("scripts/weapon_") || path.StartsWith("scripts/melee/")
    };

    private static ModTarget? GeneralTarget(string path)
    {
        if (path.StartsWith("maps/") || path.StartsWith("missions/")) return new("maps", "地图 / 战役", "地图 / 战役", false);
        if (path.StartsWith("resource/") || path.StartsWith("materials/vgui/") || path.StartsWith("scripts/hud")) return new("ui", "界面", "HUD / 界面", false);
        if (path.StartsWith("sound/music/")) return new("music", "声音", "音乐", false);
        if (path.StartsWith("sound/")) return new("sounds", "声音", "其他音效", false);
        if (path.StartsWith("scripts/")) return new("scripts", "脚本", "脚本 / 玩法", false);
        if (path.StartsWith("particles/")) return new("particles", "特效", "粒子特效", false);
        if (path.StartsWith("models/infected/") || path.StartsWith("materials/models/infected/")) return new("common_infected", "感染者", "其他感染者", false);
        return null;
    }
}
