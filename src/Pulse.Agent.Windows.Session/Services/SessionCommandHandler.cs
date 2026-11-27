using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pulse.Agent.Windows.Session.Collectors;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Services;

public interface ISessionCommandHandler
{
    Task<SessionCommandResult> HandleAsync(SessionCommand command, CancellationToken cancellationToken = default);
}

internal sealed class SessionCommandHandler(
    IAudioController audioController,
    INowPlayingController mediaController,
    TimeProvider timeProvider,
    ILogger<SessionCommandHandler> logger) : ISessionCommandHandler
{
    public event Action? StateChanged;

    public async Task<SessionCommandResult> HandleAsync(SessionCommand command, CancellationToken cancellationToken = default)
    {
        var action = command.Action?.Trim().ToLowerInvariant() ?? "";
        logger.LogInformation("Handling session command {Action} ({RequestId})", action, command.RequestId);

        try
        {
            var (ok, message) = action switch
            {
                "audio.set_playback_device" => HandleSetDevice(command.Parameters, playback: true),
                "audio.set_capture_device" => HandleSetDevice(command.Parameters, playback: false),
                "audio.set_playback_volume" => HandleSetVolume(command.Parameters, playback: true),
                "audio.set_capture_volume" => HandleSetVolume(command.Parameters, playback: false),
                "audio.set_playback_mute" => HandleSetMute(command.Parameters, playback: true),
                "audio.set_capture_mute" => HandleSetMute(command.Parameters, playback: false),
                "media.play_pause" => (await mediaController.TogglePlayPauseAsync(cancellationToken), "Toggled play/pause."),
                "media.next" => (await mediaController.SkipNextAsync(cancellationToken), "Skipped to next track."),
                "media.previous" => (await mediaController.SkipPreviousAsync(cancellationToken), "Skipped to previous track."),
                "media.stop" => (await mediaController.StopAsync(cancellationToken), "Stopped media playback."),
                _ => (false, $"Unsupported session action '{command.Action}'.")
            };

            if (ok)
            {
                StateChanged?.Invoke();
            }

            return new SessionCommandResult(
                command.RequestId,
                command.DeviceId,
                command.Action ?? "",
                ok,
                ok ? message : (message ?? "Command failed."),
                timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Command {Action} ({RequestId}) failed", action, command.RequestId);
            return new SessionCommandResult(
                command.RequestId,
                command.DeviceId,
                command.Action ?? "",
                false,
                ex.Message,
                timeProvider.GetUtcNow());
        }
    }

    private (bool, string) HandleSetDevice(JsonElement? parameters, bool playback)
    {
        if (!TryGetString(parameters, "deviceId", out var deviceId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return (false, "Missing required 'deviceId' parameter.");
        }

        var ok = playback
            ? audioController.SetDefaultPlaybackDevice(deviceId)
            : audioController.SetDefaultCaptureDevice(deviceId);
        return (ok, ok ? "Default audio device updated." : $"Could not switch to device '{deviceId}'.");
    }

    private (bool, string) HandleSetVolume(JsonElement? parameters, bool playback)
    {
        if (!TryGetInt(parameters, "volumePercent", out var volume))
        {
            return (false, "Missing required integer 'volumePercent' parameter.");
        }

        var ok = playback
            ? audioController.SetPlaybackVolume(volume)
            : audioController.SetCaptureVolume(volume);
        return (ok, ok ? $"Volume set to {Math.Clamp(volume, 0, 100)}%." : "Could not change volume.");
    }

    private (bool, string) HandleSetMute(JsonElement? parameters, bool playback)
    {
        if (!TryGetBool(parameters, "muted", out var muted))
        {
            return (false, "Missing required boolean 'muted' parameter.");
        }

        var ok = playback
            ? audioController.SetPlaybackMute(muted)
            : audioController.SetCaptureMute(muted);
        return (ok, ok ? (muted ? "Muted." : "Unmuted.") : "Could not change mute state.");
    }

    private static bool TryGetString(JsonElement? element, string propertyName, out string? value)
    {
        value = null;
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                value = prop.Value.GetString();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetInt(JsonElement? element, string propertyName, out int value)
    {
        value = 0;
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBool(JsonElement? element, string propertyName, out bool value)
    {
        value = false;
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False))
            {
                value = prop.Value.GetBoolean();
                return true;
            }
        }

        return false;
    }
}
