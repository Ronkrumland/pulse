using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulse.Contracts;
using Pulse.Server.Options;
using Pulse.Server.Services;
using Xunit;

namespace Pulse.Server.Tests;

public class DeviceStateStoreTests
{
    [Fact]
    public void TracksAvailabilityTelemetryAndStaleness()
    {
        var start = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var store = new DeviceStateStore(Microsoft.Extensions.Options.Options.Create(new MqttOptions { StaleAfterSeconds = 30 }), time);

        store.UpdateAvailability("gaming-pc", new DeviceAvailability("gaming-pc", true));
        store.UpdateTelemetry("gaming-pc", new DeviceTelemetry(
            "gaming-pc",
            start,
            52.5f,
            64.0f,
            16384f,
            32768f,
            4096f,
            12288f));

        var fresh = store.Get("gaming-pc");
        Assert.NotNull(fresh);
        Assert.True(fresh.Online);
        Assert.False(fresh.IsStale);
        Assert.Equal(52.5f, fresh.Telemetry?.CpuTemperatureCelsius);

        // Advance past stale threshold while still online
        time.Advance(TimeSpan.FromSeconds(31));
        var stale = store.Get("gaming-pc");
        Assert.NotNull(stale);
        Assert.True(stale.Online);
        Assert.True(stale.IsStale);

        // Mark offline: last-known telemetry remains preserved
        store.UpdateAvailability("gaming-pc", new DeviceAvailability("gaming-pc", false));
        var offline = store.Get("gaming-pc");
        Assert.NotNull(offline);
        Assert.False(offline.Online);
        Assert.True(offline.IsStale);
        Assert.Equal(52.5f, offline.Telemetry?.CpuTemperatureCelsius);
    }

    [Fact]
    public void TracksSessionAvailabilityAndTelemetryIndependently()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new DeviceStateStore(Microsoft.Extensions.Options.Options.Create(new MqttOptions()), time);

        store.UpdateSessionAvailability("gaming-pc", new SessionAvailability("gaming-pc", true, "Ron"));
        store.UpdateSessionTelemetry("gaming-pc", new SessionTelemetry(
            "gaming-pc",
            now,
            Audio: null,
            NowPlaying: new NowPlayingInfo(true, "Track", "Artist", "Album", "Playing", "Spotify"),
            Game: new GameStatusInfo(true, "Cyberpunk 2077", "Cyberpunk2077", "Steam"),
            Keyboard: null,
            LogitechPeripherals: []));

        var snapshot = store.Get("gaming-pc");
        Assert.NotNull(snapshot);
        Assert.True(snapshot.SessionOnline);
        Assert.Equal("Ron", snapshot.SessionUserName);
        Assert.Equal("Cyberpunk 2077", snapshot.SessionTelemetry?.Game?.GameName);
        Assert.Equal("Track", snapshot.SessionTelemetry?.NowPlaying?.Title);
    }
}
