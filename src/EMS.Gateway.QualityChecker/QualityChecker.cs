using System.Collections.Concurrent;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.QualityChecker;

public class QualityChecker : IQualityChecker
{
    private readonly IDeviceTemplateRepository _templateRepo;
    private readonly IInternalEventBus _eventBus;

    private readonly ConcurrentDictionary<string, (double Value, long TimestampNs)> _previousValues = new();
    private readonly ConcurrentDictionary<string, TagQuality> _previousQualities = new();
    private readonly ConcurrentDictionary<string, long> _lastChangedTimestamps = new();

    public QualityChecker(IDeviceTemplateRepository templateRepo, IInternalEventBus eventBus)
    {
        _templateRepo = templateRepo;
        _eventBus = eventBus;
    }

    public EnrichedTagBatch CheckAsync(EnrichedTagBatch enrichedBatch)
    {
        var template = _templateRepo.GetTemplate(enrichedBatch.DeviceId);
        if (template == null) return enrichedBatch;

        var resultTags = new List<EnrichedTagDto>();

        foreach (var tag in enrichedBatch.Tags)
        {
            var checkedTag = ApplyChecks(enrichedBatch.DeviceId, template, tag);
            resultTags.Add(checkedTag);

            // Handle transition event
            string key = $"{enrichedBatch.DeviceId}::{tag.TagName}";
            var oldQuality = _previousQualities.GetOrAdd(key, TagQuality.Good);
            if (oldQuality == TagQuality.Good && checkedTag.Quality != TagQuality.Good)
            {
                _eventBus.PublishAsync(new TagQualityDegradedEvent(
                    enrichedBatch.DeviceId,
                    tag.TagName,
                    oldQuality,
                    checkedTag.Quality,
                    checkedTag.QualityReason ?? "Unknown",
                    DateTimeOffset.UtcNow
                ));
            }
            _previousQualities[key] = checkedTag.Quality;
        }

        return enrichedBatch with { Tags = resultTags };
    }

    private EnrichedTagDto ApplyChecks(string deviceId, DeviceTemplateDto template, EnrichedTagDto tag)
    {
        if (tag.IsSimulated) return tag;

        var reg = template.Registers.FirstOrDefault(r => r.TagName == tag.TagName);
        string key = $"{deviceId}::{tag.TagName}";
        var quality = tag.Quality;
        string? reason = tag.QualityReason;

        // 1. Null/NaN/Infinity Check
        if (!tag.PhysicalValue.HasValue || double.IsNaN(tag.PhysicalValue.Value) || double.IsInfinity(tag.PhysicalValue.Value))
        {
            return tag with { Quality = TagQuality.Bad, QualityReason = "NullOrNaN" };
        }

        double val = tag.PhysicalValue.Value;

        // 2. Range Check
        if (reg != null)
        {
            if (val < reg.RangeMin || val > reg.RangeMax)
            {
                quality = TagQuality.Bad;
                reason = $"RangeExceeded:{val} not in [{reg.RangeMin},{reg.RangeMax}]";
            }
        }

        // 3. Rate of Change (RoC) Check
        if (reg != null && reg.RocLimitPerSecond > 0 && _previousValues.TryGetValue(key, out var prev))
        {
            double dt = (double)(tag.TimestampUtcNs - prev.TimestampNs) / 1_000_000_000.0;
            if (dt > 0)
            {
                double roc = Math.Abs(val - prev.Value) / dt;
                if (roc > reg.RocLimitPerSecond)
                {
                    quality = TagQuality.Bad;
                    reason = $"RoCExceeded:{roc:F2} > limit:{reg.RocLimitPerSecond}";
                }
            }
        }

        // 4. Stuck Check
        if (reg != null && !reg.AllowStuck && reg.StuckTimeoutMs > 0)
        {
            if (_previousValues.TryGetValue(key, out var lastValue) && val == lastValue.Value)
            {
                long lastChanged = _lastChangedTimestamps.GetOrAdd(key, tag.TimestampUtcNs);
                double elapsedMs = (double)(tag.TimestampUtcNs - lastChanged) / 1_000_000.0;
                if (elapsedMs > reg.StuckTimeoutMs)
                {
                    quality = TagQuality.Bad;
                    reason = "StuckValue";
                }
            }
            else
            {
                _lastChangedTimestamps[key] = tag.TimestampUtcNs;
            }
        }

        // Update state
        _previousValues[key] = (val, tag.TimestampUtcNs);

        return tag with { Quality = quality, QualityReason = reason };
    }
}
