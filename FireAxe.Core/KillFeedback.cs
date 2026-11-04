// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Newtonsoft.Json;

namespace FireAxe;

// Settings belong to the portable application, not to the VPK library.
[JsonObject(MemberSerialization.OptIn)]
public sealed class KillFeedbackSettings : ObservableObject
{
    private bool _enabled;
    private bool _autoPistolEnabled;
    private int _scope = 1;

    [JsonProperty]
    public bool Enabled { get => _enabled; set => NotifyAndSetIfChanged(ref _enabled, value); }

    [JsonProperty]
    public bool AutoPistolEnabled { get => _autoPistolEnabled; set => NotifyAndSetIfChanged(ref _autoPistolEnabled, value); }

    public bool RequiresClientModule => Enabled || AutoPistolEnabled;

    // 0 = all infected; 1 = specials, including Tank and Witch.
    [JsonProperty]
    public int Scope
    {
        get => _scope;
        set => NotifyAndSetIfChanged(ref _scope, value == 0 ? 0 : 1);
    }
}

public static class KillFeedbackLaunch
{
    public const string PluginDescription = "EmberKit Kill Feedback";
    public static string ModuleDirectory => Path.Combine(AppContext.BaseDirectory, "Feedback");

    // These hashes guard the x86 interface slots used by our native module.
    // Keep in sync with native/EmberFeedback/source_abi.h when supporting another game build.
    public static IReadOnlyDictionary<string, string> SupportedFiles { get; } = new Dictionary<string, string>
    {
        ["left4dead2.exe"] = "c1007187e05ed681173b1c44c043980edf72dfcb2183ad90ec8e6d874d1b14e3",
        ["bin/engine.dll"] = "8a6d468059394bf4cfce32f11dcba12ef45de88da1373084d5a2916d0bf7ba86",
        ["left4dead2/bin/client.dll"] = "c0a8e1e88f7312db11bcfd4b0ec574d224a34b2d9a615d6088b28335eeb4af1f",
        ["bin/vguimatsurface.dll"] = "3f2e2d74b3934a482caa8c3d5aa8d1157e04d719185b019ffaa1f9f7b4bb1d58",
        ["bin/vgui2.dll"] = "3188733e5cb87a2c72e616160e7c5298da065dcaa3248021f4abd5694eb701c1"
    };

    public static FeedbackLaunchRequest? Prepare(string gameDirectory, KillFeedbackSettings settings, string? moduleDirectory = null)
    {
        if (!settings.RequiresClientModule) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("击杀反馈目前支持 Windows 版求生之路 2。");
        var directory = Path.GetFullPath(moduleDirectory ?? ModuleDirectory);
        var module = Path.Combine(directory, "EmberFeedback.dll");
        if (!File.Exists(module)) throw new FileNotFoundException("缺少自产击杀反馈模块，请先构建 EmberFeedback。", module);
        if (!File.Exists(Path.Combine(directory, "EmberFeedback.Loader.exe")))
            throw new FileNotFoundException("缺少自产 x86 客户端加载器，请先构建 EmberFeedback.Loader。");
        foreach (var entry in SupportedFiles)
        {
            var path = Path.Combine(gameDirectory, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException("击杀反馈无法核验游戏文件：" + entry.Key, path);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("此游戏版本尚未适配击杀反馈：" + entry.Key + "。取消勾选后仍可正常启动游戏。");
        }
        var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        WriteConfiguration(directory, settings, session);
        return new(session, settings.Scope, directory, DateTime.UtcNow);
    }

    public static ProcessStartInfo BuildLoaderStartInfo(int processId, string gameExecutable, string moduleDirectory, string session)
    {
        if (processId <= 0 || !Path.IsPathFullyQualified(gameExecutable) || !Path.IsPathFullyQualified(moduleDirectory) || !ValidSession(session))
            throw new ArgumentException("客户端模块加载参数无效。");
        var start = new ProcessStartInfo(Path.Combine(moduleDirectory, "EmberFeedback.Loader.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(gameExecutable);
        start.ArgumentList.Add(Path.Combine(moduleDirectory, "EmberFeedback.dll"));
        start.ArgumentList.Add(session);
        return start;
    }

    public static bool ValidSession(string? session) => session is { Length: 32 } && session.All(char.IsAsciiHexDigit);
    public static string StateDirectory(string moduleDirectory) => Path.GetFullPath(Path.Combine(moduleDirectory, "..", "data", "Feedback"));

    public static NativeFeedbackStatus? ReadStatus(string moduleDirectory, string session, int expectedProcessId)
    {
        if (!ValidSession(session) || expectedProcessId <= 0) throw new IOException("本次游戏尚未请求或完成客户端模块加载，请通过助手重新启动。");
        var path = Path.Combine(StateDirectory(moduleDirectory), "session-" + session + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new IOException("客户端模块状态文件异常。");
        NativeFeedbackStatus? status;
        try { status = System.Text.Json.JsonSerializer.Deserialize<NativeFeedbackStatus>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (System.Text.Json.JsonException ex) { throw new IOException("客户端模块状态文件不完整。", ex); }
        if (status?.Session != session || status.Pid != expectedProcessId) return null;
        if (status.Phase == "error") throw new IOException("客户端模块初始化失败：" + status.Error);
        if (status.AutoPistolError != 0) throw new IOException("手枪连点初始化或输入失败，Windows 错误码：" + status.AutoPistolError);
        return status;
    }

    public static string WriteConfiguration(string moduleDirectory, KillFeedbackSettings settings, string session)
    {
        if (!ValidSession(session)) throw new ArgumentException("反馈会话无效。", nameof(session));
        var path = Path.Combine(StateDirectory(moduleDirectory), "settings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, $"[feedback]\nenabled={(settings.RequiresClientModule ? 1 : 0)}\nkill_feedback={(settings.Enabled ? 1 : 0)}\nauto_pistol={(settings.AutoPistolEnabled ? 1 : 0)}\nscope={settings.Scope}\nsession={session}\n");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}

public sealed record FeedbackLaunchRequest(string Session, int Scope, string ModuleDirectory, DateTime CreatedUtc);
public sealed record NativeFeedbackStatus(string Session, int Pid, string Phase, string? Error, int Scope,
    long PaintFrames, long DeathEvents, long AcceptedKills, long HudDraws, int RegisteredEvents, bool Audio)
{
    public bool KillFeedbackEnabled { get; init; } = true;
    public bool AutoPistolEnabled { get; init; }
    public bool AutoPistolReady { get; init; }
    public bool AutoPistolActive { get; init; }
    public long AutoPistolClicks { get; init; }
    public int AutoPistolError { get; init; }
    public bool Initialized => Phase is "initialized" or "listening";
    public string Describe() => (KillFeedbackEnabled
        ? $"客户端反馈已加载：{(Scope == 0 ? "所有僵尸" : "仅特感（含 Tank / Witch）")}；监听 {RegisteredEvents}/3 类死亡事件，收到 {DeathEvents}，确认本人击杀 {AcceptedKills}，绘制 {HudDraws} 帧。"
        : "客户端模块已加载。")
        + (AutoPistolEnabled ? $" 手枪连点{(AutoPistolReady ? "已就绪" : "初始化中")}，已点击 {AutoPistolClicks} 次。" : "");
}
