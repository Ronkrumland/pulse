# Pulse.Agent.Windows

The Windows agent logs CPU and GPU temperatures (in degrees Celsius) along with system and GPU memory usage (in megabytes) when it starts and every five seconds by default. Each temperature reading is the highest available measurement from the CPU or GPU temperature sensors. Sensor availability depends on the hardware and its access requirements. The agent logs a warning when readings become unavailable and keeps polling.

Set `Telemetry:PollingIntervalSeconds` in `%ProgramData%\Pulse\agent.json` (or `appsettings.json` for local development), or set the `Telemetry__PollingIntervalSeconds` environment variable. Use a whole number from 1 to 4294967 and restart the agent to apply the change.

## Configuration

The agent loads settings in the following order (later sources override earlier ones):

1. `appsettings.json` (bundled defaults)
2. `appsettings.{Environment}.json`
3. `%ProgramData%\Pulse\agent.json` (persistent machine configuration, preserved across upgrades)
4. .NET user secrets (Development only), environment variables, and command-line arguments

If the agent logs `CPU temperature is unavailable`, check the PawnIO driver with `sc.exe query PawnIO`. LibreHardwareMonitor uses PawnIO to read CPU temperature sensors on supported Windows hardware. If the command returns error 1060, install the signed driver. [Download the official PawnIO installer](https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe).

If PawnIO reports `RUNNING` and the warning persists, stop the agent and relaunch it from a terminal opened with **Run as administrator**. The running driver can still require administrator rights for sensor access. If you launch the agent from an IDE, restart that IDE as administrator. [LibreHardwareMonitor administrator requirements](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6#administrator-rights).

## MQTT publishing

Set `Mqtt:Host` in `appsettings.json` or set the `Mqtt__Host` environment variable to enable publishing. Restart the agent after changing settings.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Mqtt:Host` | Empty | Broker hostname or IP address. Leave empty to log readings locally. |
| `Mqtt:Port` | `1883` | Broker TCP port. Set this to the broker's TLS port when using TLS. |
| `Mqtt:DeviceId` | Windows computer name | Unique device identifier used in topics and the MQTT client ID. |
| `Mqtt:Username` | Empty | Broker username, if required. |
| `Mqtt:Password` | Empty | Broker password, if required. Supply it through `Mqtt__Password` or .NET user secrets for local development. |
| `Mqtt:UseTls` | `false` | Enable TLS and validate the broker certificate. Use TLS when sending credentials over the network. |

With Mosquitto client tools installed, subscribe to both topics by replacing `broker.example.net` with your broker address.

```powershell
mosquitto_sub -h broker.example.net -t 'pulse/devices/+/telemetry' -t 'pulse/devices/+/availability' -v
```

The agent keeps collecting and logging through broker outages and retries the connection after five seconds. It keeps the latest pending reading while disconnected. For payloads and retained message behavior, see [Pulse.Contracts](../Pulse.Contracts/README.md).

## Installing and upgrading as a Windows Service

The agent supports running either in a console or as a Windows Service (`Pulse Agent`). When running as a service under `LocalSystem` (the default for `sc.exe create`), it has the privileges needed for PawnIO sensor access and writes `Warning` and higher logs to the Windows Event Log.

From an elevated PowerShell prompt at the repository root, run the install script to publish a self-contained `win-x64` build, install or upgrade the binaries in `C:\Program Files\Pulse\Agent`, create `%ProgramData%\Pulse\agent.json` if missing, and register and start the service:

```powershell
.\deploy\install.ps1
```

Re-run `.\deploy\install.ps1` to upgrade in place; any existing `%ProgramData%\Pulse\agent.json` is preserved. Pass `-NoStart` if you want to edit `agent.json` before starting the service, or `-SourceDir <path>` to install from an already-published folder.

To uninstall the service and remove binaries (keeping `%ProgramData%\Pulse\agent.json` unless `-PurgeConfig` is passed):

```powershell
.\deploy\uninstall.ps1
```

Audio and desktop features that need the signed-in user's session belong in [Pulse.Agent.Windows.Session](../Pulse.Agent.Windows.Session/README.md).
