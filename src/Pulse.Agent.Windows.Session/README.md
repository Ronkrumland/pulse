# Pulse.Agent.Windows.Session

Planned companion process for audio and desktop features that need to run in the signed-in Windows user's session. The [Windows service](../Pulse.Agent.Windows/README.md) handles machine-level telemetry and coordinates requests that require the helper.

Initial audio scenarios include reading the active playback and microphone devices and changing the selected device, volume, or mute state. Requests that require an interactive session should return an explicit unavailable result if no helper is running. The local service-to-helper interface and desktop actions are still to be defined.
