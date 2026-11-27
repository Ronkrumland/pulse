# Pulse.Contracts

Shared MQTT message definitions for platform agents and [Pulse.Server](../Pulse.Server/README.md).

| Message | Topic | JSON payload |
| --- | --- | --- |
| Availability | `pulse/devices/{deviceId}/availability` | `{"deviceId":"gaming-pc","online":true}` |
| CPU temperature | `pulse/devices/{deviceId}/telemetry` | `{"deviceId":"gaming-pc","observedAt":"2026-01-01T12:00:00+00:00","cpuTemperatureCelsius":54.5}` |

Both messages are retained and use QoS 1. The agent publishes `online: true` after connecting and `online: false` on graceful shutdown. The broker publishes the retained offline last will when it detects an unexpected disconnect. When a sensor is unavailable, the broker keeps the last valid telemetry. Use `observedAt` to determine how old that reading is. QoS 1 can deliver duplicates.

Future command and result messages will carry a target device, request ID, action, and outcome. Commands are intended to be non-retained.
