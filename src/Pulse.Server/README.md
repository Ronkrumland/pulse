# Pulse.Server

Management API and live device state service for Pulse (.NET 10). It subscribes to agent updates over MQTT, retains the latest known state for each device, exposes an HTTP API and Server-Sent Events stream for [Pulse.Web](../Pulse.Web/README.md), and dispatches 2-way commands to `Pulse.Agent.Windows.Session`.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/healthz` | Liveness probe with MQTT status and tracked device count |
| `GET` | `/readyz` | Readiness probe (`200` when MQTT is connected or unconfigured, `503` while disconnected) |
| `GET` | `/api/devices` | List all known devices with availability, telemetry, session state, and staleness |
| `GET` | `/api/devices/{deviceId}` | Get a single device's latest state (`404` if unknown) |
| `POST` | `/api/devices/{deviceId}/commands` | Publish a `SessionCommand` to `pulse/devices/{deviceId}/session/commands` and await `SessionCommandResult` |
| `GET` | `/api/events` | Server-Sent Events stream (`snapshot` on connect, `device-updated` on changes) |

## Configuration

Configure via `appsettings.json` or environment variables (using `__` for nested keys):

| Setting | Default | Purpose |
| --- | --- | --- |
| `Mqtt:Host` | Empty | MQTT broker hostname or IP (e.g., `192.168.4.38`). Leave empty to run without MQTT. |
| `Mqtt:Port` | `1883` | Broker TCP port |
| `Mqtt:ClientId` | `pulse-server` | MQTT client ID |
| `Mqtt:Username` | Empty | Broker username, if required |
| `Mqtt:Password` | Empty | Broker password, if required |
| `Mqtt:UseTls` | `false` | Enable TLS with OS certificate validation |
| `Mqtt:CommandTimeoutSeconds` | `10` | Timeout waiting for a `SessionCommandResult` (`1`–`300`) |
| `Mqtt:StaleAfterSeconds` | `30` | Age after which a device's hardware telemetry is marked stale (`1`–`86400`) |
| `Cors:AllowedOrigins` | Empty | Optional array of allowed browser origins |

## Local development

```sh
dotnet run --project src/Pulse.Server/Pulse.Server.csproj
```

## Container image

The image is built from [Dockerfile](Dockerfile) and published to `ghcr.io/ronkrumland/pulse-server` via `.github/workflows/publish-server.yml`. It listens on port `8080`, runs as non-root (`$APP_UID`), and supports `read_only: true` with `cap_drop: [ALL]`.
