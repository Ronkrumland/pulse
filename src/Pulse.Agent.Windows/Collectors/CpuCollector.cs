using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal sealed class CpuCollector : ICpuTemperatureSource, IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };

    public float? ReadTemperatureCelsius()
    {
        _computer.Open();
        return ReadTemperatureCelsius(_computer.Hardware);
    }

    internal static float? ReadTemperatureCelsius(IEnumerable<IHardware> hardware)
    {
        float? highestTemperature = null;

        foreach (var device in hardware)
        {
            if (device.HardwareType != HardwareType.Cpu)
            {
                continue;
            }

            device.Update();
            foreach (var sensor in device.Sensors)
            {
                // LibreHardwareMonitor also classifies thermal headroom and AMD's control value as temperatures.
                if (sensor.SensorType != SensorType.Temperature
                    || sensor.Name.EndsWith(" Distance to TjMax", StringComparison.Ordinal)
                    || sensor.Name == "Core (Tctl)"
                    || sensor.Value is not float value
                    || !float.IsFinite(value)
                    // On newer AMD CPUs, a zero temperature register decodes to 0 °C.
                    || (value == 0 && sensor.Name == "Core (Tctl/Tdie)"))
                {
                    continue;
                }

                if (highestTemperature is null || value > highestTemperature)
                {
                    highestTemperature = value;
                }
            }
        }

        return highestTemperature;
    }

    public void Dispose() => _computer.Close();
}
