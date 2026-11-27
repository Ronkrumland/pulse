using Microsoft.Extensions.Logging;
using Pulse.Contracts;
using Windows.Media.Control;

namespace Pulse.Agent.Windows.Session.Collectors;

public interface INowPlayingController
{
    Task<NowPlayingInfo> ReadAsync(CancellationToken cancellationToken = default);
    Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default);
    Task<bool> SkipNextAsync(CancellationToken cancellationToken = default);
    Task<bool> SkipPreviousAsync(CancellationToken cancellationToken = default);
    Task<bool> StopAsync(CancellationToken cancellationToken = default);
}

internal sealed class NowPlayingCollector(ILogger<NowPlayingCollector> logger) : INowPlayingController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<NowPlayingInfo> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await GetManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session is null)
            {
                return new NowPlayingInfo(false, null, null, null, "Stopped", null);
            }

            var playbackInfo = session.GetPlaybackInfo();
            var status = playbackInfo?.PlaybackStatus.ToString() ?? "Unknown";
            var props = await session.TryGetMediaPropertiesAsync();
            var sourceApp = FormatSourceApp(session.SourceAppUserModelId);
            var isActive = !string.IsNullOrWhiteSpace(props?.Title)
                || status.Equals("Playing", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Paused", StringComparison.OrdinalIgnoreCase);

            return new NowPlayingInfo(
                isActive,
                string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title.Trim(),
                string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist.Trim(),
                string.IsNullOrWhiteSpace(props?.AlbumTitle) ? null : props.AlbumTitle.Trim(),
                status,
                sourceApp);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read Now Playing media session");
            return new NowPlayingInfo(false, null, null, null, "Unavailable", null);
        }
    }

    public Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default) =>
        InvokeSessionAsync(session => session.TryTogglePlayPauseAsync().AsTask());

    public Task<bool> SkipNextAsync(CancellationToken cancellationToken = default) =>
        InvokeSessionAsync(session => session.TrySkipNextAsync().AsTask());

    public Task<bool> SkipPreviousAsync(CancellationToken cancellationToken = default) =>
        InvokeSessionAsync(session => session.TrySkipPreviousAsync().AsTask());

    public Task<bool> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeSessionAsync(session => session.TryStopAsync().AsTask());

    private async Task<bool> InvokeSessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        try
        {
            var manager = await GetManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session is null)
            {
                return false;
            }

            return await action(session);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invoke media control command");
            return false;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager;
    }

    internal static string? FormatSourceApp(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        var trimmed = appUserModelId.Trim();
        if (trimmed.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (trimmed.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (trimmed.Contains("MSEdge", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (trimmed.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (trimmed.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (trimmed.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (trimmed.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("MediaPlayer", StringComparison.OrdinalIgnoreCase)) return "Media Player";
        if (trimmed.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "VLC";
        if (trimmed.Contains("foobar2000", StringComparison.OrdinalIgnoreCase)) return "foobar2000";
        if (trimmed.Contains("TIDAL", StringComparison.OrdinalIgnoreCase)) return "TIDAL";

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(trimmed);
        }

        var bang = trimmed.IndexOf('!');
        if (bang > 0)
        {
            trimmed = trimmed[..bang];
        }

        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 && dot < trimmed.Length - 1 ? trimmed[(dot + 1)..] : trimmed;
    }
}
