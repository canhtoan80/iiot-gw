namespace EMS.Gateway.Contracts;

/// <summary>
/// Marker interface for all domain events in the gateway.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets the date and time when the event occurred.
    /// </summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Event fired when a batch of enriched metrics is ready for further processing.
/// </summary>
public record MetricsBatchReadyEvent(EnrichedTagBatch Batch, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a tag's quality has degraded.
/// </summary>
public record TagQualityDegradedEvent(string DeviceId, string TagName, TagQuality From, TagQuality To, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a device becomes unresponsive after multiple polling attempts.
/// </summary>
public record DeviceUnresponsiveEvent(string DeviceId, int ConsecutiveFailures, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when the connection to the MQTT broker is lost.
/// </summary>
public record MqttConnectionLostEvent(string BrokerAddress, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when the connection to the MQTT broker is restored.
/// </summary>
public record MqttConnectionRestoredEvent(string BrokerAddress, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a request to reload configurations is received.
/// </summary>
public record ConfigReloadRequestedEvent(string NewConfigHash, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a configuration reload fails and a rollback is performed.
/// </summary>
public record ConfigRolledBackEvent(string Reason, double BadQualityPercent, IReadOnlyList<string> AffectedDevices, string RolledBackToHash, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when the NTP time drift exceeds the allowed threshold.
/// </summary>
public record NtpDriftExceededEvent(TimeSpan Drift, double ThresholdSeconds, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when the hardware RTC is detected to be invalid.
/// </summary>
public record RtcInvalidEvent(DateTime DetectedTime, DateTime FirmwareBuildTime, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when the hardware RTC is restored to a valid state.
/// </summary>
public record RtcRestoredEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a buffer corruption is detected.
/// </summary>
public record BufferCorruptedEvent(int EstimatedLostRecords, string CorruptedFilePath, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when hardware degradation is detected.
/// </summary>
public record HardwareDegradedEvent(string Component, DegradedSeverity Severity, string Message, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when an MQTT message is dropped due to rate limiting or buffer overflow.
/// </summary>
public record MqttMessageDroppedEvent(string DeviceId, int DroppedCount, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a device goes offline according to its MQTT LWT (Last Will and Testament).
/// </summary>
public record MqttDeviceLwtOfflineEvent(string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a device comes online according to its MQTT LWT.
/// </summary>
public record MqttDeviceLwtOnlineEvent(string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Event fired when a command is received from the business server (G3).
/// </summary>
public record CommandReceivedEvent(string DcmdId, string DeviceId, DateTimeOffset OccurredAt) : IDomainEvent;
