using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.AdminService;

public class SoftwareWatchdogService : BackgroundService, ISoftwareWatchdog
{
    private readonly ILogger<SoftwareWatchdogService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentDictionary<string, (DateTimeOffset LastSeen, int IntervalMs)> _workers = new();

    public SoftwareWatchdogService(ILogger<SoftwareWatchdogService> logger, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    public void ReportAlive(string workerId)
    {
        // For simplicity, we assume 60s if not registered
        _workers.AddOrUpdate(workerId, (DateTimeOffset.UtcNow, 60000), (_, old) => (DateTimeOffset.UtcNow, old.IntervalMs));
    }

    public void RegisterWorker(string workerId, int expectedHeartbeatIntervalMs)
    {
        _workers[workerId] = (DateTimeOffset.UtcNow, expectedHeartbeatIntervalMs);
    }

    public bool IsAnyWorkerUnhealthy()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var worker in _workers)
        {
            if ((now - worker.Value.LastSeen).TotalMilliseconds > 2 * worker.Value.IntervalMs)
            {
                return true;
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
            
            var now = DateTimeOffset.UtcNow;
            foreach (var worker in _workers)
            {
                if ((now - worker.Value.LastSeen).TotalMilliseconds > 2 * worker.Value.IntervalMs)
                {
                    _logger.LogCritical("Worker {WorkerId} is unhealthy! Last seen: {LastSeen}. Stopping application...", 
                        worker.Key, worker.Value.LastSeen);
                    _lifetime.StopApplication();
                    return;
                }
            }
        }
    }
}
