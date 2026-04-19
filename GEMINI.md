# GEMINI.md

## Project Overview
**EMS IIoT Gateway (Tier 2A)** is a high-performance industrial gateway designed for Energy Management Systems (EMS) in Vietnamese SMEs. It acts as the "Edge Layer" (Tier 2A) in a multi-tier architecture, sitting between the **Field Layer** (meters, PLCs) and the **Business Server** (Tier 3).

The primary goal is to provide a reliable, autonomous, and cost-effective solution for monitoring energy consumption (TOU, Demand Response, Peak Shaving) with industrial-grade reliability.

### Main Technologies
- **Runtime:** .NET 8 (Worker Service) for the core gateway logic.
- **Languages:** C# (Core) and Rust (Planned for high-performance async polling engine via `tokio-modbus`).
- **Protocols:** Modbus (TCP/RTU), BACnet, MQTT (Native & Sparkplug B).
- **Messaging:** Sparkplug B v1.0 (Protobuf) over MQTT for standardized OT data.
- **Local Storage:** SQLite for local buffering (72h Store-and-Forward) with `tmpfs` RAM optimization to protect flash memory.
- **Architecture:** Polyglot architecture with Akka.NET (C# Core) and async Rust polling.

## Project Structure
This repository currently serves as the **Documentation and Design Hub** for the project. It contains requirements, architectural decomposition, and AI-ready coding prompts.

- `README.md`: High-level project goals and Vietnamese overview.
- `docs/01-Requirement/`: Detailed module decomposition and SRS (Software Requirements Specification).
- `docs/02-Design/`: System design and architectural diagrams.
- `docs/03-Coding/`: **AI Coding Prompts** for modular implementation and coding standards.
- `docs/05-Deployment/`: Deployment strategies for edge devices (Docker, OTA updates).

## Building and Running
As this is currently a documentation/requirement-focused repository, the source code may reside in a separate repository or is being generated via the prompts in `docs/03-Coding/`.

### Planned Environment
- **Target OS:** Linux (Ubuntu/Debian) on Edge IPCs.
- **Framework:** .NET 8 SDK.
- **Build Commands (Inferred):**
    - Build: `dotnet build`
    - Run: `dotnet run --project src/EMS.Gateway.Worker`
    - Test: `dotnet test`
- **Deployment:** Docker-based (`docker-compose`) with Sparkplug B bridge to an EMQX OT Broker.

## Development Conventions
- **Edge-first Autonomy:** The gateway must operate 24/7 even if the upstream server is offline.
- **TagQuality First:** Every data point must have a quality status (`Good`, `Bad`, `Stale`).
- **Zero-Allocation Focus:** High-performance data pipelines using `Span<T>` and memory pooling.
- **Flash-Wear Awareness:** Minimize physical disk writes on edge devices (eMMC/SSD) by using RAM buffers.
- **AI-Driven Development:** Implementation follows the prompts defined in `docs/03-Coding/EMS_IIoTGateway_Tier2A_AI_Coding_Prompts_v1_0.md`.

## Usage
Use the files in `docs/` as the "Source of Truth" for any implementation tasks. Specifically:
1. Refer to `docs/01-Requirement/EMS_IIoTGateway_Tier2A_Module_Decomposition_v1.7.md` for the latest architecture.
2. Use the prompts in `docs/03-Coding/` to generate or refactor code modules.
