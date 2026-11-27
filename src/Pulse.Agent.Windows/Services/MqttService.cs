using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Services;

internal sealed class MqttService(IOptions<MqttOptions> options, TimeProvider timeProvider, ILogger<MqttService> logger)
    : BackgroundService, ITelemetryPublisher
{
    private static readonly TimeSpan CommunicationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly MqttOptions _options = options.Value;
    private readonly Channel<DeviceTelemetry> _readings = Channel.CreateBounded<DeviceTelemetry>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private DeviceTelemetry? _pendingReading;

    public void PublishTelemetry(float? cpuTemperatureCelsius, float? gpuTemperatureCelsius, DateTimeOffset observedAt)
    {
        if (_options.Enabled)
        {
            _readings.Writer.TryWrite(new DeviceTelemetry(
                _options.DeviceId,
                observedAt,
                cpuTemperatureCelsius,
                gpuTemperatureCelsius));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var topic = $"pulse/devices/{_options.DeviceId}";
        var availabilityTopic = $"{topic}/availability";
        var online = CreateMessage(availabilityTopic, new DeviceAvailability(_options.DeviceId, true));
        var offlinePayload = SerializePayload(new DeviceAvailability(_options.DeviceId, false));
        var offline = CreateMessage(availabilityTopic, offlinePayload);
        var clientOptions = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId($"pulse-{_options.DeviceId}")
            .WithCleanSession()
            .WithTimeout(CommunicationTimeout)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic(availabilityTopic)
            .WithWillPayload(offlinePayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain();

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            clientOptions.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            clientOptions.WithTlsOptions(tls => tls.UseTls());
        }

        var settings = clientOptions.Build();
        var factory = new MqttClientFactory();
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var client = factory.CreateMqttClient())
            {
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += _ =>
                {
                    disconnected.TrySetResult();
                    return Task.CompletedTask;
                };

                try
                {
                    var connection = await client.ConnectAsync(settings, stoppingToken);
                    if (connection.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        throw new MqttCommunicationException($"The MQTT broker rejected the connection with {connection.ResultCode}.");
                    }

                    await PublishAsync(client, online, stoppingToken);
                    logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", _options.Host, _options.Port);
                    await PublishReadingsAsync(client, $"{topic}/telemetry", disconnected.Task, stoppingToken);
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("MQTT connection lost. Retrying in {Seconds} seconds", ReconnectDelay.TotalSeconds);
                    }
                }
                catch (Exception exception) when (exception is MqttCommunicationException or OperationCanceledException)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "MQTT communication failed. Retrying in {Seconds} seconds",
                            ReconnectDelay.TotalSeconds);
                    }
                }
                finally
                {
                    if (stoppingToken.IsCancellationRequested && client.IsConnected)
                    {
                        await PublishOfflineAsync(client, offline);
                    }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ReconnectDelay, timeProvider, stoppingToken);
            }
        }
    }

    private async Task PublishReadingsAsync(IMqttClient client, string topic, Task disconnected,
        CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            while (client.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                if (_readings.Reader.TryRead(out var reading))
                {
                    _pendingReading = reading;
                }

                if (_pendingReading is not null)
                {
                    await PublishAsync(client, CreateMessage(topic, _pendingReading), session.Token);
                    _pendingReading = null;
                    continue;
                }

                var readingAvailable = _readings.Reader.WaitToReadAsync(session.Token).AsTask();
                if (await Task.WhenAny(readingAvailable, disconnected) == disconnected || !await readingAvailable)
                {
                    return;
                }
            }
        }
        finally
        {
            session.Cancel();
        }
    }

    private async Task PublishOfflineAsync(IMqttClient client, MqttApplicationMessage offline)
    {
        using var timeout = new CancellationTokenSource(CommunicationTimeout);
        try
        {
            await PublishAsync(client, offline, timeout.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
        }
        catch (Exception exception) when (exception is MqttCommunicationException or OperationCanceledException)
        {
            logger.LogDebug(exception, "Could not send MQTT offline status during shutdown");
        }
    }

    private static async Task PublishAsync(IMqttClient client, MqttApplicationMessage message, CancellationToken token)
    {
        var result = await client.PublishAsync(message, token);
        if ((int)result.ReasonCode >= 128)
        {
            throw new MqttCommunicationException($"The MQTT broker rejected publication with {result.ReasonCode}.");
        }
    }

    private static byte[] SerializePayload<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web);

    private static MqttApplicationMessage CreateMessage<T>(string topic, T payload) =>
        CreateMessage(topic, SerializePayload(payload));

    private static MqttApplicationMessage CreateMessage(string topic, byte[] payload) => new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(payload)
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .WithRetainFlag()
        .Build();
}
