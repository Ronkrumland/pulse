# Pulse.Contracts

Planned shared message definitions for the platform agents and [Pulse.Server](../Pulse.Server/README.md). The first contract will cover device availability and hardware telemetry. Control requests and results will follow as their features are implemented.

| Message | Intended contents |
| --- | --- |
| Availability | Device identity and online or offline state |
| Telemetry | Device identity, observation time, and available readings with units |
| Command and result | Target device, requested action, request ID, and corresponding outcome |

The initial design calls for the broker to retain last-known state where useful. Observation time and availability let consumers show when that state is stale. Commands are intended to be non-retained and carry a request ID so recipients can recognize duplicate deliveries and correlate results.

Topic names, payload schemas, and delivery settings are still to be defined.
