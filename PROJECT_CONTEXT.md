# PROJECT_CONTEXT: EMS IIoT Gateway (Tier 2A)

## 1. Project Intent & Scope

### Business Goal
The **EMS IIoT Gateway (Tier 2A)** is a high-performance industrial gateway designed for Energy Management Systems (EMS) in Vietnamese SMEs. It acts as the "Edge Layer" (Tier 2A) in a multi-tier architecture, providing a reliable, autonomous, and cost-effective solution for monitoring energy consumption (TOU, Demand Response, Peak Shaving) with industrial-grade reliability.

### Key Use Cases
- **Autonomous Polling:** Collect data from energy meters, PLCs, and sensors (Modbus RTU/TCP, BACnet, MQTT Native).
- **Edge Processing:** Normalize data (CT/PT ratios), calculate virtual tags (OEE, SEI), and apply deadband filtering at the edge.
- **Quality Assurance:** Annotate every data point with quality status (Good, Bad, Stale) before transmission.
- **Reliable Data Uplink:** Use Sparkplug B over MQTT to provide standardized OT data to upstream servers, with 72-hour store-and-forward buffering.
- **Edge Autonomy:** Operation 24/7 even when upstream servers (Tier 2B/Tier 3) are offline.

### Non-Goals
- **Business Logic:** The gateway does not handle cost calculations or high-level business decisions.
- **Primary Data Storage:** It is not a long-term historian; it only buffers data for 72 hours.
- **User Interface for End-Users:** The local UI is for maintenance/configuration, not for end-user energy reports.

## 2. Technology Stack

### Runtime & Languages
- **Runtime:** .NET 8 (Worker Service) for the core logic.
- **Languages:** C# (Core) and Rust (Planned for high-performance async polling engine via `tokio-modbus`).

### Communication Protocols
- **Southbound (Field):** Modbus RTU/TCP, BACnet/IP, MQTT Native.
- **Northbound (Uplink):** MQTT with Sparkplug B v1.0 (Protobuf) payload.

### Data Management
- **Local Storage:** SQLite for store-and-forward buffering.
- **Optimization:** `tmpfs` RAM disk integration for flash-wear protection on eMMC/SSD devices.

### Hardware Assumptions
- **Target OS:** Linux (Ubuntu/Debian) on Industrial PCs (x86) or ARM SBCs.
- **Watchdog:** Mandatory hardware watchdog timer integration.
- **NTP:** Continuous time synchronization with drift monitoring.

## 3. Assumptions & Known Limitations

### Assumptions
- **Network Stability:** The field network (RS485/LAN) is assumed to be electrically noisy; retry policies and CRC checks are mandatory.
- **Power Supply:** The gateway is expected to have a stable power supply, preferably with a mini-UPS for graceful shutdown.

### Known Limitations
- **Buffer Capacity:** Local buffer is limited to 72 hours by default.
- **Protocol Scope:** Initial release focuses on Modbus; BACnet and others are phased.
- **RS485 Half-Duplex:** Bus arbitration is software-managed via a priority write queue.
