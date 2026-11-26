# Pulse.Agent.Windows

Planned Windows background service and the first platform agent for Pulse. It will collect machine-level readings such as CPU and GPU temperatures and use [Pulse.Agent.Core](../Pulse.Agent.Core/README.md) to publish device availability and telemetry over MQTT.

The first milestone is to report at least one supported sensor from a Windows host and resume updates after a restart or broker disconnect. Sensor coverage will depend on the host hardware and the available collection APIs.

Audio and desktop features that need the signed-in user's session belong in [Pulse.Agent.Windows.Session](../Pulse.Agent.Windows.Session/README.md).
