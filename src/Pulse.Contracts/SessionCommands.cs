using System.Text.Json;

namespace Pulse.Contracts;

public sealed record SessionCommand(
    string RequestId,
    string DeviceId,
    string Action,
    JsonElement? Parameters = null);

public sealed record SessionCommandResult(
    string RequestId,
    string DeviceId,
    string Action,
    bool Succeeded,
    string? Message = null,
    DateTimeOffset? CompletedAt = null);
