namespace Pulse.Contracts;

public sealed record DeviceTelemetry(
    string DeviceId,
    DateTimeOffset ObservedAt,
    float? CpuTemperatureCelsius,
    float? GpuTemperatureCelsius);
