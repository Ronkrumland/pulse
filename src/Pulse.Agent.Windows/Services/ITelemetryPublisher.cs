namespace Pulse.Agent.Windows.Services;

public interface ITelemetryPublisher
{
    void PublishCpuTemperature(float temperatureCelsius, DateTimeOffset observedAt);
}
