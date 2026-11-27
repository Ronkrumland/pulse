using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Pulse.Agent.Windows.Session.Services;

public sealed class MqttSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string DeviceId { get; set; } = Environment.MachineName;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);

    public MqttSettings Clone() => new()
    {
        Host = Host,
        Port = Port,
        DeviceId = DeviceId,
        Username = Username,
        Password = Password,
        UseTls = UseTls
    };
}

public sealed class SessionAppSettings
{
    public int PollingIntervalSeconds { get; set; } = 5;
    public string ServiceName { get; set; } = "Pulse Agent";
    public List<string> CustomGameProcesses { get; set; } = [];
    public MqttSettings Mqtt { get; set; } = new();

    public SessionAppSettings Clone() => new()
    {
        PollingIntervalSeconds = PollingIntervalSeconds,
        ServiceName = ServiceName,
        CustomGameProcesses = [.. CustomGameProcesses],
        Mqtt = Mqtt.Clone()
    };
}

public sealed class AgentServiceConfig
{
    public int PollingIntervalSeconds { get; set; } = 5;
    public MqttSettings Mqtt { get; set; } = new();
}

internal sealed class SessionSettingsStore
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseSession";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _sessionConfigPath;
    private readonly string _agentConfigPath;

    public SessionSettingsStore(string? sessionConfigPath = null, string? agentConfigPath = null)
    {
        _sessionConfigPath = sessionConfigPath ?? GetDefaultSessionConfigPath();
        _agentConfigPath = agentConfigPath ?? GetDefaultAgentConfigPath();
    }

    public string SessionConfigPath => _sessionConfigPath;
    public string AgentConfigPath => _agentConfigPath;

    public static string GetDefaultSessionConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pulse",
            "session.json");

    public static string GetDefaultAgentConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Pulse",
            "agent.json");

    public SessionAppSettings LoadSessionSettings()
    {
        if (File.Exists(_sessionConfigPath))
        {
            try
            {
                var json = File.ReadAllText(_sessionConfigPath);
                var loaded = JsonSerializer.Deserialize<SessionAppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
            catch
            {
                // Fall back to seeding defaults
            }
        }

        // Seed initial MQTT settings from %ProgramData%\Pulse\agent.json if present
        var seeded = new SessionAppSettings();
        var agentConfig = LoadAgentConfig();
        seeded.Mqtt = agentConfig.Mqtt.Clone();
        Normalize(seeded);
        return seeded;
    }

    public void SaveSessionSettings(SessionAppSettings settings)
    {
        Normalize(settings);
        var dir = Path.GetDirectoryName(_sessionConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_sessionConfigPath, json);
    }

    public AgentServiceConfig LoadAgentConfig()
    {
        var result = new AgentServiceConfig();
        if (!File.Exists(_agentConfigPath))
        {
            return result;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_agentConfigPath)) as JsonObject;
            if (node is null)
            {
                return result;
            }

            if (node["Telemetry"] is JsonObject telemetry
                && telemetry["PollingIntervalSeconds"] is JsonValue pollVal
                && pollVal.TryGetValue<int>(out var pollSec)
                && pollSec > 0)
            {
                result.PollingIntervalSeconds = pollSec;
            }

            if (node["Mqtt"] is JsonObject mqtt)
            {
                result.Mqtt.Host = mqtt["Host"]?.GetValue<string>() ?? "";
                if (mqtt["Port"] is JsonValue portVal && portVal.TryGetValue<int>(out var port) && port is >= 1 and <= 65535)
                {
                    result.Mqtt.Port = port;
                }

                var devId = mqtt["DeviceId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(devId))
                {
                    result.Mqtt.DeviceId = devId;
                }

                result.Mqtt.Username = mqtt["Username"]?.GetValue<string>();
                result.Mqtt.Password = mqtt["Password"]?.GetValue<string>();
                if (mqtt["UseTls"] is JsonValue tlsVal && tlsVal.TryGetValue<bool>(out var useTls))
                {
                    result.Mqtt.UseTls = useTls;
                }
            }
        }
        catch
        {
            // Return defaults if unreadable
        }

        return result;
    }

    public (bool Succeeded, string Message) SaveAgentConfig(AgentServiceConfig config)
    {
        var root = new JsonObject();
        if (File.Exists(_agentConfigPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(_agentConfigPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }

        var telemetry = root["Telemetry"] as JsonObject ?? new JsonObject();
        telemetry["PollingIntervalSeconds"] = Math.Clamp(config.PollingIntervalSeconds, 1, 86400);
        root["Telemetry"] = telemetry;

        var mqtt = root["Mqtt"] as JsonObject ?? new JsonObject();
        mqtt["Host"] = config.Mqtt.Host.Trim();
        mqtt["Port"] = Math.Clamp(config.Mqtt.Port, 1, 65535);
        mqtt["DeviceId"] = string.IsNullOrWhiteSpace(config.Mqtt.DeviceId)
            ? Environment.MachineName
            : config.Mqtt.DeviceId.Trim();
        mqtt["Username"] = string.IsNullOrWhiteSpace(config.Mqtt.Username) ? "" : config.Mqtt.Username.Trim();
        if (!string.IsNullOrEmpty(config.Mqtt.Password))
        {
            mqtt["Password"] = config.Mqtt.Password;
        }
        else
        {
            mqtt.Remove("Password");
        }

        mqtt["UseTls"] = config.Mqtt.UseTls;
        root["Mqtt"] = mqtt;

        var content = root.ToJsonString(JsonOptions);
        try
        {
            var dir = Path.GetDirectoryName(_agentConfigPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_agentConfigPath, content);
            return (true, $"Saved service configuration to {_agentConfigPath}.");
        }
        catch (UnauthorizedAccessException)
        {
            return SaveAgentConfigElevated(content);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to save agent.json: {ex.Message}");
        }
    }

    private (bool Succeeded, string Message) SaveAgentConfigElevated(string content)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pulse-agent-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, content);
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (false, "Could not determine executable path for elevated config save.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--write-agent-config \"{tempPath}\" \"{_agentConfigPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "Elevated config save did not start.");
            }

            proc.WaitForExit(15000);
            return proc.ExitCode == 0
                ? (true, $"Saved service configuration to {_agentConfigPath} (elevated).")
                : (false, $"Elevated config save failed (exit code {proc.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator permission prompt was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"Elevated config save failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
            return key?.GetValue(RunValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true)
            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static void Normalize(SessionAppSettings settings)
    {
        settings.PollingIntervalSeconds = Math.Clamp(settings.PollingIntervalSeconds, 1, 86400);
        if (string.IsNullOrWhiteSpace(settings.ServiceName))
        {
            settings.ServiceName = "Pulse Agent";
        }

        settings.CustomGameProcesses ??= [];
        settings.Mqtt ??= new MqttSettings();
        if (string.IsNullOrWhiteSpace(settings.Mqtt.DeviceId))
        {
            settings.Mqtt.DeviceId = Environment.MachineName;
        }

        if (settings.Mqtt.Port is < 1 or > 65535)
        {
            settings.Mqtt.Port = 1883;
        }
    }
}
