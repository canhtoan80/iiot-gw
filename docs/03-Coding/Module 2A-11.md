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
