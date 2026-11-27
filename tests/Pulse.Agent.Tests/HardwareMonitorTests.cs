using LibreHardwareMonitor.Hardware;
using NSubstitute;
using Pulse.Agent.Windows.Collectors;
using Xunit;

namespace Pulse.Agent.Tests;

public class HardwareMonitorTests
{
    [Fact]
    public void OpensOnceLazilyAndClosesOnceOnDispose()
    {
        var openCount = 0;
        var closeCount = 0;
        var cpu = Substitute.For<IHardware>();
        var monitor = new HardwareMonitor(
            () => openCount++,
            () => closeCount++,
            () => [cpu]);

        Assert.Equal(0, openCount);
        Assert.Equal(0, closeCount);

        var first = monitor.GetHardware();
        var second = monitor.GetHardware();

        Assert.Equal(1, openCount);
        Assert.Equal([cpu], first);
        Assert.Equal([cpu], second);

        monitor.Dispose();
        monitor.Dispose();

        Assert.Equal(1, closeCount);
        Assert.Throws<ObjectDisposedException>(() => monitor.GetHardware());
    }

    [Fact]
    public void DoesNotCloseWhenDisposedBeforeOpening()
    {
        var closeCount = 0;
        var monitor = new HardwareMonitor(
            () => { },
            () => closeCount++,
            () => []);

        monitor.Dispose();

        Assert.Equal(0, closeCount);
    }
}
