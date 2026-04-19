using System.Text.Json;
using EMS.Gateway.Contracts;
using EMS.Gateway.ProtocolAdapter.Modbus;
using EMS.Gateway.ProtocolAdapter.Mqtt;

namespace EMS.Gateway.ProtocolAdapter;

/// <summary>
/// Factory for creating protocol-specific adapters.
/// </summary>
public class ProtocolAdapterFactory
{
    private readonly IInternalEventBus _eventBus;

    public ProtocolAdapterFactory(IInternalEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    /// <summary>
    /// Creates a protocol adapter based on the device template.
    /// </summary>
    public virtual IProtocolAdapter Create(DeviceTemplateDto template)
    {
        return template.Protocol switch
        {
            ProtocolType.ModbusTCP => CreateModbusTcp(template),
            ProtocolType.MqttNative => CreateMqttNative(template),
            // ProtocolType.ModbusRTU => CreateModbusRtu(template),
            // ProtocolType.BACnetIP => CreateBacnetIp(template),
            _ => throw new NotSupportedException($"Protocol {template.Protocol} is not supported yet.")
        };
    }

    private ModbusTcpAdapter CreateModbusTcp(DeviceTemplateDto template)
    {
        // Connection JSON format: { "host": "1.2.3.4", "port": 502, "unitId": 1 }
        var host = template.Connection.GetProperty("host").GetString() ?? throw new InvalidOperationException("Modbus TCP host is missing.");
        var port = template.Connection.GetProperty("port").GetInt32();
        var unitId = template.Connection.GetProperty("unitId").GetByte();

        return new ModbusTcpAdapter(host, port, unitId);
    }

    private MqttNativeAdapter CreateMqttNative(DeviceTemplateDto template)
    {
        if (template.MqttConnection == null)
            throw new InvalidOperationException("MQTT Connection details are missing.");

        return new MqttNativeAdapter(template.MqttConnection.BrokerHost, template.MqttConnection.BrokerPort, _eventBus);
    }
}
