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
