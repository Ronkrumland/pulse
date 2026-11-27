using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using NSubstitute;
using Pulse.Agent.Windows.Session.Collectors;
using Pulse.Agent.Windows.Session.Services;
using Pulse.Agent.Windows.Session.UI;
using Pulse.Contracts;
using Xunit;

namespace Pulse.Agent.Windows.Session.Tests;

public class SessionUnitTests
{
    [Fact]
    public async Task SessionCommandHandler_DispatchesAudioAndMediaCommands()
    {
        var audio = Substitute.For<IAudioController>();
        audio.SetDefaultPlaybackDevice("spk-1").Returns(true);
        audio.SetPlaybackVolume(75).Returns(true);
        audio.SetCaptureMute(true).Returns(true);

        var media = Substitute.For<INowPlayingController>();
        media.TogglePlayPauseAsync(Arg.Any<CancellationToken>()).Returns(true);

        var handler = new SessionCommandHandler(audio, media, TimeProvider.System, NullLogger<SessionCommandHandler>.Instance);
        var stateChangedCount = 0;
        handler.StateChanged += () => stateChangedCount++;

        var r1 = await handler.HandleAsync(new SessionCommand(
            "req-1", "pc-1", "audio.set_playback_device",
            JsonSerializer.SerializeToElement(new { deviceId = "spk-1" })));
        var r2 = await handler.HandleAsync(new SessionCommand(
            "req-2", "pc-1", "audio.set_playback_volume",
            JsonSerializer.SerializeToElement(new { volumePercent = 75 })));
        var r3 = await handler.HandleAsync(new SessionCommand(
            "req-3", "pc-1", "audio.set_capture_mute",
            JsonSerializer.SerializeToElement(new { muted = true })));
        var r4 = await handler.HandleAsync(new SessionCommand("req-4", "pc-1", "media.play_pause"));
        var r5 = await handler.HandleAsync(new SessionCommand("req-5", "pc-1", "unknown.action"));

        Assert.True(r1.Succeeded);
        Assert.True(r2.Succeeded);
        Assert.True(r3.Succeeded);
        Assert.True(r4.Succeeded);
        Assert.False(r5.Succeeded);
        Assert.Equal(4, stateChangedCount);
    }

    [Fact]
    public async Task SessionMqttService_PublishesTelemetryAndHandlesTwoWayCommand()
    {
        const string deviceId = "gaming-pc";
        var port = GetLoopbackPort();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build());

        var resultReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.InterceptingPublishAsync += args =>
        {
            if (args.ApplicationMessage.Topic == $"pulse/devices/{deviceId}/session/results")
            {
                var doc = JsonDocument.Parse(args.ApplicationMessage.Payload);
                resultReceived.TrySetResult(doc.RootElement.Clone());
            }

            return Task.CompletedTask;
        };
        await broker.StartAsync();

        var audio = Substitute.For<IAudioController>();
        audio.SetPlaybackMute(true).Returns(true);
        var media = Substitute.For<INowPlayingController>();
        var handler = new SessionCommandHandler(audio, media, TimeProvider.System, NullLogger<SessionCommandHandler>.Instance);

        await using var service = new SessionMqttService(
            new MqttSettings { Host = "127.0.0.1", Port = port, DeviceId = deviceId },
            handler,
            TimeProvider.System,
            NullLogger<SessionMqttService>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            service.Start();

            // Publish telemetry and verify retained availability & telemetry
            service.PublishTelemetry(new SessionTelemetry(
                deviceId,
                new DateTimeOffset(2026, 11, 27, 15, 30, 0, TimeSpan.Zero),
                null,
                new NowPlayingInfo(true, "Track A", "Artist B", "Album C", "Playing", "Spotify"),
                new GameStatusInfo(true, "Hades II", "Hades2", "Steam"),
                new KeyboardInfo("Logitech G915", "Lightspeed / USB", 82, false),
                [new PeripheralInfo("PRO X SUPERLIGHT", "Mouse", "Logitech", true, 91, false, "Lightspeed / USB")]));

            var availability = await WaitForRetainedMessageAsync(
                broker,
                $"pulse/devices/{deviceId}/session/availability",
                json => json.GetProperty("online").GetBoolean(),
                timeout.Token);
            Assert.Equal(deviceId, availability.GetProperty("deviceId").GetString());

            var telemetry = await WaitForRetainedMessageAsync(
                broker,
                $"pulse/devices/{deviceId}/session/telemetry",
                json => json.GetProperty("game").GetProperty("isRunning").GetBoolean(),
                timeout.Token);
            Assert.Equal("Hades II", telemetry.GetProperty("game").GetProperty("gameName").GetString());
            Assert.Equal("Track A", telemetry.GetProperty("nowPlaying").GetProperty("title").GetString());

            // Send 2-way command from broker to session client
            var commandJson = JsonSerializer.SerializeToUtf8Bytes(
                new SessionCommand("cmd-99", deviceId, "audio.set_playback_mute", JsonSerializer.SerializeToElement(new { muted = true })),
                JsonSerializerOptions.Web);

            await broker.InjectApplicationMessage(new InjectedMqttApplicationMessage(
                new MqttApplicationMessageBuilder()
                    .WithTopic($"pulse/devices/{deviceId}/session/commands")
                    .WithPayload(commandJson)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build()));

            var cmdResult = await resultReceived.Task.WaitAsync(timeout.Token);
            Assert.Equal("cmd-99", cmdResult.GetProperty("requestId").GetString());
            Assert.True(cmdResult.GetProperty("succeeded").GetBoolean());
            audio.Received(1).SetPlaybackMute(true);

            await service.StopAsync();
            var offline = await WaitForRetainedMessageAsync(
                broker,
                $"pulse/devices/{deviceId}/session/availability",
                json => !json.GetProperty("online").GetBoolean(),
                timeout.Token);
            Assert.False(offline.GetProperty("online").GetBoolean());
        }
        finally
        {
            await service.StopAsync();
            await broker.StopAsync();
        }
    }

    [Fact]
    public void GameDetector_MatchesKnownGamePathsAndExcludesLaunchers()
    {
        var steamGame = GameDetector.MatchKnownGamePath(@"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk2077");
        Assert.NotNull(steamGame);
        Assert.True(steamGame.IsRunning);
        Assert.Equal("Cyberpunk 2077", steamGame.GameName);
        Assert.Equal("Steam", steamGame.Launcher);

        var excluded = GameDetector.MatchKnownGamePath(@"C:\Program Files (x86)\Steam\steamapps\common\SomeGame\CrashHandler64.exe", "CrashHandler64");
        Assert.Null(excluded);
    }

    [Fact]
    public void Helpers_FormatNamesCategoriesTooltipsAndSettings()
    {
        Assert.Equal("Spotify", NowPlayingCollector.FormatSourceApp("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));
        Assert.Equal("Mouse", LogitechPeripheralCollector.InferCategoryFromName("PRO X SUPERLIGHT 2"));
        Assert.Equal("Headset", LogitechPeripheralCollector.InferCategoryFromName("Logitech G733 Gaming Headset"));
        Assert.Equal("Bluetooth", KeyboardCollector.InferConnectionType(@"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d"));
        Assert.InRange(LogitechPeripheralCollector.EstimateLiPoPercent(3900), 50, 70);

        var tooltip = TrayIconFactory.FormatTooltip(
            new WindowsServiceStatusInfo("Pulse Agent", true, "Running", ServiceControllerStatus.Running, "Automatic"),
            new MqttStatusSnapshot(MqttConnectionState.Connected, "Connected"));
        Assert.True(tooltip.Length <= 63);
        Assert.Contains("Service: Running", tooltip);
        Assert.Contains("MQTT: Connected", tooltip);
    }

    [Fact]
    public void SettingsStore_RoundTripsSessionAndAgentConfigs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pulse-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sessionPath = Path.Combine(tempDir, "session.json");
            var agentPath = Path.Combine(tempDir, "agent.json");
            var store = new SessionSettingsStore(sessionPath, agentPath);

            var (saved, _) = store.SaveAgentConfig(new AgentServiceConfig
            {
                PollingIntervalSeconds = 10,
                Mqtt = new MqttSettings { Host = "broker.local", Port = 8883, DeviceId = "rig-1", UseTls = true }
            });
            Assert.True(saved);

            // First load of session settings seeds MQTT from agent.json
            var seeded = store.LoadSessionSettings();
            Assert.Equal("broker.local", seeded.Mqtt.Host);
            Assert.Equal(8883, seeded.Mqtt.Port);
            Assert.Equal("rig-1", seeded.Mqtt.DeviceId);
            Assert.True(seeded.Mqtt.UseTls);

            seeded.PollingIntervalSeconds = 3;
            seeded.CustomGameProcesses.Add("MyIndieGame");
            store.SaveSessionSettings(seeded);

            var reloaded = store.LoadSessionSettings();
            Assert.Equal(3, reloaded.PollingIntervalSeconds);
            Assert.Contains("MyIndieGame", reloaded.CustomGameProcesses);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int GetLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonElement> WaitForRetainedMessageAsync(
        MqttServer broker,
        string topic,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var retained = await broker.GetRetainedMessagesAsync();
            var match = retained.FirstOrDefault(m => m.Topic == topic);
            if (match is not null)
            {
                using var doc = JsonDocument.Parse(match.Payload);
                if (predicate(doc.RootElement))
                {
                    return doc.RootElement.Clone();
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new System.TimeoutException($"Timed out waiting for retained message on {topic}.");
    }
}
