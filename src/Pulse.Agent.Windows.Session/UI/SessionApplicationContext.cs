using Microsoft.Extensions.Logging;
using Pulse.Agent.Windows.Session.Collectors;
using Pulse.Agent.Windows.Session.Services;

namespace Pulse.Agent.Windows.Session.UI;

internal sealed class SessionApplicationContext : ApplicationContext
{
    private readonly SessionSettingsStore _settingsStore;
    private readonly WindowsServiceManager _serviceManager;
    private readonly SessionMqttService _mqtt;
    private readonly SessionMonitorService _monitor;
    private readonly RawInputMessageWindow _rawInputWindow;
    private readonly MainForm _mainForm;
    private readonly NotifyIcon _trayIcon;
    private Icon _currentTrayIcon;
    private string _serviceName;

    public SessionApplicationContext(ILoggerFactory loggerFactory)
    {
        _settingsStore = new SessionSettingsStore();
        var settings = _settingsStore.LoadSessionSettings();
        _serviceName = settings.ServiceName;

        var audio = new WindowsAudioController(loggerFactory.CreateLogger<WindowsAudioController>());
        var nowPlaying = new NowPlayingCollector(loggerFactory.CreateLogger<NowPlayingCollector>());
        var gameDetector = new GameDetector(loggerFactory.CreateLogger<GameDetector>());
        var keyboard = new KeyboardCollector(loggerFactory.CreateLogger<KeyboardCollector>());
        var logitech = new LogitechPeripheralCollector(loggerFactory.CreateLogger<LogitechPeripheralCollector>());
        _serviceManager = new WindowsServiceManager();

        var commandHandler = new SessionCommandHandler(
            audio,
            nowPlaying,
            TimeProvider.System,
            loggerFactory.CreateLogger<SessionCommandHandler>());

        _mqtt = new SessionMqttService(
            settings.Mqtt,
            commandHandler,
            TimeProvider.System,
            loggerFactory.CreateLogger<SessionMqttService>());

        _monitor = new SessionMonitorService(
            settings,
            audio,
            nowPlaying,
            gameDetector,
            keyboard,
            logitech,
            _serviceManager,
            _mqtt,
            TimeProvider.System,
            loggerFactory.CreateLogger<SessionMonitorService>());

        commandHandler.StateChanged += () => _monitor.RequestImmediateRefresh();
        _rawInputWindow = new RawInputMessageWindow(keyboard);

        _mainForm = new MainForm(
            settings,
            _settingsStore,
            _monitor,
            _mqtt,
            _serviceManager,
            audio,
            nowPlaying);

        _currentTrayIcon = TrayIconFactory.CreatePulseIcon(Color.FromArgb(149, 165, 166));
        _trayIcon = new NotifyIcon
        {
            Icon = _currentTrayIcon,
            Text = "Pulse Session\nStarting...",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };

        _monitor.SnapshotUpdated += OnSnapshotUpdated;
        _mqtt.StatusChanged += OnMqttStatusChanged;

        _mqtt.Start();
        _monitor.Start();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Pulse", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Pulse Service", null, async (_, _) =>
        {
            await _serviceManager.StartAsync(_serviceName);
            _monitor.RequestImmediateRefresh();
        });
        menu.Items.Add("Stop Pulse Service", null, async (_, _) =>
        {
            await _serviceManager.StopAsync(_serviceName);
            _monitor.RequestImmediateRefresh();
        });
        menu.Items.Add("Restart Pulse Service", null, async (_, _) =>
        {
            await _serviceManager.RestartAsync(_serviceName);
            _monitor.RequestImmediateRefresh();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reconnect MQTT", null, (_, _) => _mqtt.Reconnect());
        menu.Items.Add("Refresh Now", null, (_, _) => _monitor.RequestImmediateRefresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitGracefullyAsync());
        return menu;
    }

    private void ShowMainWindow()
    {
        if (_mainForm.IsDisposed)
        {
            return;
        }

        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
        _monitor.RequestImmediateRefresh();
    }

    private void OnSnapshotUpdated(SessionLiveSnapshot snapshot)
    {
        _serviceName = snapshot.ServiceStatus.ServiceName;
        UpdateTrayVisuals(snapshot.ServiceStatus, snapshot.MqttStatus);
        _mainForm.ApplySnapshot(snapshot);
    }

    private void OnMqttStatusChanged(MqttStatusSnapshot mqttStatus)
    {
        var svc = _monitor.LatestSnapshot?.ServiceStatus ?? _serviceManager.GetStatus(_serviceName);
        UpdateTrayVisuals(svc, mqttStatus);
        if (_monitor.LatestSnapshot is { } snap)
        {
            _mainForm.ApplySnapshot(snap with { MqttStatus = mqttStatus });
        }
    }

    private void UpdateTrayVisuals(WindowsServiceStatusInfo serviceStatus, MqttStatusSnapshot mqttStatus)
    {
        if (_mainForm.IsDisposed)
        {
            return;
        }

        void Apply()
        {
            _trayIcon.Text = TrayIconFactory.FormatTooltip(serviceStatus, mqttStatus);
            var color = TrayIconFactory.GetStatusColor(serviceStatus, mqttStatus);
            var nextIcon = TrayIconFactory.CreatePulseIcon(color);
            var oldIcon = _currentTrayIcon;
            _currentTrayIcon = nextIcon;
            _trayIcon.Icon = nextIcon;
            oldIcon.Dispose();
        }

        if (_mainForm.IsHandleCreated && _mainForm.InvokeRequired)
        {
            try { _mainForm.BeginInvoke(Apply); } catch { }
        }
        else
        {
            try { Apply(); } catch { }
        }
    }

    private async Task ExitGracefullyAsync()
    {
        _trayIcon.Visible = false;
        _mainForm.AllowExit();

        await _monitor.DisposeAsync();
        await _mqtt.DisposeAsync();
        _rawInputWindow.Dispose();
        _trayIcon.Dispose();
        _currentTrayIcon.Dispose();
        _mainForm.Close();
        ExitThread();
    }
}
