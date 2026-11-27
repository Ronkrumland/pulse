using Microsoft.Extensions.Configuration;
using Pulse.Agent.Windows;
using Xunit;

namespace Pulse.Agent.Tests;

public class AgentConfigurationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pulse-config-tests-{Guid.NewGuid():N}");

    public AgentConfigurationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void MachineConfigOverridesAppSettingsWhilePreservingHigherPrecedenceSources()
    {
        var appSettingsPath = Path.Combine(_tempDir, "appsettings.json");
        var machineConfigPath = Path.Combine(_tempDir, "agent.json");

        File.WriteAllText(appSettingsPath, """
            {
              "Telemetry": { "PollingIntervalSeconds": 5 },
              "Mqtt": { "Host": "default-host", "Port": 1883 }
            }
            """);

        File.WriteAllText(machineConfigPath, """
            {
              "Telemetry": { "PollingIntervalSeconds": 10 },
              "Mqtt": { "Host": "broker.local", "Port": 8883 }
            }
            """);

        var builder = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mqtt:Port"] = "1884"
            });

        AgentConfiguration.AddMachineConfig(builder, machineConfigPath);
        var config = builder.Build();

        Assert.Equal("10", config["Telemetry:PollingIntervalSeconds"]);
        Assert.Equal("broker.local", config["Mqtt:Host"]);
        Assert.Equal("1884", config["Mqtt:Port"]);
    }

    [Fact]
    public void MissingMachineConfigFileIsIgnored()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.json");
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:PollingIntervalSeconds"] = "5"
            });

        AgentConfiguration.AddMachineConfig(builder, missingPath);
        var config = builder.Build();

        Assert.Equal("5", config["Telemetry:PollingIntervalSeconds"]);
    }

    [Fact]
    public void MachineConfigPathUsesCommonApplicationDataPulseAgentJson()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Pulse",
            "agent.json");

        Assert.Equal(expected, AgentConfiguration.GetMachineConfigPath());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
