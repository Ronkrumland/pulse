using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Collectors;

public interface IGameDetector
{
    GameStatusInfo Detect(IReadOnlyCollection<string> customGameProcesses);
}

internal sealed partial class GameDetector(ILogger<GameDetector> logger) : IGameDetector
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly (string PathFragment, string Launcher)[] KnownGameDirectories =
    [
        (@"\steamapps\common\", "Steam"),
        (@"\Epic Games\", "Epic Games"),
        (@"\XboxGames\", "Xbox"),
        (@"\GOG Galaxy\Games\", "GOG"),
        (@"\Riot Games\", "Riot"),
        (@"\EA Games\", "EA"),
        (@"\Origin Games\", "EA"),
        (@"\Ubisoft Game Launcher\games\", "Ubisoft"),
        (@"\Battle.net\", "Battle.net")
    ];

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui",
        "epicgameslauncher", "epicwebhelper", "eosoverlayrenderer-win64-shipping",
        "riotclientservices", "riotclientux", "riotclientuxrender", "vgc", "vgtray",
        "battle.net", "agent",
        "eadesktop", "eabackgroundservice", "ealocalhostsvc", "easteamproxy", "origin",
        "upc", "ubisoftconnect", "ubisoftgamelauncher",
        "galaxyclient", "galaxyclientservice", "galaxycommunication",
        "easyanticheat", "easyanticheat_eos", "beservice", "bedaisy",
        "crashhandler", "crashhandler64", "crashreportclient", "unitycrashhandler64", "unrealcefsubprocess",
        "launcher", "paradox launcher", "redprelauncher", "2klauncher"
    };

    private readonly Dictionary<int, string> _steamAppNameCache = [];

    public GameStatusInfo Detect(IReadOnlyCollection<string> customGameProcesses)
    {
        try
        {
            // 1. Check Steam's active RunningAppID registry value first (very reliable for Steam games)
            var steamGame = TryDetectSteamGame();
            if (steamGame is not null)
            {
                return steamGame;
            }

            // 2. Scan running processes for custom configured game processes or known launcher install paths
            var customSet = new HashSet<string>(
                customGameProcesses
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(NormalizeProcessName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var procName = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(procName) || ExcludedProcesses.Contains(procName))
                    {
                        continue;
                    }

                    if (customSet.Contains(procName))
                    {
                        return new GameStatusInfo(true, TryGetFriendlyTitle(process, procName), procName, "Custom");
                    }

                    var path = TryGetProcessPath(process.Id);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var match = MatchKnownGamePath(path, procName);
                    if (match is not null)
                    {
                        return match;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while detecting running game");
        }

        return new GameStatusInfo(false, null, null, null);
    }

    private GameStatusInfo? TryDetectSteamGame()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("RunningAppID") is not int appId || appId <= 0)
            {
                return null;
            }

            var steamPath = key.GetValue("SteamPath") as string;
            var name = ResolveSteamAppName(appId, steamPath) ?? $"Steam App {appId}";
            return new GameStatusInfo(true, name, null, "Steam");
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveSteamAppName(int appId, string? steamPath)
    {
        if (_steamAppNameCache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath.Replace('/', '\\') };
        var libraryFile = Path.Combine(steamPath.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            try
            {
                var content = File.ReadAllText(libraryFile);
                foreach (Match m in SteamPathRegex().Matches(content))
                {
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            catch
            {
                // Ignore VDF read errors
            }
        }

        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(manifest);
                var match = SteamNameRegex().Match(content);
                if (match.Success)
                {
                    var name = match.Groups[1].Value.Trim();
                    _steamAppNameCache[appId] = name;
                    return name;
                }
            }
            catch
            {
                // Ignore manifest read errors
            }
        }

        return null;
    }

    internal static GameStatusInfo? MatchKnownGamePath(string fullPath, string processName)
    {
        if (ExcludedProcesses.Contains(processName))
        {
            return null;
        }

        foreach (var (fragment, launcher) in KnownGameDirectories)
        {
            var idx = fullPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var after = fullPath[(idx + fragment.Length)..];
            var slash = after.IndexOf('\\');
            var folderName = slash > 0 ? after[..slash] : Path.GetFileNameWithoutExtension(fullPath);
            if (folderName.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("DirectX", StringComparison.OrdinalIgnoreCase)
                || folderName.Equals("Launcher", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new GameStatusInfo(true, folderName, processName, launcher);
        }

        return null;
    }

    private static string TryGetFriendlyTitle(Process process, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
            {
                return process.MainWindowTitle.Trim();
            }
        }
        catch
        {
            // Ignore
        }

        return fallback;
    }

    private static string NormalizeProcessName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static string? TryGetProcessPath(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamNameRegex();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
