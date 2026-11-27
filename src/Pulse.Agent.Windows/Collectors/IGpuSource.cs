namespace Pulse.Agent.Windows.Collectors;

public sealed record GpuReading(
    float? TemperatureCelsius,
    float? MemoryUsedMegabytes,
    float? MemoryTotalMegabytes);

public interface IGpuSource
{
    GpuReading Read();
}
