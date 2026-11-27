namespace Pulse.Agent.Windows.Collectors;

public interface ICpuTemperatureSource
{
    float? ReadTemperatureCelsius();
}
