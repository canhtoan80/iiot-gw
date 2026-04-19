namespace EMS.Gateway.Contracts;

/// <summary>
/// Repository for managing device templates and configurations.
/// </summary>
public interface IDeviceTemplateRepository
{
    /// <summary>
    /// Gets a device template by its unique identifier.
    /// </summary>
    DeviceTemplateDto? GetTemplate(string deviceId);

    /// <summary>
    /// Gets all registered device templates.
    /// </summary>
    IReadOnlyList<DeviceTemplateDto> GetAllTemplates();

    /// <summary>
    /// Reloads the device configurations from a JSON string.
    /// </summary>
    Task<ReloadResult> ReloadAsync(string newConfigJson);

    /// <summary>
    /// Rolls back to the last known good configuration backup.
    /// </summary>
    Task<bool> RollbackToBackupAsync();

    /// <summary>
    /// Commits the current configuration, cancelling any pending auto-rollback.
    /// </summary>
    Task CommitConfigAsync();

    /// <summary>
    /// Gets the command whitelist for a specific device (G3).
    /// </summary>
    IReadOnlyList<CommandWhitelistEntryDto> GetCommandWhitelist(string deviceId);
}

/// <summary>
/// Adapter for communicating with devices using specific industrial protocols.
/// </summary>
public interface IProtocolAdapter
{
    /// <summary>
    /// Polls a batch of tags from a device.
    /// </summary>
    Task<RawTagBatch> PollAsync(DeviceTemplateDto device,
        IReadOnlyList<CoalescedBlockDto> coalescedBlocks,
        CancellationToken ct);

    /// <summary>
    /// Writes a value to a device register (G3).
    /// </summary>
    Task<bool> WriteAsync(string deviceId, int registerAddress,
        object value, CancellationToken ct);
}

/// <summary>
/// Engine responsible for scheduling and managing device polling.
/// </summary>
public interface IPollingEngine
{
    /// <summary>
    /// Starts the polling engine.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Stops the polling engine.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Gets a snapshot of the last polled tags for a device.
    /// </summary>
    EnrichedTagBatch GetTagSnapshot(string deviceId);
}

/// <summary>
/// Engine for processing raw tag data and applying edge rules/virtual tags.
/// </summary>
public interface IEdgeRuleEngine
{
    /// <summary>
    /// Processes a batch of raw tags and returns an enriched batch.
    /// </summary>
    EnrichedTagBatch ProcessAsync(RawTagBatch rawBatch);

    /// <summary>
    /// Precompiles expressions for a set of device templates.
    /// </summary>
    void PrecompileExpressions(IReadOnlyList<DeviceTemplateDto> templates);

    /// <summary>
    /// Validates an expression against available tags.
    /// </summary>
    ValidationResult ValidateExpression(string expression, IReadOnlyList<string> availableTags);
}

/// <summary>
/// Checker for validating tag quality based on defined rules.
/// </summary>
public interface IQualityChecker
{
    /// <summary>
    /// Checks and updates the quality of tags in an enriched batch.
    /// </summary>
    EnrichedTagBatch CheckAsync(EnrichedTagBatch enrichedBatch);
}

/// <summary>
/// Encoder for Sparkplug B payloads.
/// </summary>
public interface ISparkplugEncoder
{
    /// <summary>
    /// Encodes enriched tag data into a Sparkplug B DDATA payload.
    /// </summary>
    SparkplugPayloadDto EncodeDData(EnrichedTagBatch enrichedBatch);

    /// <summary>
    /// Encodes a device birth certificate into a Sparkplug B DBIRTH payload.
    /// </summary>
    SparkplugPayloadDto EncodeDBirth(string deviceId, DeviceTemplateDto template,
        string firmwareVersion, string hardwareId);

    /// <summary>
    /// Encodes a device death certificate into a Sparkplug B DDEATH payload.
    /// </summary>
    SparkplugPayloadDto EncodeDDeath(string deviceId);

    /// <summary>
    /// Publishes a Sparkplug B payload.
    /// </summary>
    Task PublishAsync(SparkplugPayloadDto payload, CancellationToken ct);
}

/// <summary>
/// Buffer for storing Sparkplug B payloads locally when connection is lost.
/// </summary>
public interface ILocalBuffer
{
    /// <summary>
    /// Adds a payload to the local buffer.
    /// </summary>
    Task EnqueueAsync(SparkplugPayloadDto payload, CancellationToken ct);

    /// <summary>
    /// Replays stored payloads from the buffer to the publisher.
    /// </summary>
    Task ReplayAsync(CancellationToken ct);

    /// <summary>
    /// Prunes old records from the buffer to manage disk space.
    /// </summary>
    Task PruneAsync(CancellationToken ct);

    /// <summary>
    /// Gets the number of pending records in the buffer.
    /// </summary>
    long GetPendingCount();
}

/// <summary>
/// Publisher for MQTT messages.
/// </summary>
public interface IMqttPublisher
{
    /// <summary>
    /// Gets a value indicating whether the publisher is connected to the broker.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Publishes a payload to the MQTT broker.
    /// </summary>
    Task<bool> PublishAsync(SparkplugPayloadDto payload, CancellationToken ct);

    /// <summary>
    /// Connects to the MQTT broker.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Disconnects from the MQTT broker.
    /// </summary>
    Task DisconnectAsync();
}

/// <summary>
/// Administrative service for gateway health and maintenance.
/// </summary>
public interface IAdminService
{
    /// <summary>
    /// Gets the current health status of the gateway.
    /// </summary>
    Task<GatewayHealthDto> GetHealthAsync();

    /// <summary>
    /// Gets the current NTP time drift.
    /// </summary>
    Task<TimeSpan> GetNtpDriftAsync();

    /// <summary>
    /// Triggers a heartbeat for the hardware/software watchdog.
    /// </summary>
    void TriggerWatchdogHeartbeat();
}

/// <summary>
/// Handler for executing commands on devices (G3).
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Handles a command request.
    /// </summary>
    Task<CommandResult> HandleAsync(CommandRequestDto request, CancellationToken ct);
}

/// <summary>
/// Watchdog for monitoring internal worker health.
/// </summary>
public interface ISoftwareWatchdog
{
    /// <summary>
    /// Reports that a worker is still alive.
    /// </summary>
    void ReportAlive(string workerId);

    /// <summary>
    /// Checks if any worker is currently unhealthy.
    /// </summary>
    bool IsAnyWorkerUnhealthy();
}

/// <summary>
/// Internal event bus for decoupled communication between modules.
/// </summary>
public interface IInternalEventBus
{
    /// <summary>
    /// Publishes a domain event to the bus.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : IDomainEvent;

    /// <summary>
    /// Subscribes to events of a specific type.
    /// </summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct)
        where TEvent : IDomainEvent;
}

/// <summary>
/// Monitor for flash storage health.
/// </summary>
public interface IFlashHealthMonitor
{
    /// <summary>
    /// Gets the current health status of the flash storage.
    /// </summary>
    Task<FlashHealthDto> GetHealthAsync();
}

/// <summary>
/// Manager for the hardware Real-Time Clock (RTC).
/// </summary>
public interface IRtcManager
{
    /// <summary>
    /// Synchronizes the hardware RTC from the system time.
    /// </summary>
    Task SyncRtcFromSystemAsync();

    /// <summary>
    /// Reads the current time from the hardware RTC.
    /// </summary>
    Task<DateTime> ReadRtcTimeAsync();

    /// <summary>
    /// Checks if the RTC time is considered valid.
    /// </summary>
    Task<bool> IsRtcTimeValidAsync();
}

/// <summary>
/// Manager for automatic certificate renewal.
/// </summary>
public interface IAutoRenewCertManager
{
    /// <summary>
    /// Checks the expiration status of the current certificate.
    /// </summary>
    Task<CertStatusDto> CheckCertExpiryAsync();

    /// <summary>
    /// Renews the certificate if it's close to expiration.
    /// </summary>
    Task<bool> RenewIfNeededAsync(CancellationToken ct);
}

/// <summary>
/// Store for stateful edge calculations (totalizers, averages, etc.).
/// </summary>
public interface IEdgeStateStore
{
    /// <summary>
    /// Accumulates a value over time (integral).
    /// </summary>
    double Accumulate(string key, double value, double dtSeconds);

    /// <summary>
    /// Calculates a rolling average for a tag.
    /// </summary>
    double RollingAverage(string key, double value, double windowSeconds);

    /// <summary>
    /// Calculates the rate of change for a tag.
    /// </summary>
    double Rate(string key, double value, double dtSeconds);

    /// <summary>
    /// Calculates the elapsed time a condition has been true.
    /// </summary>
    double ElapsedOn(string key, bool condition);

    /// <summary>
    /// Persists the current state to non-volatile storage.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads the state from non-volatile storage.
    /// </summary>
    Task LoadAsync();
}
