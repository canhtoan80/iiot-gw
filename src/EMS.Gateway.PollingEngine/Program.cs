using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EMS.Gateway.PollingEngine;
using EMS.Gateway.Contracts;
using EMS.Gateway.DeviceTemplate;
using EMS.Gateway.ProtocolAdapter;

var builder = Host.CreateApplicationBuilder(args);

// In a real scenario, these would be implemented in their respective modules
// For now we might need to mock them or provide the actual implementations if available
// Since I already implemented DeviceTemplate and ProtocolAdapterFactory, I can register them.

builder.Services.AddSingleton<IDeviceTemplateRepository, DeviceTemplateRepository>();
builder.Services.AddSingleton<ProtocolAdapterFactory>();
builder.Services.AddSingleton<IPollingEngine, PollingEngine>();
builder.Services.AddHostedService<PollingEngine>(sp => (PollingEngine)sp.GetRequiredService<IPollingEngine>());

var host = builder.Build();
host.Run();
