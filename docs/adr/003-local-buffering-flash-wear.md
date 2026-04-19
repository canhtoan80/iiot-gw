# ADR-003: Local Buffering with Flash-Wear Protection

## Status
Accepted

## Context
Edge devices often use eMMC or SSD storage with limited write cycles. Constant writing of telemetry data to a local database can significantly reduce the lifespan of the storage medium. However, 72-hour buffering is required to ensure no data loss during network outages.

## Decision
We will implement a two-layer buffering strategy:
1. **RAM Layer (`tmpfs`):** Incoming Sparkplug B payloads are first written to a bounded channel in RAM and then flushed to a temporary file on a `tmpfs` mount.
2. **Persistent Layer (SQLite):** Payloads are flushed from RAM to a SQLite database on physical storage (eMMC/SSD) at a configurable `sync_interval` (default 5s).
- **WAL Mode:** SQLite is configured in Write-Ahead Logging (WAL) mode to improve write performance and concurrency.

## Consequences
- **Positive:** significantly reduced physical write frequency, extending hardware life. Resilient to application crashes (data in `tmpfs` or SQLite is preserved).
- **Negative:** Data in RAM is lost if a hard power failure occurs before the sync interval.
- **Mitigation:** Use of a mini-UPS or extremely short sync intervals for critical data.
