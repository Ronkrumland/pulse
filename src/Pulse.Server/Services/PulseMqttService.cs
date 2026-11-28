using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Pulse.Contracts;
using Pulse.Server.Models;
using Pulse.Server.Options;

namespace Pulse.Server.Services;

public enum CommandDispatchStatus
{
    Completed,
    MqttUnavailable,
    TimedOut
}

public sealed record CommandDispatchResult(
    CommandDispatchStatus Status,
    SessionCommandResult? Result = null,
    string? Error = null);

public interface ISessionCommandDispatcher
{
    MqttStatusSnapshot Status { get; }
    bool IsReady { get; }
    Task<CommandDispatchResult> SendCommandAsync(SessionCommand command, CancellationToken cancellationToken = default);
}

internal sealed class PulseMqttService(
    IOptions<MqttOptions> options,
    DeviceStateStore stateStore,
    TimeProvider timeProvider,
    ILogger<PulseMqttService> logger) : BackgroundService, ISessionCommandDispatcher
{
    private static readonly TimeSpan CommunicationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly MqttOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SessionCommandResult>> _pendingCommands =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();

    private IMqttClient? _activeClient;
    private MqttStatusSnapshot _status = new(MqttConnectionState.Disabled, "Broker host not configured");

    public MqttStatusSnapshot Status
    {
        get { lock (_sync) return _status; }
    }

    public bool IsReady
    {
        get
        {
            var state = Status.State;
            return state is MqttConnectionState.Connected or MqttConnectionState.Disabled;
        }
    }

    public async Task<CommandDispatchResult> SendCommandAsync(SessionCommand command, CancellationToken cancellationToken = default)
    {
        IMqttClient? client;
        lock (_sync)
        {
            client = _activeClient;
        }

        if (client is null || !client.IsConnected)
        {
            return new CommandDispatchResult(
                CommandDispatchStatus.MqttUnavailable,
                Error: "MQTT broker is not currently connected.");
        }

        var tcs = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(command.RequestId, tcs))
        {
            return new CommandDispatchResult(
                CommandDispatchStatus.MqttUnavailable,
                Error: $"A command with requestId '{command.RequestId}' is already in flight.");
        }

        try
        {
            var topic = $"pulse/devices/{command.DeviceId}/session/commands";
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(JsonSerializer.SerializeToUtf8Bytes(command, JsonSerializerOptions.Web))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            using var publishTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishTimeout.CancelAfter(CommunicationTimeout);
            var publishResult = await client.PublishAsync(message, publishTimeout.Token);
            if ((int)publishResult.ReasonCode >= 128)
            {
                return new CommandDispatchResult(
                    CommandDispatchStatus.MqttUnavailable,
                    Error: $"MQTT broker rejected command publication: {publishResult.ReasonCode}");
            }

            var commandTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds));
            try
            {
                var result = await tcs.Task.WaitAsync(commandTimeout, timeProvider, cancellationToken);
                return new CommandDispatchResult(CommandDispatchStatus.Completed, Result: result);
            }
            catch (TimeoutException)
            {
                return new CommandDispatchResult(
                    CommandDispatchStatus.TimedOut,
                    Error: $"Timed out waiting for session command '{command.Action}' result after {commandTimeout.TotalSeconds:0}s.");
            }
        }
        catch (MqttCommunicationException ex)
        {
            return new CommandDispatchResult(
                CommandDispatchStatus.MqttUnavailable,
                Error: $"Failed to publish command over MQTT: {ex.Message}");
        }
        finally
        {
            _pendingCommands.TryRemove(command.RequestId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            SetStatus(MqttConnectionState.Disabled, "Broker host not configured");
            logger.LogWarning("Mqtt:Host is not configured; Pulse.Server is running without an MQTT connection");
            return;
        }

        var clientId = string.IsNullOrWhiteSpace(_options.ClientId)
            ? "pulse-server"
            : _options.ClientId.Trim();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithTcpServer(_options.Host.Trim(), _options.Port)
            .WithClientId(clientId)
            .WithCleanSession()
            .WithTimeout(CommunicationTimeout)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            optionsBuilder.WithTlsOptions(tls => tls.UseTls());
        }

        var clientOptions = optionsBuilder.Build();
        var factory = new MqttClientFactory();

        while (!stoppingToken.IsCancellationRequested)
        {
            SetStatus(MqttConnectionState.Connecting, $"Connecting to {_options.Host}:{_options.Port}...");

            using (var client = factory.CreateMqttClient())
            {
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += _ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeClient, client))
                        {
                            _activeClient = null;
                        }
                    }

                    disconnected.TrySetResult();
                    return Task.CompletedTask;
                };

                client.ApplicationMessageReceivedAsync += e =>
                {
                    HandleMessage(e.ApplicationMessage);
                    return Task.CompletedTask;
                };

                try
                {
                    var connectResult = await client.ConnectAsync(clientOptions, stoppingToken);
                    if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        throw new MqttCommunicationException($"MQTT broker rejected connection: {connectResult.ResultCode}");
                    }

                    var subOptions = factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic("pulse/devices/+/availability").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic("pulse/devices/+/telemetry").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic("pulse/devices/+/session/availability").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic("pulse/devices/+/session/telemetry").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic("pulse/devices/+/session/results").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build();

                    var subResult = await client.SubscribeAsync(subOptions, stoppingToken);
                    foreach (var item in subResult.Items)
                    {
                        if ((int)item.ResultCode >= 128)
                        {
                            logger.LogWarning("MQTT broker rejected subscription to {Topic}: {ResultCode}",
                                item.TopicFilter.Topic, item.ResultCode);
                        }
                    }

                    lock (_sync)
                    {
                        _activeClient = client;
                    }

                    SetStatus(MqttConnectionState.Connected, $"Connected ({_options.Host}:{_options.Port})");
                    logger.LogInformation("Connected to MQTT broker at {Host}:{Port} as {ClientId}",
                        _options.Host, _options.Port, clientId);

                    await WaitForDisconnectOrStopAsync(disconnected.Task, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Disconnected, "Connection lost, reconnecting...");
                        logger.LogWarning("MQTT connection lost. Retrying in {Seconds} seconds", ReconnectDelay.TotalSeconds);
                    }
                }
                catch (Exception ex) when (ex is MqttCommunicationException or OperationCanceledException)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Error, ex.Message);
                        logger.LogWarning(ex, "MQTT communication failed. Retrying in {Seconds} seconds", ReconnectDelay.TotalSeconds);
                    }
                }
                catch (Exception ex)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Error, ex.Message);
                        logger.LogWarning(ex, "Unexpected MQTT error. Retrying in {Seconds} seconds", ReconnectDelay.TotalSeconds);
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeClient, client))
                        {
                            _activeClient = null;
                        }
                    }

                    if (client.IsConnected)
                    {
                        try
                        {
                            using var disconnectTimeout = new CancellationTokenSource(CommunicationTimeout);
                            await client.DisconnectAsync(new MqttClientDisconnectOptions(), disconnectTimeout.Token);
                        }
                        catch
                        {
                            // Ignore disconnect errors during shutdown/cleanup
                        }
                    }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetStatus(MqttConnectionState.Disconnected, "Stopped");
    }

    internal void HandleMessage(MqttApplicationMessage message)
    {
        try
        {
            if (message.Payload.IsEmpty || string.IsNullOrWhiteSpace(message.Topic))
            {
                return;
            }

            var segments = message.Topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4 || !segments[0].Equals("pulse", StringComparison.Ordinal) || !segments[1].Equals("devices", StringComparison.Ordinal))
            {
                return;
            }

            var topicDeviceId = segments[2];
            var payload = message.Payload;

            if (segments.Length == 4)
            {
                switch (segments[3])
                {
                    case "availability":
                        var availability = Deserialize<DeviceAvailability>(payload);
                        if (availability is not null)
                        {
                            var deviceId = ResolveDeviceId(topicDeviceId, availability.DeviceId);
                            stateStore.UpdateAvailability(deviceId, availability with { DeviceId = deviceId });
                        }
                        break;

                    case "telemetry":
                        var telemetry = Deserialize<DeviceTelemetry>(payload);
                        if (telemetry is not null)
                        {
                            var deviceId = ResolveDeviceId(topicDeviceId, telemetry.DeviceId);
                            stateStore.UpdateTelemetry(deviceId, telemetry with { DeviceId = deviceId });
                        }
                        break;
                }
            }
            else if (segments.Length == 5 && segments[3].Equals("session", StringComparison.Ordinal))
            {
                switch (segments[4])
                {
                    case "availability":
                        var sessionAvailability = Deserialize<SessionAvailability>(payload);
                        if (sessionAvailability is not null)
                        {
                            var deviceId = ResolveDeviceId(topicDeviceId, sessionAvailability.DeviceId);
                            stateStore.UpdateSessionAvailability(deviceId, sessionAvailability with { DeviceId = deviceId });
                        }
                        break;

                    case "telemetry":
                        var sessionTelemetry = Deserialize<SessionTelemetry>(payload);
                        if (sessionTelemetry is not null)
                        {
                            var deviceId = ResolveDeviceId(topicDeviceId, sessionTelemetry.DeviceId);
                            stateStore.UpdateSessionTelemetry(deviceId, sessionTelemetry with { DeviceId = deviceId });
                        }
                        break;

                    case "results":
                        var commandResult = Deserialize<SessionCommandResult>(payload);
                        if (commandResult is not null && !string.IsNullOrWhiteSpace(commandResult.RequestId))
                        {
                            if (_pendingCommands.TryRemove(commandResult.RequestId, out var tcs))
                            {
                                tcs.TrySetResult(commandResult);
                            }
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process MQTT message on topic {Topic}", message.Topic);
        }
    }

    private static string ResolveDeviceId(string topicDeviceId, string? payloadDeviceId) =>
        !string.IsNullOrWhiteSpace(topicDeviceId) ? topicDeviceId : (payloadDeviceId ?? "");

    private static T? Deserialize<T>(ReadOnlySequence<byte> payload) =>
        payload.IsSingleSegment
            ? JsonSerializer.Deserialize<T>(payload.FirstSpan, JsonSerializerOptions.Web)
            : JsonSerializer.Deserialize<T>(payload.ToArray(), JsonSerializerOptions.Web);

    private static async Task WaitForDisconnectOrStopAsync(Task disconnectedTask, CancellationToken stoppingToken)
    {
        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stoppingToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), stopTcs);
        await Task.WhenAny(disconnectedTask, stopTcs.Task);
    }

    private void SetStatus(MqttConnectionState state, string detail)
    {
        lock (_sync)
        {
            _status = new MqttStatusSnapshot(state, detail);
        }
    }
}
