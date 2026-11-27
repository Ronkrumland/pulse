using LibreHardwareMonitor.Hardware;
using NSubstitute;
using Pulse.Agent.Windows.Collectors;
using Xunit;

namespace Pulse.Agent.Tests;

public class GpuCollectorTests
{
    [Fact]
    public void ReadsHighestGpuTemperatureAndPrimaryGpuMemoryAcrossSupportedVendors()
    {
        var nvidiaGpu = Device(HardwareType.GpuNvidia,
            Sensor("GPU Core", 52),
            Sensor("GPU Memory Used", 6144, SensorType.SmallData),
            Sensor("GPU Memory Total", 12288, SensorType.SmallData));
        var amdCore = Sensor("GPU Core", 48);
        var currentAmdTemperature = 48f;
        amdCore.Value.Returns(_ => currentAmdTemperature);
        var amdGpu = Device(HardwareType.GpuAmd,
            amdCore,
            Sensor("GPU Hot Spot", 64),
            Sensor("GPU Memory Used", 2048, SensorType.SmallData),
            Sensor("GPU Memory Total", 8192, SensorType.SmallData));
        var intelGpu = Device(HardwareType.GpuIntel,
            Sensor("GPU Core", 58),
            Sensor("D3D Dedicated Memory Used", 256, SensorType.SmallData));
        var cpu = Device(HardwareType.Cpu, Sensor("CPU Package", 95));
        amdGpu.When(gpu => gpu.Update()).Do(_ => currentAmdTemperature = 71.5f);

        var reading = GpuCollector.Read([nvidiaGpu, amdGpu, intelGpu, cpu]);

        Assert.Equal(new GpuReading(71.5f, 6144f, 12288f), reading);
        nvidiaGpu.Received(1).Update();
        amdGpu.Received(1).Update();
        intelGpu.Received(1).Update();
        cpu.DidNotReceive().Update();
    }

    [Fact]
    public void DerivesGpuMemoryTotalFromUsedAndFreeAndSupportsD3dDedicatedFallback()
    {
        var amdGpu = Device(HardwareType.GpuAmd,
            Sensor("GPU Memory Used", 3072, SensorType.SmallData),
            Sensor("GPU Memory Free", 5120, SensorType.SmallData));

        Assert.Equal(new GpuReading(null, 3072f, 8192f), GpuCollector.Read([amdGpu]));

        var intelGpu = Device(HardwareType.GpuIntel,
            Sensor("D3D Dedicated Memory Used", 512, SensorType.SmallData));

        Assert.Equal(new GpuReading(null, 512f, null), GpuCollector.Read([intelGpu]));
    }

    [Fact]
    public void ReturnsEmptyReadingWhenNoUsableSensorExists()
    {
        var gpu = Device(HardwareType.GpuNvidia,
            Sensor("GPU Core", null),
            Sensor("GPU Hot Spot", float.NaN),
            Sensor("GPU Memory Junction", float.PositiveInfinity),
            Sensor("GPU Memory Used", -5, SensorType.SmallData),
            Sensor("GPU Memory Total", float.NaN, SensorType.SmallData),
            Sensor("GPU Core", 85, SensorType.Load));

        Assert.Equal(new GpuReading(null, null, null), GpuCollector.Read([gpu]));
        Assert.Equal(new GpuReading(null, null, null), GpuCollector.Read([]));
    }

    private static IHardware Device(HardwareType type, params ISensor[] sensors)
    {
        var device = Substitute.For<IHardware>();
        device.HardwareType.Returns(type);
        device.Sensors.Returns(sensors);
        return device;
    }

    private static ISensor Sensor(string name, float? value, SensorType type = SensorType.Temperature)
    {
        var sensor = Substitute.For<ISensor>();
        sensor.Name.Returns(name);
        sensor.SensorType.Returns(type);
        sensor.Value.Returns(value);
        return sensor;
    }
}
