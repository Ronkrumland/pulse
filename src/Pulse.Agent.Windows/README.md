# Pulse.Agent.Windows

The Windows agent logs CPU and GPU temperatures (in degrees Celsius) along with system and GPU memory usage (in megabytes) when it starts and every five seconds by default. Each temperature reading is the highest available measurement from the CPU or GPU temperature sensors. Sensor availability depends on the hardware and its access requirements. The agent logs a warning when readings become unavailable and keeps polling.

Set `CpuTemperature:PollingIntervalSeconds` in `appsettings.json`, or set the `CpuTemperature__PollingIntervalSeconds` environment variable. Use a whole number from 1 to 4294967 and restart the agent to apply the change.

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

Audio and desktop features that need the signed-in user's session belong in [Pulse.Agent.Windows.Session](../Pulse.Agent.Windows.Session/README.md).
