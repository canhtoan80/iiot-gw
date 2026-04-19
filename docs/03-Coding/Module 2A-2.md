## Module 2A-2 — `EMS.Gateway.DeviceTemplate`

```
You are a senior .NET 8 engineer. Module 2A-1 (EMS.Gateway.Contracts) is already implemented.

## Task
Implement `EMS.Gateway.DeviceTemplate` — the single source of truth for all device configurations
in the IIoT Gateway firmware. This is a Class Library, not a Worker Service.

## Key responsibilities
1. Load and validate `devices.json` at startup
2. Implement Register Coalescing algorithm (build-time, not runtime)
3. Support atomic hot-reload from Config Channel (G2) without firmware restart
4. Implement Config Auto-Rollback Grace Period monitoring hook (2A-4 calls this)
5. Backup config before swap, support rollback to backup

## Project references
- EMS.Gateway.Contracts (Module 2A-1)
- NuGet: FluentValidation (for validation rules)
- NuGet: NCalc2 (pre-compile expressions at load time)

## Class to implement: `DeviceTemplateRepository : IDeviceTemplateRepository`

### Core logic requirements

**Validation rules (use FluentValidation):**
- `device_id`: required, unique, pattern `^[a-z0-9-]{1,64}$`
- `poll_cycle_ms`: minimum 500 (hard floor — bus safety)
- `ct_ratio` and `pt_ratio`: > 0
- `scale_factor` in each register: != 0
- `deadband`: >= 0
- `range_min` < `range_max` when both specified
- `virtual_tag_expressions`: pre-compile with NCalc2; fail validation if syntax error
- `command_whitelist` register addresses must exist in `registers[]` list

**Register Coalescing algorithm:**
```
Input: sorted List<RegisterDefinitionDto> for one device
Output: List<CoalescedBlockDto>

Algorithm:
  - Sort registers ascending by Address
  - Iterate: if (current.Address - lastBlockEnd) <= MaxGapWords
               AND (current.Address - blockStart) <= MaxRegistersPerBlock
            → extend current block
            else → close block, start new
  - Each CoalescedBlockDto contains: StartAddress, Count (span), Tags with offsets
  - Build at load time, cache in ImmutableDictionary<deviceId, IReadOnlyList<CoalescedBlockDto>>
  - Default MaxGapWords = 10, MaxRegistersPerBlock = 100
```

**Atomic hot-reload with backup:**
```csharp
public async Task<ReloadResult> ReloadAsync(string newConfigJson)
{
    // 1. Parse and validate new config — fail fast on any error
    // 2. SaveBackupConfig(currentConfigJson) → /etc/ems-gateway/devices.json.bak
    // 3. Pre-compile NCalc expressions — fail fast if syntax error
    // 4. Build CoalescedBlocks for all devices
    // 5. Atomic swap: Interlocked.Exchange on ImmutableDictionary reference
    // 6. Publish ConfigReloadRequestedEvent via IInternalEventBus
    // 7. Return ReloadResult with new config hash
    // On any failure: do NOT swap, return error — old config stays active
}

public async Task<bool> RollbackToBackupAsync()
{
    // Load devices.json.bak, validate, atomic swap back, publish ConfigRolledBackEvent
}
```

**Config change history log:**
- Append-only NDJSON at `/var/log/ems-gateway/config-history.ndjson`
- Each entry: `{ timestamp, event_type, config_hash, changed_by, success }`
- Keep last 30 entries (rotate automatically)

## File structure expected
```
EMS.Gateway.DeviceTemplate/
├── DeviceTemplateRepository.cs      ← main implementation
├── Validators/
│   ├── DeviceTemplateDtoValidator.cs
│   └── RegisterDefinitionDtoValidator.cs
├── Coalescing/
│   └── RegisterCoalescingBuilder.cs  ← pure static algorithm, easy to unit test
└── ConfigHistory/
    └── ConfigHistoryLogger.cs
```

## Unit tests required (in EMS.Gateway.DeviceTemplate.Tests)
1. Coalescing: 3 scattered registers → 1 block (gap < MaxGapWords)
2. Coalescing: gap > MaxGapWords → 2 separate blocks
3. Coalescing: max registers per block boundary → splits correctly
4. Validation: poll_cycle_ms < 500 → validation fails
5. Validation: duplicate device_id → validation fails
6. Validation: NCalc syntax error in expression → validation fails
7. Hot-reload: valid new config → atomic swap succeeds, old config removed
8. Hot-reload: invalid new config → swap NOT done, old config preserved
9. Rollback: backup config restores correctly after failed reload

## Deliverable
Complete source files + unit tests. No TODOs. All edge cases handled.
```

---