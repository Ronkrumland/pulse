using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal sealed class GpuCollector : IGpuTemperatureSource, IDisposable
{
    private readonly Computer _computer = new() { IsGpuEnabled = true };

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
            if (device.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
            {
                continue;
            }

            device.Update();
            foreach (var sensor in device.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature
                    || sensor.Value is not float value
                    || !float.IsFinite(value))
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
