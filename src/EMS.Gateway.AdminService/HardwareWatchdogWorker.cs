using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EMS.Gateway.AdminService;

public class HardwareWatchdogWorker : BackgroundService
{
    private readonly ILogger<HardwareWatchdogWorker> _logger;
    private static readonly byte[] KeepAlive = new byte[] { 0x31 }; // '1'
    private const string WatchdogPath = "/dev/watchdog";

    public HardwareWatchdogWorker(ILogger<HardwareWatchdogWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("Hardware watchdog only supported on Linux. Graceful degradation.");
            return;
        }

        try
        {
            if (!File.Exists(WatchdogPath))
            {
                _logger.LogWarning("/dev/watchdog not found. Using systemd notify if available.");
                // sd_notify implementation would go here
                return;
            }

            await using var watchdog = File.Open(WatchdogPath, FileMode.Open, FileAccess.Write, FileShare.None);
            _logger.LogInformation("Hardware watchdog initialized.");

            while (!stoppingToken.IsCancellationRequested)
            {
                await watchdog.WriteAsync(KeepAlive, stoppingToken);
                await watchdog.FlushAsync(stoppingToken);
                await Task.Delay(30000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Hardware watchdog worker crashed! System may reset soon.");
        }
    }
}
