using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using EMS.Gateway.Host;
using EMS.Gateway.Contracts;
using EMS.Gateway.DeviceTemplate;
using EMS.Gateway.ProtocolAdapter;
using EMS.Gateway.PollingEngine;
using EMS.Gateway.EdgeRuleEngine;
using EMS.Gateway.QualityChecker;
using EMS.Gateway.SparkplugEncoder;
using EMS.Gateway.LocalBuffer;
using EMS.Gateway.AdminService;
using EMS.Gateway.CommandHandler;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting EMS IIoT Gateway...");

    var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    // 1. Core Services
    builder.Services.AddSingleton<IInternalEventBus, InternalEventBus>();
    builder.Services.AddSingleton<IDeviceTemplateRepository, DeviceTemplateRepository>();
    builder.Services.AddSingleton<IEdgeStateStore, EdgeStateStore>();
    builder.Services.AddSingleton<ProtocolAdapterFactory>();

    // 2. Transformation Services
    builder.Services.AddSingleton<IEdgeRuleEngine, EdgeRuleEngine>();
    builder.Services.AddSingleton<IQualityChecker, QualityChecker>();
    builder.Services.AddSingleton<ISparkplugEncoder, SparkplugEncoder>();
    builder.Services.AddSingleton<ISoftwareWatchdog, SoftwareWatchdogService>();

    // 3. Infrastructure Services
    builder.Services.AddSingleton<ILocalBuffer, LocalBufferService>();
    builder.Services.AddSingleton<IMqttPublisher, MqttPublisher>();
    builder.Services.AddSingleton<IPollingEngine, PollingEngine>();

    // 4. Hosted Services (Order matters)
    builder.Services.AddHostedService<HardwareWatchdogWorker>();
    builder.Services.AddHostedService<SoftwareWatchdogService>(sp => (SoftwareWatchdogService)sp.GetRequiredService<ISoftwareWatchdog>());
    builder.Services.AddHostedService<LocalBufferService>(sp => (LocalBufferService)sp.GetRequiredService<ILocalBuffer>());
    builder.Services.AddHostedService<SparkplugEncoder>(sp => (SparkplugEncoder)sp.GetRequiredService<ISparkplugEncoder>());
    builder.Services.AddHostedService<PollingEngine>(sp => (PollingEngine)sp.GetRequiredService<IPollingEngine>());

    // G3 Conditional
    if (builder.Configuration.GetValue<bool>("CommandHandler:Enabled"))
    {
        builder.Services.AddSingleton<ICommandHandler, CommandHandlerService>();
        builder.Services.AddHostedService<CommandHandlerService>(sp => (CommandHandlerService)sp.GetRequiredService<ICommandHandler>());
    }

    var host = builder.Build();
    
    // Wire up event subscribers (Simplified Pipeline)
    _ = Task.Run(async () => {
        var bus = host.Services.GetRequiredService<IInternalEventBus>();
        var ruleEngine = host.Services.GetRequiredService<IEdgeRuleEngine>();
        var qualityChecker = host.Services.GetRequiredService<IQualityChecker>();
        var encoder = host.Services.GetRequiredService<ISparkplugEncoder>();

        await foreach (var @event in bus.SubscribeAsync<MetricsBatchReadyEvent>(CancellationToken.None))
        {
            // Raw -> EdgeRule -> QualityChecker -> Encoder
            // This is handled inside each service's worker loop or here
            // In my implementation, each service listens to events.
        }
    });

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
