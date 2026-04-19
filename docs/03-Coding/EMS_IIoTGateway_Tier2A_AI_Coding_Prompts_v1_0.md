# AI Coding Prompts — EMS IIoT Gateway (Tầng 2A)
> Tài liệu tham chiếu: EMS_IIoTGateway_Tier2A_Module_Decomposition_v1.8  
> Mục đích: Prompt chuẩn để giao cho AI coding agent (Claude, Cursor, Copilot) implement từng module  
> Quy ước: Mỗi prompt là một đơn vị công việc độc lập, tự chứa đủ context cần thiết  
> Thứ tự thực hiện: 2A-1 → 2A-2 → 2A-3 → 2A-4 → 2A-5 → 2A-6 → 2A-7 → 2A-8 → 2A-9 → 2A-10 → 2A-11

---

## Hướng dẫn sử dụng

- **Thứ tự bắt buộc:** Implement theo thứ tự từ 2A-1 đến 2A-11. Mỗi module phụ thuộc vào output của module trước.
- **Cách dùng:** Copy toàn bộ prompt của module cần implement, paste vào AI coding tool. Đính kèm file source code hiện tại nếu đang sửa đổi.
- **Kiểm tra sau mỗi module:** Chạy unit test trước khi tiếp tục module tiếp theo.
- **Ngôn ngữ output:** AI trả lời bằng tiếng Anh (code + comment). Giải thích có thể bằng tiếng Việt nếu cần.

---

## Module 2A-1 — `EMS.Gateway.Contracts`

```
You are a senior .NET 8 engineer building an industrial IIoT Gateway firmware.

## Task
Implement the `EMS.Gateway.Contracts` Class Library project — the shared kernel for the entire
EMS IIoT Gateway (Tier 2A) solution. This project defines ALL interfaces, DTOs, domain events,
and enums used across modules. It must contain ZERO infrastructure code, ZERO business logic,
and ZERO framework dependencies (pure C# only).

## Project setup
- Project type: Class Library (.NET 8)
- NuGet references: NONE (pure C# only)
- Output: EMS.Gateway.Contracts.dll

## Interfaces to implement (pure C# interface, no implementation)

### Core interfaces
```csharp
public interface IDeviceTemplateRepository
{
    DeviceTemplateDto? GetTemplate(string deviceId);
    IReadOnlyList<DeviceTemplateDto> GetAllTemplates();
    Task<ReloadResult> ReloadAsync(string newConfigJson);
    IReadOnlyList<CommandWhitelistEntryDto> GetCommandWhitelist(string deviceId); // G3
}

public interface IProtocolAdapter
{
    Task<RawTagBatch> PollAsync(DeviceTemplateDto device,
        IReadOnlyList<CoalescedBlockDto> coalescedBlocks,
        CancellationToken ct);
    Task<bool> WriteAsync(string deviceId, int registerAddress,
        object value, CancellationToken ct); // G3 only
}

public interface IPollingEngine
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    EnrichedTagBatch GetTagSnapshot(string deviceId);
}

public interface IEdgeRuleEngine
{
    EnrichedTagBatch ProcessAsync(RawTagBatch rawBatch);
    void PrecompileExpressions(IReadOnlyList<DeviceTemplateDto> templates);
    ValidationResult ValidateExpression(string expression, IReadOnlyList<string> availableTags);
}

public interface IQualityChecker
{
    EnrichedTagBatch CheckAsync(EnrichedTagBatch enrichedBatch);
}

public interface ISparkplugEncoder
{
    SparkplugPayloadDto EncodeDData(EnrichedTagBatch enrichedBatch);
    SparkplugPayloadDto EncodeDBirth(string deviceId, DeviceTemplateDto template,
        string firmwareVersion, string hardwareId);
    SparkplugPayloadDto EncodeDDeath(string deviceId);
    Task PublishAsync(SparkplugPayloadDto payload, CancellationToken ct);
}

public interface ILocalBuffer
{
    Task EnqueueAsync(SparkplugPayloadDto payload, CancellationToken ct);
    Task ReplayAsync(CancellationToken ct);
    Task PruneAsync(CancellationToken ct);
    long GetPendingCount();
}

public interface IMqttPublisher
{
    bool IsConnected { get; }
    Task<bool> PublishAsync(SparkplugPayloadDto payload, CancellationToken ct);
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
}

public interface IAdminService
{
    Task<GatewayHealthDto> GetHealthAsync();
    Task<TimeSpan> GetNtpDriftAsync();
    void TriggerWatchdogHeartbeat();
}

public interface ICommandHandler // G3 only
{
    Task<CommandResult> HandleAsync(CommandRequestDto request, CancellationToken ct);
}

public interface ISoftwareWatchdog
{
    void ReportAlive(string workerId);
    bool IsAnyWorkerUnhealthy();
}

public interface IInternalEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : IDomainEvent;
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct)
        where TEvent : IDomainEvent;
}

public interface IFlashHealthMonitor
{
    Task<FlashHealthDto> GetHealthAsync();
}

public interface IRtcManager
{
    Task SyncRtcFromSystemAsync();
    Task<DateTime> ReadRtcTimeAsync();
    Task<bool> IsRtcTimeValidAsync();
}

public interface IAutoRenewCertManager
{
    Task<CertStatusDto> CheckCertExpiryAsync();
    Task<bool> RenewIfNeededAsync(CancellationToken ct);
}
```

## DTOs to implement (C# records, immutable)

```csharp
// Device configuration
public record DeviceTemplateDto(
    string DeviceId,
    string Description,
    ProtocolType Protocol,
    JsonElement Connection,          // parsed differently per protocol
    double CtRatio,
    double PtRatio,
    int PollCycleMs,
    int HeartbeatIntervalS,
    IReadOnlyList<RegisterDefinitionDto> Registers,
    IReadOnlyList<VirtualTagExpressionDto> VirtualTagExpressions,
    IReadOnlyList<CommandWhitelistEntryDto> CommandWhitelist,
    ModbusCoalescingConfig? MqttNativeConfig,
    MqttNativeConnectionDto? MqttConnection,
    RateLimitConfig? RateLimit
);

public record RegisterDefinitionDto(
    string TagName,
    int Address,
    string FunctionCode,   // "FC03", "FC04", "FC01", "FC02"
    string DataType,       // "float32", "uint16", "int32", "bool"
    double ScaleFactor,
    string Unit,
    double Deadband,
    double RangeMin,
    double RangeMax,
    int StuckTimeoutMs,
    double RocLimitPerSecond,
    bool AllowStuck
);

public record CoalescedBlockDto(
    int StartAddress,
    int Count,
    IReadOnlyList<(string TagName, int Offset, string DataType)> Tags
);

public record VirtualTagExpressionDto(
    string TagName,
    string Expression,
    string Unit,
    double RangeMin,
    double RangeMax
);

public record CommandWhitelistEntryDto(
    string TagName,
    int RegisterAddress,
    string FunctionCode,
    double MinValue,
    double MaxValue,
    string Description
);

public record ModbusCoalescingConfig(
    bool Enabled,
    int MaxGapWords,
    int MaxRegistersPerBlock
);

public record MqttNativeConnectionDto(
    string BrokerHost,
    int BrokerPort,
    string SubscribeTopic,
    string PayloadFormat,
    IReadOnlyList<PayloadMappingDto> PayloadMappings,
    string? LwtTopic,
    string? LwtPayloadOffline,
    string? LwtPayloadOnline
);

public record PayloadMappingDto(
    string TagName,
    string JsonPath,
    string DataType
);

public record RateLimitConfig(
    int MaxMessagesPerSecond,
    int BurstSize
);

// Tag data
public record RawTagDto(
    string TagName,
    double? RawValue,
    long TimestampUtcNs,
    TagQuality Quality,
    string? QualityReason
);

public record RawTagBatch(
    string DeviceId,
    IReadOnlyList<RawTagDto> Tags,
    long PollTimestampUtcNs
);

public record EnrichedTagDto(
    string TagName,
    double? PhysicalValue,
    string Unit,
    TagQuality Quality,
    string? QualityReason,
    long TimestampUtcNs,
    bool IsVirtual,
    bool IsSimulated
);

public record EnrichedTagBatch(
    string DeviceId,
    IReadOnlyList<EnrichedTagDto> Tags,
    long BatchTimestampUtcNs
);

public record SparkplugPayloadDto(
    string DeviceId,
    SparkplugMessageType MessageType,
    byte[] ProtobufPayload,
    long TimestampUtcNs,
    int SeqNumber
);

// Commands (G3)
public record CommandRequestDto(
    string DcmdId,
    string DeviceId,
    int RegisterAddress,
    object Value,
    string JwtToken,
    long TimestampUtcNs
);

// Health
public record GatewayHealthDto(
    string MachineId,
    string FirmwareVersion,
    TimeSpan Uptime,
    TimeSpan NtpDrift,
    bool NtpValid,
    bool RtcValid,
    bool MqttConnected,
    long BufferPendingRecords,
    double BufferFillPercent,
    int DeviceTotalCount,
    int DeviceOnlineCount,
    bool SoftwareWatchdogHealthy,
    FlashHealthDto? FlashHealth,
    CertStatusDto? CertStatus
);

public record FlashHealthDto(
    string DevicePath,
    FlashDeviceType DeviceType,
    int LifeTimeUsedPercent,
    PreEolStatus PreEolStatus,
    DateTime LastChecked
);

public record CertStatusDto(
    string CertPath,
    DateTime ExpiryDate,
    int DaysRemaining,
    bool IsRenewing
);

// Results
public record ReloadResult(bool Success, string? ErrorMessage, string? BackupConfigHash);
public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
```

## Domain Events to implement (marker interface + records)

```csharp
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

// All events as records implementing IDomainEvent:
public record MetricsBatchReadyEvent(EnrichedTagBatch Batch, DateTimeOffset OccurredAt) : IDomainEvent;
public record TagQualityDegradedEvent(string DeviceId, string TagName, TagQuality From, TagQuality To, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;
public record DeviceUnresponsiveEvent(string DeviceId, int ConsecutiveFailures, DateTimeOffset OccurredAt) : IDomainEvent;
public record MqttConnectionLostEvent(string BrokerAddress, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;
public record MqttConnectionRestoredEvent(string BrokerAddress, DateTimeOffset OccurredAt) : IDomainEvent;
public record ConfigReloadRequestedEvent(string NewConfigHash, DateTimeOffset OccurredAt) : IDomainEvent;
public record ConfigRolledBackEvent(string Reason, double BadQualityPercent, IReadOnlyList<string> AffectedDevices, string RolledBackToHash, DateTimeOffset OccurredAt) : IDomainEvent;
public record NtpDriftExceededEvent(TimeSpan Drift, double ThresholdSeconds, DateTimeOffset OccurredAt) : IDomainEvent;
public record RtcInvalidEvent(DateTime DetectedTime, DateTime FirmwareBuildTime, DateTimeOffset OccurredAt) : IDomainEvent;
public record RtcRestoredEvent(DateTimeOffset OccurredAt) : IDomainEvent;
public record BufferCorruptedEvent(int EstimatedLostRecords, string CorruptedFilePath, DateTimeOffset OccurredAt) : IDomainEvent;
public record HardwareDegradedEvent(string Component, DegradedSeverity Severity, string Message, DateTimeOffset OccurredAt) : IDomainEvent;
public record MqttMessageDroppedEvent(string DeviceId, int DroppedCount, DateTimeOffset OccurredAt) : IDomainEvent;
public record MqttDeviceLwtOfflineEvent(string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent;
public record MqttDeviceLwtOnlineEvent(string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent;
public record CommandReceivedEvent(string DcmdId, string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent; // G3
```

## Enums to implement

```csharp
public enum TagQuality { Good, Bad, Stale }
public enum ProtocolType { ModbusRTU, ModbusTCP, BACnetIP, MqttNative, Custom }
public enum SparkplugMessageType { DBirth, DData, DDeath }
public enum CommandResult { Accepted, RejectedWhitelist, RejectedRangeExceeded, RejectedJwtInvalid, DeviceError, AckTimeout } // G3
public enum FlashDeviceType { eMMC, NVMe, SATA }
public enum PreEolStatus { Normal, Warning, Urgent, ExceededEol }
public enum DegradedSeverity { Warning, Critical }
```

## Implementation constraints
- NO framework dependencies (no Microsoft.Extensions.*, no MQTTnet, no SQLite)
- All DTOs must be C# records (immutable by default)
- All collections in DTOs must be IReadOnlyList<T>, not List<T>
- IInternalEventBus implementation goes in Module 2A-11 Host, NOT here
- Add XML doc comments on all public types
- Use nullable reference types (enable in .csproj)
- Target: net8.0

## Deliverable
Complete C# source files for the `EMS.Gateway.Contracts` project.
```

---

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

## Module 2A-3 — `EMS.Gateway.ProtocolAdapter`

```
You are a senior .NET 8 engineer specializing in industrial OT protocols.
Modules 2A-1 and 2A-2 are complete.

## Task
Implement `EMS.Gateway.ProtocolAdapter` — the OT-side protocol drivers for the IIoT Gateway.
This is a Worker Service (.NET 8) hosting the following adapters:
- ModbusRtuAdapter (RS485 serial)
- ModbusTcpAdapter (TCP/IP)
- BACnetIpAdapter (UDP 47808)
- MqttNativeAdapter (MQTT subscribe, event-driven)

## Project references
- EMS.Gateway.Contracts
- EMS.Gateway.DeviceTemplate
- NuGet: FluentModbus (Modbus RTU + TCP)
- NuGet: MQTTnet (v4.x for MQTT Native Adapter)
- NuGet: System.IO.BACnet (for BACnet/IP)
- NuGet: Polly (retry + circuit breaker)
- NuGet: System.Threading.RateLimiting (.NET 7+)

## Classes to implement

### 1. ProtocolAdapterFactory (Factory Pattern)
```csharp
public class ProtocolAdapterFactory
{
    public IProtocolAdapter Create(DeviceTemplateDto template);
    // Returns: ModbusRtuAdapter | ModbusTcpAdapter | BACnetIpAdapter | MqttNativeAdapter
}
```

### 2. ModbusRtuAdapter : IProtocolAdapter
- Use FluentModbus `ModbusRtuClient`
- Open serial port once, keep persistent connection
- `PollAsync`: iterate CoalescedBlocks, send FC03/FC04 per block, parse raw bytes
- **Byte parsing per DataType**: float32 (IEEE 754, 2 registers), uint16, int16, int32, bool
- **Inter-frame silence**: enforce 3.5 char time gap between frames (calculate from baud rate)
- **Bus Arbitration WriteQueue**: `System.Threading.Channels.Channel<WriteCommand>` bounded
  - Single-threaded arbitration loop: check WriteQueue first, then Poll
  - If pending Write → execute Write (FC06/FC16) before next Poll cycle
- Retry: max 3 attempts with 50ms delay → on failure return `Quality=Bad`
- CRC error → retry → after 3 fails → `Quality=Bad, reason="CrcError"`

### 3. ModbusTcpAdapter : IProtocolAdapter
- Same coalescing and byte parsing as RTU
- Persistent TCP connection with exponential backoff reconnect (1s→2s→4s→30s max)
- No inter-frame silence needed (TCP handles framing)
- WriteQueue: same as RTU — Write before Poll

### 4. MqttNativeAdapter : IProtocolAdapter
- Subscribe to topics from `DeviceTemplateDto.MqttConnection.SubscribeTopic`
- Event-driven (push), NOT poll-based — `poll_cycle_ms = 0` means subscribe
- **LWT handling**: also subscribe to `LwtTopic`
  - Receive LwtPayloadOffline → immediately publish `MqttDeviceLwtOfflineEvent`
  - All tags for that device → `Quality=Bad, reason="LWT_Offline"`
- **Zero-allocation JSON parsing**: use `System.Text.Json.Utf8JsonReader` on `ReadOnlySpan<byte>`
  - Iterate `PayloadMappings` to extract multiple tags from one message
  - Use `ArrayPool<byte>` for temporary buffers
- **Token Bucket Rate Limiter** per device:
  - `System.Threading.RateLimiting.TokenBucketRateLimiter`
  - Config: `MaxMessagesPerSecond`, `BurstSize` from template
  - Exceeded: DROP DDATA, KEEP DBIRTH/DDEATH/LWT (never drop lifecycle messages)
  - Publish `MqttMessageDroppedEvent` when dropping

### 5. WriteQueue Bus Arbitration (shared for Modbus RTU/TCP)
```
Channel<WriteCommand> _writeQueue (Bounded, capacity=100, BoundedChannelFullMode.Wait)

Arbitration loop (single thread, per device bus):
  while (!cancellationToken.IsCancellationRequested):
    if _writeQueue.TryRead(out var writeCmd):
      await ExecuteWrite(writeCmd)  // FC06 or FC16
      writeCmd.CompletionSource.SetResult(success)
    else:
      await ExecutePollCycle()      // normal FC03/FC04 poll
    await Task.Delay(interFrameGapMs)
```

### 6. Error handling rules
- Device timeout (N retries exhausted) → return `Quality=Bad, reason="Timeout"`  
- CRC error → return `Quality=Bad, reason="CrcError"`
- Exception → return `Quality=Bad, reason="Exception:{message}"`
- After `DeviceUnresponsiveThresholdSeconds` of consecutive Bad → publish `DeviceUnresponsiveEvent`
- TCP disconnect → start reconnect loop (do NOT throw, return Bad quality during reconnect)

## Unit tests required
1. Coalescing integration: 50 scattered registers → verify N blocks with correct offsets
2. ModbusRtu: timeout returns `Quality=Bad` with correct reason
3. ModbusRtu: WriteQueue: Write executes before pending Poll when both queued
4. MqttNative: LWT offline message → all device tags become Bad immediately
5. MqttNative: rate limit exceeded → DDATA dropped, DBIRTH preserved
6. MqttNative: multi-tag payload → all tags extracted from single JSON message

Use FluentModbus test server or mock `IModbusClient` for RTU/TCP unit tests.

## Deliverable
Complete source with all adapters. Polly retry configured. All error paths return proper Quality.
```

---

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

## Module 2A-5 — `EMS.Gateway.EdgeRuleEngine`

```
You are a senior .NET 8 engineer. Modules 2A-1 through 2A-4 are complete.

## Task
Implement `EMS.Gateway.EdgeRuleEngine` — pure transformation engine that applies CT/PT
normalization and computes virtual tags using NCalc2 expressions. This is a Class Library.
NO I/O, NO side effects, NO state between batches (except stateful functions via IEdgeStateStore).

## Project references
- EMS.Gateway.Contracts
- NuGet: NCalc2

## Classes to implement

### 1. EdgeRuleEngine : IEdgeRuleEngine

**CT/PT Normalization:**
```csharp
double physical = rawValue * template.CtRatio * template.PtRatio * register.ScaleFactor;
```

**Pre-compile NCalc expressions (at startup and on config reload):**
```csharp
// Store as ImmutableDictionary<string, NCalc.Expression>
// Compile ONCE: new Expression(expressionString)
// Evaluate MANY: expression.Evaluate() with Parameters dict

public void PrecompileExpressions(IReadOnlyList<DeviceTemplateDto> templates)
{
    foreach (var template in templates)
        foreach (var vt in template.VirtualTagExpressions)
            _compiled[key] = new Expression(vt.Expression); // compile once
}
```

**Quality Propagation (strict rules):**
```
ALL input tags = Good  → virtual tag Quality = Good
ANY input tag  = Bad   → virtual tag Quality = Bad   (Bad takes priority)
ANY input tag  = Stale, NONE = Bad → virtual tag Quality = Stale
Division by zero / NaN result → Quality = Bad, reason = "NaN_DivisionByZero"
```

**Stateful virtual tag functions (IEdgeStateStore injection):**
```csharp
// Register custom NCalc EvaluateFunction callbacks:
expression.EvaluateFunction += (name, args) =>
{
    switch (name.ToUpper())
    {
        case "TOTALIZER":
            // args[0] = tag_name, args[1] = current_value, args[2] = dt_seconds
            // result = state[key] += value * dt
            args.Result = _stateStore.Accumulate(key, value, dt);
            break;
        case "ROLLING_AVG":
            // args[0] = tag_name, args[1] = current_value, args[2] = window_seconds
            args.Result = _stateStore.RollingAverage(key, value, windowSeconds);
            break;
        case "RATE":
            // args[0] = tag_name, args[1] = current_value
            // result = (current - previous) / dt
            args.Result = _stateStore.Rate(key, value, dt);
            break;
        case "ELAPSED_ON":
            // args[0] = tag_name, args[1] = current_value, args[2] = threshold
            args.Result = _stateStore.ElapsedOn(key, value > threshold);
            break;
    }
};
```

### 2. EdgeStateStore : IEdgeStateStore (new interface, define in Contracts)
- In-memory state per virtual tag key
- Persist to SQLite every 60s (separate table from LocalBuffer — same DB file is OK)
- Load from SQLite on startup (survive restart without resetting Totalizer)
- State isolated per virtual tag — no cross-tag side effects

### 3. ValidateExpression
```csharp
public ValidationResult ValidateExpression(string expression, IReadOnlyList<string> availableTags)
{
    // Try compile: new Expression(expression)
    // Extract variable names used in expression
    // Check each variable exists in availableTags
    // Return ValidationResult with list of missing tags
}
```

## Unit tests required
1. CT/PT normalization: raw=100, ct=200, pt=1, scale=0.1 → physical=2000.0
2. Virtual tag Good: both inputs Good → result Good
3. Virtual tag Bad: one input Bad → result Bad (even if other is Good)
4. Virtual tag Stale: one input Stale, none Bad → result Stale
5. Division by zero: expression `kW/kVA` where kVA=0 → Quality=Bad, reason=NaN
6. TOTALIZER: accumulates correctly across multiple poll cycles
7. RATE: correct delta/dt calculation
8. Expression validation: references non-existent tag → ValidationResult.IsValid=false
9. Compile once: PrecompileExpressions called once; Evaluate called 10,000 times — no recompile

## Deliverable
Pure transformation library. All functions pure except IEdgeStateStore. Full unit test coverage.
```

---

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

## Module 2A-7 — `EMS.Gateway.SparkplugEncoder`

```
You are a senior .NET 8 engineer with Sparkplug B protocol expertise.
Modules 2A-1 through 2A-6 are complete.

## Task
Implement `EMS.Gateway.SparkplugEncoder` — encodes enriched tag data into Sparkplug B Protobuf
payloads and publishes to Mosquitto (local MQTT broker) via MQTTnet. Manages Birth/Death lifecycle.
This is a Worker Service (.NET 8).

## Project references
- EMS.Gateway.Contracts
- NuGet: Eclipse.Tahu.Protobuf (official Sparkplug B library for .NET)
- NuGet: MQTTnet (v4.x)

## Classes to implement

### 1. SparkplugEncoder : ISparkplugEncoder, IHostedService

**Sequence number management:**
```csharp
// Monotonic 0–255, wrap around per Sparkplug B spec
private int _seqNumber = 0;
private int NextSeq() => Interlocked.Increment(ref _seqNumber) % 256;
```

**EncodeDData — DDATA payload:**
```csharp
// For each EnrichedTagDto in batch:
// Good quality → metric.SetValue(physicalValue), is_null = false
// Bad quality  → metric.is_null = true
//   PropertySet: { "quality": "Bad", "reason": tag.QualityReason, "timestamp_ns": ... }
// Stale quality → metric.is_null = true
//   PropertySet: { "quality": "Stale", "stale_since_ns": tag.TimestampUtcNs }
// IsSimulated = true → add PropertySet: { "is_simulated": true }
// Use tag.TimestampUtcNs as metric timestamp (actual poll time, NOT encode time)
```

**EncodeDBirth — DBIRTH payload (retain=true on Mosquitto):**
```csharp
// Include ALL metric definitions:
//   - name, data type, engineering unit
//   - PropertySet: { "ct_ratio": x, "pt_ratio": y, "scale_factor": z }
//   - For virtual tags: PropertySet: { "is_virtual": true, "expression": "..." }
// Include node metadata:
//   - firmware_version, hardware_id, machine_id, build_timestamp
// Publish with MqttQualityOfServiceLevel.AtLeastOnce + Retain=true
```

**EncodeDDeath — DDEATH payload:**
```csharp
// Minimal payload: just seq number and timestamp
// Publish with Retain=true (Mosquitto will overwrite DBIRTH retain)
```

**PublishAsync:**
```csharp
public async Task PublishAsync(SparkplugPayloadDto payload, CancellationToken ct)
{
    if (_mqttPublisher.IsConnected)
        await _mqttPublisher.PublishAsync(payload, ct);
    else
        await _localBuffer.EnqueueAsync(payload, ct); // fallback to buffer
}
```

### 2. MqttPublisher : IMqttPublisher
- Connect to Mosquitto localhost:1883 (configurable via appsettings)
- MQTTnet ManagedMqttClient for auto-reconnect
- QoS 1 (AtLeastOnce) for all DDATA/DBIRTH/DDEATH
- On disconnect: publish `MqttConnectionLostEvent` via `IInternalEventBus`
- On reconnect: publish `MqttConnectionRestoredEvent`
- Topic format: `spBv1.0/{group_id}/{message_type}/{device_id}`
  - DBIRTH: `spBv1.0/{group}/DBIRTH/{deviceId}` with retain=true
  - DDATA:  `spBv1.0/{group}/DDATA/{deviceId}` with retain=false
  - DDEATH: `spBv1.0/{group}/DDEATH/{deviceId}` with retain=true

**Subscribe and handle events:**
```
ConfigReloadRequestedEvent → re-publish DBIRTH for all devices
MqttConnectionRestoredEvent → trigger ILocalBuffer.ReplayAsync()
MqttConnectionLostEvent → switch to buffer-only mode
```

## Unit tests required
1. EncodeDData: Good tag → is_null=false, value set correctly
2. EncodeDData: Bad tag → is_null=true, PropertySet contains reason
3. EncodeDData: Stale tag → is_null=true, PropertySet contains stale_since_ns
4. EncodeDData: IsSimulated=true → PropertySet contains is_simulated=true
5. Sequence number: wraps 255→0 correctly
6. Timestamp: uses tag.TimestampUtcNs (not DateTime.UtcNow)
7. EncodeDBirth: contains all metric definitions + metadata
8. MQTT disconnected: PublishAsync → routes to ILocalBuffer.EnqueueAsync
9. MQTT reconnected: ILocalBuffer.ReplayAsync called

Mock IMqttPublisher and ILocalBuffer for unit tests.

## Deliverable
Complete SparkplugEncoder + MqttPublisher. Eclipse.Tahu.Protobuf used (not custom Protobuf).
```

---

## Module 2A-8 — `EMS.Gateway.LocalBuffer`

```
You are a senior .NET 8 engineer with expertise in embedded storage and reliability engineering.
Modules 2A-1 through 2A-7 are complete.

## Task
Implement `EMS.Gateway.LocalBuffer` — the reliability layer ensuring zero data loss during
MQTT outages. Buffers up to 72 hours of Sparkplug B payloads. This is a Worker Service (.NET 8).

## Project references
- EMS.Gateway.Contracts
- NuGet: Microsoft.Data.Sqlite

## Class to implement: LocalBufferService : ILocalBuffer, IHostedService

### Architecture: Two-layer write path (Flash-Wear protection)
```
Producer (SparkplugEncoder)
    ↓ EnqueueAsync
System.Threading.Channels.BoundedChannel<byte[]>  (RAM, capacity=10000)
    ↓ consumer loop (single Task)
tmpfs RAM buffer (/dev/shm/ems-buffer/)
    ↓ flush every sync_interval_ms (default 5000ms)
SQLite WAL mode (eMMC/SSD: /var/lib/ems-gateway/buffer.db)
```

### SQLite schema
```sql
CREATE TABLE IF NOT EXISTS buffer (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       TEXT NOT NULL,
    seq_number      INTEGER NOT NULL,
    timestamp_utc_ns INTEGER NOT NULL,
    payload         BLOB NOT NULL,
    enqueued_at     INTEGER NOT NULL,  -- Unix epoch seconds
    UNIQUE(device_id, seq_number, timestamp_utc_ns) -- deduplication key
);
CREATE INDEX IF NOT EXISTS idx_timestamp ON buffer(timestamp_utc_ns);
```

### EnqueueAsync
```csharp
// Use ArrayPool<byte> for payload bytes (zero GC allocation)
// Bounded channel: BoundedChannelFullMode.Wait (backpressure — don't drop)
// Channel consumer loop: batch write to SQLite (batch size = 100 or 500ms timeout)
// WAL checkpoint triggered by flush timer, not per-write
```

### ReplayAsync (rate-limited)
```csharp
// ONLY called when MqttConnectionRestoredEvent received
// Rate limit: FixedWindowRateLimiter at ReplayRateLimitPerSecond (default 500 msg/s)
// Read SQLite ORDER BY timestamp_utc_ns ASC, batch size 100
// Publish via IMqttPublisher — on fail: circuit breaker (3 consecutive fails → stop, wait)
// Delete successfully published records from SQLite
// Priority: replay DBIRTH messages before DDATA messages
```

### PruneAsync (every 15 minutes)
```csharp
// DELETE FROM buffer WHERE enqueued_at < (UtcNow - RetentionHours * 3600)
// Default RetentionHours = 72
```

### SQLite Corruption Auto-Recovery
```csharp
private async Task ExecuteWithCorruptionRecoveryAsync(Func<Task> operation)
{
    try { await operation(); }
    catch (SqliteException ex) when (
        ex.SqliteErrorCode == 11 ||  // SQLITE_CORRUPT
        ex.SqliteErrorCode == 26 ||  // SQLITE_NOTADB
        ex.SqliteErrorCode == 10)    // SQLITE_IOERR
    {
        // 1. SqliteConnection.ClearAllPools() — release all file handles
        // 2. File.Move(dbPath, $"{dbPath}.corrupted_{epoch}") — rename
        // 3. File.Delete(walPath), File.Delete(shmPath) — cleanup WAL
        // 4. InitializeDatabaseAsync() — create fresh empty DB
        // 5. Publish BufferCorruptedEvent with estimated lost records
        // 6. Log CRITICAL
        // 7. Reset _pendingCount = 0
    }
}
```

### Graceful shutdown (IHostApplicationLifetime)
```
1. Stop accepting new Enqueue requests
2. Drain Channel: wait for consumer loop to process all pending items
3. Flush remaining tmpfs → SQLite (WAL checkpoint)
4. Close SQLite connection cleanly
5. Signal completion
TimeoutStopSec=60 in systemd unit
```

## Configuration (IOptions<LocalBufferOptions>)
```csharp
public record LocalBufferOptions(
    string SqlitePath,            // /var/lib/ems-gateway/buffer.db
    string TmpfsMountPath,        // /dev/shm/ems-buffer
    int SyncIntervalMs,           // 5000
    int RetentionHours,           // 72
    int ChannelCapacity,          // 10000
    int ReplayRateLimitPerSecond, // 500
    int ReplayBurstSize           // 1000
);
```

## Unit tests required
1. Enqueue → SQLite: payload persisted after sync_interval
2. Replay: records replayed in timestamp order (ascending)
3. Replay: rate limiter enforces max messages per second
4. Prune: records older than RetentionHours deleted
5. Replay: DBIRTH replayed before DDATA for same device
6. Corruption recovery: SQLITE_CORRUPT → renamed file, fresh DB created, event published
7. Corruption recovery: recovery continues polling (no crash loop)
8. Graceful shutdown: all Channel items flushed before exit (no data loss)
9. Deduplication: same (device_id, seq, timestamp) not inserted twice

## Deliverable
Complete LocalBufferService. ArrayPool<byte> used. WAL mode enabled. Recovery tested.
```

---

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

## Module 2A-10 — `EMS.Gateway.CommandHandler` *(G3 only)*

```
You are a senior .NET 8 engineer specializing in industrial control security.
Modules 2A-1 through 2A-9 are complete.

## Task
Implement `EMS.Gateway.CommandHandler` — the G3 Command Channel handler that receives
DCMD messages from Business Server (via Mosquitto), validates them rigorously, and
executes Modbus write operations. This is the LAST ENFORCER — cannot be bypassed from IT.
This is a Worker Service (.NET 8), activated only when feature flag is enabled.

## Project references
- EMS.Gateway.Contracts
- EMS.Gateway.ProtocolAdapter
- NuGet: Microsoft.IdentityModel.Tokens (JWT validation)
- NuGet: System.IdentityModel.Tokens.Jwt

## Feature flag gate
```csharp
// Only registered if "CommandHandler:Enabled" = true in appsettings
// Host.cs: if (config["CommandHandler:Enabled"] == "true")
//              services.AddHostedService<CommandHandlerService>();
```

## Class to implement: CommandHandlerService : ICommandHandler, IHostedService

### DCMD flow with strict validation (fail-fast, all-or-nothing)

```
Step 1: Receive DCMD message
  Subscribe topic: spBv1.0/{group}/{gatewayId}/DCMD via Mosquitto
  Deserialize CommandRequestDto from Sparkplug B payload

Step 2: Validate JWT signature (LOCAL, no network call)
  Load Business Server public key from: /etc/ems-gateway/certs/bs-public.pem
  Verify JWT signature using RS256 or ES256 (configured)
  Verify JWT expiry (reject expired tokens even if signature valid)
  On fail → CommandResult.RejectedJwtInvalid → Audit Log → NACK

Step 3: Validate command whitelist (enforce from IDeviceTemplateRepository)
  Check: device_id exists in template
  Check: register_address in device's CommandWhitelist[]
  On fail → CommandResult.RejectedWhitelist → Audit Log → NACK

Step 4: Validate physical range
  Check: value within [WhitelistEntry.MinValue, WhitelistEntry.MaxValue]
  On fail → CommandResult.RejectedRangeExceeded → Audit Log → NACK

Step 5: Write to Gateway Audit Log BEFORE executing command
  Append to /var/log/ems-gateway/audit.ndjson:
  { "dcmd_id": ..., "ts": ..., "device_id": ..., "reg": ..., "value": ..., "status": "Executing" }

Step 6: Execute write via WriteQueue (Bus Arbitration)
  _protocolAdapter.WriteAsync(deviceId, registerAddress, value, ct)
  Write takes priority over ongoing Poll (via WriteQueue Channel)

Step 7: Update Audit Log with result
  { "dcmd_id": ..., "ts_complete": ..., "result": "Accepted|DeviceError" }

Step 8: Send ACK/NACK to Business Server
  Publish to: spBv1.0/{group}/{gatewayId}/DCMD_ACK
  Payload: { "dcmd_id": ..., "result": "Accepted", "ts": ... }
```

### For MQTT Native devices — Command ACK Tracker
```csharp
// MQTT devices receive commands asynchronously
// Register: _ackTracker.TrackCommand(dcmdId, deviceId, ackTopic, timeoutMs)
// MqttNativeAdapter calls: _ackTracker.ReceiveAck(topic, payload) on matching topic
// Timeout (default 5s): CommandResult.AckTimeout → Audit Log → NACK
```

### Audit Log format (NDJSON, append-only)
```json
{"ts":"2024-11-15T14:32:01.001Z","dcmd_id":"abc-123","device_id":"vfd-01",
 "register":1001,"value":45.0,"jwt_sub":"business-server","status":"Executing"}
{"ts":"2024-11-15T14:32:01.045Z","dcmd_id":"abc-123","result":"Accepted",
 "ack_received_at":"2024-11-15T14:32:01.043Z"}
```
- Append-only — NEVER modify existing entries
- logrotate weekly, keep 12 weeks
- Independent of network — never write to PostgreSQL directly

## G3 Safety banner notification
```csharp
// When CommandHandlerService starts:
// Log all whitelisted devices and registers at INFO level
// Publish startup event to IInternalEventBus so AdminService UI can show G3 banner
```

## Unit tests required
1. JWT invalid signature → RejectedJwtInvalid, Audit Log written, NACK sent
2. JWT expired → RejectedJwtInvalid (even with valid signature)
3. Device not in whitelist → RejectedWhitelist, no write executed
4. Register not in whitelist → RejectedWhitelist, no write executed
5. Value outside physical range → RejectedRangeExceeded, no write executed
6. Valid command → WriteAsync called with correct args, Audit Log has both entries
7. MQTT ACK tracker: ACK received within timeout → Accepted
8. MQTT ACK tracker: timeout → AckTimeout, NACK sent
9. Audit log: verify NDJSON format, append-only behavior across multiple commands

## IMPORTANT CONSTRAINTS
- Never call WriteAsync when validation fails (fail-fast, no partial execution)
- Audit Log written BEFORE WriteAsync (pre-execution record)
- Whitelist loaded from IDeviceTemplateRepository — NOT from network or config at runtime
- Feature flag check in Host, not in this class (class always enforces, never conditionally)

## Deliverable
Complete CommandHandlerService. Security-first design. All rejection paths produce Audit Log entries.
```

---

## Module 2A-11 — `EMS.Gateway.Host`

```
You are a senior .NET 8 engineer. All modules 2A-1 through 2A-10 are complete.

## Task
Implement `EMS.Gateway.Host` — the composition root that wires together ALL modules,
controls startup order, registers all DI bindings and IInternalEventBus subscribers,
and ensures graceful shutdown drains all data before process exit.
This is a Worker Host (.NET 8).

## Project references (all modules)
- EMS.Gateway.Contracts
- EMS.Gateway.DeviceTemplate
- EMS.Gateway.ProtocolAdapter
- EMS.Gateway.PollingEngine
- EMS.Gateway.EdgeRuleEngine
- EMS.Gateway.QualityChecker
- EMS.Gateway.SparkplugEncoder
- EMS.Gateway.LocalBuffer
- EMS.Gateway.AdminService
- EMS.Gateway.CommandHandler (conditional)
- NuGet: Serilog.Extensions.Hosting
- NuGet: Serilog.Sinks.Console + Serilog.Sinks.File

## Program.cs — Host builder

```csharp
var host = Host.CreateDefaultBuilder(args)
    .UseSystemd()                    // sd_notify integration
    .UseSerilog((ctx, cfg) =>        // structured JSON logging
    {
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .Enrich.WithProperty("machine_id", MachineId.Get())
           .Enrich.WithProperty("firmware_version", FirmwareVersion.Current);
    })
    .ConfigureServices((ctx, services) =>
    {
        // 1. Configuration (strongly-typed IOptions<T>)
        services.Configure<LocalBufferOptions>(ctx.Configuration.GetSection("LocalBuffer"));
        services.Configure<MqttOptions>(ctx.Configuration.GetSection("Mqtt"));
        services.Configure<NtpWatchdogOptions>(ctx.Configuration.GetSection("NtpWatchdog"));
        services.Configure<OtaOptions>(ctx.Configuration.GetSection("Ota"));
        // ... all other options

        // 2. Core singletons
        services.AddSingleton<IInternalEventBus, InternalEventBus>(); // Channel-based
        services.AddSingleton<IDeviceTemplateRepository, DeviceTemplateRepository>();
        services.AddSingleton<IEdgeStateStore, SqliteEdgeStateStore>();
        services.AddSingleton<ILocalBuffer, LocalBufferService>();
        services.AddSingleton<IMqttPublisher, MqttPublisher>();
        services.AddSingleton<IProtocolAdapter, ProtocolAdapterFactory>(); // factory resolves per device

        // 3. Transient/Scoped services
        services.AddSingleton<IEdgeRuleEngine, EdgeRuleEngine>();
        services.AddSingleton<IQualityChecker, QualityChecker>();
        services.AddSingleton<ISparkplugEncoder, SparkplugEncoder>();
        services.AddSingleton<ISoftwareWatchdog, SoftwareWatchdogService>();

        // 4. Hosted services (order matters — see startup sequence)
        services.AddHostedService<HardwareWatchdogWorker>();    // must start first
        services.AddHostedService<AdminServiceWorker>();
        services.AddHostedService<LocalBufferService>();
        services.AddHostedService<MqttPublisher>();
        services.AddHostedService<PollingEngine>();
        services.AddHostedService<SparkplugEncoderWorker>();

        // 5. Conditional G2 — Config Channel receiver (in PollingEngine or AdminService)
        if (ctx.Configuration.GetValue<bool>("Features:ConfigChannel:Enabled"))
            services.AddHostedService<ConfigChannelReceiver>();

        // 6. Conditional G3 — Command Handler
        if (ctx.Configuration.GetValue<bool>("Features:CommandHandler:Enabled"))
            services.AddHostedService<CommandHandlerService>();

        // 7. Health checks + Prometheus
        services.AddHealthChecks()
            .AddCheck<NtpHealthCheck>("ntp")
            .AddCheck<MqttHealthCheck>("mqtt")
            .AddCheck<BufferHealthCheck>("buffer")
            .AddCheck<RtcHealthCheck>("rtc");

        services.AddPrometheusMetrics(); // extension method
    })
    .ConfigureWebHostDefaults(web =>
    {
        web.UseKestrel(o => o.ListenAnyIP(8080));  // UI + health endpoint
        web.UseStartup<WebStartup>();
    })
    .Build();
```

## IInternalEventBus implementation (InternalEventBus.cs)

```csharp
// Channel-based, type-safe, lock-free, no God Object
public class InternalEventBus : IInternalEventBus
{
    // One BoundedChannel per event type, created on first subscribe
    private readonly ConcurrentDictionary<Type, object> _channels = new();

    public Task PublishAsync<TEvent>(TEvent e, CancellationToken ct)
    {
        var channel = GetOrCreate<TEvent>();
        return channel.Writer.WriteAsync(e, ct).AsTask();
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct)
    {
        var channel = GetOrCreate<TEvent>();
        return channel.Reader.ReadAllAsync(ct);
    }

    private Channel<TEvent> GetOrCreate<TEvent>()
        => (Channel<TEvent>)_channels.GetOrAdd(typeof(TEvent),
            _ => Channel.CreateBounded<TEvent>(
                new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait }));
}
```

## IInternalEventBus subscriber wiring (in WebStartup or Host startup)

```csharp
// Register ALL subscribers at startup — NOT in constructors
// Each subscriber is a BackgroundService Task:

// MetricsBatchReadyEvent → EdgeRuleEngine → QualityChecker → SparkplugEncoder
Task.Run(() => ProcessPipelineAsync(ct));

// MqttConnectionRestoredEvent → LocalBuffer.ReplayAsync()
Task.Run(() => HandleReconnectAsync(ct));

// ConfigReloadRequestedEvent → PollingEngine.RestartAsync() + EdgeRuleEngine.Recompile()
Task.Run(() => HandleConfigReloadAsync(ct));

// NtpDriftExceededEvent → annotate Stale if drift > 30s
// RtcInvalidEvent → all tags Stale
// BufferCorruptedEvent → AdminService UI alert
// TagQualityDegradedEvent → SoftwareWatchdog metrics
```

## Startup order (CRITICAL — enforced via IHostedLifecycleService)

```
1. HardwareWatchdogWorker    — start immediately, must not block other services
2. RtcManagerService         — validate RTC before any polling begins
3. LocalBufferService        — SQLite must be ready before encoder publishes
4. MqttPublisher             — connect to Mosquitto, DBIRTH published when ready
5. PollingEngine             — start polling only after buffer and MQTT ready
6. SparkplugEncoderWorker    — start after pipeline ready
7. AdminServiceWorker        — health endpoints exposed last
8. CommandHandlerService     — G3 conditional, last to start
```

## Graceful shutdown sequence (reverse order, TimeoutStopSec=60)

```
1. Stop accepting new DCMD / Config Channel messages
2. Stop PollingEngine — no new poll cycles
3. Drain IInternalEventBus MetricsBatchReadyEvent Channel
4. SparkplugEncoder publishes DDEATH for all devices
5. Drain LocalBuffer Channel → SQLite WAL checkpoint
6. Disconnect MQTT cleanly (MQTT DISCONNECT packet)
7. Stop AdminService HTTP endpoint
8. Stop HardwareWatchdogWorker (watchdog expires → hardware reset is expected behavior on crash)
```

## appsettings.json template (full production config)

```json
{
  "Gateway": {
    "MachineId": "gw-line-a-001",
    "GroupId": "factory-hanoi",
    "FirmwareBuildUtc": "2024-11-01T00:00:00Z"
  },
  "Mqtt": {
    "BrokerHost": "localhost",
    "BrokerPort": 1883,
    "UseTls": false
  },
  "LocalBuffer": {
    "SqlitePath": "/var/lib/ems-gateway/buffer.db",
    "TmpfsMountPath": "/dev/shm/ems-buffer",
    "SyncIntervalMs": 5000,
    "RetentionHours": 72,
    "ChannelCapacity": 10000,
    "ReplayRateLimitPerSecond": 500,
    "ReplayBurstSize": 1000
  },
  "NtpWatchdog": {
    "DriftAlertThresholdSeconds": 5,
    "DriftStaleThresholdSeconds": 30,
    "CheckIntervalSeconds": 30,
    "HwclockSyncIntervalMinutes": 15
  },
  "Cert": {
    "CertPath": "/etc/mosquitto/certs/gw.crt",
    "KeyPath": "/etc/mosquitto/certs/gw.key",
    "CaUrl": "https://business-server:8443",
    "RenewDaysBeforeExpiry": 30
  },
  "Features": {
    "ConfigChannel": { "Enabled": false },
    "CommandHandler": { "Enabled": false },
    "MockDataMode": { "Enabled": false },
    "Ota": { "Enabled": false, "Strategy": "InPlaceAtomic" }
  },
  "Watchdog": {
    "DevicePath": "/dev/watchdog",
    "HeartbeatIntervalSeconds": 30,
    "SoftwareWatchdogCheckIntervalSeconds": 10
  },
  "HealthChecks": { "Port": 8080 },
  "Prometheus": { "Port": 9090 },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "/var/log/ems-gateway/app-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7
        }
      }
    ]
  }
}
```

## Integration test required (in EMS.IIoTGateway.IntegrationTests)

```csharp
// Full pipeline integration test using MockProtocolAdapter + in-memory Mosquitto (MQTTnet.Server):
// 1. Start host with MockDataMode:Enabled=true
// 2. Inject mock tag values: kW_total=842.3 (Good), V_L1=0 (→ Bad after range check)
// 3. Verify: DBIRTH published on startup
// 4. Verify: DDATA published with kW_total.is_null=false, V_L1.is_null=true
// 5. Simulate MQTT disconnect → values accumulate in LocalBuffer
// 6. Reconnect → replay sent in timestamp order
// 7. Graceful shutdown: DDEATH published, no data loss confirmed
```

## Deliverable
Complete Host wiring. All services registered. Startup order enforced.
Serilog with machine_id and firmware_version on every log entry.
Full integration test passing.
```

---

## Phụ lục — Thứ tự phụ thuộc và kiểm tra

### Dependency graph

```
2A-1 Contracts
  └─► 2A-2 DeviceTemplate
        └─► 2A-3 ProtocolAdapter
              └─► 2A-4 PollingEngine
                    └─► 2A-5 EdgeRuleEngine
                          └─► 2A-6 QualityChecker
                                └─► 2A-7 SparkplugEncoder
                                      └─► 2A-8 LocalBuffer
                                            └─► 2A-9 AdminService
                                                  └─► 2A-10 CommandHandler (G3)
                                                        └─► 2A-11 Host
```

### Checklist trước khi chuyển module tiếp theo

| Bước | Kiểm tra |
|---|---|
| Sau 2A-1 | `dotnet build EMS.Gateway.Contracts` → 0 errors, 0 warnings |
| Sau 2A-2 | `dotnet test EMS.Gateway.DeviceTemplate.Tests` → all pass |
| Sau 2A-3 | `dotnet test EMS.Gateway.ProtocolAdapter.Tests` → all pass |
| Sau 2A-4 | `dotnet test EMS.Gateway.PollingEngine.Tests` → all pass |
| Sau 2A-5 | `dotnet test EMS.Gateway.EdgeRuleEngine.Tests` → all pass |
| Sau 2A-6 | `dotnet test EMS.Gateway.QualityChecker.Tests` → all pass |
| Sau 2A-7 | `dotnet test EMS.Gateway.SparkplugEncoder.Tests` → all pass |
| Sau 2A-8 | `dotnet test EMS.Gateway.LocalBuffer.Tests` → all pass |
| Sau 2A-9 | `dotnet build EMS.Gateway.AdminService` → 0 errors |
| Sau 2A-10 | `dotnet test EMS.Gateway.CommandHandler.Tests` → all pass |
| Sau 2A-11 | `dotnet test EMS.IIoTGateway.IntegrationTests` → all pass |

### NuGet packages summary

| Package | Version | Dùng trong |
|---|---|---|
| FluentModbus | 5.x | 2A-3 |
| MQTTnet | 4.3.x | 2A-3, 2A-7 |
| System.IO.BACnet | latest | 2A-3 |
| Polly | 8.x | 2A-3 |
| System.Threading.RateLimiting | built-in .NET 7+ | 2A-3, 2A-8 |
| NCalc2 | 2.x | 2A-2, 2A-5 |
| FluentValidation | 11.x | 2A-2 |
| Microsoft.Data.Sqlite | 8.x | 2A-8 |
| Eclipse.Tahu.Protobuf | latest | 2A-7 |
| Microsoft.IdentityModel.Tokens | 7.x | 2A-10 |
| System.IdentityModel.Tokens.Jwt | 7.x | 2A-10 |
| prometheus-net.AspNetCore | 8.x | 2A-9 |
| Serilog.Extensions.Hosting | 8.x | 2A-11 |
| Serilog.Sinks.Console + File | 5.x | 2A-11 |

---

*Phiên bản 1.0 — AI Coding Prompts cho toàn bộ 11 module Tầng 2A · Dựa trên EMS_IIoTGateway_Tier2A_Module_Decomposition_v1.8 · Mỗi prompt tự chứa đủ context · Thứ tự implement: 2A-1 → 2A-11*
