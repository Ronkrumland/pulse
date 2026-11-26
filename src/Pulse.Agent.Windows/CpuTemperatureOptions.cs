namespace Pulse.Agent.Windows;

public sealed class CpuTemperatureOptions
{
    public const string SectionName = "CpuTemperature";
    internal const int MaxPollingIntervalSeconds = (int)((uint.MaxValue - 1) / 1000);

    public int PollingIntervalSeconds { get; set; } = 5;
}
