# Pulse.Agent.MacOS

Planned macOS host agent. It will provide macOS-specific sensor collection and process lifecycle integration while using [Pulse.Agent.Core](../Pulse.Agent.Core/README.md) for device identity, periodic reporting, and MQTT connectivity.

Hardware readings will depend on what the operating system and hardware expose. The first macOS milestone is to report availability and at least one supported reading using the same [contracts](../Pulse.Contracts/README.md) as the Windows agent.
