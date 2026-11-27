using System.Runtime.InteropServices;
using Pulse.Agent.Windows.Session.Collectors;

namespace Pulse.Agent.Windows.Session.UI;

internal sealed class RawInputMessageWindow : NativeWindow, IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_HEADER = 0x10000005;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint RIM_TYPEKEYBOARD = 1;

    private readonly IKeyboardCollector _keyboardCollector;
    private bool _disposed;

    public RawInputMessageWindow(IKeyboardCollector keyboardCollector)
    {
        _keyboardCollector = keyboardCollector;
        CreateHandle(new CreateParams
        {
            Caption = "PulseRawInputSink",
            Style = 0
        });

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle
            }
        };

        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            var header = new RAWINPUTHEADER();
            if (GetRawInputData(m.LParam, RID_HEADER, ref header, ref headerSize, headerSize) != uint.MaxValue
                && header.dwType == RIM_TYPEKEYBOARD
                && header.hDevice != nint.Zero)
            {
                _keyboardCollector.RecordKeyboardInput(header.hDevice);
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (Handle != nint.Zero)
            {
                DestroyHandle();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint hRawInput,
        uint uiCommand,
        ref RAWINPUTHEADER pData,
        ref uint pcbSize,
        uint cbSizeHeader);
}
