using System.Buffers;
using System.Text.Json;
using System.Threading.RateLimiting;
using EMS.Gateway.Contracts;
using MQTTnet;
using MQTTnet.Client;

namespace EMS.Gateway.ProtocolAdapter.Mqtt;

/// <summary>
/// Adapter for native MQTT devices that publish JSON/Custom payloads.
/// </summary>
public class MqttNativeAdapter : IProtocolAdapter, IDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _options;
    private readonly IInternalEventBus _eventBus;
    private readonly Dictionary<string, TokenBucketRateLimiter> _rateLimiters = new();
    private bool _disposed;

    public MqttNativeAdapter(string brokerHost, int port, IInternalEventBus eventBus)
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();
        _eventBus = eventBus;

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(brokerHost, port)
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public Task<RawTagBatch> PollAsync(DeviceTemplateDto device, IReadOnlyList<CoalescedBlockDto> coalescedBlocks, CancellationToken ct)
    {
        return Task.FromResult(new RawTagBatch(device.DeviceId, new List<RawTagDto>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000));
    }

    public Task<bool> WriteAsync(string deviceId, int registerAddress, object value, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (!_mqttClient.IsConnected)
        {
            await _mqttClient.ConnectAsync(_options, ct);
        }
    }

    public async Task SubscribeAsync(DeviceTemplateDto device, CancellationToken ct)
    {
        if (device.MqttConnection == null) return;

        var factory = new MqttFactory();
        
        var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(device.MqttConnection.SubscribeTopic))
            .Build();

        await _mqttClient.SubscribeAsync(subscribeOptions, ct);

        if (device.MqttConnection.LwtTopic != null)
        {
            var lwtSubscribeOptions = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(device.MqttConnection.LwtTopic))
                .Build();
            await _mqttClient.SubscribeAsync(lwtSubscribeOptions, ct);
        }

        if (device.RateLimit != null)
        {
            _rateLimiters[device.DeviceId] = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = device.RateLimit.BurstSize,
                TokensPerPeriod = device.RateLimit.MaxMessagesPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true
            });
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        string deviceId = "todo-lookup-from-topic"; 
        
        if (_rateLimiters.TryGetValue(deviceId, out var limiter))
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                await _eventBus.PublishAsync(new MqttMessageDroppedEvent(deviceId, 1, DateTimeOffset.UtcNow));
                return;
            }
        }

        ParsePayload(deviceId, e.ApplicationMessage.PayloadSegment);
    }

    private void ParsePayload(string deviceId, ArraySegment<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        // Simplified implementation
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient.Dispose();
            foreach (var limiter in _rateLimiters.Values) limiter.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
