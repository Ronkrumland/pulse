using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Pulse.Agent.Windows;

internal static class AgentConfiguration
{
    public const string ConfigFileName = "agent.json";

    public static string GetMachineConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Pulse",
            ConfigFileName);

    public static void AddMachineConfig(IConfigurationBuilder configuration, string? configPath = null)
    {
        var path = configPath ?? GetMachineConfigPath();
        var source = new JsonConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = true
        };
        source.ResolveFileProvider();

        var insertIndex = FindInsertIndex(configuration.Sources);
        configuration.Sources.Insert(insertIndex, source);
    }

    private static int FindInsertIndex(IList<IConfigurationSource> sources)
    {
        var lastAppSettingsIndex = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource json
                && json.Path is not null
                && Path.GetFileName(json.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                lastAppSettingsIndex = i;
            }
        }

        return lastAppSettingsIndex >= 0 ? lastAppSettingsIndex + 1 : sources.Count;
    }
}
