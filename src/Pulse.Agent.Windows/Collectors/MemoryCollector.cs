using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal sealed class MemoryCollector : IMemorySource, IDisposable
{
    private const float MegabytesPerGigabyte = 1024f;
    private readonly Computer _computer = new() { IsMemoryEnabled = true };

    public MemoryReading? ReadMemory()
    {
        _computer.Open();
        return ReadMemory(_computer.Hardware);
    }

    internal static MemoryReading? ReadMemory(IEnumerable<IHardware> hardware)
    {
        float? usedGigabytes = null;
        float? availableGigabytes = null;

        foreach (var device in hardware)
        {
            if (device.HardwareType != HardwareType.Memory)
            {
                continue;
            }

            device.Update();
            foreach (var sensor in device.Sensors)
            {
                if (sensor.SensorType != SensorType.Data
                    || sensor.Value is not float value
                    || !float.IsFinite(value)
                    || value < 0)
                {
                    continue;
                }

                if (sensor.Name == "Memory Used")
                {
                    usedGigabytes = value;
                }
                else if (sensor.Name == "Memory Available")
                {
                    availableGigabytes = value;
                }
            }
        }

        if (usedGigabytes is null)
        {
            return null;
        }

        var usedMegabytes = usedGigabytes.Value * MegabytesPerGigabyte;
        float? totalMegabytes = availableGigabytes is float available
            ? (usedGigabytes.Value + available) * MegabytesPerGigabyte
            : null;

        return new MemoryReading(usedMegabytes, totalMegabytes);
    }

    public void Dispose() => _computer.Close();
}
