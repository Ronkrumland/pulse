namespace Pulse.Contracts;

public sealed record CpuTemperatureTelemetry(string DeviceId, DateTimeOffset ObservedAt, float CpuTemperatureCelsius);
