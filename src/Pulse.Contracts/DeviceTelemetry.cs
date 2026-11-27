namespace Pulse.Contracts;

public sealed record DeviceTelemetry(
    string DeviceId,
    DateTimeOffset ObservedAt,
    float? CpuTemperatureCelsius,
    float? GpuTemperatureCelsius,
    float? MemoryUsedMegabytes,
    float? MemoryTotalMegabytes,
    float? GpuMemoryUsedMegabytes,
    float? GpuMemoryTotalMegabytes);
