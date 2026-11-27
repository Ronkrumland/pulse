namespace Pulse.Agent.Windows.Services;

internal sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string DeviceId { get; set; } = Environment.MachineName;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}
