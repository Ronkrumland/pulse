# Pulse.Agent.Core

Planned shared runtime for the Windows, Linux, and macOS agents. Platform adapters supply local readings and capabilities. The core handles the work common to every agent.

- Load agent configuration and device identity.
- Manage the MQTT connection, reconnect after a disconnect, and resume reporting.
- Schedule collection and publish timestamped readings and device availability using [Pulse.Contracts](../Pulse.Contracts/README.md).

The first consumer will be the [Windows service](../Pulse.Agent.Windows/README.md). Its sensor adapter will supply the initial readings and signal when a metric is unavailable.
