using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal interface IHardwareProvider
{
    IEnumerable<IHardware> GetHardware();
}
