# Pulse

Pulse is a home-lab project for monitoring and managing computers on a private network. The first target is a Windows gaming PC. The initial goal is to see whether the PC is online and view a small set of hardware readings, such as CPU and GPU temperatures, from a browser. Audio and desktop controls are planned for a later milestone, with Linux and macOS agents planned as additional platforms.

## Project status

The Windows agent runs on .NET 10, logs CPU temperatures at a configurable interval, and publishes telemetry and availability when a broker is configured. Pulse.Contracts defines the MQTT payloads. The remaining projects are scaffolds.

For CPU temperature setup, the PawnIO driver download, and broker configuration, see the [Windows agent README](src/Pulse.Agent.Windows/README.md).

## Architecture

1. A platform agent reads local sensors and publishes telemetry and availability over MQTT. Eclipse Mosquitto is the planned broker.
2. The Pulse server consumes MQTT updates, tracks the latest state for each device, and exposes it through a management API. A reading's timestamp and the device's availability help the server distinguish fresh data from a last-known reading.
3. The web interface uses the management API to display devices and their readings. Future control requests will go through the server and report their results back to the interface.

On Windows, the background service owns machine-level work. A separate helper in the signed-in user's session handles features tied to the interactive desktop, including audio controls.

## Repository layout

| Component | Planned responsibility |
| --- | --- |
| [Pulse.Contracts](src/Pulse.Contracts/README.md) | Shared MQTT messages and command contracts |
| [Pulse.Agent.Core](src/Pulse.Agent.Core/README.md) | Shared agent configuration, reporting, and MQTT connection lifecycle |
| [Pulse.Agent.Windows](src/Pulse.Agent.Windows/README.md) | Windows background service and hardware sensor collection |
| [Pulse.Agent.Windows.Session](src/Pulse.Agent.Windows.Session/README.md) | Audio and desktop controls in the signed-in Windows session |
| [Pulse.Agent.Linux](src/Pulse.Agent.Linux/README.md) | Linux agent and platform adapters |
| [Pulse.Agent.MacOS](src/Pulse.Agent.MacOS/README.md) | macOS agent and platform adapters |
| [Pulse.Server](src/Pulse.Server/README.md) | Management API and device state |
| [Pulse.Web](src/Pulse.Web/README.md) | Browser-based management interface |

Run `dotnet test` from the repository root to run the Windows agent tests. The server and integration test projects are scaffolds. The local scaffold reserves `deploy/` for deployment assets.

## First milestone

Start with the shared telemetry and availability contracts, then connect the Windows service, MQTT broker, server, and web interface. The first end-to-end check is to see a Windows host and a live reading in the browser, stop its agent and confirm the interface shows its last-known data and offline state, then restart the agent and confirm that fresh readings resume.
