# Pulse.Contracts

Shared MQTT message definitions for platform agents and [Pulse.Server](../Pulse.Server/README.md).

| Message | Topic | Retained | JSON payload |
| --- | --- | --- | --- |
| Availability | `pulse/devices/{deviceId}/availability` | Yes | `{"deviceId":"gaming-pc","online":true}` |
| Device telemetry | `pulse/devices/{deviceId}/telemetry` | Yes | `{"deviceId":"gaming-pc","observedAt":"2026-01-01T12:00:00+00:00","cpuTemperatureCelsius":54.5,"gpuTemperatureCelsius":61.0,"memoryUsedMegabytes":16384,"memoryTotalMegabytes":32768,"gpuMemoryUsedMegabytes":4096,"gpuMemoryTotalMegabytes":12288}` |
| Session availability | `pulse/devices/{deviceId}/session/availability` | Yes | `{"deviceId":"gaming-pc","online":true,"userName":"Ron"}` |
| Session telemetry | `pulse/devices/{deviceId}/session/telemetry` | Yes | `SessionTelemetry` (audio devices/volume/mute, now playing, running game, active keyboard, Logitech peripherals) |
| Session commands | `pulse/devices/{deviceId}/session/commands` | No | `{"requestId":"req-1","deviceId":"gaming-pc","action":"audio.set_playback_volume","parameters":{"volumePercent":65}}` |
| Session command results | `pulse/devices/{deviceId}/session/results` | No | `{"requestId":"req-1","deviceId":"gaming-pc","action":"audio.set_playback_volume","succeeded":true,"message":"Volume set to 65%.","completedAt":"2026-01-01T12:00:01+00:00"}` |

Telemetry and availability messages are retained and use QoS 1. Each agent publishes `online: true` after connecting and `online: false` on graceful shutdown; the broker publishes the retained offline last will on unexpected disconnect. Individual metric fields are `null` when that sensor is unavailable. Commands and results are non-retained and use QoS 1.
