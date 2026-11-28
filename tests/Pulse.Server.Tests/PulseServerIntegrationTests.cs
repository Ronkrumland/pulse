using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Pulse.Contracts;
using Pulse.Server.Models;
using Xunit;

namespace Pulse.Server.Tests;

public class PulseServerIntegrationTests
{
    [Fact]
    public async Task IngestsMqttTelemetryAndDispatchesTwoWaySessionCommands()
    {
        const string deviceId = "gaming-pc";
        const string username = "mqtt";
        const string password = "test-password";
        var port = GetLoopbackPort();

        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback)
            .WithDefaultEndpointPort(port)
            .Build());

        broker.ValidatingConnectionAsync += context =>
        {
            context.ReasonCode = context.UserName == username && context.Password == password
                ? MqttConnectReasonCode.Success
                : MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };

        await broker.StartAsync();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mqtt:Host"] = "127.0.0.1",
                    ["Mqtt:Port"] = port.ToString(),
                    ["Mqtt:ClientId"] = "pulse-server-test",
                    ["Mqtt:Username"] = username,
                    ["Mqtt:Password"] = password,
                    ["Mqtt:CommandTimeoutSeconds"] = "2",
                    ["Mqtt:StaleAfterSeconds"] = "60"
                });
            });
        });

        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            // Wait until Pulse.Server connects to the MQTT broker and reports ready
            await WaitUntilAsync(async () =>
            {
                var readyResponse = await client.GetAsync("/readyz", timeout.Token);
                return readyResponse.StatusCode == HttpStatusCode.OK;
            }, timeout.Token);

            var health = await client.GetFromJsonAsync<HealthResponse>("/healthz", JsonSerializerOptions.Web, timeout.Token);
            Assert.NotNull(health);
            Assert.Equal("ok", health.Status);
            Assert.Equal("connected", health.MqttState);

            // Publish retained device availability & telemetry from the broker
            var observedAt = DateTimeOffset.UtcNow;
            await PublishAsync(broker, $"pulse/devices/{deviceId}/availability", new DeviceAvailability(deviceId, true));
            await PublishAsync(broker, $"pulse/devices/{deviceId}/telemetry", new DeviceTelemetry(
                deviceId, observedAt, 51.5f, 62.0f, 15000f, 32768f, 3500f, 12288f));
            await PublishAsync(broker, $"pulse/devices/{deviceId}/session/availability", new SessionAvailability(deviceId, true, "Ron"));
            await PublishAsync(broker, $"pulse/devices/{deviceId}/session/telemetry", new SessionTelemetry(
                deviceId,
                observedAt,
                Audio: null,
                NowPlaying: new NowPlayingInfo(true, "Song", "Artist", "Album", "Playing", "Spotify"),
                Game: null,
                Keyboard: null,
                LogitechPeripherals: []));

            // Verify GET /api/devices/{deviceId}
            DeviceSnapshot? device = null;
            await WaitUntilAsync(async () =>
            {
                var resp = await client.GetAsync($"/api/devices/{deviceId}", timeout.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    return false;
                }

                device = await resp.Content.ReadFromJsonAsync<DeviceSnapshot>(JsonSerializerOptions.Web, timeout.Token);
                return device is { Online: true, Telemetry: not null, SessionOnline: true, SessionTelemetry: not null };
            }, timeout.Token);

            Assert.NotNull(device);
            Assert.False(device!.IsStale);
            Assert.Equal(51.5f, device.Telemetry!.CpuTemperatureCelsius);
            Assert.Equal("Ron", device.SessionUserName);
            Assert.Equal("Song", device.SessionTelemetry!.NowPlaying!.Title);

            // Verify 404 for unknown device
            var missingResp = await client.GetAsync("/api/devices/nonexistent", timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);

            // Wire up simulated session agent responder on the broker
            broker.InterceptingPublishAsync += async args =>
            {
                if (args.ApplicationMessage.Topic == $"pulse/devices/{deviceId}/session/commands")
                {
                    var cmd = JsonSerializer.Deserialize<SessionCommand>(
                        args.ApplicationMessage.Payload.ToArray(),
                        JsonSerializerOptions.Web);
                    if (cmd is not null && cmd.Action == "media.play_pause")
                    {
                        var result = new SessionCommandResult(
                            cmd.RequestId,
                            deviceId,
                            cmd.Action,
                            true,
                            "Toggled play/pause.",
                            DateTimeOffset.UtcNow);
                        await PublishAsync(broker, $"pulse/devices/{deviceId}/session/results", result, retain: false);
                    }
                }
            };

            // Verify 2-way command succeeds
            var cmdResp = await client.PostAsJsonAsync(
                $"/api/devices/{deviceId}/commands",
                new SendCommandRequest("media.play_pause", RequestId: "req-123"),
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, cmdResp.StatusCode);
            var cmdResult = await cmdResp.Content.ReadFromJsonAsync<SessionCommandResult>(JsonSerializerOptions.Web, timeout.Token);
            Assert.NotNull(cmdResult);
            Assert.Equal("req-123", cmdResult!.RequestId);
            Assert.True(cmdResult.Succeeded);

            // Verify command timeout returns 504
            var timeoutResp = await client.PostAsJsonAsync(
                $"/api/devices/{deviceId}/commands",
                new SendCommandRequest("audio.set_playback_volume"),
                timeout.Token);
            Assert.Equal(HttpStatusCode.GatewayTimeout, timeoutResp.StatusCode);

            // Verify invalid command returns 400
            var badResp = await client.PostAsJsonAsync(
                $"/api/devices/{deviceId}/commands",
                new SendCommandRequest(""),
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

            // Verify SSE /api/events sends initial snapshot
            using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
            await using var stream = await sseResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var eventLine = await reader.ReadLineAsync(timeout.Token);
            var dataLine = await reader.ReadLineAsync(timeout.Token);
            Assert.Equal("event: snapshot", eventLine);
            Assert.NotNull(dataLine);
            Assert.Contains(deviceId, dataLine);
        }
        finally
        {
            await broker.StopAsync();
        }
    }

    private static async Task PublishAsync<T>(MqttServer broker, string topic, T payload, bool retain = true)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await broker.InjectApplicationMessage(new InjectedMqttApplicationMessage(message)
        {
            SenderClientId = "test-agent"
        });
    }

    private static int GetLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("Condition was not satisfied before timeout.");
    }
}
