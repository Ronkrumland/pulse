using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Collectors;

public interface ILogitechCollector
{
    IReadOnlyList<PeripheralInfo> ReadPeripherals();
}

internal sealed class LogitechPeripheralCollector(ILogger<LogitechPeripheralCollector> logger) : ILogitechCollector
{
    private const ushort LogitechVendorId = 0x046D;
    private const byte HidppShortReportId = 0x10;
    private const byte HidppLongReportId = 0x11;
    private const int HidppShortLength = 7;
    private const int HidppLongLength = 20;

    // HID++ 2.0 Feature IDs
    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureAdcMeasurement = 0x1F20;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint ERROR_IO_PENDING = 997;

    public IReadOnlyList<PeripheralInfo> ReadPeripherals()
    {
        var results = new Dictionary<string, PeripheralInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ProbeHidppDevices(results);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HID++ probe encountered an error");
        }

        try
        {
            MergePnpLogitechDevices(results);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PnP Logitech scan encountered an error");
        }

        return results.Values
            .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ProbeHidppDevices(Dictionary<string, PeripheralInfo> results)
    {
        HidD_GetHidGuid(out var hidGuid);
        var devInfoSet = WindowsNativePnp.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            nint.Zero,
            WindowsNativePnp.DIGCF_PRESENT | WindowsNativePnp.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == nint.Zero || devInfoSet == new nint(-1))
        {
            return;
        }

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(devInfoSet, nint.Zero, ref hidGuid, index, ref interfaceData); index++)
            {
                var path = GetInterfacePath(devInfoSet, ref interfaceData);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains("vid&0002046d", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ProbeSingleHidPath(path, results);
            }
        }
        finally
        {
            WindowsNativePnp.SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    private void ProbeSingleHidPath(string devicePath, Dictionary<string, PeripheralInfo> results)
    {
        using var handle = CreateFileW(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            nint.Zero);

        if (handle.IsInvalid)
        {
            return;
        }

        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != LogitechVendorId)
        {
            return;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsed) || preparsed == nint.Zero)
        {
            return;
        }

        ushort usagePage;
        ushort outputReportLen;
        ushort inputReportLen;
        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != 0x00110000) // HIDP_STATUS_SUCCESS
            {
                return;
            }

            usagePage = caps.UsagePage;
            outputReportLen = caps.OutputReportByteLength;
            inputReportLen = caps.InputReportByteLength;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }

        // Logitech HID++ vendor usage pages are 0xFF00 (USB/Lightspeed/Unifying/Bolt) or 0xFF43 (Bluetooth)
        if (usagePage != 0xFF00 && usagePage != 0xFF43)
        {
            return;
        }

        var useLongReport = outputReportLen >= HidppLongLength && inputReportLen >= HidppLongLength;
        if (!useLongReport && (outputReportLen < HidppShortLength || inputReportLen < HidppShortLength))
        {
            return;
        }

        var reportId = useLongReport ? HidppLongReportId : HidppShortReportId;
        var reportLen = useLongReport ? HidppLongLength : HidppShortLength;
        var connectionType = usagePage == 0xFF43 ? "Bluetooth" : "Lightspeed / USB";

        // Probe direct device index (0xFF) first; if it responds, no need to scan receiver slots
        var direct = QueryHidppDevice(handle, 0xFF, reportId, reportLen, connectionType);
        if (direct is not null)
        {
            results[direct.Name] = direct;
            return;
        }

        // Otherwise probe receiver slots 1..6 (Unifying / Bolt / Lightspeed multi-device receiver)
        for (byte devIndex = 1; devIndex <= 6; devIndex++)
        {
            var slotDevice = QueryHidppDevice(handle, devIndex, reportId, reportLen, connectionType);
            if (slotDevice is not null)
            {
                results[slotDevice.Name] = slotDevice;
            }
        }
    }

    private PeripheralInfo? QueryHidppDevice(SafeFileHandle handle, byte deviceIndex, byte reportId, int reportLen, string connectionType)
    {
        // Root feature (0x0000), function 0x01 = Ping
        var ping = SendHidppRequest(handle, reportId, reportLen, deviceIndex, 0x00, 0x01, [0x00, 0x00, 0xAA]);
        if (ping is null)
        {
            return null;
        }

        // Resolve device name and type via feature 0x0005
        var nameFeatureIdx = FindFeatureIndex(handle, reportId, reportLen, deviceIndex, FeatureDeviceName);
        var name = nameFeatureIdx is byte nIdx
            ? ReadDeviceName(handle, reportId, reportLen, deviceIndex, nIdx)
            : null;
        var category = nameFeatureIdx is byte tIdx
            ? ReadDeviceCategory(handle, reportId, reportLen, deviceIndex, tIdx)
            : InferCategoryFromName(name);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Logitech {category}";
        }

        var (battery, charging) = ReadBattery(handle, reportId, reportLen, deviceIndex);

        return new PeripheralInfo(
            name,
            category,
            "Logitech",
            IsConnected: true,
            BatteryPercent: battery,
            IsCharging: charging,
            ConnectionType: connectionType);
    }

    private byte? FindFeatureIndex(SafeFileHandle handle, byte reportId, int reportLen, byte deviceIndex, ushort featureId)
    {
        var resp = SendHidppRequest(
            handle,
            reportId,
            reportLen,
            deviceIndex,
            0x00,
            0x00,
            [(byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00]);

        return resp is not null && resp.Length >= 5 && resp[4] != 0 ? resp[4] : null;
    }

    private string? ReadDeviceName(SafeFileHandle handle, byte reportId, int reportLen, byte deviceIndex, byte featureIdx)
    {
        var lenResp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, featureIdx, 0x00, []);
        if (lenResp is null || lenResp.Length < 5)
        {
            return null;
        }

        var totalLen = Math.Min((int)lenResp[4], 64);
        if (totalLen <= 0)
        {
            return null;
        }

        var sb = new StringBuilder(totalLen);
        var offset = 0;
        while (offset < totalLen)
        {
            var chunkResp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, featureIdx, 0x01, [(byte)offset]);
            if (chunkResp is null || chunkResp.Length <= 4)
            {
                break;
            }

            var available = Math.Min(totalLen - offset, chunkResp.Length - 4);
            for (var i = 0; i < available; i++)
            {
                var ch = chunkResp[4 + i];
                if (ch == 0)
                {
                    break;
                }

                sb.Append((char)ch);
            }

            offset += available;
            if (available == 0)
            {
                break;
            }
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private string ReadDeviceCategory(SafeFileHandle handle, byte reportId, int reportLen, byte deviceIndex, byte featureIdx)
    {
        var typeResp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, featureIdx, 0x02, []);
        if (typeResp is null || typeResp.Length < 5)
        {
            return "Peripheral";
        }

        return typeResp[4] switch
        {
            0 => "Keyboard",
            3 => "Mouse",
            4 => "Trackpad",
            5 => "Trackball",
            8 => "Headset",
            _ => "Peripheral"
        };
    }

    private (int? BatteryPercent, bool? IsCharging) ReadBattery(SafeFileHandle handle, byte reportId, int reportLen, byte deviceIndex)
    {
        // 1. Unified Battery (0x1004) - modern Lightspeed/Bolt/MX/G Pro
        if (FindFeatureIndex(handle, reportId, reportLen, deviceIndex, FeatureUnifiedBattery) is byte unifiedIdx)
        {
            var resp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, unifiedIdx, 0x01, []);
            if (resp is not null && resp.Length >= 7)
            {
                int? pct = resp[4] <= 100 ? resp[4] : MapBatteryLevelBits(resp[5]);
                bool? charging = resp[6] switch
                {
                    1 or 2 or 3 => true,
                    0 => false,
                    _ => null
                };
                if (pct.HasValue)
                {
                    return (pct, charging);
                }
            }
        }

        // 2. Battery Status (0x1000)
        if (FindFeatureIndex(handle, reportId, reportLen, deviceIndex, FeatureBatteryStatus) is byte statusIdx)
        {
            var resp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, statusIdx, 0x00, []);
            if (resp is not null && resp.Length >= 7 && resp[4] <= 100)
            {
                bool? charging = resp[6] switch
                {
                    1 or 2 or 3 => true,
                    0 => false,
                    _ => null
                };
                return (resp[4], charging);
            }
        }

        // 3. Battery Voltage (0x1001)
        if (FindFeatureIndex(handle, reportId, reportLen, deviceIndex, FeatureBatteryVoltage) is byte voltageIdx)
        {
            var resp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, voltageIdx, 0x00, []);
            if (resp is not null && resp.Length >= 7)
            {
                var millivolts = (resp[4] << 8) | resp[5];
                var charging = (resp[6] & 0x80) != 0;
                return (EstimateLiPoPercent(millivolts), charging);
            }
        }

        // 4. ADC Measurement (0x1F20) - used by some Logitech G wireless headsets
        if (FindFeatureIndex(handle, reportId, reportLen, deviceIndex, FeatureAdcMeasurement) is byte adcIdx)
        {
            var resp = SendHidppRequest(handle, reportId, reportLen, deviceIndex, adcIdx, 0x00, []);
            if (resp is not null && resp.Length >= 7)
            {
                var millivolts = (resp[4] << 8) | resp[5];
                var charging = (resp[6] & 0x01) != 0;
                return (EstimateLiPoPercent(millivolts), charging);
            }
        }

        return (null, null);
    }

    internal static int EstimateLiPoPercent(int millivolts)
    {
        // Standard 1S LiPo curve (3500mV = 0%, 4180mV = 100%)
        if (millivolts <= 3500) return 0;
        if (millivolts >= 4180) return 100;
        return (int)Math.Round((millivolts - 3500) / 6.8);
    }

    private static int? MapBatteryLevelBits(byte levelFlags)
    {
        if ((levelFlags & 0x08) != 0) return 90; // Full
        if ((levelFlags & 0x04) != 0) return 50; // Good
        if ((levelFlags & 0x02) != 0) return 20; // Low
        if ((levelFlags & 0x01) != 0) return 5;  // Critical
        return null;
    }

    private static byte[]? SendHidppRequest(
        SafeFileHandle handle,
        byte reportId,
        int reportLen,
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte[] parameters)
    {
        const byte softwareId = 0x0A;
        var outBuf = new byte[reportLen];
        outBuf[0] = reportId;
        outBuf[1] = deviceIndex;
        outBuf[2] = featureIndex;
        outBuf[3] = (byte)((functionId << 4) | softwareId);
        Array.Copy(parameters, 0, outBuf, 4, Math.Min(parameters.Length, reportLen - 4));

        if (!WriteOverlapped(handle, outBuf, timeoutMs: 120))
        {
            return null;
        }

        var deadline = Environment.TickCount64 + 160;
        var inBuf = new byte[reportLen];
        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)Math.Max(10, deadline - Environment.TickCount64);
            if (!ReadOverlapped(handle, inBuf, remaining))
            {
                return null;
            }

            if (inBuf[1] != deviceIndex)
            {
                continue;
            }

            // HID++ 2.0 error report (0xFF) or HID++ 1.0 error report (0x8F)
            if (inBuf[2] == 0xFF || inBuf[2] == 0x8F)
            {
                return null;
            }

            if (inBuf[2] == featureIndex && (inBuf[3] & 0x0F) == softwareId)
            {
                return inBuf;
            }
        }

        return null;
    }

    private static bool WriteOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        if (WriteFile(handle, buffer, (uint)buffer.Length, out _, ref overlapped))
        {
            return true;
        }

        if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
        {
            return false;
        }

        if (!evt.WaitOne(timeoutMs))
        {
            CancelIoEx(handle, ref overlapped);
            return false;
        }

        return GetOverlappedResult(handle, ref overlapped, out _, false);
    }

    private static bool ReadOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        if (ReadFile(handle, buffer, (uint)buffer.Length, out var read, ref overlapped))
        {
            return read > 0;
        }

        if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
        {
            return false;
        }

        if (!evt.WaitOne(timeoutMs))
        {
            CancelIoEx(handle, ref overlapped);
            return false;
        }

        return GetOverlappedResult(handle, ref overlapped, out var transferred, false) && transferred > 0;
    }

    private static void MergePnpLogitechDevices(Dictionary<string, PeripheralInfo> results)
    {
        var allDevices = WindowsNativePnp.EnumeratePresentDevices();
        var byContainer = allDevices
            .Where(d => d.ContainerId.HasValue)
            .GroupBy(d => d.ContainerId!.Value);

        foreach (var group in byContainer)
        {
            var members = group.ToList();
            var isLogitech = members.Any(m =>
                m.InstanceId.Contains("VID_046D", StringComparison.OrdinalIgnoreCase)
                || m.InstanceId.Contains("VID&0002046D", StringComparison.OrdinalIgnoreCase)
                || (m.Manufacturer?.Contains("Logitech", StringComparison.OrdinalIgnoreCase) ?? false)
                || (m.FriendlyName?.Contains("Logitech", StringComparison.OrdinalIgnoreCase) ?? false));

            if (!isLogitech)
            {
                continue;
            }

            var bestName = members
                .Select(m => m.FriendlyName ?? m.BusReportedName)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)
                    && !n.Contains("USB Input", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("USB Composite", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("USB Receiver", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("LIGHTSPEED Receiver", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(bestName))
            {
                continue;
            }

            var category = InferCategoryFromName(bestName);
            if (category == "Peripheral")
            {
                if (members.Any(m => m.Description?.Contains("Audio", StringComparison.OrdinalIgnoreCase) == true
                    || m.FriendlyName?.Contains("Headset", StringComparison.OrdinalIgnoreCase) == true))
                {
                    category = "Headset";
                }
                else if (members.Any(m => m.Description?.Contains("mouse", StringComparison.OrdinalIgnoreCase) == true))
                {
                    category = "Mouse";
                }
                else if (members.Any(m => m.Description?.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true))
                {
                    category = "Keyboard";
                }
            }

            // Focus on mice, headsets, and keyboards
            if (category is not ("Mouse" or "Headset" or "Keyboard"))
            {
                continue;
            }

            var btBattery = members.Select(m => m.BluetoothBatteryPercent).FirstOrDefault(b => b.HasValue);
            var isBt = members.Any(m => m.InstanceId.Contains("BTH", StringComparison.OrdinalIgnoreCase));

            if (results.TryGetValue(bestName, out var existing))
            {
                if (!existing.BatteryPercent.HasValue && btBattery.HasValue)
                {
                    results[bestName] = existing with { BatteryPercent = btBattery };
                }
            }
            else
            {
                results[bestName] = new PeripheralInfo(
                    bestName.Trim(),
                    category,
                    "Logitech",
                    IsConnected: true,
                    BatteryPercent: btBattery,
                    IsCharging: null,
                    ConnectionType: isBt ? "Bluetooth" : "USB / Wireless");
            }
        }
    }

    internal static string InferCategoryFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Peripheral";
        }

        if (name.Contains("Mouse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Superlight", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G502", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G305", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G703", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G903", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MX Master", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MX Anywhere", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MX Vertical", StringComparison.OrdinalIgnoreCase))
        {
            return "Mouse";
        }

        if (name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G915", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G715", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TKL", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MX Keys", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MX Mechanical", StringComparison.OrdinalIgnoreCase))
        {
            return "Keyboard";
        }

        if (name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PRO X", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G733", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G533", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G535", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G935", StringComparison.OrdinalIgnoreCase)
            || name.Contains("G435", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Astro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Zone", StringComparison.OrdinalIgnoreCase))
        {
            return "Headset";
        }

        return "Peripheral";
    }

    private static string? GetInterfacePath(nint devInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref interfaceData, nint.Zero, 0, out var requiredSize, nint.Zero);
        if (requiredSize <= 4 || requiredSize > 4096)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref interfaceData, buffer, requiredSize, out _, nint.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(nint preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);
}
