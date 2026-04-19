using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EMS.Gateway.AdminService;
using EMS.Gateway.Contracts;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Register Watchdogs
builder.Services.AddSingleton<ISoftwareWatchdog, SoftwareWatchdogService>();
builder.Services.AddHostedService<SoftwareWatchdogService>(sp => (SoftwareWatchdogService)sp.GetRequiredService<ISoftwareWatchdog>());
builder.Services.AddHostedService<HardwareWatchdogWorker>();

// Health Checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Prometheus metrics
app.UseMetricServer();
app.UseHttpMetrics();

app.MapHealthChecks("/health");
app.MapGet("/health/detail", async (ISoftwareWatchdog watchdog) =>
{
    // Return mock GatewayHealthDto
    return Results.Ok(new { 
        machineId = "ems-edge-01",
        uptime = TimeSpan.FromHours(1),
        softwareWatchdogHealthy = !watchdog.IsAnyWorkerUnhealthy()
    });
});

app.Run();
