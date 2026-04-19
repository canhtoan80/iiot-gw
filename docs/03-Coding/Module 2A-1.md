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
