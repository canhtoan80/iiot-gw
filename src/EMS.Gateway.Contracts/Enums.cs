namespace EMS.Gateway.Contracts;

/// <summary>
/// Represents the quality of a tag value.
/// </summary>
public enum TagQuality
{
    /// <summary>
    /// Value is reliable and within normal operating parameters.
    /// </summary>
    Good,

    /// <summary>
    /// Value is unreliable due to sensor error, out of range, or communication failure.
    /// </summary>
    Bad,

    /// <summary>
    /// Value is old and has not been updated within the expected timeframe.
    /// </summary>
    Stale
}

/// <summary>
/// Supported industrial protocols for device communication.
/// </summary>
public enum ProtocolType
{
    /// <summary>
    /// Modbus RTU (Serial).
    /// </summary>
    ModbusRTU,

    /// <summary>
    /// Modbus TCP (Ethernet).
    /// </summary>
    ModbusTCP,

    /// <summary>
    /// BACnet IP.
    /// </summary>
    BACnetIP,

    /// <summary>
    /// Native MQTT devices publishing non-Sparkplug payloads.
    /// </summary>
    MqttNative,

    /// <summary>
    /// Custom protocol implementation.
    /// </summary>
    Custom
}

/// <summary>
/// Sparkplug B message types.
/// </summary>
public enum SparkplugMessageType
{
    /// <summary>
    /// Device Birth certificate.
    /// </summary>
    DBirth,

    /// <summary>
    /// Device Data message.
    /// </summary>
    DData,

    /// <summary>
    /// Device Death certificate.
    /// </summary>
    DDeath
}

/// <summary>
/// Result of a device command execution.
/// </summary>
public enum CommandResult
{
    /// <summary>
    /// Command was accepted and processed.
    /// </summary>
    Accepted,

    /// <summary>
    /// Command was rejected because it is not in the whitelist.
    /// </summary>
    RejectedWhitelist,

    /// <summary>
    /// Command value was outside the allowed range.
    /// </summary>
    RejectedRangeExceeded,

    /// <summary>
    /// JWT token provided with the command was invalid or expired.
    /// </summary>
    RejectedJwtInvalid,

    /// <summary>
    /// An error occurred on the device while processing the command.
    /// </summary>
    DeviceError,

    /// <summary>
    /// Command acknowledgement was not received within the timeout period.
    /// </summary>
    AckTimeout
}

/// <summary>
/// Types of flash storage devices used in edge IPCs.
/// </summary>
public enum FlashDeviceType
{
    /// <summary>
    /// Embedded Multi-Media Card.
    /// </summary>
    eMMC,

    /// <summary>
    /// Non-Volatile Memory Express.
    /// </summary>
    NVMe,

    /// <summary>
    /// Serial ATA SSD.
    /// </summary>
    SATA
}

/// <summary>
/// Pre-End-of-Life status for flash storage devices.
/// </summary>
public enum PreEolStatus
{
    /// <summary>
    /// Device is in normal operating condition.
    /// </summary>
    Normal,

    /// <summary>
    /// Device is showing signs of wear.
    /// </summary>
    Warning,

    /// <summary>
    /// Device is near the end of its life.
    /// </summary>
    Urgent,

    /// <summary>
    /// Device has exceeded its rated end-of-life.
    /// </summary>
    ExceededEol
}

/// <summary>
/// Severity of hardware degradation.
/// </summary>
public enum DegradedSeverity
{
    /// <summary>
    /// Non-critical degradation, system can continue to operate.
    /// </summary>
    Warning,

    /// <summary>
    /// Critical degradation, immediate action required.
    /// </summary>
    Critical
}
