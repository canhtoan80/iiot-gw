# ADR-002: Sparkplug B for OT-IT Bridge

## Status
Accepted

## Context
Industrial data often lacks standardized metadata and quality information when transmitted via raw MQTT. To ensure the Business Server (Tier 3) can automatically discover devices and interpret data quality without manual mapping, a structured protocol is needed.

## Decision
We will use **Sparkplug B v1.0** over MQTT for all Northbound communication.
- Every data point must include quality status (`Good`, `Bad`, `Stale`).
- Bad/Stale data is encoded with `is_null=true` and an associated reason in the `PropertySet`.
- Device lifecycle (Birth/Death certificates) is mandatory to ensure state awareness at the server level.

## Consequences
- **Positive:** Standardized data format, automatic device discovery, and consistent handling of data quality across the entire system.
- **Negative:** Increased payload size due to Protobuf and metadata. Requires specialized Sparkplug B encoding/decoding libraries.
