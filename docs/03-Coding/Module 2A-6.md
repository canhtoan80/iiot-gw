## Module 2A-6 — `EMS.Gateway.QualityChecker`

```
You are a senior .NET 8 engineer. Modules 2A-1 through 2A-5 are complete.

## Task
Implement `EMS.Gateway.QualityChecker` — the final quality gate before data enters the
Sparkplug B encoder. This is a Class Library. Pure transformation: annotates TagQuality
on EnrichedTagBatch. Does NOT modify values. Does NOT filter/drop tags.

## Project references
- EMS.Gateway.Contracts
- EMS.Gateway.DeviceTemplate

## Class to implement: QualityChecker : IQualityChecker

### Quality check pipeline (applied in order to each tag)

**1. Null/NaN Check (first priority)**
```
if value == null || double.IsNaN(value) || double.IsInfinity(value):
    → Quality = Bad, reason = "NullOrNaN"
    → STOP, don't apply further checks
```

**2. Range Check**
```
if value < RangeMin || value > RangeMax:
    → Quality = Bad, reason = "RangeExceeded:{value} not in [{min},{max}]"
```

**3. Rate of Change Check (RoC)**
```
roc = |current_value - previous_value| / elapsed_seconds
if roc > RocLimitPerSecond:
    → Quality = Bad, reason = "RoCExceeded:{roc:.2f} > limit:{limit}"
Previous value stored in ConcurrentDictionary<string, (double value, long timestampNs)>
```

**4. Stuck Check**
```
if AllowStuck == false:
    if value == previous_value AND elapsed > stuck_timeout_ms:
        → Quality = Bad, reason = "StuckValue"
// Do NOT apply stuck check when AllowStuck = true (e.g., fixed setpoint registers)
```

**5. Stale passthrough**
```
if tag.Quality == Stale (from PollingEngine): keep as Stale, add reason if missing
```

**Transition event publishing:**
- Track previous quality per tag in `ConcurrentDictionary<string, TagQuality>`
- If transition Good→Bad OR Good→Stale: publish `TagQualityDegradedEvent`
- Do NOT re-publish for Bad→Bad or Stale→Stale (avoid event flood)
- Do NOT publish for Bad→Good or Stale→Good (recovery is normal)

**IMPORTANT: Never filter or drop tags**
- Even if Quality = Bad: tag MUST pass through to SparkplugEncoder
- Reason: Tầng 3 must receive IsNull=true with PropertySet to distinguish
  "data is bad" from "no data sent"

## Unit tests required
1. Null value → Quality=Bad, reason contains "NullOrNaN"
2. Value below RangeMin → Quality=Bad, reason contains "RangeExceeded"
3. RoC exceeded → Quality=Bad, reason contains "RoCExceeded"
4. Stuck value after stuck_timeout_ms → Quality=Bad, reason "StuckValue"
5. AllowStuck=true tag: same value repeated → Quality=Good (no stuck detection)
6. Good→Bad transition: TagQualityDegradedEvent published once
7. Bad→Bad: TagQualityDegradedEvent NOT published again
8. Bad tag passes through (not dropped): output batch has same count as input
9. IsSimulated tag: passes through all checks unchanged (simulation overrides quality)

## Deliverable
Complete QualityChecker class library. Thread-safe state via ConcurrentDictionary.
All tags pass through — no filtering.
```

---
