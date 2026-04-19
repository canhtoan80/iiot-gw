## Module 2A-3 — `EMS.Gateway.ProtocolAdapter`

```
You are a senior .NET 8 engineer specializing in industrial OT protocols.
Modules 2A-1 and 2A-2 are complete.

## Task
Implement `EMS.Gateway.ProtocolAdapter` — the OT-side protocol drivers for the IIoT Gateway.
This is a Worker Service (.NET 8) hosting the following adapters:
- ModbusRtuAdapter (RS485 serial)
- ModbusTcpAdapter (TCP/IP)
- BACnetIpAdapter (UDP 47808)
- MqttNativeAdapter (MQTT subscribe, event-driven)

## Project references
- EMS.Gateway.Contracts
- EMS.Gateway.DeviceTemplate
- NuGet: FluentModbus (Modbus RTU + TCP)
- NuGet: MQTTnet (v4.x for MQTT Native Adapter)
- NuGet: System.IO.BACnet (for BACnet/IP)
- NuGet: Polly (retry + circuit breaker)
- NuGet: System.Threading.RateLimiting (.NET 7+)

## Classes to implement

### 1. ProtocolAdapterFactory (Factory Pattern)
```csharp
public class ProtocolAdapterFactory
{
    public IProtocolAdapter Create(DeviceTemplateDto template);
    // Returns: ModbusRtuAdapter | ModbusTcpAdapter | BACnetIpAdapter | MqttNativeAdapter
}
```

### 2. ModbusRtuAdapter : IProtocolAdapter
- Use FluentModbus `ModbusRtuClient`
- Open serial port once, keep persistent connection
- `PollAsync`: iterate CoalescedBlocks, send FC03/FC04 per block, parse raw bytes
- **Byte parsing per DataType**: float32 (IEEE 754, 2 registers), uint16, int16, int32, bool
- **Inter-frame silence**: enforce 3.5 char time gap between frames (calculate from baud rate)
- **Bus Arbitration WriteQueue**: `System.Threading.Channels.Channel<WriteCommand>` bounded
  - Single-threaded arbitration loop: check WriteQueue first, then Poll
  - If pending Write → execute Write (FC06/FC16) before next Poll cycle
- Retry: max 3 attempts with 50ms delay → on failure return `Quality=Bad`
- CRC error → retry → after 3 fails → `Quality=Bad, reason="CrcError"`

### 3. ModbusTcpAdapter : IProtocolAdapter
- Same coalescing and byte parsing as RTU
- Persistent TCP connection with exponential backoff reconnect (1s→2s→4s→30s max)
- No inter-frame silence needed (TCP handles framing)
- WriteQueue: same as RTU — Write before Poll

### 4. MqttNativeAdapter : IProtocolAdapter
- Subscribe to topics from `DeviceTemplateDto.MqttConnection.SubscribeTopic`
- Event-driven (push), NOT poll-based — `poll_cycle_ms = 0` means subscribe
- **LWT handling**: also subscribe to `LwtTopic`
  - Receive LwtPayloadOffline → immediately publish `MqttDeviceLwtOfflineEvent`
  - All tags for that device → `Quality=Bad, reason="LWT_Offline"`
- **Zero-allocation JSON parsing**: use `System.Text.Json.Utf8JsonReader` on `ReadOnlySpan<byte>`
  - Iterate `PayloadMappings` to extract multiple tags from one message
  - Use `ArrayPool<byte>` for temporary buffers
- **Token Bucket Rate Limiter** per device:
  - `System.Threading.RateLimiting.TokenBucketRateLimiter`
  - Config: `MaxMessagesPerSecond`, `BurstSize` from template
  - Exceeded: DROP DDATA, KEEP DBIRTH/DDEATH/LWT (never drop lifecycle messages)
  - Publish `MqttMessageDroppedEvent` when dropping

### 5. WriteQueue Bus Arbitration (shared for Modbus RTU/TCP)
```
Channel<WriteCommand> _writeQueue (Bounded, capacity=100, BoundedChannelFullMode.Wait)

Arbitration loop (single thread, per device bus):
  while (!cancellationToken.IsCancellationRequested):
    if _writeQueue.TryRead(out var writeCmd):
      await ExecuteWrite(writeCmd)  // FC06 or FC16
      writeCmd.CompletionSource.SetResult(success)
    else:
      await ExecutePollCycle()      // normal FC03/FC04 poll
    await Task.Delay(interFrameGapMs)
```

### 6. Error handling rules
- Device timeout (N retries exhausted) → return `Quality=Bad, reason="Timeout"`  
- CRC error → return `Quality=Bad, reason="CrcError"`
- Exception → return `Quality=Bad, reason="Exception:{message}"`
- After `DeviceUnresponsiveThresholdSeconds` of consecutive Bad → publish `DeviceUnresponsiveEvent`
- TCP disconnect → start reconnect loop (do NOT throw, return Bad quality during reconnect)

## Unit tests required
1. Coalescing integration: 50 scattered registers → verify N blocks with correct offsets
2. ModbusRtu: timeout returns `Quality=Bad` with correct reason
3. ModbusRtu: WriteQueue: Write executes before pending Poll when both queued
4. MqttNative: LWT offline message → all device tags become Bad immediately
5. MqttNative: rate limit exceeded → DDATA dropped, DBIRTH preserved
6. MqttNative: multi-tag payload → all tags extracted from single JSON message

Use FluentModbus test server or mock `IModbusClient` for RTU/TCP unit tests.

## Deliverable
Complete source with all adapters. Polly retry configured. All error paths return proper Quality.
```

---