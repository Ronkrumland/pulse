using System.Text.Json;
using Pulse.Contracts;

namespace Pulse.Server.Models;

public sealed record DeviceSnapshot(
    string DeviceId,
    bool Online,
    bool IsStale,
    DateTimeOffset? LastAvailabilityChangedAt,
    DeviceTelemetry? Telemetry,
    DateTimeOffset? LastTelemetryReceivedAt,
    bool SessionOnline,
    string? SessionUserName,
    DateTimeOffset? SessionLastAvailabilityChangedAt,
    SessionTelemetry? SessionTelemetry,
    DateTimeOffset? LastSessionTelemetryReceivedAt,
    DateTimeOffset? LastSeenAt);

public sealed record SendCommandRequest(
    string Action,
    JsonElement? Parameters = null,
    string? RequestId = null);

public enum MqttConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Disconnected,
    Error
}

public sealed record MqttStatusSnapshot(MqttConnectionState State, string Detail);

public sealed record HealthResponse(
    string Status,
    string MqttState,
    string MqttDetail,
    int DeviceCount);
