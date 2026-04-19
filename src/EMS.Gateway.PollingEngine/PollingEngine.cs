using System.Collections.Immutable;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EMS.Gateway.Contracts;
using EMS.Gateway.ProtocolAdapter;

namespace EMS.Gateway.PollingEngine;

public class PollingEngine : BackgroundService, IPollingEngine
{
    private readonly ILogger<PollingEngine> _logger;
    private readonly IDeviceTemplateRepository _templateRepo;
    private readonly ProtocolAdapterFactory _adapterFactory;
    private readonly IInternalEventBus _eventBus;

    private ImmutableDictionary<string, EnrichedTagDto> _tagDatabase = ImmutableDictionary<string, EnrichedTagDto>.Empty;
    private readonly ConcurrentDictionary<string, double> _lastForwardedValues = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastForwardedTimes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _deviceTasks = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public PollingEngine(
        ILogger<PollingEngine> logger,
        IDeviceTemplateRepository templateRepo,
        ProtocolAdapterFactory adapterFactory,
        IInternalEventBus eventBus)
    {
        _logger = logger;
        _templateRepo = templateRepo;
        _adapterFactory = adapterFactory;
        _eventBus = eventBus;
    }

    public EnrichedTagBatch GetTagSnapshot(string deviceId)
    {
        var tags = _tagDatabase.Values
            .Where(t => t.TagName.StartsWith($"{deviceId}::"))
            .Select(t => t with { TagName = t.TagName.Split("::")[1] })
            .ToList();

        return new EnrichedTagBatch(deviceId, tags, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopAllPollingAsync();
        await base.StopAsync(cancellationToken);
    }

    async Task IPollingEngine.StopAsync()
    {
        await StopAllPollingAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Polling Engine starting...");

        // Start initial polling
        await StartAllPollingAsync(stoppingToken);

        // Listen for config reloads
        _ = Task.Run(async () =>
        {
            await foreach (var @event in _eventBus.SubscribeAsync<ConfigReloadRequestedEvent>(stoppingToken))
            {
                _logger.LogInformation("Config reload requested. Restarting polling tasks...");
                await _reloadLock.WaitAsync(stoppingToken);
                try
                {
                    await StopAllPollingAsync();
                    await StartAllPollingAsync(stoppingToken);
                }
                finally
                {
                    _reloadLock.Release();
                }
            }
        }, stoppingToken);

        // Stale detection loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            await DetectStaleTagsAsync(stoppingToken);
        }
    }

    private Task StartAllPollingAsync(CancellationToken ct)
    {
        var templates = _templateRepo.GetAllTemplates();
        foreach (var template in templates)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_deviceTasks.TryAdd(template.DeviceId, cts))
            {
                _ = Task.Run(() => PollDeviceLoop(template, cts.Token), cts.Token);
            }
        }
        return Task.CompletedTask;
    }

    private Task StopAllPollingAsync()
    {
        foreach (var deviceId in _deviceTasks.Keys)
        {
            if (_deviceTasks.TryRemove(deviceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        _consecutiveFailures.Clear();
        return Task.CompletedTask;
    }

    private async Task PollDeviceLoop(DeviceTemplateDto device, CancellationToken ct)
    {
        _logger.LogInformation("Starting polling loop for device {DeviceId}", device.DeviceId);
        
        IProtocolAdapter? adapter = null;
        try
        {
            adapter = _adapterFactory.Create(device);
            if (device.Protocol == ProtocolType.MqttNative)
            {
                await Task.Delay(-1, ct);
                return;
            }

            var blocks = (_templateRepo as dynamic).GetCoalescedBlocks(device.DeviceId) as IReadOnlyList<CoalescedBlockDto> 
                         ?? Array.Empty<CoalescedBlockDto>();

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(device.PollCycleMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var rawBatch = await adapter.PollAsync(device, blocks, ct);
                await ApplyDeadbandAndPublish(device, rawBatch, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in polling loop for device {DeviceId}", device.DeviceId);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    private async Task ApplyDeadbandAndPublish(DeviceTemplateDto device, RawTagBatch rawBatch, CancellationToken ct)
    {
        var enrichedTags = new List<EnrichedTagDto>();
        bool anyGood = false;

        foreach (var raw in rawBatch.Tags)
        {
            var reg = device.Registers.FirstOrDefault(r => r.TagName == raw.TagName);
            double physicalValue = (raw.RawValue ?? 0) * (reg?.ScaleFactor ?? 1.0);
            
            bool shouldForward = raw.Quality != TagQuality.Good;
            string key = $"{device.DeviceId}::{raw.TagName}";

            if (!shouldForward)
            {
                anyGood = true;
                double deadband = reg?.Deadband ?? 0;
                
                if (!_lastForwardedValues.TryGetValue(key, out double lastValue) ||
                    Math.Abs(physicalValue - lastValue) > deadband)
                {
                    shouldForward = true;
                }
                else if (_lastForwardedTimes.TryGetValue(key, out var lastTime) &&
                         (DateTimeOffset.UtcNow - lastTime).TotalSeconds >= device.HeartbeatIntervalS)
                {
                    shouldForward = true;
                }
            }

            var enriched = new EnrichedTagDto(
                raw.TagName,
                physicalValue,
                reg?.Unit ?? "",
                raw.Quality,
                raw.QualityReason,
                raw.TimestampUtcNs,
                false,
                false
            );

            // Update database with global key
            var dbTag = enriched with { TagName = key };
            ImmutableInterlocked.Update(ref _tagDatabase, db => db.SetItem(key, dbTag));

            if (shouldForward)
            {
                _lastForwardedValues[key] = physicalValue;
                _lastForwardedTimes[key] = DateTimeOffset.UtcNow;
                enrichedTags.Add(enriched);
            }
        }

        if (enrichedTags.Count > 0)
        {
            await _eventBus.PublishAsync(new MetricsBatchReadyEvent(
                new EnrichedTagBatch(device.DeviceId, enrichedTags, rawBatch.PollTimestampUtcNs),
                DateTimeOffset.UtcNow), ct);
        }

        if (!anyGood)
        {
            int fails = _consecutiveFailures.AddOrUpdate(device.DeviceId, 1, (_, v) => v + 1);
            if (fails == 3) 
            {
                await _eventBus.PublishAsync(new DeviceUnresponsiveEvent(device.DeviceId, fails, DateTimeOffset.UtcNow), ct);
            }
        }
        else
        {
            _consecutiveFailures[device.DeviceId] = 0;
        }
    }

    private async Task DetectStaleTagsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var staleTags = new List<(string DeviceId, EnrichedTagDto Tag)>();

        foreach (var tag in _tagDatabase.Values)
        {
            if (tag.Quality == TagQuality.Stale) continue;

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tag.TimestampUtcNs / 1000000);
            
            if ((now - timestamp).TotalSeconds > 10) 
            {
                var staleTag = tag with { Quality = TagQuality.Stale, QualityReason = "Stale timeout" };
                ImmutableInterlocked.Update(ref _tagDatabase, db => db.SetItem(tag.TagName, staleTag));
                
                string[] parts = tag.TagName.Split("::");
                staleTags.Add((parts[0], staleTag with { TagName = parts[1] }));
            }
        }

        foreach (var group in staleTags.GroupBy(x => x.DeviceId))
        {
            await _eventBus.PublishAsync(new MetricsBatchReadyEvent(
                new EnrichedTagBatch(group.Key, group.Select(x => x.Tag).ToList(), now.ToUnixTimeMilliseconds() * 1000000),
                now), ct);
        }
    }

    public async Task EnterGracePeriodAsync(int durationMinutes, CancellationToken ct)
    {
        _logger.LogInformation("Entering config rollback grace period for {Duration} minutes", durationMinutes);
        var endTime = DateTimeOffset.UtcNow.AddMinutes(durationMinutes);
        int badChecks = 0;

        while (DateTimeOffset.UtcNow < endTime && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var activeDevices = _consecutiveFailures.Keys.ToList();
            if (activeDevices.Count == 0) continue;

            int badCount = activeDevices.Count(d => _consecutiveFailures.TryGetValue(d, out int f) && f > 0);
            double badPercent = (double)badCount / activeDevices.Count;

            _logger.LogDebug("Grace period check: {BadPercent:P} bad devices", badPercent);

            if (badPercent > 0.5)
            {
                badChecks++;
            }
            else
            {
                badChecks = 0;
            }

            if (badChecks >= 2)
            {
                _logger.LogWarning("Grace period failed: {BadPercent:P} bad devices for 2 checks. Rolling back...", badPercent);
                await _templateRepo.RollbackToBackupAsync();
                return;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Grace period passed. Committing configuration.");
            await _templateRepo.CommitConfigAsync();
        }
    }
}
