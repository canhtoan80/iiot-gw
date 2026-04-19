using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EMS.Gateway.SparkplugEncoder;
using EMS.Gateway.Contracts;

var builder = Host.CreateApplicationBuilder(args);

// Register dependencies (placeholders for missing ones)
builder.Services.AddSingleton<IMqttPublisher, MqttPublisher>();
builder.Services.AddSingleton<ISparkplugEncoder, SparkplugEncoder>();
builder.Services.AddHostedService<SparkplugEncoder>(sp => (SparkplugEncoder)sp.GetRequiredService<ISparkplugEncoder>());

// ILocalBuffer and IInternalEventBus would be registered from their respective modules
// builder.Services.AddSingleton<ILocalBuffer, LocalBuffer>();
// builder.Services.AddSingleton<IInternalEventBus, InternalEventBus>();

var host = builder.Build();
host.Run();
