namespace Pulse.Agent.Windows.Collectors;

public interface IGpuTemperatureSource
{
    float? ReadTemperatureCelsius();
}
