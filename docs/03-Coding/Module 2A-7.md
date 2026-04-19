## Module 2A-7 — `EMS.Gateway.SparkplugEncoder`

```
You are a senior .NET 8 engineer with Sparkplug B protocol expertise.
Modules 2A-1 through 2A-6 are complete.

## Task
Implement `EMS.Gateway.SparkplugEncoder` — encodes enriched tag data into Sparkplug B Protobuf
payloads and publishes to Mosquitto (local MQTT broker) via MQTTnet. Manages Birth/Death lifecycle.
This is a Worker Service (.NET 8).

## Project references
- EMS.Gateway.Contracts
- NuGet: Eclipse.Tahu.Protobuf (official Sparkplug B library for .NET)
- NuGet: MQTTnet (v4.x)

## Classes to implement

### 1. SparkplugEncoder : ISparkplugEncoder, IHostedService

**Sequence number management:**
```csharp
// Monotonic 0–255, wrap around per Sparkplug B spec
private int _seqNumber = 0;
private int NextSeq() => Interlocked.Increment(ref _seqNumber) % 256;
```

**EncodeDData — DDATA payload:**
```csharp
// For each EnrichedTagDto in batch:
// Good quality → metric.SetValue(physicalValue), is_null = false
// Bad quality  → metric.is_null = true
//   PropertySet: { "quality": "Bad", "reason": tag.QualityReason, "timestamp_ns": ... }
// Stale quality → metric.is_null = true
//   PropertySet: { "quality": "Stale", "stale_since_ns": tag.TimestampUtcNs }
// IsSimulated = true → add PropertySet: { "is_simulated": true }
// Use tag.TimestampUtcNs as metric timestamp (actual poll time, NOT encode time)
```

**EncodeDBirth — DBIRTH payload (retain=true on Mosquitto):**
```csharp
// Include ALL metric definitions:
//   - name, data type, engineering unit
//   - PropertySet: { "ct_ratio": x, "pt_ratio": y, "scale_factor": z }
//   - For virtual tags: PropertySet: { "is_virtual": true, "expression": "..." }
// Include node metadata:
//   - firmware_version, hardware_id, machine_id, build_timestamp
// Publish with MqttQualityOfServiceLevel.AtLeastOnce + Retain=true
```

**EncodeDDeath — DDEATH payload:**
```csharp
// Minimal payload: just seq number and timestamp
// Publish with Retain=true (Mosquitto will overwrite DBIRTH retain)
```

**PublishAsync:**
```csharp
public async Task PublishAsync(SparkplugPayloadDto payload, CancellationToken ct)
{
    if (_mqttPublisher.IsConnected)
        await _mqttPublisher.PublishAsync(payload, ct);
    else
        await _localBuffer.EnqueueAsync(payload, ct); // fallback to buffer
}
```

### 2. MqttPublisher : IMqttPublisher
- Connect to Mosquitto localhost:1883 (configurable via appsettings)
- MQTTnet ManagedMqttClient for auto-reconnect
- QoS 1 (AtLeastOnce) for all DDATA/DBIRTH/DDEATH
- On disconnect: publish `MqttConnectionLostEvent` via `IInternalEventBus`
- On reconnect: publish `MqttConnectionRestoredEvent`
- Topic format: `spBv1.0/{group_id}/{message_type}/{device_id}`
  - DBIRTH: `spBv1.0/{group}/DBIRTH/{deviceId}` with retain=true
  - DDATA:  `spBv1.0/{group}/DDATA/{deviceId}` with retain=false
  - DDEATH: `spBv1.0/{group}/DDEATH/{deviceId}` with retain=true

**Subscribe and handle events:**
```
ConfigReloadRequestedEvent → re-publish DBIRTH for all devices
MqttConnectionRestoredEvent → trigger ILocalBuffer.ReplayAsync()
MqttConnectionLostEvent → switch to buffer-only mode
```

## Unit tests required
1. EncodeDData: Good tag → is_null=false, value set correctly
2. EncodeDData: Bad tag → is_null=true, PropertySet contains reason
3. EncodeDData: Stale tag → is_null=true, PropertySet contains stale_since_ns
4. EncodeDData: IsSimulated=true → PropertySet contains is_simulated=true
5. Sequence number: wraps 255→0 correctly
6. Timestamp: uses tag.TimestampUtcNs (not DateTime.UtcNow)
7. EncodeDBirth: contains all metric definitions + metadata
8. MQTT disconnected: PublishAsync → routes to ILocalBuffer.EnqueueAsync
9. MQTT reconnected: ILocalBuffer.ReplayAsync called

Mock IMqttPublisher and ILocalBuffer for unit tests.

## Deliverable
Complete SparkplugEncoder + MqttPublisher. Eclipse.Tahu.Protobuf used (not custom Protobuf).
```

---