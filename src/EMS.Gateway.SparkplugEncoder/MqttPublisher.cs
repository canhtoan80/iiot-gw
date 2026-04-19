using EMS.Gateway.Contracts;
using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Logging;

namespace EMS.Gateway.SparkplugEncoder;

public class MqttPublisher : IMqttPublisher, IDisposable
{
    private readonly ILogger<MqttPublisher> _logger;
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _options;
    private readonly IInternalEventBus _eventBus;
    private bool _disposed;

    public bool IsConnected => _mqttClient.IsConnected;

    public MqttPublisher(ILogger<MqttPublisher> logger, IInternalEventBus eventBus)
    {
        _logger = logger;
        _eventBus = eventBus;
        
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithCleanSession()
            .Build();

        _mqttClient.DisconnectedAsync += async e =>
        {
            _logger.LogWarning("Disconnected from MQTT broker.");
            await _eventBus.PublishAsync(new MqttConnectionLostEvent("localhost", e.Reason.ToString(), DateTimeOffset.UtcNow));
        };

        _mqttClient.ConnectedAsync += async e =>
        {
            _logger.LogInformation("Connected to MQTT broker.");
            await _eventBus.PublishAsync(new MqttConnectionRestoredEvent("localhost", DateTimeOffset.UtcNow));
        };
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (!_mqttClient.IsConnected)
        {
            await _mqttClient.ConnectAsync(_options, ct);
        }
    }

    public async Task DisconnectAsync()
    {
        await _mqttClient.DisconnectAsync();
    }

    public async Task<bool> PublishAsync(SparkplugPayloadDto payload, CancellationToken ct)
    {
        try
        {
            string topic = $"spBv1.0/ems_group/{payload.MessageType}/{payload.DeviceId}";
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload.ProtobufPayload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(payload.MessageType != SparkplugMessageType.DData)
                .Build();

            var result = await _mqttClient.PublishAsync(message, ct);
            return result.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MQTT message.");
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
