namespace Pulse.Agent.Windows;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";
    internal const int MaxPollingIntervalSeconds = (int)((uint.MaxValue - 1) / 1000);

    public int PollingIntervalSeconds { get; set; } = 5;
}
