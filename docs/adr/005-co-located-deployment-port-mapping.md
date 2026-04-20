# ADR-005: Co-located G1/G2 Deployment Port Mapping

## Status
Accepted

## Context
In some deployments (G1/G2 models), the Tier 2A Gateway firmware and the Tier 2B OT Broker (EMQX) run on the same physical hardware. Both Mosquitto (used for south-facing communication) and EMQX (used for OT-level aggregation) default to binding to MQTT port 1883. This creates a port conflict preventing both services from starting.

## Decision
We will implement a standardized port mapping strategy for co-located deployments:
1. **Mosquitto (South-facing):** Remains on port **1883**, but binds only to `127.0.0.1` (loopback). This receives data from the local .NET firmware and MQTT-native devices.
2. **EMQX (Tier 2B OT Broker):**
   - **Internal Listener:** Port **1884** on `127.0.0.1`. Mosquitto bridges its data to this port.
   - **External Listener:** Port **8883** (TLS) on `0.0.0.0`. This receives data from remote G3 gateways or uplink from other field devices.
3. **Firmware:** Always connects to `localhost:1883`.

## Consequences
- **Positive:** Resolves service start-up conflicts. Clearly separates the "Southbound" (local/internal) and "Northbound" (uplink/external) traffic flows on the same machine.
- **Negative:** Adds slight complexity to the Mosquitto bridge configuration (must explicitly target port 1884).
