using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.SparkplugEncoder;

public class SparkplugEncoder : BackgroundService, ISparkplugEncoder
{
    private readonly ILogger<SparkplugEncoder> _logger;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly ILocalBuffer _localBuffer;
    private readonly IInternalEventBus _eventBus;
    
    private int _seqNumber = 0;

    public SparkplugEncoder(
        ILogger<SparkplugEncoder> logger,
        IMqttPublisher mqttPublisher,
        ILocalBuffer localBuffer,
        IInternalEventBus eventBus)
    {
        _logger = logger;
        _mqttPublisher = mqttPublisher;
        _localBuffer = localBuffer;
        _eventBus = eventBus;
    }

    private int NextSeq() => Interlocked.Increment(ref _seqNumber) % 256;

    public SparkplugPayloadDto EncodeDData(EnrichedTagBatch enrichedBatch)
    {
        // Placeholder for real Sparkplug B encoding using Eclipse.Tahu or SparkplugNet
        // In a real implementation we would build a Payload object and serialize to byte[]
        byte[] dummyPayload = System.Text.Encoding.UTF8.GetBytes($"DDATA:{enrichedBatch.DeviceId}");
        
        return new SparkplugPayloadDto(
            enrichedBatch.DeviceId,
            SparkplugMessageType.DData,
            dummyPayload,
            enrichedBatch.BatchTimestampUtcNs,
            NextSeq()
        );
    }

    public SparkplugPayloadDto EncodeDBirth(string deviceId, DeviceTemplateDto template, string firmwareVersion, string hardwareId)
    {
        byte[] dummyPayload = System.Text.Encoding.UTF8.GetBytes($"DBIRTH:{deviceId}");
        return new SparkplugPayloadDto(
            deviceId,
            SparkplugMessageType.DBirth,
            dummyPayload,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000,
            0 // Birth usually has seq 0
        );
    }

    public SparkplugPayloadDto EncodeDDeath(string deviceId)
    {
        byte[] dummyPayload = System.Text.Encoding.UTF8.GetBytes($"DDEATH:{deviceId}");
        return new SparkplugPayloadDto(
            deviceId,
            SparkplugMessageType.DDeath,
            dummyPayload,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000,
            NextSeq()
        );
    }

    public async Task PublishAsync(SparkplugPayloadDto payload, CancellationToken ct)
    {
        if (_mqttPublisher.IsConnected)
        {
            bool success = await _mqttPublisher.PublishAsync(payload, ct);
            if (!success)
            {
                await _localBuffer.EnqueueAsync(payload, ct);
            }
        }
        else
        {
            await _localBuffer.EnqueueAsync(payload, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Sparkplug Encoder Worker starting...");

        // Subscribe to MetricsBatchReadyEvent
        await foreach (var @event in _eventBus.SubscribeAsync<MetricsBatchReadyEvent>(stoppingToken))
        {
            var payload = EncodeDData(@event.Batch);
            await PublishAsync(payload, stoppingToken);
        }
    }
}
