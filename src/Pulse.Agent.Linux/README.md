# Pulse.Agent.Linux

Planned Linux host agent. It will provide Linux-specific sensor collection and service lifecycle integration while using [Pulse.Agent.Core](../Pulse.Agent.Core/README.md) for device identity, periodic reporting, and MQTT connectivity.

Hardware readings will depend on the sensors exposed by the host and the permissions of the service account. The first Linux milestone is to report availability and at least one supported reading using the same [contracts](../Pulse.Contracts/README.md) as the Windows agent.
