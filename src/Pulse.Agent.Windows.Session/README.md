# Pulse.Agent.Windows.Session

Companion Windows desktop tray application (.NET 10) running in the signed-in user's interactive session. It provides interactive session telemetry, 2-way MQTT controls, a settings/status GUI, and Windows Service management for [Pulse.Agent.Windows](../Pulse.Agent.Windows/README.md).

## Features

- **Audio status & controls**: Reads active playback and microphone devices, volume, and mute state; switches default playback/microphone devices and adjusts volume or mute locally or via MQTT commands.
- **Now Playing**: Reads the active Windows media session (title, artist, album, playback status, source app) via Windows GSMTC and supports Play/Pause, Next, Previous, and Stop commands.
- **Running game detection**: Detects active games via Steam (`RunningAppID` + app manifests), common launcher directories (Steam, Epic, Xbox, GOG, Riot, EA, Ubisoft, Battle.net), and user-configured custom process names.
- **Active keyboard & battery**: Tracks the keyboard currently being typed on via Windows Raw Input and reads its friendly name, connection type, and battery level (Bluetooth PnP battery or Logitech HID++).
- **Logitech peripherals**: Discovers connected Logitech mice, headsets, and keyboards via HID++ 2.0 and Windows PnP/Bluetooth battery properties, reporting connection status, battery percentage, and charging state.
- **2-way MQTT**:
  - Client ID: `pulse-{deviceId}-session`
  - Retained availability (with LWT): `pulse/devices/{deviceId}/session/availability`
  - Retained telemetry: `pulse/devices/{deviceId}/session/telemetry`
  - Non-retained commands: `pulse/devices/{deviceId}/session/commands`
  - Non-retained command results: `pulse/devices/{deviceId}/session/results`
- **Management GUI & System Tray**:
  - Lives in the Windows system tray with a live status indicator icon.
  - **Hovering** displays current `Pulse Agent` Windows Service status and MQTT connection status.
  - **Clicking** opens the management window to control audio/media, view live telemetry, configure session/MQTT/polling settings (`%LocalAppData%\Pulse\session.json`), and manage the `Pulse Agent` Windows Service and `%ProgramData%\Pulse\agent.json` (with automatic UAC elevation when needed).

## Supported MQTT commands

Send a JSON `SessionCommand` to `pulse/devices/{deviceId}/session/commands`:

| Action | Parameters |
| --- | --- |
| `audio.set_playback_device` | `{"deviceId": "<id>"}` |
| `audio.set_capture_device` | `{"deviceId": "<id>"}` |
| `audio.set_playback_volume` | `{"volumePercent": 0..100}` |
| `audio.set_capture_volume` | `{"volumePercent": 0..100}` |
| `audio.set_playback_mute` | `{"muted": true\|false}` |
| `audio.set_capture_mute` | `{"muted": true\|false}` |
| `media.play_pause` | *(none)* |
| `media.next` | *(none)* |
| `media.previous` | *(none)* |
| `media.stop` | *(none)* |

## Installing and upgrading

From a regular (non-admin) PowerShell prompt at the repository root, run the session installer to publish a self-contained `win-x64` build into `%LocalAppData%\Pulse\Session`, seed `%LocalAppData%\Pulse\session.json` from `%ProgramData%\Pulse\agent.json` if present, register sign-in auto-start, create a Start Menu shortcut, and launch the tray app:

```powershell
.\deploy\install-session.ps1
```

Re-run `.\deploy\install-session.ps1` to upgrade in place; any existing `%LocalAppData%\Pulse\session.json` is preserved. Optional flags: `-NoAutoStart`, `-NoStart`, and `-SourceDir <path>`.

To uninstall (keeping `%LocalAppData%\Pulse\session.json` unless `-PurgeConfig` is passed):

```powershell
.\deploy\uninstall-session.ps1
```

