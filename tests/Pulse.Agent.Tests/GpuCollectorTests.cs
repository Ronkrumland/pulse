using LibreHardwareMonitor.Hardware;
using NSubstitute;
using Pulse.Agent.Windows.Collectors;
using Xunit;

namespace Pulse.Agent.Tests;

public class GpuCollectorTests
{
    [Fact]
    public void ReadsHighestCurrentGpuTemperatureAcrossSupportedVendorsAfterRefreshingEachGpu()
    {
        var nvidiaGpu = Device(HardwareType.GpuNvidia, Sensor("GPU Core", 52));
        var amdCore = Sensor("GPU Core", 48);
        var currentAmdTemperature = 48f;
        amdCore.Value.Returns(_ => currentAmdTemperature);
        var amdGpu = Device(HardwareType.GpuAmd, amdCore, Sensor("GPU Hot Spot", 64));
        var intelGpu = Device(HardwareType.GpuIntel, Sensor("GPU Core", 58));
        var cpu = Device(HardwareType.Cpu, Sensor("CPU Package", 95));
        amdGpu.When(gpu => gpu.Update()).Do(_ => currentAmdTemperature = 71.5f);

        Assert.Equal(71.5f, GpuCollector.ReadTemperatureCelsius([nvidiaGpu, amdGpu, intelGpu, cpu]));
        nvidiaGpu.Received(1).Update();
        amdGpu.Received(1).Update();
        intelGpu.Received(1).Update();
        cpu.DidNotReceive().Update();
    }

    [Fact]
    public void ReturnsNullWhenNoUsableTemperatureExists()
    {
        var gpu = Device(HardwareType.GpuNvidia,
            Sensor("GPU Core", null),
            Sensor("GPU Hot Spot", float.NaN),
            Sensor("GPU Memory Junction", float.PositiveInfinity),
            Sensor("GPU Core", 85, SensorType.Load));

        Assert.Null(GpuCollector.ReadTemperatureCelsius([gpu]));
        Assert.Null(GpuCollector.ReadTemperatureCelsius([]));
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
