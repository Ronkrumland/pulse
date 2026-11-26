# Pulse.Server

Planned management API and device state service for Pulse. It will subscribe to agent messages over MQTT and provide device status to [Pulse.Web](../Pulse.Web/README.md).

The first milestone is to track the latest readings and availability for each device, preserve the observation time, and expose enough state for the interface to show when a device is offline or its readings are stale.

The design calls for MQTT to carry live updates and PostgreSQL to store durable records such as device registration, hardware inventory, and future command history. The database schema and API routes are still to be defined. Future controls will use the request and result messages in [Pulse.Contracts](../Pulse.Contracts/README.md).
