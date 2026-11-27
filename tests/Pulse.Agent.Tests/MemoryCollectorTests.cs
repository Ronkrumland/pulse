using LibreHardwareMonitor.Hardware;
using NSubstitute;
using Pulse.Agent.Windows.Collectors;
using Xunit;

namespace Pulse.Agent.Tests;

public class MemoryCollectorTests
{
    [Fact]
    public void ReadsPhysicalMemoryInMegabytesAndIgnoresVirtualMemoryAndOtherHardware()
    {
        var memory = Device(HardwareType.Memory,
            Sensor("Memory Used", 12.5f),
            Sensor("Memory Available", 19.5f),
            Sensor("Virtual Memory Used", 24f),
            Sensor("Virtual Memory Available", 40f),
            Sensor("Memory", 39.1f, SensorType.Load));
        var cpu = Device(HardwareType.Cpu, Sensor("Memory Used", 99f));

        var reading = MemoryCollector.ReadMemory([memory, cpu]);

        Assert.Equal(new MemoryReading(12.5f * 1024f, 32f * 1024f), reading);
        memory.Received(1).Update();
        cpu.DidNotReceive().Update();
    }

    [Fact]
    public void ReturnsUsedMemoryWhenAvailableMemoryIsMissing()
    {
        var memory = Device(HardwareType.Memory, Sensor("Memory Used", 8f));

        var reading = MemoryCollector.ReadMemory([memory]);

        Assert.Equal(new MemoryReading(8f * 1024f, null), reading);
    }

    [Fact]
    public void ReturnsNullWhenNoUsableMemorySensorExists()
    {
        var memory = Device(HardwareType.Memory,
            Sensor("Memory Used", null),
            Sensor("Memory Used", float.NaN),
            Sensor("Memory Used", -1f),
            Sensor("Memory Available", 16f),
            Sensor("Virtual Memory Used", 10f),
            Sensor("Memory", 50f, SensorType.Load));

        Assert.Null(MemoryCollector.ReadMemory([memory]));
        Assert.Null(MemoryCollector.ReadMemory([]));
    }

    private static IHardware Device(HardwareType type, params ISensor[] sensors)
    {
        var device = Substitute.For<IHardware>();
        device.HardwareType.Returns(type);
        device.Sensors.Returns(sensors);
        return device;
    }

    private static ISensor Sensor(string name, float? value, SensorType type = SensorType.Data)
    {
        var sensor = Substitute.For<ISensor>();
        sensor.Name.Returns(name);
        sensor.SensorType.Returns(type);
        sensor.Value.Returns(value);
        return sensor;
    }
}
