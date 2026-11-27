using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Collectors;

public interface IAudioController
{
    AudioState ReadState();
    bool SetDefaultPlaybackDevice(string deviceId);
    bool SetDefaultCaptureDevice(string deviceId);
    bool SetPlaybackVolume(int volumePercent);
    bool SetCaptureVolume(int volumePercent);
    bool SetPlaybackMute(bool muted);
    bool SetCaptureMute(bool muted);
}

internal sealed class WindowsAudioController(ILogger<WindowsAudioController> logger) : IAudioController
{
    public AudioState ReadState()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultPlayback = TryGetDefaultEndpoint(enumerator, DataFlow.Render);
            var defaultCapture = TryGetDefaultEndpoint(enumerator, DataFlow.Capture);

            var playbackDevices = EnumerateDevices(enumerator, DataFlow.Render, defaultPlayback?.ID);
            var captureDevices = EnumerateDevices(enumerator, DataFlow.Capture, defaultCapture?.ID);

            var playbackVol = TryGetVolumePercent(defaultPlayback);
            var playbackMuted = TryGetMute(defaultPlayback);
            var captureVol = TryGetVolumePercent(defaultCapture);
            var captureMuted = TryGetMute(defaultCapture);

            var state = new AudioState(
                defaultPlayback?.ID,
                defaultPlayback?.FriendlyName,
                playbackVol,
                playbackMuted,
                defaultCapture?.ID,
                defaultCapture?.FriendlyName,
                captureVol,
                captureMuted,
                playbackDevices,
                captureDevices);

            defaultPlayback?.Dispose();
            defaultCapture?.Dispose();
            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Windows audio state");
            return new AudioState(null, null, null, null, null, null, null, null, [], []);
        }
    }

    public bool SetDefaultPlaybackDevice(string deviceId) => SetDefaultDevice(deviceId);

    public bool SetDefaultCaptureDevice(string deviceId) => SetDefaultDevice(deviceId);

    public bool SetPlaybackVolume(int volumePercent) => SetVolume(DataFlow.Render, volumePercent);

    public bool SetCaptureVolume(int volumePercent) => SetVolume(DataFlow.Capture, volumePercent);

    public bool SetPlaybackMute(bool muted) => SetMute(DataFlow.Render, muted);

    public bool SetCaptureMute(bool muted) => SetMute(DataFlow.Capture, muted);

    private bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to switch default audio device to {DeviceId}", deviceId);
            return false;
        }
    }

    private bool SetVolume(DataFlow flow, int volumePercent)
    {
        try
        {
            var clamped = Math.Clamp(volumePercent, 0, 100);
            using var enumerator = new MMDeviceEnumerator();
            using var device = TryGetDefaultEndpoint(enumerator, flow);
            if (device is null)
            {
                return false;
            }

            device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100f;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set {Flow} volume to {Volume}", flow, volumePercent);
            return false;
        }
    }

    private bool SetMute(DataFlow flow, bool muted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = TryGetDefaultEndpoint(enumerator, flow);
            if (device is null)
            {
                return false;
            }

            device.AudioEndpointVolume.Mute = muted;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set {Flow} mute to {Muted}", flow, muted);
            return false;
        }
    }

    private static MMDevice? TryGetDefaultEndpoint(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static List<AudioDeviceInfo> EnumerateDevices(MMDeviceEnumerator enumerator, DataFlow flow, string? defaultId)
    {
        var list = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                list.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                    TryGetVolumePercent(device) ?? 0,
                    TryGetMute(device) ?? false));
            }
        }

        return list;
    }

    private static int? TryGetVolumePercent(MMDevice? device)
    {
        if (device is null)
        {
            return null;
        }

        try
        {
            return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool? TryGetMute(MMDevice? device)
    {
        if (device is null)
        {
            return null;
        }

        try
        {
            return device.AudioEndpointVolume.Mute;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, nint ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, nint ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, nint pEndpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, nint pmftDefaultPeriod, nint pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, nint pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, nint pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, nint mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, int bFxStore, nint key, nint pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, int bFxStore, nint key, nint pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }
}
