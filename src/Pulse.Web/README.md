# Pulse.Web

Planned browser interface for viewing and managing Pulse devices. It will render device state from the [Pulse.Server](../Pulse.Server/README.md) management API.

The first view will show each device's availability, latest reported readings with units, and when those readings were taken. If an agent goes offline, its last-known readings should remain visible with a clear status.

Controls for supported devices are planned after the telemetry view. The interface should show whether a request is pending or has completed once the server can report its outcome.
