## Module 2A-9 — `EMS.Gateway.AdminService`

```
You are a senior .NET 8 engineer with Linux systems programming experience.
Modules 2A-1 through 2A-8 are complete.

## Task
Implement `EMS.Gateway.AdminService` — the operational backbone keeping the Gateway alive 24/7.
This is a Worker Service (.NET 8) hosting multiple background workers.

## Project references
- EMS.Gateway.Contracts
- NuGet: prometheus-net.AspNetCore
- NuGet: Microsoft.Extensions.Diagnostics.HealthChecks

## Sub-components to implement

### 1. HardwareWatchdogWorker
```csharp
// Write keepalive to /dev/watchdog every 30s (configurable)
// Use FileStream with FileShare.None — only one writer allowed
// If /dev/watchdog does not exist → use systemd sd_notify watchdog instead
// CRITICAL: if this worker crashes, hardware resets the board → intentional

private static readonly byte[] KeepAlive = new byte[] { 0x31 }; // '1'
await using var watchdog = File.Open("/dev/watchdog", FileMode.Open, 
    FileAccess.Write, FileShare.None);
while (!ct.IsCancellationRequested)
{
    await watchdog.WriteAsync(KeepAlive, ct);
    await Task.Delay(WatchdogIntervalMs, ct);
}
```

### 2. SoftwareWatchdogService : ISoftwareWatchdog
```csharp
// Each Worker reports alive by calling ReportAlive(workerId) after each cycle
// If any Worker hasn't reported within 2 * its expected cycle time:
//   → Log CRITICAL
//   → Call IHostApplicationLifetime.StopApplication()
//   → Hardware Watchdog then triggers hard reset
// Worker registration: Workers register with expected_heartbeat_interval_ms at startup
```

### 3. NtpWatchdogWorker
```csharp
// Run every 30s
// Execute: Process.Start("chronyc", "tracking") and parse output
//   OR read /run/chrony/tracking.sock via chrony protocol
// Drift > 5s  → publish NtpDriftExceededEvent, log WARNING
// Drift > 30s → publish NtpDriftExceededEvent(severe), all tags → Stale
```

### 4. RtcManagerService : IRtcManager
```csharp
// SyncRtcFromSystemAsync: 
//   Only when NTP drift < 2s
//   Execute: Process.Start("hwclock", "-w --systematic")
//   Run every 15 minutes via PeriodicTimer

// IsRtcTimeValidAsync (called at boot, BEFORE Worker services start):
//   Compare DateTime.UtcNow with FIRMWARE_BUILD_UTC (embedded compile-time constant)
//   If UtcNow < (FIRMWARE_BUILD_UTC - 30 days) → invalid
//   Publish RtcInvalidEvent if invalid
//   Return false → PollingEngine marks all tags Stale

// Optional LED indicator:
//   if (File.Exists("/sys/class/leds/error/brightness"))
//       File.WriteAllText(..., "1"); // graceful degrade if not available
```

### 5. FlashHealthMonitor : IFlashHealthMonitor
```csharp
// Run once per day at 00:00 UTC
// Detect device type: if /sys/block/mmcblk0 exists → eMMC, else try NVMe smartctl

// eMMC (JEDEC JESD84-B51):
//   Read /sys/block/mmcblk0/device/life_time → "0x02 0x03"
//   Parse hex, take max → life_percent = max * 10 (0x01=10%, 0x0A=100%)
//   Read /sys/block/mmcblk0/device/pre_eol_info → decode PreEolStatus
//   life_percent >= 70 → HardwareDegradedEvent(Warning)
//   life_percent >= 90 → HardwareDegradedEvent(Critical)
//   PreEolStatus = ExceededEol → disable LocalBuffer writes (only RAM)
```

### 6. AutoRenewCertManager : IAutoRenewCertManager
```csharp
// Run check every 24h (PeriodicTimer)
// Read cert expiry: X509Certificate2.GetExpirationDateString()
// If days_remaining < CertRenewDaysBeforeExpiry (default 30):
//   Execute: step ca renew {certPath} {keyPath} --ca-url {caUrl} --root {caRoot} --force
//   On success: systemctl restart mosquitto (NOT reload — reload may not apply new cert)
//   Wait 10s → verify bridge reconnected
//   On failure: retry 3 times (1h apart) → CertRenewalFailedEvent
// Publish metric: gateway_cert_expiry_days_remaining
```

### 7. Health Check endpoint
```csharp
// GET /health → 200 OK or 503
// GET /health/detail → full GatewayHealthDto as JSON
// ASP.NET Core HealthChecks: register custom checks for each subsystem
// Prometheus endpoint: GET /metrics (prometheus-net)
```

### 8. Prometheus metrics to expose
```
gateway_ntp_drift_seconds                  (gauge)
gateway_buffer_pending_records             (gauge)
gateway_mqtt_connected                     (gauge 0/1)
gateway_device_online_count                (gauge)
gateway_poll_success_rate{device}          (gauge)
gateway_tag_quality_good_ratio{device}     (gauge)
gateway_emmc_wear_percent                  (gauge)
gateway_emmc_pre_eol_status                (gauge 0-3)
gateway_rtc_valid                          (gauge 0/1)
gateway_cert_expiry_days_remaining         (gauge)
gateway_cert_renewal_total                 (counter)
gateway_buffer_corruption_total            (counter)
gateway_mqtt_dropped_messages_total        (counter)
```

### 9. OTA Update Client (G3, feature flag)
```csharp
// Feature flag: "Ota:Enabled": true/false
// Subscribe topic: ems/gateway/{machineId}/OTA via Mosquitto
// Download bundle to /tmp/ems-update/
// Verify SHA-256 checksum + Ed25519 signature
// Execute /usr/lib/ems-gateway/ota-apply.sh {bundlePath}
// OtaStrategy: "InPlaceAtomic" (x86) | "RaucAB" (ARM, if rauc binary exists)
```

### 10. Remote Diagnostics (Admin only)
```csharp
// POST /api/diagnostics/dump → dotnet-dump collect (save to /tmp/ems-diag/)
// POST /api/diagnostics/trace?duration=30 → dotnet-trace collect (max 60s)
// GET /api/diagnostics/files → list available diagnostic files
// GET /api/diagnostics/files/{name} → download file (chunked)
// Auto-cleanup diagnostic files after 1 hour
// Require Admin role on all endpoints
```

## Unit tests required
1. NTP drift > 5s → NtpDriftExceededEvent published
2. NTP drift > 30s → all tags annotated Stale
3. RTC invalid (time < firmware build) → RtcInvalidEvent published, PollingEngine notified
4. SoftwareWatchdog: worker misses heartbeat → StopApplication called
5. eMMC wear: life_percent=72 → HardwareDegradedEvent(Warning) published
6. eMMC wear: pre_eol_info=Urgent → HardwareDegradedEvent(Critical) regardless of percent
7. Cert renew: days_remaining=25 → step ca renew executed, mosquitto restarted
8. Cert renew: 3 consecutive failures → CertRenewalFailedEvent published

## Deliverable
Complete AdminService with all sub-components. Linux syscall wrappers gracefully degrade
if hardware features unavailable (no /dev/watchdog, no /sys/block/mmcblk0, etc.).
```

---