using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Pulse.Contracts;
using Pulse.Server.Models;
using Pulse.Server.Options;

namespace Pulse.Server.Services;

public sealed class DeviceStateStore(IOptions<MqttOptions> options, TimeProvider timeProvider)
{
    private readonly TimeSpan _staleThreshold = TimeSpan.FromSeconds(Math.Max(1, options.Value.StaleAfterSeconds));
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<DeviceSnapshot>> _subscribers = new();

    public int DeviceCount => _devices.Count;

    public IReadOnlyList<DeviceSnapshot> GetAll()
    {
        var now = timeProvider.GetUtcNow();
        return _devices.Values
            .Select(record => record.ToSnapshot(now, _staleThreshold))
            .OrderBy(snapshot => snapshot.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DeviceSnapshot? Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        return _devices.TryGetValue(deviceId.Trim(), out var record)
            ? record.ToSnapshot(timeProvider.GetUtcNow(), _staleThreshold)
            : null;
    }

    public DeviceSnapshot UpdateAvailability(string deviceId, DeviceAvailability availability)
    {
        var now = timeProvider.GetUtcNow();
        var record = GetOrCreate(deviceId);
        DeviceSnapshot snapshot;
        lock (record.Sync)
        {
            record.Online = availability.Online;
            record.LastAvailabilityChangedAt = now;
            record.LastSeenAt = now;
            snapshot = record.ToSnapshotUnsafe(now, _staleThreshold);
        }

        Broadcast(snapshot);
        return snapshot;
    }

    public DeviceSnapshot UpdateTelemetry(string deviceId, DeviceTelemetry telemetry)
    {
        var now = timeProvider.GetUtcNow();
        var record = GetOrCreate(deviceId);
        DeviceSnapshot snapshot;
        lock (record.Sync)
        {
            record.Telemetry = telemetry;
            record.LastTelemetryReceivedAt = now;
            record.LastSeenAt = now;
            snapshot = record.ToSnapshotUnsafe(now, _staleThreshold);
        }

        Broadcast(snapshot);
        return snapshot;
    }

    public DeviceSnapshot UpdateSessionAvailability(string deviceId, SessionAvailability availability)
    {
        var now = timeProvider.GetUtcNow();
        var record = GetOrCreate(deviceId);
        DeviceSnapshot snapshot;
        lock (record.Sync)
        {
            record.SessionOnline = availability.Online;
            if (!string.IsNullOrWhiteSpace(availability.UserName))
            {
                record.SessionUserName = availability.UserName;
            }

            record.SessionLastAvailabilityChangedAt = now;
            record.LastSeenAt = now;
            snapshot = record.ToSnapshotUnsafe(now, _staleThreshold);
        }

        Broadcast(snapshot);
        return snapshot;
    }

    public DeviceSnapshot UpdateSessionTelemetry(string deviceId, SessionTelemetry telemetry)
    {
        var now = timeProvider.GetUtcNow();
        var record = GetOrCreate(deviceId);
        DeviceSnapshot snapshot;
        lock (record.Sync)
        {
            record.SessionTelemetry = telemetry;
            record.LastSessionTelemetryReceivedAt = now;
            record.LastSeenAt = now;
            snapshot = record.ToSnapshotUnsafe(now, _staleThreshold);
        }

        Broadcast(snapshot);
        return snapshot;
    }

    public async IAsyncEnumerable<DeviceSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<DeviceSnapshot>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscribers[id] = channel;
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return snapshot;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private DeviceRecord GetOrCreate(string deviceId)
    {
        var normalized = deviceId.Trim();
        return _devices.GetOrAdd(normalized, static id => new DeviceRecord(id));
    }

    private void Broadcast(DeviceSnapshot snapshot)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(snapshot);
        }
    }

    private sealed class DeviceRecord(string deviceId)
    {
        public object Sync { get; } = new();
        public string DeviceId { get; } = deviceId;
        public bool Online { get; set; }
        public DateTimeOffset? LastAvailabilityChangedAt { get; set; }
        public DeviceTelemetry? Telemetry { get; set; }
        public DateTimeOffset? LastTelemetryReceivedAt { get; set; }
        public bool SessionOnline { get; set; }
        public string? SessionUserName { get; set; }
        public DateTimeOffset? SessionLastAvailabilityChangedAt { get; set; }
        public SessionTelemetry? SessionTelemetry { get; set; }
        public DateTimeOffset? LastSessionTelemetryReceivedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }

        public DeviceSnapshot ToSnapshot(DateTimeOffset now, TimeSpan staleThreshold)
        {
            lock (Sync)
            {
                return ToSnapshotUnsafe(now, staleThreshold);
            }
        }

        public DeviceSnapshot ToSnapshotUnsafe(DateTimeOffset now, TimeSpan staleThreshold)
        {
            var isStale = !Online
                || Telemetry is null
                || (now - Telemetry.ObservedAt) > staleThreshold;

            return new DeviceSnapshot(
                DeviceId,
                Online,
                isStale,
                LastAvailabilityChangedAt,
                Telemetry,
                LastTelemetryReceivedAt,
                SessionOnline,
                SessionUserName,
                SessionLastAvailabilityChangedAt,
                SessionTelemetry,
                LastSessionTelemetryReceivedAt,
                LastSeenAt);
        }
    }
}
