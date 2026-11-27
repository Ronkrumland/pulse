using LibreHardwareMonitor.Hardware;

namespace Pulse.Agent.Windows.Collectors;

internal sealed class HardwareMonitor : IHardwareProvider, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Action _open;
    private readonly Action _close;
    private readonly Func<IEnumerable<IHardware>> _getHardware;
    private bool _isOpen;
    private bool _isDisposed;

    public HardwareMonitor()
        : this(new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true
        })
    {
    }

    private HardwareMonitor(Computer computer)
        : this(computer.Open, computer.Close, () => computer.Hardware)
    {
    }

    internal HardwareMonitor(Action open, Action close, Func<IEnumerable<IHardware>> getHardware)
    {
        _open = open;
        _close = close;
        _getHardware = getHardware;
    }

    public IEnumerable<IHardware> GetHardware()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (!_isOpen)
            {
                _open();
                _isOpen = true;
            }

            return _getHardware();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_isOpen)
            {
                _close();
                _isOpen = false;
            }

            _isDisposed = true;
        }
    }
}
