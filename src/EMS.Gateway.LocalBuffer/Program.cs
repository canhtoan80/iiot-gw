using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EMS.Gateway.LocalBuffer;
using EMS.Gateway.Contracts;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<LocalBufferOptions>(builder.Configuration.GetSection("LocalBuffer"));
builder.Services.AddSingleton<ILocalBuffer, LocalBufferService>();
builder.Services.AddHostedService<LocalBufferService>(sp => (LocalBufferService)sp.GetRequiredService<ILocalBuffer>());

var host = builder.Build();
host.Run();
