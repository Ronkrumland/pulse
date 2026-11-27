namespace Pulse.Agent.Windows.Services;

public interface ITelemetryPublisher
{
    void PublishTelemetry(float? cpuTemperatureCelsius, float? gpuTemperatureCelsius, DateTimeOffset observedAt);
}
