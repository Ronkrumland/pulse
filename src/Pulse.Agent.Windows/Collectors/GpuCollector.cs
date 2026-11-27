using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal sealed class GpuCollector(IHardwareProvider hardwareProvider) : IGpuSource
{
    public GpuReading Read() =>
        Read(hardwareProvider.GetHardware());

    internal static GpuReading Read(IEnumerable<IHardware> hardware)
    {
        float? highestTemperature = null;
        float? selectedMemoryUsed = null;
        float? selectedMemoryTotal = null;

        foreach (var device in hardware)
        {
            if (device.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
            {
                continue;
            }

            device.Update();

            float? deviceMemoryUsed = null;
            float? deviceDedicatedMemoryUsed = null;
            float? deviceMemoryFree = null;
            float? deviceMemoryTotal = null;

            foreach (var sensor in device.Sensors)
            {
                if (sensor.Value is not float value || !float.IsFinite(value))
                {
                    continue;
                }

                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (highestTemperature is null || value > highestTemperature)
                    {
                        highestTemperature = value;
                    }
                }
                else if (sensor.SensorType == SensorType.SmallData && value >= 0)
                {
                    switch (sensor.Name)
                    {
                        case "GPU Memory Used":
                            deviceMemoryUsed = value;
                            break;
                        case "D3D Dedicated Memory Used":
                            deviceDedicatedMemoryUsed = value;
                            break;
                        case "GPU Memory Free":
                            deviceMemoryFree = value;
                            break;
                        case "GPU Memory Total":
                            deviceMemoryTotal = value;
                            break;
                    }
                }
            }

            var used = deviceMemoryUsed ?? deviceDedicatedMemoryUsed;
            float? total = deviceMemoryTotal is > 0
                ? deviceMemoryTotal
                : used is float u && deviceMemoryFree is float f && u + f > 0
                    ? u + f
                    : null;

            if (used is not null && IsPreferredGpuMemory(used.Value, total, selectedMemoryUsed, selectedMemoryTotal))
            {
                selectedMemoryUsed = used;
                selectedMemoryTotal = total;
            }
        }

        return new GpuReading(highestTemperature, selectedMemoryUsed, selectedMemoryTotal);
    }

    private static bool IsPreferredGpuMemory(float candidateUsed, float? candidateTotal, float? currentUsed, float? currentTotal)
    {
        if (currentUsed is null)
        {
            return true;
        }

        if (candidateTotal is not null && currentTotal is null)
        {
            return true;
        }

        if (candidateTotal is not null && currentTotal is not null)
        {
            return candidateTotal.Value > currentTotal.Value;
        }

        return currentTotal is null && candidateUsed > currentUsed.Value;
    }
}
