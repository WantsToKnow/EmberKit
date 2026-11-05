using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FireAxe;
using FireAxe.ViewModels;
using FireAxe.Views;
using Newtonsoft.Json;

internal static partial class Program
{
    private static void CheckKillFeedback(string output)
    {
        var settings = new KillFeedbackSettings();
        Check(!settings.Enabled && !settings.AutoPistolEnabled && !settings.RequiresClientModule && settings.Scope == 1,
            "kill feedback and automatic pistol clicks default off, with specials selected");
        settings.Enabled = true; settings.Scope = 0;
        var reloaded = JsonConvert.DeserializeObject<KillFeedbackSettings>(JsonConvert.SerializeObject(settings))!;
        Check(reloaded.Enabled && reloaded.Scope == 0, "both feedback settings survive JSON round-trip");
        reloaded.Scope = 12;
        Check(reloaded.Scope == 1, "unknown feedback scopes resolve to specials rather than enabling all infected");

        var bundle = Path.Combine(output, "feedback-bundle");
        var moduleDirectory = Path.Combine(bundle, "Feedback");
        Directory.CreateDirectory(moduleDirectory);
        var session = new string('A', 32);
        var config = KillFeedbackLaunch.WriteConfiguration(moduleDirectory, settings, session);
        Check(File.ReadAllText(config) == $"[feedback]\nenabled=1\nkill_feedback=1\nauto_pistol=0\nscope=0\nsession={session}\n"
            && FileSystemUtils.IsSamePath(config, Path.Combine(bundle, "data", "Feedback", "settings.ini")),
            "native configuration and a per-launch session stay in the portable data directory");
        var pistolSettings = JsonConvert.DeserializeObject<KillFeedbackSettings>(
            JsonConvert.SerializeObject(new KillFeedbackSettings { AutoPistolEnabled = true }))!;
        config = KillFeedbackLaunch.WriteConfiguration(moduleDirectory, pistolSettings, session);
        Check(pistolSettings.RequiresClientModule && !pistolSettings.Enabled && pistolSettings.AutoPistolEnabled
            && File.ReadAllText(config).Contains("enabled=1\nkill_feedback=0\nauto_pistol=1\n"),
            "pistol-only settings survive persistence and request the client module without enabling kill feedback");
        var loaderInfo = KillFeedbackLaunch.BuildLoaderStartInfo(1234, Path.Combine(bundle, "game path", "left4dead2.exe"), moduleDirectory, session);
        Check(!loaderInfo.UseShellExecute && loaderInfo.CreateNoWindow && loaderInfo.ArgumentList.Count == 4
            && loaderInfo.ArgumentList[1].Contains("game path") && loaderInfo.ArgumentList[3] == session,
            "x86 client helper uses structured arguments without shell parsing, retaining spaces and the session identity");
        Check(!loaderInfo.ArgumentList.Any(value => value.Contains("-insecure") || value.Contains("plugin_load")),
            "client feedback never builds insecure or server-plugin command arguments");
        CheckThrows<ArgumentException>(() => KillFeedbackLaunch.BuildLoaderStartInfo(0, "relative.exe", moduleDirectory, session),
            "a client helper requires a positive PID and absolute paths");
        CheckThrows<ArgumentException>(() => KillFeedbackLaunch.BuildLoaderStartInfo(1234, Path.Combine(bundle, "left4dead2.exe"), moduleDirectory, "../bad"),
            "invalid feedback sessions cannot become helper arguments or status-file paths");
        Check(KillFeedbackLaunch.Prepare(bundle, new(), moduleDirectory) is null, "disabled feedback does not require native files or a supported game build");
        CheckThrows<FileNotFoundException>(() => KillFeedbackLaunch.Prepare(bundle, settings, moduleDirectory), "a missing native module cannot produce a successful enabled launch");

        var shipped = Path.Combine(AppContext.BaseDirectory, "Feedback", "EmberFeedback.dll");
        var module = File.ReadAllBytes(shipped);
        var pe = BitConverter.ToInt32(module, 0x3c);
        Check(BitConverter.ToUInt16(module, pe + 4) == 0x14c, "the x64 manager ships an x86 game module");
        var shippedLoader = Path.Combine(AppContext.BaseDirectory, "Feedback", "EmberFeedback.Loader.exe");
        var loaderBytes = File.ReadAllBytes(shippedLoader);
        Check(BitConverter.ToUInt16(loaderBytes, BitConverter.ToInt32(loaderBytes, 0x3c) + 4) == 0x14c,
            "the client loader is x86 as well as the game DLL");
        Check(new[] { "skull.png", "headshot.png", "kill.wav", "headshot.wav", "PROVENANCE.txt" }
            .All(name => File.Exists(Path.Combine(AppContext.BaseDirectory, "Feedback", "assets", name))),
            "native module output includes the reference images, sounds and separate provenance notice");

        var game = Path.Combine(output, "feedback-game");
        var addons = Path.Combine(game, "left4dead2", "addons");
        Directory.CreateDirectory(addons);
        foreach (var path in KillFeedbackLaunch.SupportedFiles.Keys)
        {
            var target = Path.Combine(game, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "fixture: not a supported game binary");
        }
        File.Copy(shipped, Path.Combine(moduleDirectory, "EmberFeedback.dll"), true);
        File.Copy(shippedLoader, Path.Combine(moduleDirectory, "EmberFeedback.Loader.exe"), true);
        CheckThrows<InvalidOperationException>(() => KillFeedbackLaunch.Prepare(game, settings, moduleDirectory),
            "an unrecognized game build is rejected before the ABI-dependent module is requested");
        CheckThrows<InvalidOperationException>(() => new GameConsole(game, Path.Combine(bundle, "pistol-console"), moduleDirectory).PrepareLaunch(pistolSettings),
            "a pistol-only launch follows the same supported-game validation and module preparation");

        var root = new AddonRoot { DirectoryPath = addons, GamePath = game, TaskScheduler = TaskScheduler.FromCurrentSynchronizationContext(), DownloadService = new FixtureDownloads([]) };
        using var explorer = new AddonNodeExplorerViewModel(root);
        using var transfer = new WorkshopTransferViewModel(root);
        Check(ReferenceEquals(explorer.KillFeedback, transfer.KillFeedback), "main and workshop launch controls share the same feedback settings object");
        var window = new Window { Width = 1100, Height = 680, Content = new AddonNodeExplorerView { DataContext = explorer } };
        window.Show(); PumpFrames();
        window.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "launchSettingsExpander").IsExpanded = true;
        PumpFrames();
        var enabled = window.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "killFeedbackCheckBox");
        var scope = window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "killFeedbackScope");
        Check(enabled.IsChecked == false && scope.SelectedIndex == 1 && !scope.IsEffectivelyEnabled,
            "launch-area feedback controls show the persisted defaults and disable scope while off");
        var pistolToggle = window.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "autoPistolCheckBox");
        pistolToggle.IsChecked = true; PumpFrames();
        Check(root.KillFeedback.AutoPistolEnabled && transfer.KillFeedback.AutoPistolEnabled && !root.KillFeedback.Enabled && !scope.IsEffectivelyEnabled,
            "the independent pistol checkbox is shared by both launch paths without enabling the kill HUD");
        enabled.IsChecked = true; scope.SelectedIndex = 0; PumpFrames();
        Check(root.KillFeedback.Enabled && transfer.KillFeedback.Scope == 0 && scope.IsEffectivelyEnabled,
            "launch-area checkbox and range selection bind through to both launch paths");
        Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text?.Contains("普通启动") == true),
            "feedback launch controls describe ordinary startup and the module receipt, without an insecure-mode prompt");
        var origin = scope.TranslatePoint(default, window)!.Value;
        Check(scope.IsEffectivelyVisible && origin.X >= 0 && origin.X + scope.Bounds.Width <= window.Bounds.Width,
            "feedback controls fit beside the existing launch button at the minimum window size");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "kill-feedback-settings.png"));
        window.Close();

        var consoleDirectory = Path.Combine(bundle, "console");
        var console = new GameConsole(game, consoleDirectory, moduleDirectory);
        var launch = console.PrepareLaunch(new()); // Never start a game or load a game DLL.
        var args = Uri.UnescapeDataString(launch.FileName);
        Check(!args.Contains("plugin_load") && !args.Contains("-insecure") && args.Contains("-netconport") && args.Contains("-netconpassword"),
            "unchecked feedback preserves the original authenticated Steam console launch");
        Check(console.FeedbackRequest is null, "normal startup schedules no client helper when feedback is off");
        var port = int.Parse(Regex.Match(args, @"-netconport (\d+)").Groups[1].Value);
        var password = Regex.Match(args, @"-netconpassword ([A-F0-9]{64})").Groups[1].Value;
        using var server = new FakeGameConsole(game, port, password, "");
        var consoleSettingsPath = Directory.GetFiles(consoleDirectory, "*.json").Single();
        var consoleSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(consoleSettingsPath))!.AsObject();
        consoleSettings["FeedbackSession"] = session; consoleSettings["FeedbackProcessId"] = 1234;
        File.WriteAllText(consoleSettingsPath, consoleSettings.ToJsonString());
        Complete(CheckReadback());
        Complete(root.DisposeAsync().AsTask());
        async Task CheckReadback()
        {
            await using var connection = await console.ConnectAsync();
            var statusPath = Path.Combine(KillFeedbackLaunch.StateDirectory(moduleDirectory), "session-" + session + ".json");
            var native = new NativeFeedbackStatus(session, 1234, "listening", "", 0, 300, 8, 3, 120, 3, true);
            WriteNative(native);
            var summary = await connection.ReadKillFeedbackAsync();
            Check(summary.Contains("所有僵尸") && summary.Contains("本人击杀 3") && summary.Contains("绘制 120"),
                "a native owner-thread receipt reports real event/ownership/drawing counters instead of plugin_print");
            WriteNative(native with { Scope = 1 });
            Check((await connection.ReadKillFeedbackAsync()).Contains("仅特感"), "feedback reports the native module's actual loaded scope");
            WriteNative(native with { Session = new string('B', 32) });
            Check(KillFeedbackLaunch.ReadStatus(moduleDirectory, session, 1234) is null, "another launch's native status cannot be accepted");
            WriteNative(native with { Pid = 9999 });
            Check(KillFeedbackLaunch.ReadStatus(moduleDirectory, session, 1234) is null, "another process's native status cannot be accepted");
            WriteNative(native with { Phase = "starting" });
            try { await connection.ReadKillFeedbackAsync(); throw new Exception("Expected missing owner receipt"); }
            catch (IOException) { Check(true, "DLL load alone does not count as owner-thread initialization"); }
            WriteNative(native with { Phase = "error", Error = "source_interface_unavailable" });
            try { await connection.ReadKillFeedbackAsync(); throw new Exception("Expected native error"); }
            catch (IOException ex) { Check(ex.Message.Contains("source_interface_unavailable"), "native startup failures surface their actual stage"); }
            Check(!server.Commands.Contains("plugin_print"), "client feedback checks never require the blocked server-plugin interface");
            void WriteNative(NativeFeedbackStatus status) => File.WriteAllText(statusPath, System.Text.Json.JsonSerializer.Serialize(status));
        }
    }
}
