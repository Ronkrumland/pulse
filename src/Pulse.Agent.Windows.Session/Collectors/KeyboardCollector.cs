using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Collectors;

public interface IKeyboardCollector
{
    void RecordKeyboardInput(nint rawInputDeviceHandle);
    KeyboardInfo ReadCurrentKeyboard(IReadOnlyList<PeripheralInfo> logitechPeripherals);
}

internal sealed class KeyboardCollector(ILogger<KeyboardCollector> logger) : IKeyboardCollector
{
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIDI_DEVICENAME = 0x20000007;

    private long _lastActiveHandle;

    public void RecordKeyboardInput(nint rawInputDeviceHandle)
    {
        if (rawInputDeviceHandle != nint.Zero)
        {
            Interlocked.Exchange(ref _lastActiveHandle, rawInputDeviceHandle.ToInt64());
        }
    }

    public KeyboardInfo ReadCurrentKeyboard(IReadOnlyList<PeripheralInfo> logitechPeripherals)
    {
        try
        {
            var activeHandle = new nint(Interlocked.Read(ref _lastActiveHandle));
            var devicePath = activeHandle != nint.Zero ? GetRawInputDevicePath(activeHandle) : null;

            if (string.IsNullOrWhiteSpace(devicePath))
            {
                devicePath = FindFirstPhysicalKeyboardPath();
            }

            if (string.IsNullOrWhiteSpace(devicePath))
            {
                return new KeyboardInfo(null, null, null, null);
            }

            var instanceId = ConvertDevicePathToInstanceId(devicePath);
            var connectionType = InferConnectionType(devicePath);
            var allDevices = WindowsNativePnp.EnumeratePresentDevices();

            var matched = allDevices.FirstOrDefault(d =>
                d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));

            string? name = matched?.BestName;
            int? battery = matched?.BluetoothBatteryPercent;
            bool? isCharging = null;

            // Search siblings in the same PnP container for a better friendly name or Bluetooth battery level
            if (matched?.ContainerId is Guid containerId)
            {
                var siblings = allDevices.Where(d => d.ContainerId == containerId).ToList();
                var betterName = siblings
                    .Select(s => s.FriendlyName ?? s.BusReportedName)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)
                        && !n.Contains("HID", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("USB Input", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(betterName))
                {
                    name = betterName.Trim();
                }

                battery ??= siblings.Select(s => s.BluetoothBatteryPercent).FirstOrDefault(b => b.HasValue);
            }

            // If this is a Logitech keyboard (VID_046D) or matches a detected Logitech keyboard, enrich with HID++ battery
            if (devicePath.Contains("VID_046D", StringComparison.OrdinalIgnoreCase)
                || devicePath.Contains("VID&0002046D", StringComparison.OrdinalIgnoreCase))
            {
                var logiKb = logitechPeripherals.FirstOrDefault(p =>
                    p.Category.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) && p.IsConnected);
                if (logiKb is not null)
                {
                    name = logiKb.Name;
                    battery ??= logiKb.BatteryPercent;
                    isCharging ??= logiKb.IsCharging;
                    if (!string.IsNullOrWhiteSpace(logiKb.ConnectionType))
                    {
                        connectionType = logiKb.ConnectionType;
                    }
                }
            }

            return new KeyboardInfo(name ?? "Keyboard", connectionType, battery, isCharging);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read current keyboard info");
            return new KeyboardInfo(null, null, null, null);
        }
    }

    private static string? FindFirstPhysicalKeyboardPath()
    {
        uint deviceCount = 0;
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref deviceCount, size) != 0 || deviceCount == 0)
        {
            return null;
        }

        var devices = new RAWINPUTDEVICELIST[deviceCount];
        if (GetRawInputDeviceList(devices, ref deviceCount, size) == uint.MaxValue)
        {
            return null;
        }

        string? fallback = null;
        foreach (var dev in devices)
        {
            if (dev.dwType != RIM_TYPEKEYBOARD)
            {
                continue;
            }

            var path = GetRawInputDevicePath(dev.hDevice);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!path.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("ROOT#", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            fallback ??= path;
        }

        return fallback;
    }

    private static string? GetRawInputDevicePath(nint hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, nint.Zero, ref size);
        if (size == 0 || size > 1024)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size) == uint.MaxValue)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string ConvertDevicePathToInstanceId(string rawDevicePath)
    {
        // Example: \\?\HID#VID_046D&PID_C547&MI_00#7&123456&0&0000#{884b96c3-...}
        var trimmed = rawDevicePath;
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        var guidStart = trimmed.IndexOf("#{", StringComparison.Ordinal);
        if (guidStart > 0)
        {
            trimmed = trimmed[..guidStart];
        }

        return trimmed.Replace('#', '\\');
    }

    internal static string InferConnectionType(string devicePath)
    {
        if (devicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("00001812", StringComparison.OrdinalIgnoreCase))
        {
            return "Bluetooth";
        }

        if (devicePath.Contains("ACPI", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("I8042", StringComparison.OrdinalIgnoreCase))
        {
            return "PS/2 / Internal";
        }

        if (devicePath.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase))
        {
            return "Remote Desktop";
        }

        return "USB / Wireless";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public nint hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(
        nint hDevice,
        uint uiCommand,
        nint pData,
        ref uint pcbSize);
}
