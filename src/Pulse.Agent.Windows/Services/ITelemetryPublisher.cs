namespace Pulse.Agent.Windows.Services;

public sealed record TelemetrySample(
    DateTimeOffset ObservedAt,
    float? CpuTemperatureCelsius,
    float? GpuTemperatureCelsius,
    float? MemoryUsedMegabytes,
    float? MemoryTotalMegabytes,
    float? GpuMemoryUsedMegabytes,
    float? GpuMemoryTotalMegabytes);

public interface ITelemetryPublisher
{
    void PublishTelemetry(TelemetrySample sample);
}
