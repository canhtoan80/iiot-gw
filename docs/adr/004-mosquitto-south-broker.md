# ADR-004: Mosquitto as South-facing Broker

## Status
Accepted

## Context
The gateway needs to handle devices that publish data via MQTT Native (non-Sparkplug). To ensure fault isolation and prevent these devices from directly affecting the core firmware, a local MQTT broker is required.

## Decision
We will use **Mosquitto** as the south-facing MQTT broker running as a separate process on the gateway machine.
- **Role:** It acts as the ingestion point for MQTT Native devices and the primary uplink handler for the firmware's Sparkplug B payloads.
- **Bridge:** Mosquitto will be configured to bridge data to the Tier 2B EMQX OT Broker.
- **Isolation:** This separates the MQTT session management from the .NET firmware process, allowing independent restarts and better fault tolerance.

## Consequences
- **Positive:** Robust session management, proven stability, and low resource overhead (~5MB RAM). Provides a second layer of buffering via Mosquitto's persistence.
- **Negative:** Adds an extra process to manage and monitor.
