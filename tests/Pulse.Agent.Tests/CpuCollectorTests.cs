using LibreHardwareMonitor.Hardware;
using NSubstitute;
using Pulse.Agent.Windows.Collectors;
using Xunit;

namespace Pulse.Agent.Tests;

public class CpuCollectorTests
{
    [Fact]
    public void ReadsHighestCurrentCpuTemperatureAfterRefreshingEachCpu()
    {
        var firstCpu = Device(HardwareType.Cpu, Sensor("CPU Package", 40));
        var core = Sensor("CPU Core #1", 35);
        var currentTemperature = 35f;
        core.Value.Returns(_ => currentTemperature);
        var secondCpu = Device(HardwareType.Cpu, core);
        var gpu = Device(HardwareType.GpuNvidia, Sensor("GPU Core", 95));
        secondCpu.When(cpu => cpu.Update()).Do(_ => currentTemperature = 72.5f);

        Assert.Equal(72.5f, CpuCollector.ReadTemperatureCelsius([firstCpu, secondCpu, gpu]));
        firstCpu.Received(1).Update();
        secondCpu.Received(1).Update();
        gpu.DidNotReceive().Update();
    }

    [Fact]
    public void IgnoresThermalHeadroomAndControlTemperature()
    {
        var cpu = Device(HardwareType.Cpu,
            Sensor("CPU Core #1 Distance to TjMax", 70),
            Sensor("Core (Tctl)", 65),
            Sensor("Core (Tdie)", 45));

        Assert.Equal(45f, CpuCollector.ReadTemperatureCelsius([cpu]));
    }

    [Fact]
    public void ReturnsNullWhenNoUsableTemperatureExists()
    {
        var cpu = Device(HardwareType.Cpu,
            Sensor("CPU Package", null),
            Sensor("CPU Core #1", float.NaN),
            Sensor("CPU Core #2", float.PositiveInfinity),
            Sensor("Core (Tctl/Tdie)", 0),
            Sensor("CPU Load", 90, SensorType.Load));

        Assert.Null(CpuCollector.ReadTemperatureCelsius([cpu]));
        Assert.Null(CpuCollector.ReadTemperatureCelsius([]));
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
