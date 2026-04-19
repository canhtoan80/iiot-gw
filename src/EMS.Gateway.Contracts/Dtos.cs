using System.Text.Json;

namespace EMS.Gateway.Contracts;

/// <summary>
/// Represents a complete device template configuration.
/// </summary>
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
    ModbusCoalescingConfig? MqttNativeConfig, // The prompt used this name, but it might be a typo for ModbusCoalescingConfig
    MqttNativeConnectionDto? MqttConnection,
    RateLimitConfig? RateLimit
);

/// <summary>
/// Definition of a single register or tag to be polled from a device.
/// </summary>
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

/// <summary>
/// A block of registers optimized for a single protocol read operation.
/// </summary>
public record CoalescedBlockDto(
    int StartAddress,
    int Count,
    IReadOnlyList<(string TagName, int Offset, string DataType)> Tags
);

/// <summary>
/// Definition of a virtual tag calculated at the edge.
/// </summary>
public record VirtualTagExpressionDto(
    string TagName,
    string Expression,
    string Unit,
    double RangeMin,
    double RangeMax
);

/// <summary>
/// Entry in the command whitelist for a device.
/// </summary>
public record CommandWhitelistEntryDto(
    string TagName,
    int RegisterAddress,
    string FunctionCode,
    double MinValue,
    double MaxValue,
    string Description
);

/// <summary>
/// Configuration for Modbus register coalescing.
/// </summary>
public record ModbusCoalescingConfig(
    bool Enabled,
    int MaxGapWords,
    int MaxRegistersPerBlock
);

/// <summary>
/// Connection details for a native MQTT device.
/// </summary>
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

/// <summary>
/// Mapping for a specific tag within an MQTT payload.
/// </summary>
public record PayloadMappingDto(
    string TagName,
    string JsonPath,
    string DataType
);

/// <summary>
/// Configuration for rate limiting communications.
/// </summary>
public record RateLimitConfig(
    int MaxMessagesPerSecond,
    int BurstSize
);

/// <summary>
/// Raw tag data as polled from the device.
/// </summary>
public record RawTagDto(
    string TagName,
    double? RawValue,
    long TimestampUtcNs,
    TagQuality Quality,
    string? QualityReason
);

/// <summary>
/// A batch of raw tag data from a single polling cycle of a device.
/// </summary>
public record RawTagBatch(
    string DeviceId,
    IReadOnlyList<RawTagDto> Tags,
    long PollTimestampUtcNs
);

/// <summary>
/// Enriched tag data after processing and quality checks.
/// </summary>
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

/// <summary>
/// A batch of enriched tag data ready for encoding or local storage.
/// </summary>
public record EnrichedTagBatch(
    string DeviceId,
    IReadOnlyList<EnrichedTagDto> Tags,
    long BatchTimestampUtcNs
);

/// <summary>
/// A Sparkplug B message payload.
/// </summary>
public record SparkplugPayloadDto(
    string DeviceId,
    SparkplugMessageType MessageType,
    byte[] ProtobufPayload,
    long TimestampUtcNs,
    int SeqNumber
);

/// <summary>
/// Request to execute a command on a device.
/// </summary>
public record CommandRequestDto(
    string DcmdId,
    string DeviceId,
    int RegisterAddress,
    object Value,
    string JwtToken,
    long TimestampUtcNs
);

/// <summary>
/// Overall health status of the gateway.
/// </summary>
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

/// <summary>
/// Health status of a flash storage device.
/// </summary>
public record FlashHealthDto(
    string DevicePath,
    FlashDeviceType DeviceType,
    int LifeTimeUsedPercent,
    PreEolStatus PreEolStatus,
    DateTime LastChecked
);

/// <summary>
/// Status of a TLS certificate.
/// </summary>
public record CertStatusDto(
    string CertPath,
    DateTime ExpiryDate,
    int DaysRemaining,
    bool IsRenewing
);

/// <summary>
/// Result of a configuration reload operation.
/// </summary>
public record ReloadResult(bool Success, string? ErrorMessage, string? BackupConfigHash);

/// <summary>
/// Result of a validation check.
/// </summary>
public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
