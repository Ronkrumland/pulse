using System.ServiceProcess;
using Pulse.Agent.Windows.Session.Collectors;
using Pulse.Agent.Windows.Session.Services;
using Pulse.Contracts;

namespace Pulse.Agent.Windows.Session.UI;

internal sealed class MainForm : Form
{
    private readonly SessionSettingsStore _settingsStore;
    private readonly SessionMonitorService _monitor;
    private readonly SessionMqttService _mqtt;
    private readonly WindowsServiceManager _serviceManager;
    private readonly IAudioController _audioController;
    private readonly INowPlayingController _mediaController;

    private SessionAppSettings _currentSettings;
    private bool _updatingAudioUi;
    private bool _allowClose;

    // Status tab controls
    private readonly Label _lblBannerService = new();
    private readonly Label _lblBannerMqtt = new();
    private readonly Label _lblLastUpdated = new();

    private readonly ComboBox _cmbPlaybackDevice = new();
    private readonly TrackBar _trkPlaybackVolume = new();
    private readonly Label _lblPlaybackVolume = new();
    private readonly CheckBox _chkPlaybackMute = new();

    private readonly ComboBox _cmbCaptureDevice = new();
    private readonly TrackBar _trkCaptureVolume = new();
    private readonly Label _lblCaptureVolume = new();
    private readonly CheckBox _chkCaptureMute = new();

    private readonly Label _lblNowPlayingTitle = new();
    private readonly Label _lblNowPlayingDetails = new();

    private readonly Label _lblGameStatus = new();
    private readonly Label _lblKeyboardStatus = new();
    private readonly ListView _lvLogitech = new();

    // Session settings tab controls
    private readonly NumericUpDown _numSessionPoll = new();
    private readonly TextBox _txtSessionMqttHost = new();
    private readonly NumericUpDown _numSessionMqttPort = new();
    private readonly TextBox _txtSessionDeviceId = new();
    private readonly TextBox _txtSessionUsername = new();
    private readonly TextBox _txtSessionPassword = new();
    private readonly CheckBox _chkSessionUseTls = new();
    private readonly TextBox _txtCustomGames = new();
    private readonly CheckBox _chkStartWithWindows = new();
    private readonly CheckBox _chkSyncMqttToService = new();
    private readonly Label _lblSettingsFeedback = new();

    // Windows Service tab controls
    private readonly TextBox _txtServiceName = new();
    private readonly Label _lblServiceState = new();
    private readonly Label _lblServiceStartType = new();
    private readonly Label _lblAgentConfigPath = new();
    private readonly NumericUpDown _numServicePoll = new();
    private readonly TextBox _txtServiceMqttHost = new();
    private readonly NumericUpDown _numServiceMqttPort = new();
    private readonly TextBox _txtServiceDeviceId = new();
    private readonly TextBox _txtServiceUsername = new();
    private readonly TextBox _txtServicePassword = new();
    private readonly CheckBox _chkServiceUseTls = new();
    private readonly Label _lblServiceFeedback = new();

    private List<AudioDeviceInfo> _playbackDevices = [];
    private List<AudioDeviceInfo> _captureDevices = [];

    public MainForm(
        SessionAppSettings initialSettings,
        SessionSettingsStore settingsStore,
        SessionMonitorService monitor,
        SessionMqttService mqtt,
        WindowsServiceManager serviceManager,
        IAudioController audioController,
        INowPlayingController mediaController)
    {
        _currentSettings = initialSettings.Clone();
        _settingsStore = settingsStore;
        _monitor = monitor;
        _mqtt = mqtt;
        _serviceManager = serviceManager;
        _audioController = audioController;
        _mediaController = mediaController;

        Text = "Pulse — Windows Session Agent";
        Size = new Size(780, 680);
        MinimumSize = new Size(700, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        Icon = TrayIconFactory.CreatePulseIcon(Color.FromArgb(96, 195, 255));

        BuildLayout();
        LoadSettingsIntoUi();
        LoadAgentConfigIntoUi();

        if (_monitor.LatestSnapshot is { } snap)
        {
            ApplySnapshot(snap);
        }
    }

    public void AllowExit() => _allowClose = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    public void ApplySnapshot(SessionLiveSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplySnapshot(snapshot)); } catch { }
            return;
        }

        // Top banner
        _lblBannerService.Text = $"Service ({snapshot.ServiceStatus.ServiceName}): {snapshot.ServiceStatus.StatusText}";
        _lblBannerService.ForeColor = snapshot.ServiceStatus.Status == ServiceControllerStatus.Running
            ? Color.ForestGreen
            : Color.DarkOrange;

        _lblBannerMqtt.Text = $"MQTT: {snapshot.MqttStatus.State} — {snapshot.MqttStatus.Detail}";
        _lblBannerMqtt.ForeColor = snapshot.MqttStatus.State switch
        {
            MqttConnectionState.Connected => Color.ForestGreen,
            MqttConnectionState.Error => Color.Firebrick,
            MqttConnectionState.Connecting => Color.DarkOrange,
            _ => Color.DimGray
        };

        _lblLastUpdated.Text = $"Updated: {snapshot.Telemetry.ObservedAt.ToLocalTime():HH:mm:ss}";

        // Service tab status
        _lblServiceState.Text = snapshot.ServiceStatus.StatusText;
        _lblServiceStartType.Text = snapshot.ServiceStatus.StartType ?? "Unknown";

        // Audio
        if (snapshot.Telemetry.Audio is { } audio)
        {
            UpdateAudioUi(audio);
        }

        // Now Playing
        if (snapshot.Telemetry.NowPlaying is { } np && np.IsActive)
        {
            _lblNowPlayingTitle.Text = string.IsNullOrWhiteSpace(np.Title) ? "(Untitled)" : np.Title;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(np.Artist)) parts.Add(np.Artist);
            if (!string.IsNullOrWhiteSpace(np.Album)) parts.Add(np.Album);
            if (!string.IsNullOrWhiteSpace(np.SourceApp)) parts.Add($"[{np.SourceApp}]");
            if (!string.IsNullOrWhiteSpace(np.PlaybackStatus)) parts.Add($"• {np.PlaybackStatus}");
            _lblNowPlayingDetails.Text = string.Join("  ", parts);
        }
        else
        {
            _lblNowPlayingTitle.Text = "Nothing playing";
            _lblNowPlayingDetails.Text = "No active Windows media session";
        }

        // Game
        if (snapshot.Telemetry.Game is { IsRunning: true } game)
        {
            var launcher = string.IsNullOrWhiteSpace(game.Launcher) ? "" : $" ({game.Launcher})";
            _lblGameStatus.Text = $"Running: {game.GameName ?? game.ProcessName}{launcher}";
            _lblGameStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblGameStatus.Text = "Not running";
            _lblGameStatus.ForeColor = Color.DimGray;
        }

        // Keyboard
        if (snapshot.Telemetry.Keyboard is { } kb && !string.IsNullOrWhiteSpace(kb.Name))
        {
            var bat = kb.BatteryPercent.HasValue
                ? $"  •  Battery: {kb.BatteryPercent}%{(kb.IsCharging == true ? " (Charging)" : "")}"
                : "";
            var conn = string.IsNullOrWhiteSpace(kb.ConnectionType) ? "" : $" [{kb.ConnectionType}]";
            _lblKeyboardStatus.Text = $"{kb.Name}{conn}{bat}";
        }
        else
        {
            _lblKeyboardStatus.Text = "Not detected";
        }

        // Logitech Peripherals
        _lvLogitech.BeginUpdate();
        _lvLogitech.Items.Clear();
        foreach (var p in snapshot.Telemetry.LogitechPeripherals)
        {
            var batteryText = p.BatteryPercent.HasValue
                ? $"{p.BatteryPercent}%{(p.IsCharging == true ? " (Charging)" : "")}"
                : "N/A";
            var item = new ListViewItem(p.Name);
            item.SubItems.Add(p.Category);
            item.SubItems.Add(p.IsConnected ? "Connected" : "Offline");
            item.SubItems.Add(batteryText);
            item.SubItems.Add(p.ConnectionType ?? "");
            _lvLogitech.Items.Add(item);
        }

        if (_lvLogitech.Items.Count == 0)
        {
            var empty = new ListViewItem("No Logitech peripherals detected");
            empty.ForeColor = Color.Gray;
            _lvLogitech.Items.Add(empty);
        }

        _lvLogitech.EndUpdate();
    }

    private void UpdateAudioUi(AudioState audio)
    {
        _updatingAudioUi = true;
        try
        {
            UpdateDeviceCombo(_cmbPlaybackDevice, ref _playbackDevices, audio.PlaybackDevices, audio.DefaultPlaybackDeviceId);
            UpdateDeviceCombo(_cmbCaptureDevice, ref _captureDevices, audio.CaptureDevices, audio.DefaultCaptureDeviceId);

            if (audio.PlaybackVolumePercent.HasValue)
            {
                var v = Math.Clamp(audio.PlaybackVolumePercent.Value, 0, 100);
                _trkPlaybackVolume.Value = v;
                _lblPlaybackVolume.Text = $"{v}%";
            }

            if (audio.PlaybackMuted.HasValue)
            {
                _chkPlaybackMute.Checked = audio.PlaybackMuted.Value;
            }

            if (audio.CaptureVolumePercent.HasValue)
            {
                var v = Math.Clamp(audio.CaptureVolumePercent.Value, 0, 100);
                _trkCaptureVolume.Value = v;
                _lblCaptureVolume.Text = $"{v}%";
            }

            if (audio.CaptureMuted.HasValue)
            {
                _chkCaptureMute.Checked = audio.CaptureMuted.Value;
            }
        }
        finally
        {
            _updatingAudioUi = false;
        }
    }

    private static void UpdateDeviceCombo(
        ComboBox combo,
        ref List<AudioDeviceInfo> cache,
        IReadOnlyList<AudioDeviceInfo> devices,
        string? defaultId)
    {
        var changed = cache.Count != devices.Count
            || !cache.Select(d => d.Id).SequenceEqual(devices.Select(d => d.Id));

        if (changed)
        {
            cache = [.. devices];
            combo.Items.Clear();
            foreach (var d in cache)
            {
                combo.Items.Add(d.Name);
            }
        }

        var idx = cache.FindIndex(d => string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && combo.SelectedIndex != idx)
        {
            combo.SelectedIndex = idx;
        }
    }

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildSessionSettingsTab());
        tabs.TabPages.Add(BuildServiceTab());
        Controls.Add(tabs);
    }

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Status & Controls") { Padding = new Padding(10), AutoScroll = true };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(4)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 1. Banner
        var banner = new GroupBox { Text = "Connection & Service Status", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var bannerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80));
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

        _lblBannerService.AutoSize = true;
        _lblBannerService.Font = new Font(Font, FontStyle.Bold);
        _lblBannerService.Text = "Service: Checking...";
        _lblBannerMqtt.AutoSize = true;
        _lblBannerMqtt.Text = "MQTT: Initializing...";
        _lblLastUpdated.AutoSize = true;
        _lblLastUpdated.ForeColor = Color.Gray;

        var btnRefresh = new Button { Text = "Refresh Now", AutoSize = true, Anchor = AnchorStyles.Right };
        btnRefresh.Click += (_, _) => _monitor.RequestImmediateRefresh();

        bannerGrid.Controls.Add(_lblBannerService, 0, 0);
        bannerGrid.Controls.Add(btnRefresh, 1, 0);
        bannerGrid.Controls.Add(_lblBannerMqtt, 0, 1);
        bannerGrid.Controls.Add(_lblLastUpdated, 1, 1);
        banner.Controls.Add(bannerGrid);
        root.Controls.Add(banner);

        // 2. Audio Controls
        var audioGroup = new GroupBox { Text = "Audio Devices & Volume", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var audioGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        audioGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        audioGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        audioGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        audioGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

        _cmbPlaybackDevice.Dock = DockStyle.Fill;
        _cmbPlaybackDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPlaybackDevice.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingAudioUi && _cmbPlaybackDevice.SelectedIndex >= 0 && _cmbPlaybackDevice.SelectedIndex < _playbackDevices.Count)
            {
                _audioController.SetDefaultPlaybackDevice(_playbackDevices[_cmbPlaybackDevice.SelectedIndex].Id);
                _monitor.RequestImmediateRefresh();
            }
        };

        _trkPlaybackVolume.Dock = DockStyle.Fill;
        _trkPlaybackVolume.Minimum = 0;
        _trkPlaybackVolume.Maximum = 100;
        _trkPlaybackVolume.TickStyle = TickStyle.None;
        _trkPlaybackVolume.Scroll += (_, _) =>
        {
            if (!_updatingAudioUi)
            {
                _lblPlaybackVolume.Text = $"{_trkPlaybackVolume.Value}%";
                _audioController.SetPlaybackVolume(_trkPlaybackVolume.Value);
            }
        };
        _trkPlaybackVolume.MouseUp += (_, _) => _monitor.RequestImmediateRefresh();

        _lblPlaybackVolume.AutoSize = true;
        _lblPlaybackVolume.Anchor = AnchorStyles.Left;
        _lblPlaybackVolume.Text = "0%";
        _chkPlaybackMute.Text = "Mute";
        _chkPlaybackMute.AutoSize = true;
        _chkPlaybackMute.CheckedChanged += (_, _) =>
        {
            if (!_updatingAudioUi)
            {
                _audioController.SetPlaybackMute(_chkPlaybackMute.Checked);
                _monitor.RequestImmediateRefresh();
            }
        };

        _cmbCaptureDevice.Dock = DockStyle.Fill;
        _cmbCaptureDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbCaptureDevice.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingAudioUi && _cmbCaptureDevice.SelectedIndex >= 0 && _cmbCaptureDevice.SelectedIndex < _captureDevices.Count)
            {
                _audioController.SetDefaultCaptureDevice(_captureDevices[_cmbCaptureDevice.SelectedIndex].Id);
                _monitor.RequestImmediateRefresh();
            }
        };

        _trkCaptureVolume.Dock = DockStyle.Fill;
        _trkCaptureVolume.Minimum = 0;
        _trkCaptureVolume.Maximum = 100;
        _trkCaptureVolume.TickStyle = TickStyle.None;
        _trkCaptureVolume.Scroll += (_, _) =>
        {
            if (!_updatingAudioUi)
            {
                _lblCaptureVolume.Text = $"{_trkCaptureVolume.Value}%";
                _audioController.SetCaptureVolume(_trkCaptureVolume.Value);
            }
        };
        _trkCaptureVolume.MouseUp += (_, _) => _monitor.RequestImmediateRefresh();

        _lblCaptureVolume.AutoSize = true;
        _lblCaptureVolume.Anchor = AnchorStyles.Left;
        _lblCaptureVolume.Text = "0%";
        _chkCaptureMute.Text = "Mute";
        _chkCaptureMute.AutoSize = true;
        _chkCaptureMute.CheckedChanged += (_, _) =>
        {
            if (!_updatingAudioUi)
            {
                _audioController.SetCaptureMute(_chkCaptureMute.Checked);
                _monitor.RequestImmediateRefresh();
            }
        };

        audioGrid.Controls.Add(new Label { Text = "Playback:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        audioGrid.Controls.Add(_cmbPlaybackDevice, 1, 0);
        audioGrid.SetColumnSpan(_cmbPlaybackDevice, 3);

        audioGrid.Controls.Add(new Label { Text = "Volume:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        audioGrid.Controls.Add(_trkPlaybackVolume, 1, 1);
        audioGrid.Controls.Add(_lblPlaybackVolume, 2, 1);
        audioGrid.Controls.Add(_chkPlaybackMute, 3, 1);

        audioGrid.Controls.Add(new Label { Text = "Microphone:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        audioGrid.Controls.Add(_cmbCaptureDevice, 1, 2);
        audioGrid.SetColumnSpan(_cmbCaptureDevice, 3);

        audioGrid.Controls.Add(new Label { Text = "Mic Level:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        audioGrid.Controls.Add(_trkCaptureVolume, 1, 3);
        audioGrid.Controls.Add(_lblCaptureVolume, 2, 3);
        audioGrid.Controls.Add(_chkCaptureMute, 3, 3);

        audioGroup.Controls.Add(audioGrid);
        root.Controls.Add(audioGroup);

        // 3. Now Playing
        var npGroup = new GroupBox { Text = "Now Playing", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var npLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        _lblNowPlayingTitle.AutoSize = true;
        _lblNowPlayingTitle.Font = new Font(Font, FontStyle.Bold);
        _lblNowPlayingTitle.Text = "Nothing playing";
        _lblNowPlayingDetails.AutoSize = true;
        _lblNowPlayingDetails.ForeColor = Color.DimGray;

        var mediaButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var btnPrev = new Button { Text = "⏮ Previous", AutoSize = true };
        var btnPlayPause = new Button { Text = "⏯ Play / Pause", AutoSize = true };
        var btnNext = new Button { Text = "⏭ Next", AutoSize = true };
        var btnStop = new Button { Text = "⏹ Stop", AutoSize = true };
        btnPrev.Click += async (_, _) => { await _mediaController.SkipPreviousAsync(); _monitor.RequestImmediateRefresh(); };
        btnPlayPause.Click += async (_, _) => { await _mediaController.TogglePlayPauseAsync(); _monitor.RequestImmediateRefresh(); };
        btnNext.Click += async (_, _) => { await _mediaController.SkipNextAsync(); _monitor.RequestImmediateRefresh(); };
        btnStop.Click += async (_, _) => { await _mediaController.StopAsync(); _monitor.RequestImmediateRefresh(); };
        mediaButtons.Controls.AddRange([btnPrev, btnPlayPause, btnNext, btnStop]);

        npLayout.Controls.Add(_lblNowPlayingTitle);
        npLayout.Controls.Add(_lblNowPlayingDetails);
        npLayout.Controls.Add(mediaButtons);
        npGroup.Controls.Add(npLayout);
        root.Controls.Add(npGroup);

        // 4. Game & Keyboard
        var activityGroup = new GroupBox { Text = "Game & Active Keyboard", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var actGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        actGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        actGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _lblGameStatus.AutoSize = true;
        _lblGameStatus.Font = new Font(Font, FontStyle.Bold);
        _lblGameStatus.Text = "Not running";
        _lblKeyboardStatus.AutoSize = true;
        _lblKeyboardStatus.Text = "Detecting...";

        actGrid.Controls.Add(new Label { Text = "Current Game:", AutoSize = true }, 0, 0);
        actGrid.Controls.Add(_lblGameStatus, 1, 0);
        actGrid.Controls.Add(new Label { Text = "Active Keyboard:", AutoSize = true }, 0, 1);
        actGrid.Controls.Add(_lblKeyboardStatus, 1, 1);
        activityGroup.Controls.Add(actGrid);
        root.Controls.Add(activityGroup);

        // 5. Logitech Peripherals
        var logiGroup = new GroupBox { Text = "Logitech Peripherals", Dock = DockStyle.Fill, Height = 155, Padding = new Padding(10) };
        _lvLogitech.Dock = DockStyle.Fill;
        _lvLogitech.View = View.Details;
        _lvLogitech.FullRowSelect = true;
        _lvLogitech.GridLines = true;
        _lvLogitech.Columns.Add("Device", 220);
        _lvLogitech.Columns.Add("Type", 90);
        _lvLogitech.Columns.Add("Status", 90);
        _lvLogitech.Columns.Add("Battery", 110);
        _lvLogitech.Columns.Add("Connection", 130);
        logiGroup.Controls.Add(_lvLogitech);
        root.Controls.Add(logiGroup);

        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildSessionSettingsTab()
    {
        var page = new TabPage("Session & MQTT Settings") { Padding = new Padding(12), AutoScroll = true };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _numSessionPoll.Minimum = 1;
        _numSessionPoll.Maximum = 86400;
        _numSessionPoll.Width = 100;

        _txtSessionMqttHost.Dock = DockStyle.Fill;
        _numSessionMqttPort.Minimum = 1;
        _numSessionMqttPort.Maximum = 65535;
        _numSessionMqttPort.Width = 100;
        _txtSessionDeviceId.Dock = DockStyle.Fill;
        _txtSessionUsername.Dock = DockStyle.Fill;
        _txtSessionPassword.Dock = DockStyle.Fill;
        _txtSessionPassword.UseSystemPasswordChar = true;
        _chkSessionUseTls.Text = "Use TLS (encrypt MQTT connection)";
        _chkSessionUseTls.AutoSize = true;

        _txtCustomGames.Dock = DockStyle.Fill;
        _txtCustomGames.Multiline = true;
        _txtCustomGames.Height = 72;
        _txtCustomGames.ScrollBars = ScrollBars.Vertical;
        _txtCustomGames.PlaceholderText = "One process name per line (e.g. Cyberpunk2077, PathOfExile)";

        _chkStartWithWindows.Text = "Launch Pulse Session automatically when signing in to Windows";
        _chkStartWithWindows.AutoSize = true;

        _chkSyncMqttToService.Text = "Also apply these MQTT settings to the Windows Service (agent.json)";
        _chkSyncMqttToService.AutoSize = true;

        var row = 0;
        AddRow(grid, row++, "Session Polling (seconds):", _numSessionPoll);
        AddRow(grid, row++, "MQTT Broker Host:", _txtSessionMqttHost);
        AddRow(grid, row++, "MQTT Broker Port:", _numSessionMqttPort);
        AddRow(grid, row++, "Device ID:", _txtSessionDeviceId);
        AddRow(grid, row++, "MQTT Username:", _txtSessionUsername);
        AddRow(grid, row++, "MQTT Password:", _txtSessionPassword);
        AddRow(grid, row++, "", _chkSessionUseTls);
        AddRow(grid, row++, "Custom Game Processes:", _txtCustomGames);
        AddRow(grid, row++, "", _chkStartWithWindows);
        AddRow(grid, row++, "", _chkSyncMqttToService);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        var btnSave = new Button { Text = "Save & Apply", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        var btnReconnect = new Button { Text = "Reconnect MQTT", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        btnSave.Click += (_, _) => SaveSessionSettings();
        btnReconnect.Click += (_, _) =>
        {
            _mqtt.Reconnect();
            _lblSettingsFeedback.Text = "MQTT reconnect triggered.";
        };
        buttons.Controls.AddRange([btnSave, btnReconnect]);
        AddRow(grid, row++, "", buttons);

        _lblSettingsFeedback.AutoSize = true;
        _lblSettingsFeedback.ForeColor = Color.DimGray;
        AddRow(grid, row, "", _lblSettingsFeedback);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildServiceTab()
    {
        var page = new TabPage("Windows Service") { Padding = new Padding(12), AutoScroll = true };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Service Control Group
        var ctrlGroup = new GroupBox { Text = "Pulse Agent Windows Service", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var ctrlGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        ctrlGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        ctrlGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtServiceName.Dock = DockStyle.Fill;
        _lblServiceState.AutoSize = true;
        _lblServiceState.Font = new Font(Font, FontStyle.Bold);
        _lblServiceStartType.AutoSize = true;
        _lblAgentConfigPath.AutoSize = true;
        _lblAgentConfigPath.ForeColor = Color.DimGray;
        _lblAgentConfigPath.Text = _settingsStore.AgentConfigPath;

        var svcButtons = new FlowLayoutPanel { AutoSize = true };
        var btnStart = new Button { Text = "Start Service", AutoSize = true };
        var btnStop = new Button { Text = "Stop Service", AutoSize = true };
        var btnRestart = new Button { Text = "Restart Service", AutoSize = true };
        var btnReload = new Button { Text = "Reload Config", AutoSize = true };

        btnStart.Click += async (_, _) => await RunServiceActionAsync(() => _serviceManager.StartAsync(_txtServiceName.Text.Trim()));
        btnStop.Click += async (_, _) => await RunServiceActionAsync(() => _serviceManager.StopAsync(_txtServiceName.Text.Trim()));
        btnRestart.Click += async (_, _) => await RunServiceActionAsync(() => _serviceManager.RestartAsync(_txtServiceName.Text.Trim()));
        btnReload.Click += (_, _) =>
        {
            LoadAgentConfigIntoUi();
            _monitor.RequestImmediateRefresh();
            _lblServiceFeedback.Text = "Reloaded service configuration.";
        };
        svcButtons.Controls.AddRange([btnStart, btnStop, btnRestart, btnReload]);

        AddRow(ctrlGrid, 0, "Service Name:", _txtServiceName);
        AddRow(ctrlGrid, 1, "Current Status:", _lblServiceState);
        AddRow(ctrlGrid, 2, "Startup Type:", _lblServiceStartType);
        AddRow(ctrlGrid, 3, "Config File:", _lblAgentConfigPath);
        AddRow(ctrlGrid, 4, "Actions:", svcButtons);
        ctrlGroup.Controls.Add(ctrlGrid);
        root.Controls.Add(ctrlGroup);

        // Service agent.json Config Editor
        var cfgGroup = new GroupBox { Text = "Windows Service Configuration (agent.json)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var cfgGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        cfgGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _numServicePoll.Minimum = 1;
        _numServicePoll.Maximum = 86400;
        _numServicePoll.Width = 100;
        _txtServiceMqttHost.Dock = DockStyle.Fill;
        _numServiceMqttPort.Minimum = 1;
        _numServiceMqttPort.Maximum = 65535;
        _numServiceMqttPort.Width = 100;
        _txtServiceDeviceId.Dock = DockStyle.Fill;
        _txtServiceUsername.Dock = DockStyle.Fill;
        _txtServicePassword.Dock = DockStyle.Fill;
        _txtServicePassword.UseSystemPasswordChar = true;
        _chkServiceUseTls.Text = "Use TLS";
        _chkServiceUseTls.AutoSize = true;

        var cfgButtons = new FlowLayoutPanel { AutoSize = true };
        var btnSaveAgent = new Button { Text = "Save Service Config", AutoSize = true };
        var btnSaveRestart = new Button { Text = "Save & Restart Service", AutoSize = true };
        btnSaveAgent.Click += (_, _) => SaveAgentConfigFromUi(restart: false);
        btnSaveRestart.Click += (_, _) => SaveAgentConfigFromUi(restart: true);
        cfgButtons.Controls.AddRange([btnSaveAgent, btnSaveRestart]);

        _lblServiceFeedback.AutoSize = true;
        _lblServiceFeedback.ForeColor = Color.DimGray;

        var r = 0;
        AddRow(cfgGrid, r++, "Hardware Polling (seconds):", _numServicePoll);
        AddRow(cfgGrid, r++, "MQTT Broker Host:", _txtServiceMqttHost);
        AddRow(cfgGrid, r++, "MQTT Broker Port:", _numServiceMqttPort);
        AddRow(cfgGrid, r++, "Device ID:", _txtServiceDeviceId);
        AddRow(cfgGrid, r++, "MQTT Username:", _txtServiceUsername);
        AddRow(cfgGrid, r++, "MQTT Password:", _txtServicePassword);
        AddRow(cfgGrid, r++, "", _chkServiceUseTls);
        AddRow(cfgGrid, r++, "", cfgButtons);
        AddRow(cfgGrid, r, "", _lblServiceFeedback);

        cfgGroup.Controls.Add(cfgGrid);
        root.Controls.Add(cfgGroup);

        page.Controls.Add(root);
        return page;
    }

    private void LoadSettingsIntoUi()
    {
        _numSessionPoll.Value = Math.Clamp(_currentSettings.PollingIntervalSeconds, 1, 86400);
        _txtSessionMqttHost.Text = _currentSettings.Mqtt.Host;
        _numSessionMqttPort.Value = Math.Clamp(_currentSettings.Mqtt.Port, 1, 65535);
        _txtSessionDeviceId.Text = _currentSettings.Mqtt.DeviceId;
        _txtSessionUsername.Text = _currentSettings.Mqtt.Username ?? "";
        _txtSessionPassword.Text = _currentSettings.Mqtt.Password ?? "";
        _chkSessionUseTls.Checked = _currentSettings.Mqtt.UseTls;
        _txtCustomGames.Text = string.Join(Environment.NewLine, _currentSettings.CustomGameProcesses);
        _chkStartWithWindows.Checked = SessionSettingsStore.IsStartWithWindowsEnabled();
        _txtServiceName.Text = _currentSettings.ServiceName;
    }

    private void LoadAgentConfigIntoUi()
    {
        var cfg = _settingsStore.LoadAgentConfig();
        _numServicePoll.Value = Math.Clamp(cfg.PollingIntervalSeconds, 1, 86400);
        _txtServiceMqttHost.Text = cfg.Mqtt.Host;
        _numServiceMqttPort.Value = Math.Clamp(cfg.Mqtt.Port, 1, 65535);
        _txtServiceDeviceId.Text = cfg.Mqtt.DeviceId;
        _txtServiceUsername.Text = cfg.Mqtt.Username ?? "";
        _txtServicePassword.Text = cfg.Mqtt.Password ?? "";
        _chkServiceUseTls.Checked = cfg.Mqtt.UseTls;
    }

    private void SaveSessionSettings()
    {
        var customGames = _txtCustomGames.Text
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _currentSettings = new SessionAppSettings
        {
            PollingIntervalSeconds = (int)_numSessionPoll.Value,
            ServiceName = string.IsNullOrWhiteSpace(_txtServiceName.Text) ? "Pulse Agent" : _txtServiceName.Text.Trim(),
            CustomGameProcesses = customGames,
            Mqtt = new MqttSettings
            {
                Host = _txtSessionMqttHost.Text.Trim(),
                Port = (int)_numSessionMqttPort.Value,
                DeviceId = string.IsNullOrWhiteSpace(_txtSessionDeviceId.Text) ? Environment.MachineName : _txtSessionDeviceId.Text.Trim(),
                Username = string.IsNullOrWhiteSpace(_txtSessionUsername.Text) ? null : _txtSessionUsername.Text.Trim(),
                Password = string.IsNullOrEmpty(_txtSessionPassword.Text) ? null : _txtSessionPassword.Text,
                UseTls = _chkSessionUseTls.Checked
            }
        };

        try
        {
            _settingsStore.SaveSessionSettings(_currentSettings);
            SessionSettingsStore.SetStartWithWindows(_chkStartWithWindows.Checked);
            _monitor.UpdateSettings(_currentSettings);
            _mqtt.Reconfigure(_currentSettings.Mqtt);

            var msg = $"Saved session settings ({_settingsStore.SessionConfigPath}).";
            if (_chkSyncMqttToService.Checked)
            {
                var agentCfg = _settingsStore.LoadAgentConfig();
                agentCfg.Mqtt = _currentSettings.Mqtt.Clone();
                var (ok, svcMsg) = _settingsStore.SaveAgentConfig(agentCfg);
                LoadAgentConfigIntoUi();
                msg += " " + (ok ? svcMsg : $"Service sync failed: {svcMsg}");
            }

            _lblSettingsFeedback.ForeColor = Color.ForestGreen;
            _lblSettingsFeedback.Text = msg;
        }
        catch (Exception ex)
        {
            _lblSettingsFeedback.ForeColor = Color.Firebrick;
            _lblSettingsFeedback.Text = $"Failed to save settings: {ex.Message}";
        }
    }

    private async void SaveAgentConfigFromUi(bool restart)
    {
        // Also persist updated service name if changed
        var serviceName = string.IsNullOrWhiteSpace(_txtServiceName.Text) ? "Pulse Agent" : _txtServiceName.Text.Trim();
        if (!string.Equals(_currentSettings.ServiceName, serviceName, StringComparison.Ordinal))
        {
            _currentSettings.ServiceName = serviceName;
            _settingsStore.SaveSessionSettings(_currentSettings);
            _monitor.UpdateSettings(_currentSettings);
        }

        var cfg = new AgentServiceConfig
        {
            PollingIntervalSeconds = (int)_numServicePoll.Value,
            Mqtt = new MqttSettings
            {
                Host = _txtServiceMqttHost.Text.Trim(),
                Port = (int)_numServiceMqttPort.Value,
                DeviceId = string.IsNullOrWhiteSpace(_txtServiceDeviceId.Text) ? Environment.MachineName : _txtServiceDeviceId.Text.Trim(),
                Username = string.IsNullOrWhiteSpace(_txtServiceUsername.Text) ? null : _txtServiceUsername.Text.Trim(),
                Password = string.IsNullOrEmpty(_txtServicePassword.Text) ? null : _txtServicePassword.Text,
                UseTls = _chkServiceUseTls.Checked
            }
        };

        var (ok, message) = _settingsStore.SaveAgentConfig(cfg);
        _lblServiceFeedback.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _lblServiceFeedback.Text = message;

        if (ok && restart)
        {
            await RunServiceActionAsync(() => _serviceManager.RestartAsync(serviceName));
        }
    }

    private async Task RunServiceActionAsync(Func<Task<(bool Succeeded, string Message)>> action)
    {
        _lblServiceFeedback.ForeColor = Color.DimGray;
        _lblServiceFeedback.Text = "Executing service action...";
        var (ok, message) = await action();
        _lblServiceFeedback.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _lblServiceFeedback.Text = message;
        _monitor.RequestImmediateRefresh();
    }

    private static void AddRow(TableLayoutPanel panel, int row, string labelText, Control control)
    {
        if (!string.IsNullOrEmpty(labelText))
        {
            panel.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            }, 0, row);
        }

        control.Margin = new Padding(0, 4, 0, 4);
        panel.Controls.Add(control, 1, row);
    }
}
