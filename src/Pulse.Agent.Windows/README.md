# Pulse.Agent.Windows

The Windows agent logs CPU temperature in degrees Celsius when it starts and every five seconds by default. Each reading is the highest available measurement from the CPU temperature sensors. Sensor availability depends on the hardware and its access requirements. The agent logs a warning when readings become unavailable and keeps polling.

Set `CpuTemperature:PollingIntervalSeconds` in `appsettings.json`, or set the `CpuTemperature__PollingIntervalSeconds` environment variable. Use a whole number from 1 to 4294967 and restart the agent to apply the change.

If the agent logs `CPU temperature is unavailable`, check the PawnIO driver with `sc.exe query PawnIO`. LibreHardwareMonitor uses PawnIO to read CPU temperature sensors on supported Windows hardware. If the command returns error 1060, install the signed driver. [Download the official PawnIO installer](https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe).

If PawnIO reports `RUNNING` and the warning persists, stop the agent and relaunch it from a terminal opened with **Run as administrator**. The running driver can still require administrator rights for sensor access. If you launch the agent from an IDE, restart that IDE as administrator. [LibreHardwareMonitor administrator requirements](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6#administrator-rights).

The agent will use [Pulse.Agent.Core](../Pulse.Agent.Core/README.md) to publish device availability and telemetry over MQTT.

The first milestone is to report at least one supported sensor from a Windows host and resume updates after a restart or broker disconnect. Sensor coverage will depend on the host hardware and the available collection APIs.

Audio and desktop features that need the signed-in user's session belong in [Pulse.Agent.Windows.Session](../Pulse.Agent.Windows.Session/README.md).
