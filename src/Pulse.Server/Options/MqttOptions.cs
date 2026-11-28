namespace Pulse.Server.Options;

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "pulse-server";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int StaleAfterSeconds { get; set; } = 30;

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}
