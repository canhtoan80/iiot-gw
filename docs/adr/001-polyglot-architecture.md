# ADR-001: Polyglot Architecture (C# Core & Rust Polling Engine)

## Status
Proposed

## Context
The IIoT Gateway requires high-performance, asynchronous polling of industrial protocols (Modbus, BACnet) while maintaining a high-level, manageable application structure for telemetry, buffering, and business rules. 

C# (.NET 8) provides excellent productivity, dependency injection, and a rich library ecosystem for the gateway's core logic. However, high-frequency async polling and bus arbitration (especially on RS485) benefit from the zero-overhead and strict safety of Rust.

## Decision
We will employ a polyglot architecture:
- **C# (.NET 8):** For the Gateway Core, including Device Template management, Sparkplug B encoding, SQLite buffering, and SignalR telemetry.
- **Rust:** For the high-performance async polling engine (utilizing `tokio-modbus`).
- **Integration:** The C# layer manages the Rust engine via FFI or a controlled IPC handle (u64 handles for connections).

## Consequences
- **Positive:** Improved performance and predictability for sub-second polling cycles. Better memory safety for protocol drivers.
- **Negative:** Increased complexity in the build system and debugging across language boundaries. Requires both .NET and Rust toolchains.
- **Risk:** FFI overhead and data marshaling complexity.
