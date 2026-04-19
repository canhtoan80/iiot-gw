## Module 2A-4 — `EMS.Gateway.PollingEngine`

```
You are a senior .NET 8 engineer. Modules 2A-1, 2A-2, 2A-3 are complete.

## Task
Implement `EMS.Gateway.PollingEngine` — the scheduler that drives all device polling,
maintains the TagDatabase snapshot, applies deadband filtering, and detects stale tags.
This is a Worker Service (.NET 8).

## Project references
- EMS.Gateway.Contracts
- EMS.Gateway.DeviceTemplate
- EMS.Gateway.ProtocolAdapter

## Classes to implement

### PollingEngine : IPollingEngine, IHostedService

**TagDatabase:**
```csharp
// ImmutableDictionary snapshot — thread-safe read, atomic write
private volatile ImmutableDictionary<string, EnrichedTagDto> _tagDatabase
    = ImmutableDictionary<string, EnrichedTagDto>.Empty;

// Key format: "{deviceId}::{tagName}"
```

**Polling loop per device (one Task per device):**
```csharp
// Use System.Threading.PeriodicTimer (NOT Task.Delay — avoids drift accumulation)
// Each device runs independently

async Task PollDeviceLoop(DeviceTemplateDto device, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(device.PollCycleMs));
    while (await timer.WaitForNextTickAsync(ct))
    {
        var rawBatch = await _protocolAdapter.PollAsync(device, coalescedBlocks, ct);
        ApplyDeadbandAndPublish(device, rawBatch);
    }
}
```

**Deadband Filter rules:**
- `|new_value - last_forwarded_value| > deadband` → forward to pipeline
- `Quality != Good` (Bad or Stale) → ALWAYS forward immediately, bypass deadband
- Heartbeat: even if value within deadband, forward every `heartbeat_interval_s` (default 60s)
- Last forwarded value stored per tag in local `Dictionary<string, double>`

**Stale Detection (background timer, 1s interval):**
```
For each tag in TagDatabase:
  if (UtcNow - tag.TimestampUtcNs_as_DateTime) > stale_timeout_ms → Quality = Stale
  if transition from Good/Bad → Stale: publish MetricsBatchReadyEvent with Stale tag
```

**Config Auto-Rollback Grace Period (called by Module 2A-2 after hot-reload):**
```csharp
public async Task EnterGracePeriodAsync(int durationMinutes, CancellationToken ct)
{
    // Monitor every 30s: calculate % Bad among active devices
    //   "Active device" = device that had at least 1 Good tag since session start
    // Two consecutive checks > 50% Bad → call _templateRepo.RollbackToBackupAsync()
    // On rollback: publish ConfigRolledBackEvent, stop grace period
    // On success (full duration, no rollback): clear backup file
}
```

**IInternalEventBus publish:**
- After deadband pass and quality annotation: publish `MetricsBatchReadyEvent`
- On device consecutive fail threshold: publish `DeviceUnresponsiveEvent`

**Subscribe ConfigReloadRequestedEvent:**
- Stop all polling tasks → reload templates → restart polling tasks (graceful cycle)

## Unit tests required
1. Deadband: value change < deadband → NOT forwarded (no event published)
2. Deadband: value change > deadband → forwarded (event published)
3. Deadband: Bad quality always forwarded regardless of deadband value
4. Heartbeat: value within deadband but heartbeat interval elapsed → forwarded
5. Stale detection: tag not updated for stale_timeout_ms → Quality=Stale event published
6. Grace period: 60% Bad for 2 consecutive checks → rollback triggered
7. Grace period: <50% Bad for full duration → backup cleared, CommittedEvent published
8. Hot-reload: polling restarts cleanly with new device list

## Deliverable
Complete PollingEngine with all features. PeriodicTimer used (not Task.Delay). Thread-safe.
```

---