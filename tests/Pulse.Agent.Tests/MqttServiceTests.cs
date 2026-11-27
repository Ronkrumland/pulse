using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Pulse.Agent.Windows.Services;
using Xunit;

namespace Pulse.Agent.Tests;

public class MqttServiceTests
{
    [Fact]
    public async Task PublishesRetainedTelemetryAndAvailabilityAndReconnectsAfterDisconnect()
    {
        const string deviceId = "test-device";
        const string telemetryTopic = "pulse/devices/test-device/telemetry";
        const string availabilityTopic = "pulse/devices/test-device/availability";
        const string username = "test-user";
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

        using var service = new MqttService(Options.Create(new MqttOptions
        {
            Host = "127.0.0.1",
            Port = port,
            DeviceId = deviceId,
            Username = username,
            Password = password
        }), TimeProvider.System, NullLogger<MqttService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstObservation = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var secondObservation = firstObservation.AddSeconds(5);
        var latestObservation = secondObservation.AddSeconds(5);

        try
        {
            await service.StartAsync(timeout.Token);
            service.PublishTelemetry(43.25f, 61.5f, firstObservation);

            var first = await WaitForRetainedMessageAsync(broker, telemetryTopic, _ => true, timeout.Token);
            Assert.Equal(deviceId, first.GetProperty("deviceId").GetString());
            Assert.Equal(firstObservation, first.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal(43.25f, first.GetProperty("cpuTemperatureCelsius").GetSingle());
            Assert.Equal(61.5f, first.GetProperty("gpuTemperatureCelsius").GetSingle());
            var online = await WaitForRetainedMessageAsync(broker, availabilityTopic,
                json => json.GetProperty("online").GetBoolean(), timeout.Token);
            Assert.Equal(deviceId, online.GetProperty("deviceId").GetString());

            var agent = Assert.Single(await broker.GetClientsAsync());
            await agent.DisconnectAsync();
            await WaitForRetainedMessageAsync(broker, availabilityTopic,
                json => !json.GetProperty("online").GetBoolean(), timeout.Token);

            service.PublishTelemetry(46f, 63f, secondObservation);
            service.PublishTelemetry(48f, null, latestObservation);
            var latest = await WaitForRetainedMessageAsync(broker, telemetryTopic,
                json => json.GetProperty("observedAt").GetDateTimeOffset() == latestObservation, timeout.Token);
            Assert.Equal(48f, latest.GetProperty("cpuTemperatureCelsius").GetSingle());
            Assert.Equal(JsonValueKind.Null, latest.GetProperty("gpuTemperatureCelsius").ValueKind);
            await WaitForRetainedMessageAsync(broker, availabilityTopic,
                json => json.GetProperty("online").GetBoolean(), timeout.Token);

            await service.StopAsync(timeout.Token);
            await WaitForRetainedMessageAsync(broker, availabilityTopic,
                json => !json.GetProperty("online").GetBoolean(), timeout.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await broker.StopAsync();
        }
    }

    private static int GetLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonElement> WaitForRetainedMessageAsync(MqttServer broker, string topic,
        Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            var messages = await broker.GetRetainedMessagesAsync();
            var message = messages.SingleOrDefault(message => message.Topic == topic);
            if (message is not null)
            {
                Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.QualityOfServiceLevel);
                var payload = JsonSerializer.Deserialize<JsonElement>(message.Payload.ToArray());
                if (predicate(payload))
                {
                    return payload;
                }
            }

            await Task.Delay(25, cancellationToken);
        }
    }
}
