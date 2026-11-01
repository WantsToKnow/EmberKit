using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FireAxe;

// Source's built-in, password-protected net console. No game process memory access.
public sealed class GameConsole(string gameDirectory, string? settingsDirectory = null)
{
    private sealed record Settings(int Port, string Password);
    public string GameDirectory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
    private string SettingsPath => Path.Combine(settingsDirectory ?? AppDataPaths.GameConsoleDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameDirectory.ToUpperInvariant())))[..24] + ".json");

    // Called only by the explicit launch button, while the game is not running.
    public ProcessStartInfo PrepareLaunch()
    {
        if (!GamePathUtils.CheckValidity(GameDirectory)) throw new InvalidOperationException("请先设置游戏目录。");
        if (ModStorage.IsGameRunning(GameDirectory))
            throw new InvalidOperationException("游戏已经运行。若本次不是通过助手启动，请先退出游戏，再点「通过助手启动游戏」。");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new Settings(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var arguments = $"-netconport {settings.Port} -netconpassword {settings.Password}";
        return new("steam://run/550//" + Uri.EscapeDataString(arguments) + "/") { UseShellExecute = true };
    }

    public async Task<Connection> ConnectAsync(CancellationToken token = default)
    {
        if (!File.Exists(SettingsPath)) throw new InvalidOperationException("尚未建立游戏连接。请先用「通过助手启动游戏」启动一次。");
        var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(SettingsPath, token));
        if (settings is null || settings.Port is < 1024 or > 65535 || settings.Password is not { Length: 64 } || !settings.Password.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("游戏连接设置无效。请退出游戏，再用助手启动。");
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(IPAddress.Loopback, settings.Port, timeout.Token);
            var connection = new Connection(client, GameDirectory);
            try
            {
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PASS " + settings.Password + "\n"), timeout.Token);
                // Framed echo replies prove authentication; a listening port alone is not success.
                await connection.ExecuteAsync("echo EmberKit_connected", token);
                await connection.ReadPathsAsync(token);
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException("尚未连接到游戏。请通过助手启动，等主菜单出现后重试；已在运行的普通启动需先退出一次。", ex);
        }
        catch { client.Dispose(); throw; }
    }

    public sealed class Connection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly string _gameDirectory;

        internal Connection(TcpClient client, string gameDirectory)
        {
            _client = client;
            _reader = new(client.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true);
            _gameDirectory = NormalizePath(Path.Combine(gameDirectory, "left4dead2"));
        }

        public static string NormalizePath(string text) => text.Replace('\\', '/');

        // Commands are internal constants, never titles, file contents or remote input.
        public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var nonce = Guid.NewGuid().ToString("N");
            var begin = "EK_BEGIN_" + nonce;
            var end = "EK_END_" + nonce;
            var output = new StringBuilder();
            var started = false;
            try
            {
                await _client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"echo {begin}\n{command}\necho {end}\n"), timeout.Token);
                while (true)
                {
                    var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("游戏控制台连接已断开。");
                    var marker = line.Trim().TrimStart('\0', '\x01', '\x02');
                    if (marker == end && started) return output.ToString();
                    if (marker == begin) { started = true; continue; }
                    if (started) output.AppendLine(line);
                    if (output.Length > 4 * 1024 * 1024) throw new IOException("游戏回执过大，请重启游戏后重试。");
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("游戏没有及时返回控制台回执，无法确认操作完成；请等游戏加载完毕后重试。");
            }
        }

        public async Task<string> ReadPathsAsync(CancellationToken token)
        {
            var paths = NormalizePath(await ExecuteAsync("path", token));
            if (!paths.Split('\n').Any(line => !line.Contains(_gameDirectory + "/addons/", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(line, Regex.Escape(_gameDirectory) + "(?:[/\\s\"']|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new IOException("游戏回执中的安装路径与当前 Mod 库不符，未修改游戏入口。");
            return paths;
        }

        public async Task<HashSet<string>> ReadMountedAddonsAsync(CancellationToken token)
        {
            // L4D2's `path` lists directories, not mounted VPKs. This command also
            // lists the official pak01 archives, which are outside our addons folder.
            var reply = await ExecuteAsync("show_addon_load_order", token);
            CheckCommand(reply);
            var prefix = _gameDirectory + "/addons/";
            return NormalizePath(reply).Split('\n').Select(line => line.Trim().Trim('"'))
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static void CheckCommand(string output)
        {
            if (new[] { "Unknown command", "Can't", "Cannot", "not allowed", "failed" }.Any(word => output.Contains(word, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("游戏拒绝了刷新命令：" + output.Trim());
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
