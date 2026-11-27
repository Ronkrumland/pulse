namespace Pulse.Contracts;

public sealed record SessionAvailability(string DeviceId, bool Online, string? UserName = null);

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    int VolumePercent,
    bool IsMuted);

public sealed record AudioState(
    string? DefaultPlaybackDeviceId,
    string? DefaultPlaybackDeviceName,
    int? PlaybackVolumePercent,
    bool? PlaybackMuted,
    string? DefaultCaptureDeviceId,
    string? DefaultCaptureDeviceName,
    int? CaptureVolumePercent,
    bool? CaptureMuted,
    IReadOnlyList<AudioDeviceInfo> PlaybackDevices,
    IReadOnlyList<AudioDeviceInfo> CaptureDevices);

public sealed record NowPlayingInfo(
    bool IsActive,
    string? Title,
    string? Artist,
    string? Album,
    string? PlaybackStatus,
    string? SourceApp);

public sealed record GameStatusInfo(
    bool IsRunning,
    string? GameName,
    string? ProcessName,
    string? Launcher);

public sealed record KeyboardInfo(
    string? Name,
    string? ConnectionType,
    int? BatteryPercent,
    bool? IsCharging);

public sealed record PeripheralInfo(
    string Name,
    string Category,
    string Vendor,
    bool IsConnected,
    int? BatteryPercent,
    bool? IsCharging,
    string? ConnectionType);

public sealed record SessionTelemetry(
    string DeviceId,
    DateTimeOffset ObservedAt,
    AudioState? Audio,
    NowPlayingInfo? NowPlaying,
    GameStatusInfo? Game,
    KeyboardInfo? Keyboard,
    IReadOnlyList<PeripheralInfo> LogitechPeripherals);
