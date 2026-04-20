# ARCHITECTURE: EMS IIoT Gateway (Tier 2A)

## 1. System Design Overview
The gateway follows a modular, event-driven architecture built on .NET 8 Worker Services. It is designed for high-performance OT data ingestion and standardized IT publishing.

### High-Level Data Flow
1. **M03 (Protocol Adapter):** Polls field devices (Modbus/BACnet/MQTT).
2. **M04 (Polling Engine):** Schedules cycles and maintains a Tag Database snapshot.
3. **M05 (Edge Rule Engine):** Normalizes values and calculates Virtual Tags (NCalc).
4. **M06 (Quality Checker):** Annotates tags with `Good`, `Bad`, or `Stale` quality.
5. **M07 (Sparkplug Encoder):** Wraps data into Sparkplug B Protobuf payloads.
6. **M08 (Local Buffer):** Stores payloads in SQLite if the uplink is down.
7. **M11 (Mqtt Publisher):** Publishes data to the local Mosquitto broker (Port 1883).

## 2. Deployment Models (Tiers)

### G1/G2: Co-located Model (Gateway + OT Broker)
- **Host:** Single Industrial PC.
- **Components:** .NET Gateway + Mosquitto (1883) + EMQX OT Broker (1884 Internal / 8883 External).
- **Communication:** Local bridge from Mosquitto:1883 to EMQX:1884.

### G3: Distributed Model (Gateway Only)
- **Host:** Industrial PC (Gateway) + Dedicated Server (OT Broker).
- **Components:** .NET Gateway + Mosquitto (1883).
- **Communication:** Secure bridge (TLS 1.3) from Mosquitto:1883 to remote EMQX:8883.

## 3. Process Boundaries & Communication
- **Internal Communication:** Uses `IInternalEventBus` powered by `System.Threading.Channels` (Bounded, lock-free) for inter-module handoff.
- **External Communication:** 
  - **Southbound:** RS485 (Serial) or RJ45 (TCP/UDP) to field devices.
  - **Northbound:** MQTT Sparkplug B v1.0 to local/remote MQTT Broker.

## 4. Core Modules
| ID | Module | Responsibility |
|---|---|---|
| 2A-1 | `Contracts` | Shared interfaces, DTOs, and Domain Events. |
| 2A-2 | `DeviceTemplate` | Single source of truth for device configs and coalescing. |
| 2A-3 | `ProtocolAdapter` | Driver layer for Modbus, BACnet, and MQTT Native. |
| 2A-4 | `PollingEngine` | Scheduler, Deadband filtering, and Stale detection. |
| 2A-5 | `EdgeRuleEngine` | CT/PT normalization and NCalc expression evaluation. |
| 2A-6 | `QualityChecker` | Range, Stuck, and Rate-of-Change checks. |
| 2A-7 | `SparkplugEncoder` | Sparkplug B v1.0 Protobuf encoding. |
| 2A-8 | `LocalBuffer` | 72h SQLite buffer with `tmpfs` optimization. |
| 2A-9 | `AdminService` | Health monitoring, NTP synchronization, and Watchdog. |
| 2A-10| `CommandHandler` | (G3) Validates and executes control commands. |
| 2A-11| `Host` | Composition root and graceful shutdown management. |

## 5. Design Principles
- **Edge-First Autonomy:** No dependency on Tier 3 for data collection or processing.
- **Tag Quality First:** Every value must have a quality status. Bad/Stale values are null-encoded with reasons.
- **Flash-Wear Awareness:** Minimized physical writes using RAM-to-SQLite flush strategy.
- **Bus Arbitration:** Priority queueing for RS485 half-duplex to prevent collisions between Poll and Write commands.
- **Zero-Allocation Focus:** Use of `ArrayPool<byte>`, `Span<T>`, and `Utf8JsonReader` in high-throughput paths.

## 6. Security Architecture
- **JWT Validation:** Control commands (G3) must have a valid JWT signed by the Business Server.
- **Whitelist Enforcement:** Only specific registers and ranges defined in the local template can be written to.
- **Audit Logging:** All control actions are logged in a local append-only NDJSON file.
- **mTLS:** Mutual TLS 1.3 for all "Northbound" bridges to ensure secure data uplink.
