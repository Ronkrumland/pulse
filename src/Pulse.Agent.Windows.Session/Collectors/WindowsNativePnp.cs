using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Agent.Windows.Session.Collectors;

internal static class WindowsNativePnp
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    public const uint DEVPROP_TYPE_BYTE = 0x00000003;
    public const uint DEVPROP_TYPE_UINT32 = 0x00000007;
    public const uint DEVPROP_TYPE_STRING = 0x00000012;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    // Standard PnP device property keys
    public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 2
    };

    public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    public static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 13
    };

    public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new()
    {
        fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
        pid = 4
    };

    public static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new()
    {
        fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        pid = 2
    };

    // Bluetooth battery level property (Windows 10 1809+): 0-100 byte
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2
    };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SetupDiGetClassDevs(
        nint classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(
        nint deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(
        nint deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder? deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    public sealed record PnpDeviceRecord(
        string InstanceId,
        string? FriendlyName,
        string? BusReportedName,
        string? Description,
        string? Manufacturer,
        Guid? ContainerId,
        int? BluetoothBatteryPercent)
    {
        public string BestName =>
            FirstNonGeneric(FriendlyName, BusReportedName, Description) ?? InstanceId;

        private static string? FirstNonGeneric(params string?[] candidates)
        {
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c)
                    && !c.Equals("HID Keyboard Device", StringComparison.OrdinalIgnoreCase)
                    && !c.Equals("HID-compliant mouse", StringComparison.OrdinalIgnoreCase)
                    && !c.Equals("HID-compliant vendor-defined device", StringComparison.OrdinalIgnoreCase)
                    && !c.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase))
                {
                    return c.Trim();
                }
            }

            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
        }
    }

    public static List<PnpDeviceRecord> EnumeratePresentDevices()
    {
        var results = new List<PnpDeviceRecord>();
        var devInfoSet = SetupDiGetClassDevs(nint.Zero, null, nint.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devInfoSet == nint.Zero || devInfoSet == new nint(-1))
        {
            return results;
        }

        try
        {
            var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData); index++)
            {
                var instanceId = GetInstanceId(devInfoSet, ref devInfoData);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var friendly = GetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_FriendlyName);
                var busName = GetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_BusReportedDeviceDesc);
                var desc = GetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_DeviceDesc);
                var mfg = GetStringProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_Manufacturer);
                var containerId = GetGuidProperty(devInfoSet, ref devInfoData, DEVPKEY_Device_ContainerId);
                var battery = GetBatteryProperty(devInfoSet, ref devInfoData);

                results.Add(new PnpDeviceRecord(instanceId, friendly, busName, desc, mfg, containerId, battery));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    private static string? GetInstanceId(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData)
    {
        SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, null, 0, out var required);
        if (required == 0 || required > 1024)
        {
            return null;
        }

        var sb = new StringBuilder((int)required);
        return SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, sb, required, out _)
            ? sb.ToString()
            : null;
    }

    private static string? GetStringProperty(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out var propType, null, 0, out var required, 0);
        if (required == 0 || required > 4096 || propType != DEVPROP_TYPE_STRING)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out _, buffer, required, out _, 0))
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static Guid? GetGuidProperty(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData, DEVPROPKEY key)
    {
        var buffer = new byte[16];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out _, buffer, 16, out var required, 0)
            || required != 16)
        {
            return null;
        }

        var guid = new Guid(buffer);
        return guid == Guid.Empty ? null : guid;
    }

    private static int? GetBatteryProperty(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData)
    {
        var key = DEVPKEY_Bluetooth_Battery;
        var buffer = new byte[4];
        if (!SetupDiGetDevicePropertyW(devInfoSet, ref devInfoData, ref key, out var propType, buffer, (uint)buffer.Length, out var required, 0))
        {
            return null;
        }

        int value = propType switch
        {
            DEVPROP_TYPE_BYTE when required >= 1 => buffer[0],
            DEVPROP_TYPE_UINT32 when required >= 4 => (int)BitConverter.ToUInt32(buffer, 0),
            _ => -1
        };

        return value is >= 0 and <= 100 ? value : null;
    }
}
