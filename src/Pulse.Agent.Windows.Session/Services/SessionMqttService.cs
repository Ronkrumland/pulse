using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Services;

public enum MqttConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Disconnected,
    Error
}

public sealed record MqttStatusSnapshot(MqttConnectionState State, string Detail);

internal sealed class SessionMqttService : IAsyncDisposable
{
    private static readonly TimeSpan CommunicationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly ISessionCommandHandler _commandHandler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionMqttService> _logger;
    private readonly Channel<SessionTelemetry> _telemetryChannel = Channel.CreateBounded<SessionTelemetry>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<SessionCommandResult> _resultChannel = Channel.CreateUnbounded<SessionCommandResult>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly object _sync = new();
    private MqttSettings _settings;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _loopTask;
    private SessionTelemetry? _pendingTelemetry;
    private MqttStatusSnapshot _status = new(MqttConnectionState.Disabled, "Not configured");

    public SessionMqttService(
        MqttSettings initialSettings,
        ISessionCommandHandler commandHandler,
        TimeProvider timeProvider,
        ILogger<SessionMqttService> logger)
    {
        _settings = initialSettings.Clone();
        _commandHandler = commandHandler;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event Action<MqttStatusSnapshot>? StatusChanged;

    public MqttStatusSnapshot Status
    {
        get { lock (_sync) return _status; }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token);
            _loopTask = Task.Run(() => RunLoopAsync(_runCts.Token));
        }
    }

    public void Reconfigure(MqttSettings newSettings)
    {
        lock (_sync)
        {
            _settings = newSettings.Clone();
            _reconnectCts?.Cancel();
        }
    }

    public void Reconnect()
    {
        lock (_sync)
        {
            _reconnectCts?.Cancel();
        }
    }

    public void PublishTelemetry(SessionTelemetry telemetry)
    {
        MqttSettings current;
        lock (_sync) current = _settings;
        if (current.Enabled)
        {
            _telemetryChannel.Writer.TryWrite(telemetry);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_sync)
        {
            _runCts?.Cancel();
            _reconnectCts?.Cancel();
            loop = _loopTask;
            _loopTask = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Ignore shutdown timeout
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _reconnectCts?.Dispose();
        _runCts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken appStoppingToken)
    {
        var factory = new MqttClientFactory();

        while (!appStoppingToken.IsCancellationRequested)
        {
            MqttSettings settings;
            CancellationToken cycleToken;
            lock (_sync)
            {
                _reconnectCts?.Dispose();
                _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(appStoppingToken);
                cycleToken = _reconnectCts.Token;
                settings = _settings.Clone();
            }

            if (!settings.Enabled)
            {
                SetStatus(MqttConnectionState.Disabled, "Broker host not configured");
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cycleToken);
                }
                catch (OperationCanceledException)
                {
                    // Reconfigured or stopping
                }

                continue;
            }

            var deviceId = string.IsNullOrWhiteSpace(settings.DeviceId)
                ? Environment.MachineName
                : settings.DeviceId.Trim();
            var baseTopic = $"pulse/devices/{deviceId}/session";
            var availabilityTopic = $"{baseTopic}/availability";
            var telemetryTopic = $"{baseTopic}/telemetry";
            var commandTopic = $"{baseTopic}/commands";
            var resultTopic = $"{baseTopic}/results";

            var onlinePayload = CreateRetainedMessage(availabilityTopic, new SessionAvailability(deviceId, true, Environment.UserName));
            var offlineBytes = SerializePayload(new SessionAvailability(deviceId, false, Environment.UserName));
            var offlinePayload = CreateRetainedMessage(availabilityTopic, offlineBytes);

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithTcpServer(settings.Host.Trim(), settings.Port)
                .WithClientId($"pulse-{deviceId}-session")
                .WithCleanSession()
                .WithTimeout(CommunicationTimeout)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .WithWillTopic(availabilityTopic)
                .WithWillPayload(offlineBytes)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain();

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                optionsBuilder.WithCredentials(settings.Username, settings.Password);
            }

            if (settings.UseTls)
            {
                optionsBuilder.WithTlsOptions(tls => tls.UseTls());
            }

            var clientOptions = optionsBuilder.Build();
            SetStatus(MqttConnectionState.Connecting, $"Connecting to {settings.Host}:{settings.Port}...");

            using (var client = factory.CreateMqttClient())
            {
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.DisconnectedAsync += _ =>
                {
                    disconnected.TrySetResult();
                    return Task.CompletedTask;
                };

                client.ApplicationMessageReceivedAsync += async e =>
                {
                    await HandleIncomingMessageAsync(e.ApplicationMessage, deviceId, cycleToken);
                };

                try
                {
                    var connectResult = await client.ConnectAsync(clientOptions, cycleToken);
                    if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        throw new MqttCommunicationException($"Broker rejected connection: {connectResult.ResultCode}");
                    }

                    // Subscribe to 2-way command topic
                    var subOptions = factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f
                            .WithTopic(commandTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build();
                    await client.SubscribeAsync(subOptions, cycleToken);

                    // Publish retained session online status
                    await PublishAsync(client, onlinePayload, cycleToken);
                    SetStatus(MqttConnectionState.Connected, $"Connected ({settings.Host}:{settings.Port})");
                    _logger.LogInformation("Session MQTT connected to {Host}:{Port} (2-way on {CommandTopic})",
                        settings.Host, settings.Port, commandTopic);

                    await PumpOutboundAsync(client, telemetryTopic, resultTopic, disconnected.Task, cycleToken);

                    if (!cycleToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Disconnected, "Connection lost, reconnecting...");
                    }
                }
                catch (Exception ex) when (ex is MqttCommunicationException or OperationCanceledException)
                {
                    if (!cycleToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Error, ex.Message);
                        _logger.LogWarning(ex, "Session MQTT error; retrying in {Seconds}s", ReconnectDelay.TotalSeconds);
                    }
                }
                catch (Exception ex)
                {
                    if (!cycleToken.IsCancellationRequested)
                    {
                        SetStatus(MqttConnectionState.Error, ex.Message);
                        _logger.LogWarning(ex, "Unexpected Session MQTT error");
                    }
                }
                finally
                {
                    if (cycleToken.IsCancellationRequested && client.IsConnected)
                    {
                        await PublishOfflineGracefullyAsync(client, offlinePayload);
                    }
                }
            }

            if (!cycleToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelay, _timeProvider, cycleToken);
                }
                catch (OperationCanceledException)
                {
                    // Reconfigured or stopping
                }
            }
        }

        SetStatus(MqttConnectionState.Disconnected, "Stopped");
    }

    private async Task HandleIncomingMessageAsync(MqttApplicationMessage message, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var payload = message.Payload;
            if (payload.IsEmpty)
            {
                return;
            }

            var command = JsonSerializer.Deserialize<SessionCommand>(payload.FirstSpan, JsonSerializerOptions.Web);
            if (command is null)
            {
                return;
            }

            var normalizedCommand = string.IsNullOrWhiteSpace(command.DeviceId)
                ? command with { DeviceId = deviceId }
                : command;

            var result = await _commandHandler.HandleAsync(normalizedCommand, cancellationToken);
            _resultChannel.Writer.TryWrite(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process incoming MQTT command on {Topic}", message.Topic);
        }
    }

    private async Task PumpOutboundAsync(
        IMqttClient client,
        string telemetryTopic,
        string resultTopic,
        Task disconnected,
        CancellationToken cycleToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
        try
        {
            while (client.IsConnected && !cycleToken.IsCancellationRequested)
            {
                // 1. Drain any pending command results first (non-retained)
                while (_resultChannel.Reader.TryRead(out var result))
                {
                    await PublishAsync(client, CreateNonRetainedMessage(resultTopic, result), linked.Token);
                }

                // 2. Drain latest telemetry (retained)
                if (_telemetryChannel.Reader.TryRead(out var reading))
                {
                    _pendingTelemetry = reading;
                }

                if (_pendingTelemetry is not null)
                {
                    await PublishAsync(client, CreateRetainedMessage(telemetryTopic, _pendingTelemetry), linked.Token);
                    _pendingTelemetry = null;
                    continue;
                }

                var waitTelemetry = _telemetryChannel.Reader.WaitToReadAsync(linked.Token).AsTask();
                var waitResult = _resultChannel.Reader.WaitToReadAsync(linked.Token).AsTask();
                var completed = await Task.WhenAny(waitTelemetry, waitResult, disconnected);
                if (completed == disconnected)
                {
                    return;
                }
            }
        }
        finally
        {
            linked.Cancel();
        }
    }

    private static async Task PublishOfflineGracefullyAsync(IMqttClient client, MqttApplicationMessage offline)
    {
        using var timeout = new CancellationTokenSource(CommunicationTimeout);
        try
        {
            await PublishAsync(client, offline, timeout.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
        }
        catch
        {
            // Ignore shutdown errors
        }
    }

    private static async Task PublishAsync(IMqttClient client, MqttApplicationMessage message, CancellationToken token)
    {
        var result = await client.PublishAsync(message, token);
        if ((int)result.ReasonCode >= 128)
        {
            throw new MqttCommunicationException($"MQTT broker rejected publication: {result.ReasonCode}");
        }
    }

    private void SetStatus(MqttConnectionState state, string detail)
    {
        var snapshot = new MqttStatusSnapshot(state, detail);
        lock (_sync)
        {
            if (_status == snapshot)
            {
                return;
            }

            _status = snapshot;
        }

        StatusChanged?.Invoke(snapshot);
    }

    private static byte[] SerializePayload<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web);

    private static MqttApplicationMessage CreateRetainedMessage<T>(string topic, T payload) =>
        CreateRetainedMessage(topic, SerializePayload(payload));

    private static MqttApplicationMessage CreateRetainedMessage(string topic, byte[] payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

    private static MqttApplicationMessage CreateNonRetainedMessage<T>(string topic, T payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(SerializePayload(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();
}
