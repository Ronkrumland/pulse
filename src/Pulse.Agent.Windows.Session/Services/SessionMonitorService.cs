using Microsoft.Extensions.Logging;
using Pulse.Agent.Windows.Session.Collectors;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.Services;

public sealed record SessionLiveSnapshot(
    SessionTelemetry Telemetry,
    WindowsServiceStatusInfo ServiceStatus,
    MqttStatusSnapshot MqttStatus);

internal sealed class SessionMonitorService : IAsyncDisposable
{
    private readonly IAudioController _audio;
    private readonly INowPlayingController _nowPlaying;
    private readonly IGameDetector _gameDetector;
    private readonly IKeyboardCollector _keyboard;
    private readonly ILogitechCollector _logitech;
    private readonly WindowsServiceManager _serviceManager;
    private readonly SessionMqttService _mqtt;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionMonitorService> _logger;

    private readonly object _sync = new();
    private SessionAppSettings _settings;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private SessionLiveSnapshot? _latestSnapshot;

    public SessionMonitorService(
        SessionAppSettings initialSettings,
        IAudioController audio,
        INowPlayingController nowPlaying,
        IGameDetector gameDetector,
        IKeyboardCollector keyboard,
        ILogitechCollector logitech,
        WindowsServiceManager serviceManager,
        SessionMqttService mqtt,
        TimeProvider timeProvider,
        ILogger<SessionMonitorService> logger)
    {
        _settings = initialSettings.Clone();
        _audio = audio;
        _nowPlaying = nowPlaying;
        _gameDetector = gameDetector;
        _keyboard = keyboard;
        _logitech = logitech;
        _serviceManager = serviceManager;
        _mqtt = mqtt;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event Action<SessionLiveSnapshot>? SnapshotUpdated;

    public SessionLiveSnapshot? LatestSnapshot
    {
        get { lock (_sync) return _latestSnapshot; }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void UpdateSettings(SessionAppSettings settings)
    {
        lock (_sync)
        {
            _settings = settings.Clone();
        }

        RequestImmediateRefresh();
    }

    public void RequestImmediateRefresh()
    {
        if (_refreshSignal.CurrentCount == 0)
        {
            try { _refreshSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public async Task<SessionLiveSnapshot> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        SessionAppSettings settings;
        lock (_sync) settings = _settings.Clone();

        var deviceId = string.IsNullOrWhiteSpace(settings.Mqtt.DeviceId)
            ? Environment.MachineName
            : settings.Mqtt.DeviceId.Trim();

        var audio = _audio.ReadState();
        var nowPlaying = await _nowPlaying.ReadAsync(cancellationToken);
        var game = _gameDetector.Detect(settings.CustomGameProcesses);
        var logitech = _logitech.ReadPeripherals();
        var keyboard = _keyboard.ReadCurrentKeyboard(logitech);
        var serviceStatus = _serviceManager.GetStatus(settings.ServiceName);

        var telemetry = new SessionTelemetry(
            deviceId,
            _timeProvider.GetUtcNow(),
            audio,
            nowPlaying,
            game,
            keyboard,
            logitech);

        _mqtt.PublishTelemetry(telemetry);

        var snapshot = new SessionLiveSnapshot(telemetry, serviceStatus, _mqtt.Status);
        lock (_sync)
        {
            _latestSnapshot = snapshot;
        }

        SnapshotUpdated?.Invoke(snapshot);
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_sync)
        {
            _cts?.Cancel();
            loop = _loopTask;
            _loopTask = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore
            }
        }

        _cts?.Dispose();
        _refreshSignal.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session polling iteration failed");
            }

            int intervalSeconds;
            lock (_sync) intervalSeconds = Math.Clamp(_settings.PollingIntervalSeconds, 1, 86400);

            try
            {
                await _refreshSignal.WaitAsync(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
