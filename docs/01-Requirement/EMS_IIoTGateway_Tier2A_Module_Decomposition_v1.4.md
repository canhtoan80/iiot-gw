# Phân rã Module — EMS IIoT Gateway (Tầng 2A)
> Kiến trúc: .NET 8 Worker Service · Firmware biên · On-premise SME  
> Phạm vi: **Tầng 2A (IIoT Gateway — Firmware Pipeline)** — solution độc lập với Tầng 2B và Tầng 3  
> Tài liệu tham chiếu: EMS On-premise SME v2.4 · EMS IIoT Server Tier 2B Module Decomposition v1.1  
> Phiên bản: 1.1 — [ngày cập nhật]

---

## Vị trí trong kiến trúc 5 tầng

```
Tầng 1 (Field Layer)          Tầng 2A (IIoT Gateway)           Tầng 2B (IIoT Server)
────────────────────          ──────────────────────           ─────────────────────
Smart Meter / PLC             Firmware .NET 8                  EMQX OT Broker (*)
CT/PT Sensor      ──────────► M02 Device Template              Store-and-Forward
VFD / BACnet        Modbus    M03 Protocol Adapter  ─────────► MQTT Bridge → Tầng 3
RS485 / TCP/IP      BACnet    M05 Polling Engine     Sparkplug
MQTT Native         MQTT      M13 Edge Rule Engine   B MQTT
                              M06 Sparkplug B Encoder
                    [G3]      M09 Command Handler (G3)
                    DCMD ◄─── M09 ◄── EMQX OT DCMD routing

(*) EMQX OT Broker là infrastructure dependency của Tầng 2B.
    Gateway chỉ publish/subscribe, không quản lý Broker.
```

Tầng 2A **thực hiện toàn bộ công việc thu thập tại hiện trường** — trách nhiệm duy nhất là: poll thiết bị theo giao thức OT, chuẩn hóa dữ liệu, tính toán virtual tag tại biên, kiểm soát chất lượng, encode Sparkplug B, buffer cục bộ và publish lên EMQX OT Broker. Tầng 2A **không phụ thuộc Tầng 3 (Business Server)** để tiếp tục thu thập — hoạt động tự trị 24/7.

---

## Nguyên tắc thiết kế cốt lõi

- **Edge-first autonomy** — Gateway tiếp tục poll, buffer và publish kể cả khi Tầng 2B hoặc Tầng 3 down. Không có lệnh nào từ IT có thể khiến Gateway ngừng thu thập.
- **Không business logic** — Gateway không tính chi phí, không ra quyết định nghiệp vụ. Chuẩn hóa (CT/PT ratio, deadband, virtual tag OEE/SEI) là trách nhiệm hợp lệ của biên để giảm tải mạng — không phải business logic.
- **TagQuality là công dân hạng nhất** — mọi giá trị đều mang nhãn `Good / Bad / Stale`. Giá trị Bad/Stale không bao giờ được phép đi vào pipeline phía sau mà không có annotation rõ ràng trong Sparkplug B (`IsNull=true + PropertySet`).
- **Bus Arbitration bắt buộc** — trên RS485 half-duplex, lệnh ghi (Write) luôn được ưu tiên trước lệnh đọc (Poll) thông qua `WriteQueue` lock-free. Không có collision Modbus.
- **G1 & G2: Read-Only tuyệt đối** — không có module nào ở Tầng 2A ghi xuống thiết bị trường trong G1 và G2. Module M09 Command Handler chỉ được kích hoạt từ G3 khi đủ điều kiện tiên quyết.
- **Firmware không thay thế được bằng flow tool** — firmware .NET/C# được lựa chọn thay vì Node-RED để đảm bảo type-safety, kiểm soát hoàn toàn protocol driver, bus arbitration native và hiệu năng NCalc expression.
- **Flash-Wear awareness** — SQLite local buffer ghi qua `tmpfs` RAM trước khi flush xuống eMMC/SSD để giảm số lần ghi vật lý. Cấu hình `sync_interval` để cân bằng giữa bền vững dữ liệu và tuổi thọ flash.

---

## Chính sách định tuyến thiết bị MQTT tại Tầng 1

Tầng 1 bao gồm cả thiết bị sử dụng giao thức MQTT Native (không dùng Modbus hay BACnet). Đây là điểm cần làm rõ trong kiến trúc vì có **hai luồng xử lý khác nhau** tùy loại thiết bị, và lựa chọn sai sẽ làm mất toàn bộ lợi ích của pipeline biên.

### Hai luồng xử lý thiết bị MQTT

```
LUỒNG A — Qua Gateway (MqttNativeAdapter)
──────────────────────────────────────────
[Sensor MQTT]
    │ publish topic riêng (ví dụ: factory/sensor/temp-01)
    │ format JSON hoặc proprietary, KHÔNG phải Sparkplug B
    ▼
[Gateway 2A — MqttNativeAdapter subscribe topic đó]
    │ đọc value → RawTagBatch (tương tự đọc Modbus register)
    ▼
[Pipeline M04 → M05 → M06 → M07 bình thường]
    │ CT/PT norm · Virtual Tag · TagQuality · Deadband · Buffer 72h
    ▼
[EMQX OT Broker — Tầng 2B]
    │ Sparkplug B chuẩn hóa, đầy đủ quality annotation


LUỒNG B — Bypass Gateway (publish thẳng EMQX OT)
──────────────────────────────────────────────────
[Thiết bị MQTT Sparkplug B native]
    │ publish spBv1.0/{group}/{deviceId}/DDATA (Protobuf)
    │ tự quản lý DBIRTH / DDEATH / IsNull / quality
    ▼
[EMQX OT Broker — Tầng 2B]  ← KHÔNG qua Gateway
    │ Bridge TLS → Tầng 3
    ▼
[Tầng 3 — Business Server]
```

### Nguyên tắc quyết định: Qua Gateway hay Bypass?

| Tiêu chí | Qua Gateway (Luồng A) | Bypass Gateway (Luồng B) |
|---|---|---|
| Format payload | JSON, CSV, proprietary, binary custom | **Sparkplug B v1.0 đúng spec** |
| Birth / Death Certificate | Không tự quản lý | Tự publish DBIRTH / DDEATH |
| TagQuality / IsNull | Không có hoặc không chuẩn | Implement đầy đủ IsNull + PropertySet |
| CT/PT normalization | Cần Gateway xử lý | Đã tự nhân hệ số trước khi publish |
| Kết nối mạng | Không ổn định, cần proxy | Kết nối ổn định, IP cố định |
| Local buffer khi mất mạng | Cần Gateway buffer | Tự buffer hoặc chấp nhận mất |
| Ví dụ thiết bị | Cảm biến IoT giá rẻ, logger tự chế, thiết bị publish JSON đơn giản | Thiết bị IIoT cao cấp với firmware Sparkplug B tích hợp sẵn |

> **Nguyên tắc bất biến:** Nếu thiết bị đã tự publish **Sparkplug B đúng spec** — có DBIRTH với đầy đủ metric definition, có DDEATH khi offline, có `IsNull=true + PropertySet` khi giá trị xấu — thì cho bypass thẳng lên EMQX OT, không cần qua Gateway. **Mọi trường hợp còn lại bắt buộc qua Gateway** để đảm bảo tính đồng nhất của pipeline và không để dữ liệu thiếu quality annotation lọt vào Tầng 3.

### Hệ quả kiến trúc khi thiết bị MQTT bypass Gateway

Khi thiết bị MQTT publish thẳng lên EMQX OT (Luồng B), cần lưu ý:

- **EMQX OT Broker (Tầng 2B) phải cấu hình ACL** cho phép client ID của thiết bị đó publish lên topic `spBv1.0/#` — mặc định EMQX deny-by-default
- **Tầng 2B `BridgeMonitor`** theo dõi Birth/Death của thiết bị bypass tương tự Gateway — không phân biệt nguồn gốc, chỉ cần đúng Sparkplug B spec
- **Không có Local Buffer 72h tại biên** cho thiết bị bypass — nếu EMQX OT restart, dữ liệu trong khoảng thời gian đó mất (trừ khi thiết bị tự buffer và republish)
- **Tầng 3 không phân biệt** dữ liệu từ Gateway hay thiết bị bypass — miễn là Sparkplug B đúng spec, pipeline decode hoạt động như nhau

### `MqttNativeAdapter` trong Module 2A-3

Gateway subscribe topic MQTT của thiết bị Luồng A như sau:

```
appsettings / DeviceTemplate config:
{
  "device_id": "sensor-temp-zone-b",
  "protocol": "MqttNative",
  "connection": {
    "broker_host": "localhost",   ← subscribe EMQX OT cùng máy (G1/G2)
    "broker_port": 1883,          ← hoặc IP thiết bị nếu thiết bị có broker riêng
    "subscribe_topic": "factory/zone-b/temperature",
    "payload_format": "JSON",
    "value_json_path": "$.temperature"
  },
  "ct_ratio": 1,
  "pt_ratio": 1,
  "poll_cycle_ms": 0,             ← 0 = event-driven (không poll, chờ message)
  ...
}
```

`MqttNativeAdapter` hoạt động theo mô hình **event-driven** (push) thay vì poll (pull) như Modbus — khi nhận message MQTT từ thiết bị, tạo `RawTagBatch` và phát `MetricsBatchReadyEvent` ngay lập tức vào pipeline. Deadband và Quality Check vẫn áp dụng bình thường.

---

## Đặc điểm phần cứng Tầng 2A

| Yêu cầu | Thông số |
|---|---|
| Form factor | Industrial PC (x86) hoặc ARM SBC |
| Tiêu chuẩn bảo vệ | IP54 tối thiểu (lắp trong tủ điện) |
| Nhiệt độ vận hành | -20°C đến +70°C |
| Nguồn điện | 12V/24V DC, hỗ trợ UPS mini |
| Lưu trữ | eMMC 32GB+ hoặc SSD industrial (DWPD ≥ 1) |
| RAM | 2GB+ (tmpfs buffer + .NET runtime) |
| Kết nối OT | RS485 (Modbus RTU) · RJ45 LAN (Modbus TCP, BACnet/IP, MQTT Native Subscribe) |
| Watchdog | Hardware Watchdog Timer bắt buộc — hard-reset khi OS treo |
| NTP | Đồng bộ thời gian liên tục; cảnh báo khi drift > 5 giây |
| Uptime yêu cầu | 99,9% — không restart tùy tiện |

---

## Quan hệ với Tầng 2B — Chiến lược Contract

Tầng 2A và Tầng 2B là hai **.NET Solution độc lập**. Điểm kết nối duy nhất là giao thức **MQTT Sparkplug B** qua EMQX OT Broker.

### Không chia sẻ code trực tiếp

Tầng 2A **không** reference `EMS.Contracts` (NuGet Tầng 3). Lý do:
- Tầng 2A phải hoàn toàn độc lập với Tầng 3 về mặt dependency
- Sparkplug B spec là "contract" đủ để giao tiếp — không cần thêm shared DTO
- Lifecycle nâng cấp firmware (OTA cẩn thận) khác hẳn với Tầng 3 (maintenance window)

### Package nội bộ Tầng 2A: `EMS.Gateway.Contracts` (NuGet nội bộ)

Package được publish từ solution Tầng 2A lên private NuGet feed nội bộ để các project trong cùng solution dùng.

```
EMS.Gateway.Contracts (NuGet nội bộ)
├── Publish bởi  : EMS.IIoTGateway solution (Tầng 2A)
├── Consume bởi  : Các module trong cùng solution
└── Versioning   : SemVer — không phụ thuộc version Tầng 2B hay Tầng 3
```

### Giao tiếp với Tầng 2B (MQTT Sparkplug B)

| Topic | Hướng | Nội dung |
|---|---|---|
| `spBv1.0/{group}/{deviceId}/DBIRTH` | 2A → 2B | Birth Certificate — khai báo metrics, metadata, CT/PT ratio |
| `spBv1.0/{group}/{deviceId}/DDATA` | 2A → 2B | Metric values (Protobuf) — Good/Bad/Stale annotated |
| `spBv1.0/{group}/{deviceId}/DDEATH` | 2A → 2B | Death Certificate — Gateway offline hoặc mất kết nối |
| `spBv1.0/{group}/{deviceId}/CONFIG` | 2B → 2A | Config update từ Config Channel (G2) |
| `spBv1.0/{group}/{deviceId}/DCMD` | 2B → 2A | Device Command từ Business Server (G3 only) |

---

## Tổ chức Solution

```
EMS.IIoTGateway/                              ← .NET Solution root
├── src/
│   ├── EMS.Gateway.Contracts/                ← Module 2A-1: Interface & DTO nội bộ Tầng 2A
│   ├── EMS.Gateway.DeviceTemplate/           ← Module 2A-2: Device Template & Config Manager
│   ├── EMS.Gateway.ProtocolAdapter/          ← Module 2A-3: Protocol Adapter (Modbus/BACnet/MQTT)
│   ├── EMS.Gateway.PollingEngine/            ← Module 2A-4: Polling Engine & TagDatabase
│   ├── EMS.Gateway.EdgeRuleEngine/           ← Module 2A-5: Edge Rule Engine (Virtual Tags)
│   ├── EMS.Gateway.QualityChecker/           ← Module 2A-6: TagQuality Checker & Annotator
│   ├── EMS.Gateway.SparkplugEncoder/         ← Module 2A-7: Sparkplug B Encoder & Publisher
│   ├── EMS.Gateway.LocalBuffer/              ← Module 2A-8: Local Buffer (tmpfs + SQLite 72h)
│   ├── EMS.Gateway.AdminService/             ← Module 2A-9: Admin — NTP, Watchdog, Health, OTA
│   ├── EMS.Gateway.CommandHandler/           ← Module 2A-10: Command Handler (G3 only)
│   └── EMS.Gateway.Host/                     ← Module 2A-11: Host — Composition Root
├── tests/
│   ├── EMS.Gateway.DeviceTemplate.Tests/
│   ├── EMS.Gateway.ProtocolAdapter.Tests/
│   ├── EMS.Gateway.PollingEngine.Tests/
│   ├── EMS.Gateway.EdgeRuleEngine.Tests/
│   ├── EMS.Gateway.QualityChecker.Tests/
│   ├── EMS.Gateway.SparkplugEncoder.Tests/
│   ├── EMS.Gateway.LocalBuffer.Tests/
│   └── EMS.Gateway.CommandHandler.Tests/
└── EMS.IIoTGateway.sln
```

> **Infrastructure artifacts** (systemd unit files, MQTT broker address config, deploy/OTA scripts, cert-renew scripts) nằm trong thư mục `deploy/` cùng cấp solution — là artifact vận hành, không phải module phần mềm.

---

## Tóm tắt danh sách Module Tầng 2A

| # | Module | Loại | Nhóm | Giai đoạn |
|---|---|---|---|---|
| 2A-1 | `EMS.Gateway.Contracts` | Class Library (.NET 8) | Foundation | G1 |
| 2A-2 | `EMS.Gateway.DeviceTemplate` | Class Library (.NET 8) | Foundation | G1 |
| 2A-3 | `EMS.Gateway.ProtocolAdapter` | Worker Service (.NET 8) | Thu thập | G1 |
| 2A-4 | `EMS.Gateway.PollingEngine` | Worker Service (.NET 8) | Thu thập | G1 |
| 2A-5 | `EMS.Gateway.EdgeRuleEngine` | Class Library (.NET 8) | Xử lý biên | G1 |
| 2A-6 | `EMS.Gateway.QualityChecker` | Class Library (.NET 8) | Xử lý biên | G1 |
| 2A-7 | `EMS.Gateway.SparkplugEncoder` | Worker Service (.NET 8) | Xuất bản | G1 |
| 2A-8 | `EMS.Gateway.LocalBuffer` | Worker Service (.NET 8) | Xuất bản | G1 |
| 2A-9 | `EMS.Gateway.AdminService` | Worker Service (.NET 8) | Vận hành | G1 |
| 2A-10 | `EMS.Gateway.CommandHandler` | Worker Service (.NET 8) | Điều khiển | G3 |
| 2A-11 | `EMS.Gateway.Host` | Worker Host (.NET 8) | Host | G1 |

---

## Nhóm 0 — Nền tảng (Foundation)

---

### Module 2A-1 — `EMS.Gateway.Contracts`

**Trách nhiệm cốt lõi:** Định nghĩa toàn bộ Interface, DTO, Domain Event và Enum nội bộ của solution Tầng 2A. Đóng vai trò "ngôn ngữ chung" giữa các module Worker Service — không chứa bất kỳ logic hay infrastructure code nào. Tương tự `EMS.IIoTServer.Contracts` của Tầng 2B nhưng phản ánh trách nhiệm của firmware biên.

**Giao diện / Hợp đồng (Core Contracts):**

*Interfaces chính:*
- `IDeviceTemplateRepository` — load, reload và truy vấn device template từ JSON config; cung cấp `GetTemplate(deviceId)`, `GetAllTemplates()`, `ReloadAsync()` (khi nhận config update từ G2)
- `IProtocolAdapter` — abstraction cho mọi protocol driver; `PollAsync(device, registers)` trả về `RawTagBatch`; `WriteAsync(device, register, value)` dành cho M09 (G3)
- `IPollingEngine` — điều phối lịch poll theo cycle time từng device; phát event `MetricsBatchReady` lên `IInternalEventBus`
- `IEdgeRuleEngine` — nhận `RawTagBatch`, tính virtual tag qua NCalc expression, trả về `EnrichedTagBatch`
- `IQualityChecker` — annotate `TagQuality` (Good/Bad/Stale) cho từng metric trong `EnrichedTagBatch`
- `ISparkplugEncoder` — encode `EnrichedTagBatch` thành Sparkplug B Protobuf payload (DDATA/DBIRTH/DDEATH)
- `ILocalBuffer` — enqueue payload, replay khi kết nối phục hồi, prune record cũ; interface tương tự `IStoreAndForwardEngine` của Tầng 2B nhưng độc lập hoàn toàn
- `IMqttPublisher` — publish Sparkplug B payload lên EMQX OT Broker; tách biệt khỏi business logic để dễ mock trong unit test
- `IAdminService` — health check, NTP drift monitor, Watchdog heartbeat, system log
- `ICommandHandler` — (G3) validate DCMD, enforce whitelist, gọi `IProtocolAdapter.WriteAsync`

*DTOs chính:*
- `DeviceTemplateDto` — JSON mapping cho device config: `device_id`, `protocol`, `connection_string`, `registers[]`, `ct_pt_ratio`, `poll_cycle_ms`, `virtual_tag_expressions[]`, `command_whitelist[]`
- `RegisterDefinitionDto` — định nghĩa từng register: `address`, `function_code`, `data_type`, `scale_factor`, `unit`, `deadband`
- `RawTagDto` — giá trị thô sau khi đọc register: `tag_name`, `raw_value`, `timestamp_utc_ns`
- `RawTagBatch` — tập hợp `RawTagDto` từ một poll cycle của một device
- `EnrichedTagDto` — giá trị đã chuẩn hóa + virtual tag: `tag_name`, `physical_value`, `unit`, `quality: TagQuality`, `timestamp_utc_ns`
- `EnrichedTagBatch` — đầu ra sau M05 + M13 + M06 Quality Check
- `SparkplugPayloadDto` — Protobuf binary payload + metadata cho buffer và publish
- `CommandRequestDto` — DCMD payload: `dcmd_id`, `device_id`, `register_address`, `value`, `jwt_token`, `timestamp_utc_ns`
- `GatewayHealthDto` — trạng thái tổng hợp: NTP drift, buffer pending, MQTT connection, device count

*Domain Events nội bộ (qua `IInternalEventBus`):*
- `MetricsBatchReadyEvent` — Polling Engine → Edge Rule Engine → Quality Checker → Encoder
- `TagQualityDegradedEvent` — Quality Checker phát khi một tag chuyển từ Good sang Bad/Stale
- `DeviceUnresponsiveEvent` — Polling Engine phát sau N lần poll thất bại liên tiếp
- `MqttConnectionLostEvent` — MQTT Publisher phát khi mất kết nối EMQX OT Broker
- `MqttConnectionRestoredEvent` — MQTT Publisher phát khi kết nối phục hồi → trigger Replay
- `ConfigReloadRequestedEvent` — Admin Service phát khi nhận config update từ G2
- `CommandReceivedEvent` — (G3) MQTT subscriber phát khi nhận DCMD topic
- `NtpDriftExceededEvent` — Admin Service phát khi drift > 5 giây

*Enum:*
- `TagQuality` — `Good`, `Bad`, `Stale`
- `ProtocolType` — `ModbusRTU`, `ModbusTCP`, `BACnetIP`, `MqttNative`, `Custom`
- `CommandResult` — `Accepted`, `RejectedWhitelist`, `RejectedRangeExceeded`, `RejectedJwtInvalid`, `DeviceError`

**Công nghệ & Design Pattern:** Pure C# interfaces và records — không phụ thuộc framework. Shared Kernel pattern nội bộ Tầng 2A. `IInternalEventBus` dùng `System.Threading.Channels` Bounded Channel — type-safe, lock-free, không circular dependency. Subscriber đăng ký tại Host (Module 2A-11) khi khởi động.

**Chiến lược Nâng cấp:** Chỉ thêm interface/DTO mới, không sửa đã publish. Khi thêm protocol mới (OPC UA, IEC 61850), chỉ thêm implementation `IProtocolAdapter` mới — interface không thay đổi. Khi thêm event type mới, tạo Bounded Channel riêng tại Host — channel đang chạy không bị ảnh hưởng.

---

### Module 2A-2 — `EMS.Gateway.DeviceTemplate`

**Trách nhiệm cốt lõi:** Load, validate, parse và cung cấp device template từ file JSON config. Đây là "nguồn sự thật duy nhất" cho toàn bộ firmware về cấu hình thiết bị: địa chỉ Modbus, CT/PT ratio, deadband, chu kỳ poll, whitelist command và biểu thức NCalc cho virtual tag. Hỗ trợ hot-reload khi nhận config update từ Config Channel (G2) mà không restart firmware.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IDeviceTemplateRepository`:
  - `GetTemplate(deviceId)` — trả `DeviceTemplateDto` hoặc null nếu không tồn tại
  - `GetAllTemplates()` — trả toàn bộ template đang active
  - `ReloadAsync(newConfigJson)` — hot-reload từ JSON mới (từ Config Channel G2): validate → atomic swap → phát `ConfigReloadRequestedEvent` lên `IInternalEventBus`; rollback về config cũ nếu validation thất bại; không restart firmware
  - `GetCommandWhitelist(deviceId)` — (G3) trả danh sách register address được phép ghi, giới hạn vật lý min/max
- Validate cấu hình đầu vào:
  - `device_id` duy nhất trong toàn bộ config
  - `ct_pt_ratio` phải > 0, hợp lệ theo giá trị standard (5:1, 10:1, ..., 3000:5, ...)
  - `poll_cycle_ms` tối thiểu 500ms — không cho phép poll nhanh hơn để tránh overload RS485 bus
  - `virtual_tag_expressions` hợp lệ NCalc — pre-compile tại load time, báo lỗi ngay nếu syntax sai
  - `deadband` ≥ 0; `scale_factor` != 0
- Theo dõi lịch sử config change: lưu timestamp và hash của mỗi lần reload vào local append-only log

**Công nghệ & Design Pattern:** `System.Text.Json` deserialization với custom converter cho CT/PT ratio. FluentValidation cho validation rules phức tạp. `ImmutableDictionary<string, DeviceTemplateDto>` làm in-memory store — atomic swap đảm bảo thread-safe khi hot-reload (không lock). Pre-compile NCalc expression tại `ReloadAsync` để phát hiện lỗi syntax sớm và tăng hiệu năng khi tính toán (không compile runtime mỗi lần poll).

**Chiến lược Nâng cấp:** Thêm field mới vào `DeviceTemplateDto` (minor bump) — validation chỉ kiểm tra field bắt buộc, field mới optional không làm hỏng config cũ. Khi thêm protocol mới cần field đặc thù, dùng `AdditionalProperties` dictionary trong DTO — không thay đổi schema cứng. Config history log dùng NDJSON append-only, giữ 30 version gần nhất.

---

## Nhóm 1 — Thu thập dữ liệu (Data Collection)

---

### Module 2A-3 — `EMS.Gateway.ProtocolAdapter`

**Trách nhiệm cốt lõi:** Triển khai toàn bộ driver giao thức OT — Modbus RTU (RS485), Modbus TCP, BACnet/IP và MQTT Native Subscribe. Cung cấp interface thống nhất `IProtocolAdapter` để Polling Engine không cần biết giao thức bên dưới. Quản lý `WriteQueue` lock-free để đảm bảo Bus Arbitration trên RS485 half-duplex: lệnh ghi (FC06/FC16) luôn được xử lý trước lệnh đọc (FC03/FC04) khi có xung đột.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IProtocolAdapter` cho từng protocol:
  - `ModbusRtuAdapter` — RS485 serial port, hỗ trợ FC01/FC02/FC03/FC04 (đọc) và FC06/FC16 (ghi G3)
  - `ModbusTcpAdapter` — TCP/IP socket, kết nối persistent với retry exponential backoff
  - `BACnetIpAdapter` — BACnet/IP (UDP 47808), đọc Present Value qua ReadProperty/ReadPropertyMultiple
  - `MqttNativeAdapter` — subscribe topic từ thiết bị publish MQTT trực tiếp (không qua Modbus)
  - `CustomPluginAdapter` — (extension point) load plugin DLL theo interface — cho thiết bị proprietary
- `PollAsync(device, registers)` — đọc danh sách register theo batch, trả `RawTagBatch` với timestamp UTC nanosecond tại thời điểm đọc thực tế (không phải thời điểm schedule)
- `WriteAsync(device, register, value)` — (G3) ghi qua `WriteQueue` Channel; chỉ được gọi từ M09 Command Handler; **không bao giờ được gọi từ Polling Engine hay Edge Rule Engine**

**WriteQueue Bus Arbitration (RS485):**
```
WriteQueue (lock-free Channel<WriteCommand>)
     │
     ├─ Có pending Write? → Thực hiện Write (FC06/FC16) ngay
     │                      → Ghi Gateway Audit Log (G3)
     │                      → Gửi ACK/NACK qua IInternalEventBus
     │
     └─ Không có Write   → Thực hiện Poll (FC03/FC04) theo lịch
```
- `WriteQueue` implement bằng `System.Threading.Channels` Bounded Channel (`BoundedChannelFullMode.Wait`)
- Arbitration logic chạy trong single-threaded loop để đảm bảo serial bus không bao giờ có 2 master cùng lúc
- Timeout per request: cấu hình trong `appsettings.json` (mặc định Modbus RTU: 500ms, TCP: 1000ms)

**Xử lý lỗi & TagQuality:**
- Timeout sau N lần retry → trả `RawTagDto` với `Quality=Bad`, `raw_value=null`
- CRC error (Modbus RTU) → retry tối đa 3 lần, sau đó `Quality=Bad`
- Thiết bị không phản hồi liên tiếp > T giây → phát `DeviceUnresponsiveEvent`
- Kết nối TCP/IP lost → reconnect với exponential backoff (1s, 2s, 4s, tối đa 30s)

**Công nghệ & Design Pattern:** `NModbus4` hoặc `FluentModbus` làm Modbus library. `System.IO.BACnet` cho BACnet/IP. `MQTTnet` cho MQTT Native Adapter. `System.Threading.Channels` cho `WriteQueue`. Strategy Pattern: `IProtocolAdapter` factory tạo đúng implementation theo `ProtocolType` trong template. Retry Policy dùng `Polly` với circuit breaker — tránh flood retry khi thiết bị hỏng.

**Chiến lược Nâng cấp:** Thêm protocol mới chỉ cần implement `IProtocolAdapter` mới và đăng ký trong factory — các module khác không thay đổi. Khi nâng cấp Modbus library, chỉ sửa nội bộ `ModbusRtuAdapter` — interface không thay đổi. [G3] OPC UA Adapter được thêm vào đây khi cần, không phải module riêng.

---

### Module 2A-4 — `EMS.Gateway.PollingEngine`

**Trách nhiệm cốt lõi:** Điều phối lịch poll cho toàn bộ thiết bị theo chu kỳ cấu hình trong `DeviceTemplateDto`. Phát event `MetricsBatchReadyEvent` lên `IInternalEventBus` sau mỗi poll cycle thành công. Áp dụng **Deadband Filter** để chỉ forward giá trị khi thay đổi vượt ngưỡng — giảm lưu lượng publish xuống EMQX OT. Duy trì `TagDatabase` snapshot — trạng thái cuối cùng của mọi tag để phát hiện `Stale`.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IPollingEngine`:
  - `StartAsync()` — tạo polling loop cho từng device theo `poll_cycle_ms` trong template
  - `StopAsync()` — graceful stop, flush tag batch đang pending
  - `GetTagSnapshot(deviceId)` — trả trạng thái hiện tại của tất cả tag thuộc device (dùng bởi Admin Health Check và DBIRTH)
- `TagDatabase` — in-memory snapshot (`ImmutableDictionary<string, EnrichedTagDto>`):
  - Cập nhật sau mỗi poll cycle hoàn thành
  - Không lock khi đọc — `ImmutableDictionary` thread-safe by design
  - Dùng để phát hiện `Stale`: tag không được cập nhật trong `stale_timeout_ms` → Quality chuyển sang `Stale`
- **Deadband Filter:**
  - `|new_value - last_forwarded_value| > deadband` → forward
  - Bất kể deadband, luôn forward mỗi `heartbeat_interval_s` (mặc định 60s) để Tầng 3 biết thiết bị vẫn alive
  - `Quality=Bad` hoặc `Quality=Stale` luôn forward ngay — không áp dụng deadband
- **Stale Detection:**
  - Mỗi tag có `last_updated_utc_ns` trong TagDatabase
  - Background timer 1s kiểm tra toàn bộ tag, tag nào quá `stale_timeout_ms` → Quality = Stale → phát `MetricsBatchReadyEvent` với Stale annotation
- Lịch poll multi-device: mỗi device chạy độc lập trong `Task` riêng (không dùng `Task.Delay` thô — dùng `PeriodicTimer` .NET 6+ để tránh drift tích lũy)

**Công nghệ & Design Pattern:** `System.Threading.PeriodicTimer` cho polling loop không drift. `ImmutableDictionary` cho TagDatabase snapshot. `System.Threading.Channels` cho `MetricsBatchReadyEvent` handoff sang Edge Rule Engine. Deadband logic đơn giản — không cần thư viện ngoài. Subscribe `ConfigReloadRequestedEvent` để reload lịch poll khi template thay đổi (G2 hot-reload).

**Chiến lược Nâng cấp:** Thêm thiết bị mới chỉ cần thêm entry trong JSON template và hot-reload — không restart firmware. Điều chỉnh `poll_cycle_ms`, `deadband`, `stale_timeout_ms` qua config update G2 mà không rebuild. Khi cần poll priority (thiết bị tổng quan trọng hơn nhánh), thêm `priority` field vào `DeviceTemplateDto` và điều chỉnh scheduler — interface không thay đổi.

---

## Nhóm 2 — Xử lý tại biên (Edge Processing)

---

### Module 2A-5 — `EMS.Gateway.EdgeRuleEngine`

**Trách nhiệm cốt lõi:** Tính toán Virtual Tag tại biên — nhận `RawTagBatch` từ Polling Engine, áp dụng CT/PT ratio normalization và NCalc expression để tạo ra các metric dẫn xuất (OEE, SEI, tỷ lệ công suất, cosφ tính toán...) mà không cần Business Server. Kết quả là `EnrichedTagBatch` chứa cả physical tag đã chuẩn hóa và virtual tag sẵn sàng cho Quality Check.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IEdgeRuleEngine`:
  - `ProcessAsync(rawBatch)` → `EnrichedTagBatch`
  - `PrecompileExpressions(templates)` — compile toàn bộ NCalc expression từ template khi startup hoặc hot-reload; không compile runtime mỗi lần poll
  - `ValidateExpression(expression)` — kiểm tra syntax NCalc, trả danh sách tag tham chiếu chưa tồn tại
- **CT/PT Normalization** (thực hiện tại đây, không tại Tầng 3):
  - `physical_value = raw_value × ct_ratio × pt_ratio × scale_factor`
  - Nếu bất kỳ tag tham chiếu trong expression có `Quality ≠ Good` → virtual tag kết quả tự động `Quality = Bad` (quality propagation)
- **Virtual Tag Computation (NCalc):**
  - Ví dụ: `OEE = (run_time / available_time) × (good_pieces / total_pieces) × (actual_output / theoretical_output)`
  - Ví dụ: `SEI = total_kWh / production_count` (kWh/sản phẩm)
  - Ví dụ: `cosφ = kW / kVA` (tính từ P và S đo được)
  - Ví dụ: `THD_alert = THDv > 5.0 ? 1 : 0` (flag binary)
  - NCalc expression được compile 1 lần tại startup / hot-reload → chỉ `Evaluate()` khi poll (không overhead compile)
- **Quality Propagation:**
  - Virtual tag inherit quality xấu nhất của các tag đầu vào
  - `ALL(inputs) = Good` → virtual tag = `Good`
  - `ANY(inputs) = Bad` → virtual tag = `Bad`
  - `ANY(inputs) = Stale, NONE = Bad` → virtual tag = `Stale`

**Công nghệ & Design Pattern:** `NCalc2` (NuGet) — thư viện tính toán expression .NET, compile-once evaluate-many. Expression được lưu compiled dưới dạng `Expression` object trong `ImmutableDictionary<string, Expression>`. Subscribe `ConfigReloadRequestedEvent` để recompile expression khi config update. Pure transformation — không có I/O, không có side effect, không lưu state giữa các batch. Dễ unit test hoàn toàn.

**Chiến lược Nâng cấp:** Thêm virtual tag mới chỉ cần thêm expression vào JSON template và reload — không rebuild. Khi cần logic phức tạp hơn NCalc (ARIMA, regression), thêm custom NCalc function bằng `EvaluateFunction` callback — không thay đổi interface. [G3] Nếu cần ML inference tại biên (ONNX Runtime), thêm `IMlInferenceAdapter` song song với `IEdgeRuleEngine` — không thay thế module này.

---

### Module 2A-6 — `EMS.Gateway.QualityChecker`

**Trách nhiệm cốt lõi:** Annotate `TagQuality` (Good/Bad/Stale) cho từng metric trong `EnrichedTagBatch`. Kiểm tra nhiều loại bất thường: vượt range vật lý, giá trị bị kẹt (stuck), tốc độ thay đổi bất thường (Rate of Change), và Stale từ Polling Engine. Đây là cổng kiểm soát cuối cùng trước khi dữ liệu được encode Sparkplug B — không có giá trị nào thoát khỏi module này mà không có quality annotation rõ ràng.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IQualityChecker`:
  - `CheckAsync(enrichedBatch)` → `EnrichedTagBatch` (với `TagQuality` đã annotate trên từng tag)
  - Không thay đổi value, không lọc bỏ tag — chỉ annotate quality
- **Các kiểm tra Quality:**
  - **Range Check:** `physical_value < range_min` hoặc `> range_max` (từ template) → `Quality = Bad`, ghi `reason = "RangeExceeded"`
  - **Stuck Check:** giá trị không thay đổi trong `stuck_timeout_ms` (cấu hình per-tag) với `Quality` đang là `Good` → `Quality = Bad`, ghi `reason = "StuckValue"`. Không áp dụng cho tag có `allow_stuck = true` (ví dụ: setpoint cố định)
  - **Rate of Change (RoC):** `|delta_value / delta_time| > roc_limit` → `Quality = Bad`, ghi `reason = "RoCExceeded"`. Bắt các spike bất thường do nhiễu RS485 hoặc CRC error lọt qua retry
  - **Null / NaN Check:** giá trị null (từ timeout poll) hoặc NaN (từ chia cho 0 trong NCalc) → `Quality = Bad`
  - **Stale:** tag không được cập nhật trong `stale_timeout_ms` (từ Polling Engine TagDatabase) → `Quality = Stale`
- Phát `TagQualityDegradedEvent` lên `IInternalEventBus` khi tag chuyển từ `Good` → `Bad` hoặc `Good` → `Stale` (không phát lại liên tục khi đã ở trạng thái xấu)
- **Không lọc bỏ Bad/Stale:** dữ liệu xấu phải được encode thành Sparkplug B `IsNull=true + PropertySet` để Tầng 3 biết lý do — tránh Tầng 3 nhầm "không có dữ liệu" với "dữ liệu xấu"

**Công nghệ & Design Pattern:** Pure transformation — không có I/O, không có state ngoài TagDatabase snapshot (inject qua `IPollingEngine.GetTagSnapshot`). `ImmutableDictionary` cho per-tag quality history (phát hiện transition). Kiểm tra RoC cần lưu giá trị trước — `ConcurrentDictionary<string, double>` làm sliding window size=1. Tất cả threshold cấu hình trong `appsettings.json` per-tag hoặc per-device-type, không hard-code.

**Chiến lược Nâng cấp:** Thêm loại kiểm tra mới (ví dụ: cross-tag consistency check — tổng nhánh > tổng cái) chỉ cần thêm method private trong `CheckAsync` loop — không thay đổi interface. Điều chỉnh ngưỡng per-tag qua config update G2, không rebuild.

---

## Nhóm 3 — Xuất bản dữ liệu (Data Publishing)

---

### Module 2A-7 — `EMS.Gateway.SparkplugEncoder`

**Trách nhiệm cốt lõi:** Encode `EnrichedTagBatch` (sau Quality Check) thành Sparkplug B Protobuf binary payload theo đúng spec Sparkplug B v1.0. Quản lý vòng đời Birth/Death Certificate. Đảm bảo `IsNull=true + PropertySet` cho mọi metric `Bad` hoặc `Stale`. Publish payload lên EMQX OT Broker qua `IMqttPublisher`. Phối hợp với `ILocalBuffer` để đảm bảo không mất dữ liệu khi MQTT connection gián đoạn.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `ISparkplugEncoder`:
  - `EncodeDData(enrichedBatch)` → `SparkplugPayloadDto` (DDATA Protobuf binary)
  - `EncodeDBirth(deviceId, templates)` → `SparkplugPayloadDto` (DBIRTH — khai báo toàn bộ metric, data type, engineering unit, CT/PT ratio trong PropertySet)
  - `EncodeDDeath(deviceId)` → `SparkplugPayloadDto` (DDEATH — Gateway offline)
  - `PublishAsync(payload)` — gửi qua `IMqttPublisher`; nếu MQTT disconnected → enqueue vào `ILocalBuffer`
- **Quy tắc encode TagQuality → Sparkplug B:**
  - `Good` → `SetMetricValue(value)` — giá trị thực, `is_null = false`
  - `Bad` → `is_null = true`, `PropertySet` chứa `{"quality": "Bad", "reason": "<reason>", "timestamp_ns": ...}`
  - `Stale` → `is_null = true`, `PropertySet` chứa `{"quality": "Stale", "stale_since_ns": ...}`
- **DBIRTH — Birth Certificate:**
  - Publish khi firmware khởi động (trước DDATA đầu tiên)
  - Chứa: toàn bộ metric definitions (tên, data type, unit), CT/PT ratio, firmware version, hardware ID
  - Tầng 2B dùng DBIRTH để auto device discovery — không cần cấu hình thủ công trên Broker
  - Re-publish DBIRTH khi nhận `ConfigReloadRequestedEvent` (G2 hot-reload template)
- **Sequence Number:** `seq` field tăng monotonic 0–255 (wrap around) theo Sparkplug B spec — dùng cho deduplication tại Tầng 3
- **Timestamp:** UTC nanosecond từ `RawTagDto.timestamp_utc_ns` — thời điểm đọc thực tế, không phải thời điểm encode

**Công nghệ & Design Pattern:** `Eclipse.Tahu.Protobuf` (.NET NuGet) — thư viện Sparkplug B chính thức, không tự viết Protobuf schema. `MQTTnet` cho `IMqttPublisher` implementation. Subscribe `MqttConnectionRestoredEvent` từ `IInternalEventBus` để trigger replay từ `ILocalBuffer`. Subscribe `MqttConnectionLostEvent` để chuyển sang buffer-only mode. QoS 1 (at-least-once) cho DDATA — deduplication tại Tầng 3 qua `(seq + device_id + timestamp_ns)`.

**Chiến lược Nâng cấp:** Khi Sparkplug B ra spec mới (v2.0), tạo `SparkplugV2Encoder` implement cùng `ISparkplugEncoder` — version negotiation tại DBIRTH. Thêm metric mới vào DBIRTH chỉ cần thêm entry trong template JSON — không thay đổi encoder logic. Custom PropertySet fields thêm qua config, không hard-code trong encoder.

---

### Module 2A-8 — `EMS.Gateway.LocalBuffer`

**Trách nhiệm cốt lõi:** Đảm bảo dữ liệu Sparkplug B payload không bị mất khi MQTT connection đến EMQX OT Broker gián đoạn (trong kịch bản G1/G2 co-located, EMQX OT có thể restart; trong G3, network OT có thể đứt). Buffer tối đa 72 giờ, circular (xóa record cũ nhất khi đầy), replay theo đúng thứ tự timestamp khi kết nối phục hồi.

**Lưu ý kiến trúc G1/G2 vs G3:**
- **G1/G2 (co-located):** EMQX OT Broker cùng máy với Gateway. Local buffer chủ yếu bảo vệ khi EMQX OT restart ngắn (<5 phút) hoặc khi MQTT Bridge đến Tầng 3 gián đoạn. Tầng 2B cũng có buffer riêng (72h) — hai lớp buffer bổ sung nhau, không loại trừ.
- **G3 (tách máy):** Gateway firmware publish qua mạng LAN OT đến EMQX OT Server riêng. Local buffer bảo vệ khi LAN OT đứt.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `ILocalBuffer`:
  - `EnqueueAsync(payload)` — MQTTnet publish fail → enqueue vào `System.Threading.Channels` Bounded Channel (producer side); SQLite writer loop đọc từ Channel (consumer side), flush theo batch — hai luồng tách biệt, không block lẫn nhau
  - `ReplayAsync()` — đọc SQLite theo `timestamp_utc_ns` tăng dần, publish qua `IMqttPublisher`; **chỉ replay khi `IMqttPublisher` báo connected** — không tự detect; được trigger bởi `MqttConnectionRestoredEvent`
  - `PruneAsync()` — xóa record cũ hơn 72 giờ (chạy mỗi 15 phút)
  - `GetPendingCount()` — số record đang chờ publish, expose cho Admin Health Check
- **Flash-Wear Protection:**
  - Ghi qua `tmpfs` RAM buffer trước (`/dev/shm` hoặc mount tmpfs riêng) — giảm số lần ghi trực tiếp lên eMMC/SSD
  - SQLite WAL mode — ghi không block đọc
  - `sync_interval_ms` cấu hình (mặc định 5000ms) — flush RAM → eMMC; cân bằng bền vững vs tuổi thọ flash
  - Graceful shutdown: drain Channel → SQLite WAL checkpoint trước khi tắt process (không mất dữ liệu đang trong RAM)
- Deduplication key cho Tầng 3: `(seq_number + device_id + timestamp_utc_ns)` — nhất quán với Tầng 2B

**Công nghệ & Design Pattern:** `System.Threading.Channels` Bounded Channel với `BoundedChannelFullMode.Wait` (backpressure nội bộ). `ArrayPool<byte>` cho Sparkplug B payload binary — zero-allocation, tránh GC pressure. `Microsoft.Data.Sqlite` với WAL mode. `tmpfs` mount cho RAM write buffer — cấu hình trong systemd mount unit. Subscribe `MqttConnectionRestoredEvent` để trigger `ReplayAsync`. Circuit breaker logic: nếu replay thất bại 3 lần liên tiếp → dừng replay, chờ event reconnect tiếp theo.

**Chiến lược Nâng cấp:** Tăng giới hạn buffer từ 72h qua `BufferRetentionHours` trong config — không rebuild. Swap SQLite sang LevelDB hoặc storage khác chỉ cần implement lại `ILocalBuffer` — không ảnh hưởng module khác. Schema migration versioned.

---

## Nhóm 4 — Vận hành & Giám sát (Operations & Admin)

---

### Module 2A-9 — `EMS.Gateway.AdminService`

**Trách nhiệm cốt lõi:** Đảm bảo Gateway hoạt động ổn định 24/7 — đồng bộ NTP, giám sát drift thời gian, kích hoạt Hardware Watchdog Timer, expose health check endpoint, ghi system log, và cung cấp Mock Data Mode cho testing tại văn phòng (không cần thiết bị thực). [G3] Điều phối OTA firmware update an toàn.

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `IAdminService`:
  - `GetHealthAsync()` → `GatewayHealthDto` — trạng thái tổng hợp: NTP drift, buffer pending, MQTT connection state, device count online, firmware version, uptime
  - `GetNtpDriftAsync()` → `TimeSpan` — lệch thời gian hiện tại so với NTP server
  - `TriggerWatchdogHeartbeat()` — ghi vào `/dev/watchdog` hoặc tương đương; phải được gọi định kỳ (mỗi 30s) hoặc hardware sẽ hard-reset thiết bị
- **NTP Watchdog:**
  - Background timer 30 giây kiểm tra NTP drift qua `chronyc tracking` hoặc `systemd-timesyncd` D-Bus API
  - Drift > 5 giây → phát `NtpDriftExceededEvent` → DBIRTH re-publish với timestamp cảnh báo → Ghi cảnh báo vào system log
  - Drift > 30 giây → annotate toàn bộ tag đang active là `Quality = Stale` (timestamp không đáng tin)
- **Hardware Watchdog Timer:**
  - Ghi heartbeat vào `/dev/watchdog` mỗi 30 giây (hoặc thời gian cấu hình watchdog driver)
  - Nếu process .NET bị treo và không ghi watchdog → hardware tự hard-reset board sau timeout (thường 60–120 giây)
  - Sử dụng `FileStream("/dev/watchdog", FileMode.Open)` + `Write(keepAliveToken)` theo Linux Watchdog API
- **Health Check Endpoint:**
  - `GET /health` — ASP.NET Core Health Checks: tổng hợp NTP drift, buffer fill %, MQTT state, device responsiveness
  - `GET /health/detail` — chi tiết từng device: last poll timestamp, quality distribution, pending commands (G3)
  - Dùng bởi external monitoring (Prometheus, Grafana, Zabbix) và Tầng 2B `BridgeMonitor`
- **Prometheus Metrics:**
  - `gateway_ntp_drift_seconds` (gauge)
  - `gateway_buffer_pending_records` (gauge)
  - `gateway_mqtt_connected` (gauge 0/1)
  - `gateway_device_online_count` (gauge)
  - `gateway_poll_success_rate` (per device, gauge)
  - `gateway_tag_quality_good_ratio` (per device, gauge)
- **Mock Data Mode ([G3]):**
  - Feature flag `MockDataMode:Enabled` trong `appsettings.json`
  - Thay thế `IProtocolAdapter` bằng `MockProtocolAdapter` — sinh dữ liệu giả lập theo profile (sin wave, step function, random walk)
  - Dùng khi test Business Server tại văn phòng mà không cần thiết bị Modbus thực
- **OTA Update Client ([G3]):**
  - Subscribe topic `ems/gateway/{deviceId}/OTA` từ EMQX OT
  - Download firmware bundle từ URL trong payload (internal HTTPS server)
  - Verify checksum SHA-256 và signature trước khi apply
  - Thực hiện graceful shutdown + apply + restart qua systemd
  - Rollback tự động nếu firmware mới không startup thành công trong 120 giây

**Công nghệ & Design Pattern:** ASP.NET Core Health Checks (`Microsoft.Extensions.Diagnostics.HealthChecks`). `prometheus-net` client library. `P/Invoke` hoặc `FileStream` cho Linux Watchdog `/dev/watchdog`. `Serilog` structured JSON logging với `machine_id`, `firmware_version`, `timestamp_utc` trên mọi log entry. Systemd integration (`sd_notify`) cho readiness signal và watchdog keepalive (alternative nếu không có hardware watchdog).

**Chiến lược Nâng cấp:** Thêm metric Prometheus mới không ảnh hưởng endpoint hiện tại. Thêm Health Check mới chỉ cần đăng ký `AddCheck<T>()` tại Host. OTA update logic mở rộng qua strategy pattern khi cần hỗ trợ nhiều loại firmware packaging.

---

## Nhóm 5 — Điều khiển (Control) — G3 Only

---

### Module 2A-10 — `EMS.Gateway.CommandHandler` *(G3 only)*

**Trách nhiệm cốt lõi:** (G3) Nhận lệnh điều khiển DCMD từ Business Server (qua EMQX OT Broker), validate JWT signature, enforce command whitelist từ `DeviceTemplateDto` (last enforcer — không thể bypass từ IT), validate giới hạn vật lý, ghi Gateway Audit Log append-only độc lập với mạng, và gửi lệnh ghi xuống thiết bị trường qua `WriteQueue` của `IProtocolAdapter`. Gửi ACK/NACK về Business Server.

**Điều kiện tiên quyết kích hoạt M09 (G3):**
- Nhà máy đã vận hành ổn định ≥ 6 tháng với hệ thống Read-Only
- Audit & Liability framework đã được duyệt
- Operator override hardware (nút dừng khẩn) đã lắp tại hiện trường
- Whitelist thiết bị và register được phép ghi đã được phê duyệt và ký số
- Feature flag `CommandHandler:Enabled = true` trong `appsettings.json` (không rebuild để bật)

**Giao diện / Hợp đồng (Core Contracts):**
- Implement `ICommandHandler`:
  - `HandleAsync(commandRequest)` → `CommandResult`
    1. Validate JWT signature (public key của Business Server — không call mạng để verify, dùng local public key)
    2. Kiểm tra `device_id` và `register_address` có trong whitelist M02 của device đó (enforce từ `IDeviceTemplateRepository.GetCommandWhitelist`)
    3. Validate `value` trong giới hạn vật lý `[min_value, max_value]` từ whitelist config
    4. Ghi Gateway Audit Log TRƯỚC KHI gửi lệnh: `(dcmd_id, timestamp_utc_ns, device_id, register, value, jwt_claims)`
    5. Gọi `IProtocolAdapter.WriteAsync` qua `WriteQueue` — Bus Arbitration đảm bảo Write ưu tiên trước Poll
    6. Ghi kết quả vào Audit Log: `(dcmd_id, result, actual_timestamp_utc_ns)`
    7. Publish ACK/NACK về Business Server qua topic `spBv1.0/{group}/{deviceId}/DCMD_ACK`
- **Whitelist Enforcement (Last Enforcer):**
  - Chỉ register address trong `command_whitelist[]` của template được phép ghi
  - Whitelist được load từ `IDeviceTemplateRepository` — **không thể override từ IT, kể cả qua Config Channel**
  - Mọi lệnh không có trong whitelist → `CommandResult.RejectedWhitelist` → Audit Log → NACK về Business Server
  - Whitelist thay đổi chỉ được phép qua OTA firmware update có chữ ký (không qua Config Channel G2)
- **Gateway Audit Log:**
  - Append-only NDJSON local file: `/var/log/ems-gateway/audit.ndjson`
  - Ghi mọi DCMD nhận được — kể cả bị reject
  - Không phụ thuộc mạng, không phụ thuộc Business Server
  - `logrotate` weekly, giữ 12 tuần gần nhất
  - `dcmd_id` dùng để đối chiếu với PostgreSQL Audit Trail tại Business Server (Tầng 3)
- Subscribe topic `spBv1.0/+/+/DCMD` từ EMQX OT Broker qua MQTTnet
- Phát `CommandReceivedEvent` lên `IInternalEventBus` sau khi validate xong (trước khi gửi write)

**Công nghệ & Design Pattern:** `Microsoft.IdentityModel.Tokens` (`System.IdentityModel.Tokens.Jwt`) cho JWT validation — verify signature với public key local, không call mạng. `System.Text.Json` cho DCMD payload deserialization. Append-only NDJSON file log (không dùng database). `IProtocolAdapter.WriteAsync` qua `WriteQueue` (Module 2A-3) — không bypass Bus Arbitration. Toàn bộ validation fail-fast: từ chối ngay nếu bất kỳ điều kiện nào không thỏa — không ghi xuống thiết bị khi còn nghi ngờ.

**Chiến lược Nâng cấp:** Thêm loại validation mới (ví dụ: cooldown period giữa 2 lệnh cùng register) chỉ cần thêm logic trong `HandleAsync` — không thay đổi interface. Khi cần batch command (gửi nhiều register cùng lúc), mở rộng `CommandRequestDto` với `commands[]` array — backward compatible. Audit Log format NDJSON cho phép thêm field mới mà không phá vỡ parser đọc record cũ.

---

## Nhóm 6 — Host & Composition Root

---

### Module 2A-11 — `EMS.Gateway.Host`

**Trách nhiệm cốt lõi:** Entry point duy nhất của Tầng 2A — khởi động tất cả Worker Services và Class Libraries theo đúng thứ tự phụ thuộc, đăng ký Dependency Injection, load cấu hình, đăng ký toàn bộ `IInternalEventBus` subscriber, và đảm bảo graceful shutdown an toàn: drain pipeline, flush buffer SQLite, ghi DDEATH Sparkplug B trước khi tắt.

**Giao diện / Hợp đồng (Core Contracts):**
- Không expose interface ra ngoài — đây là composition root duy nhất
- **Thứ tự khởi động (bắt buộc):**
  1. `AdminService` — Health endpoint phải sẵn sàng đầu tiên; Hardware Watchdog bắt đầu heartbeat
  2. `DeviceTemplate` — load và validate toàn bộ config; NCalc expressions pre-compiled
  3. `LocalBuffer` — khởi động SQLite WAL, Channel writer loop; sẵn sàng nhận payload trước khi MQTT connect
  4. `ProtocolAdapter` — mở serial port / TCP connections; bắt đầu kết nối đến thiết bị
  5. `PollingEngine` — bắt đầu lịch poll sau khi `ProtocolAdapter` sẵn sàng
  6. `SparkplugEncoder` + `MqttPublisher` — publish DBIRTH → bắt đầu publish DDATA
  7. `EdgeRuleEngine` + `QualityChecker` — được activate trong pipeline sau khi `PollingEngine` phát event đầu tiên
  8. `CommandHandler` (G3, chỉ khi `CommandHandler:Enabled = true`)
  9. `ConfigChannelReceiver` (G2, chỉ khi `ConfigChannel:Enabled = true`) — subscribe Config topic
- **Đăng ký `IInternalEventBus` subscribers tại đây** (không wire trong constructor module):
  - `MetricsBatchReadyEvent` → `EdgeRuleEngine` → `QualityChecker` → `SparkplugEncoder`
  - `ConfigReloadRequestedEvent` → `DeviceTemplate.ReloadAsync` → `PollingEngine` → `EdgeRuleEngine.PrecompileExpressions`
  - `MqttConnectionLostEvent` → `SparkplugEncoder` (chuyển sang buffer mode)
  - `MqttConnectionRestoredEvent` → `LocalBuffer.ReplayAsync`
  - `TagQualityDegradedEvent` → `AdminService` (update health metrics)
  - `DeviceUnresponsiveEvent` → `AdminService` (update device status)
  - `NtpDriftExceededEvent` → `SparkplugEncoder` (annotate DBIRTH với cảnh báo)
  - `CommandReceivedEvent` (G3) → `AdminService` (ghi metrics command throughput)
- **Graceful shutdown (thứ tự ngược):**
  1. Dừng nhận DCMD và Config Channel message mới
  2. Dừng Polling Engine — không poll mới
  3. Drain `MetricsBatchReadyEvent` Channel — xử lý hết batch đang pending
  4. Publish DDEATH Sparkplug B — Tầng 2B biết Gateway offline
  5. Drain `LocalBuffer` Channel → SQLite WAL checkpoint
  6. Dừng MQTT connection cleanly (MQTT DISCONNECT)
  7. Dừng Hardware Watchdog heartbeat (watchdog timer sẽ expire — là hành vi đúng khi shutdown có chủ ý)
  8. Dừng Health endpoint
- Systemd `TimeoutStopSec=60` — đủ cho toàn bộ quy trình với buffer kích thước thông thường
- Load config từ `appsettings.json` + `appsettings.{Environment}.json` + environment variables (12-factor), strongly-typed qua `IOptions<T>`

**Công nghệ & Design Pattern:** .NET Generic Host (`IHostedService`). `IHostApplicationLifetime` để kiểm soát thứ tự shutdown callback. `IHostedLifecycleService` (IStartupFilter pattern) để kiểm soát thứ tự startup chính xác. `IOptions<T>` strongly-typed config. `Serilog` JSON logging với `machine_id`, `firmware_version` trên mọi entry. Systemd integration (`UseSystemd()`, `sd_notify` readiness + watchdog). Feature flag: `CommandHandler:Enabled`, `ConfigChannel:Enabled`, `MockDataMode:Enabled` — bật/tắt không rebuild.

**Chiến lược Nâng cấp:** Thêm Worker Service mới chỉ cần `AddHostedService<T>()` và đăng ký subscriber mới — service đang chạy không bị ảnh hưởng. OTA deploy: systemd `systemctl restart ems-gateway` — Host graceful shutdown, firmware mới startup, DBIRTH re-publish tự động.

---

## Sơ đồ luồng nội bộ Tầng 2A

```
[Thiết bị trường — Tầng 1]
Modbus RTU/TCP · BACnet/IP · MQTT Native
      │
      ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M03 — ProtocolAdapter                                              │
│  PollAsync(device, registers)    WriteQueue Bus Arbitration (G3)   │
│  Bad/Stale nếu timeout/CRC error  ◄── WriteAsync từ M09 (G3 only)  │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ RawTagBatch (IInternalEventBus)
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M04 — PollingEngine                                                │
│  PeriodicTimer per device · Deadband Filter · Stale Detection       │
│  TagDatabase snapshot (ImmutableDictionary)                         │
│  Phát MetricsBatchReadyEvent                                        │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ MetricsBatchReadyEvent (IInternalEventBus)
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M05 — EdgeRuleEngine                                               │
│  CT/PT Normalization · NCalc Virtual Tag Computation                │
│  Quality Propagation (worst-case)                                   │
│  EnrichedTagBatch (physical + virtual)                              │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ EnrichedTagBatch
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M06 — QualityChecker                                               │
│  Range Check · Stuck Check · RoC Check · Stale Check                │
│  Phát TagQualityDegradedEvent khi transition Good→Bad/Stale         │
│  Tất cả tag đều có TagQuality annotation rõ ràng                    │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ EnrichedTagBatch (quality annotated)
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M07 — SparkplugEncoder                                             │
│  Good  → SetMetricValue(value), is_null=false                       │
│  Bad   → is_null=true, PropertySet{quality, reason, timestamp}      │
│  Stale → is_null=true, PropertySet{quality, stale_since_ns}         │
│  DBIRTH khi startup / config reload                                 │
│  DDEATH khi shutdown                                                │
│  seq number monotonic 0-255                                         │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ SparkplugPayloadDto (Protobuf binary)
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  IMqttPublisher (MQTTnet)                                           │
│  Connected?                                                         │
│   ├─ YES → Publish MQTT QoS 1 → EMQX OT Broker (Tầng 2B)          │
│   └─ NO  → Enqueue ILocalBuffer                                     │
└──────────────────────┬──────────────────────────────────────────────┘
                       │ (MQTT connected)
                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│  M08 — LocalBuffer (khi MQTT disconnected)                          │
│  System.Threading.Channels → SQLite WAL (tmpfs → eMMC)             │
│  Replay khi MqttConnectionRestoredEvent                             │
│  Prune > 72h · Flash-Wear protection                                │
└─────────────────────────────────────────────────────────────────────┘
                       │ (sau khi reconnect: Replay qua IMqttPublisher)
                       ▼
                  [EMQX OT Broker — Tầng 2B]
                  spBv1.0/{group}/{deviceId}/DDATA
                  spBv1.0/{group}/{deviceId}/DBIRTH
                  spBv1.0/{group}/{deviceId}/DDEATH

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
[G2] Config Channel (IT → Gateway):
  Business Server → EMQX IT → Bridge → EMQX OT
        → Gateway subscribe topic CONFIG
        → AdminService / DeviceTemplate.ReloadAsync(newConfig)
        → ConfigReloadRequestedEvent → PollingEngine + EdgeRuleEngine
        → DBIRTH re-publish với template mới

[G3] DCMD Command Channel (IT → Gateway):
  Business Server → JWT sign → EMQX IT → Bridge → EMQX OT
        → EMQX OT route topic DCMD → M09 CommandHandler
        → Validate JWT + Whitelist + Range
        → WriteAsync qua WriteQueue (Bus Arbitration)
        → Gateway Audit Log (append-only local)
        → ACK/NACK về Business Server
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

## Sơ đồ phụ thuộc giữa các Module

```
                    ┌────────────────────────────────┐
                    │  2A-1: Gateway.Contracts       │
                    │  (Interfaces + DTOs + Events)  │
                    └──────────────┬─────────────────┘
                                   │ (tất cả module phụ thuộc)
          ┌────────────────────────┼────────────────────────┐
          │                        │                        │
          ▼                        ▼                        ▼
┌─────────────────┐   ┌────────────────────┐   ┌──────────────────┐
│ 2A-2: Device    │   │ 2A-8: LocalBuffer  │   │ 2A-9: Admin      │
│ Template        │   │ (SQLite + Channel) │   │ Service          │
│ (ImmutableDict) │   └────────────────────┘   └──────────────────┘
└────────┬────────┘          ▲                         ▲
         │ GetTemplate       │ EnqueueAsync             │ HealthDto
         ▼                   │                         │
┌─────────────────┐   ┌────────────────────┐          │
│ 2A-3: Protocol  │   │ 2A-7: Sparkplug    │──────────┘
│ Adapter         │   │ Encoder + Publisher │
│ (Modbus/BACnet) │   └────────────────────┘
└────────┬────────┘          ▲
         │ RawTagBatch        │ EnrichedTagBatch
         ▼                   │ (quality annotated)
┌─────────────────┐   ┌────────────────────┐
│ 2A-4: Polling   │   │ 2A-6: Quality      │
│ Engine          │──►│ Checker            │
│ (PeriodicTimer) │   └────────────────────┘
└─────────────────┘          ▲
         │ MetricsBatch       │ EnrichedTagBatch
         │ ReadyEvent         │ (pre-quality)
         ▼                   │
┌─────────────────────────────┘
│ 2A-5: EdgeRule Engine
│ (NCalc + CT/PT Normalization)
└─────────────────────────────

[G3]
┌─────────────────┐
│ 2A-10: Command  │──► 2A-3 WriteAsync (WriteQueue)
│ Handler         │──► 2A-2 GetCommandWhitelist
└─────────────────┘──► 2A-9 AdminService (metrics)

┌─────────────────┐
│ 2A-11: Host     │ ← Composition Root
│ (Entry Point)   │    Đăng ký tất cả DI và IInternalEventBus subscribers
└─────────────────┘    Kiểm soát startup/shutdown order
```

---

## Ma trận phụ thuộc với Tầng 2B và Tầng 3

| Tầng 2A cần từ Tầng 2B | Hình thức | Ghi chú |
|---|---|---|
| EMQX OT Broker endpoint | `appsettings.json` (IP:Port) | G1/G2: localhost:1883; G3: IP LAN OT |
| mTLS CA Certificate (nếu EMQX OT bật mTLS) | step-ca (trên Business Server) | Script renew tự động |
| MQTT topic routing DCMD (G3) | EMQX config — Tầng 2B vận hành | Gateway chỉ subscribe topic |

| Tầng 2A cần từ Tầng 3 | Hình thức | Ghi chú |
|---|---|---|
| JWT Public Key (G3 — verify DCMD) | File local `ems_business_server.pub` | Rotate qua OTA khi cert thay đổi |
| NTP Server | systemd-timesyncd config | Business Server làm NTP server nội bộ |
| Config update (G2) | MQTT topic CONFIG qua Tầng 2B | Không gọi REST API trực tiếp |

**Tầng 2A không phụ thuộc:**
- InfluxDB — không đọc/ghi time-series database
- PostgreSQL — không đọc/ghi relational database
- `EMS.Contracts` NuGet của Tầng 3 — không share code trực tiếp
- Bất kỳ business logic module nào của Tầng 3

> **Nguyên tắc bất biến:** Tầng 2A có thể được deploy, restart, và nâng cấp firmware hoàn toàn độc lập với Tầng 2B và Tầng 3. Khi Business Server và IIoT Server cùng maintenance, Tầng 2A vẫn tiếp tục poll, tính toán và buffer đầy đủ trong 72 giờ.

---

## Cấu hình & appsettings

### Cấu trúc `appsettings.json` Tầng 2A (tham chiếu)

```json
{
  "Gateway": {
    "MachineId": "gw-line-a-001",
    "GroupId": "factory-hanoi",
    "MqttBroker": {
      "Host": "localhost",
      "Port": 1883,
      "UseTls": false,
      "ClientCertPath": "",
      "CaPath": ""
    },
    "DeviceTemplateFile": "/etc/ems-gateway/devices.json",
    "LocalBuffer": {
      "RetentionHours": 72,
      "TmpfsMountPath": "/dev/shm/ems-buffer",
      "SqlitePath": "/var/lib/ems-gateway/buffer.db",
      "SyncIntervalMs": 5000,
      "ChannelCapacity": 10000
    },
    "NtpWatchdog": {
      "DriftAlertThresholdSeconds": 5,
      "DriftStaleThresholdSeconds": 30,
      "CheckIntervalSeconds": 30
    },
    "WatchdogDevice": "/dev/watchdog",
    "WatchdogHeartbeatIntervalSeconds": 30
  },
  "Features": {
    "CommandHandler": { "Enabled": false },
    "ConfigChannel": { "Enabled": false },
    "MockDataMode": { "Enabled": false }
  },
  "HealthChecks": {
    "Port": 8080
  },
  "Prometheus": {
    "Port": 9090
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "/var/log/ems-gateway/app.log", "rollingInterval": "Day" } }
    ]
  }
}
```

### Device Template JSON (tham chiếu — `devices.json`)

```json
[
  {
    "device_id": "meter-main-01",
    "description": "Đồng hồ tổng nhà máy",
    "protocol": "ModbusRTU",
    "connection": {
      "port": "/dev/ttyUSB0",
      "baud_rate": 9600,
      "slave_id": 1
    },
    "ct_ratio": 200,
    "pt_ratio": 1,
    "poll_cycle_ms": 5000,
    "heartbeat_interval_s": 60,
    "registers": [
      {
        "tag_name": "kW_total",
        "address": 3000,
        "function_code": "FC03",
        "data_type": "float32",
        "scale_factor": 0.1,
        "unit": "kW",
        "deadband": 0.5,
        "range_min": 0,
        "range_max": 10000,
        "stuck_timeout_ms": 120000,
        "roc_limit_per_second": 500,
        "allow_stuck": false
      },
      {
        "tag_name": "kWh_total",
        "address": 3002,
        "function_code": "FC03",
        "data_type": "float32",
        "scale_factor": 1.0,
        "unit": "kWh",
        "deadband": 0.1,
        "range_min": 0,
        "range_max": 9999999,
        "allow_stuck": true
      }
    ],
    "virtual_tag_expressions": [
      {
        "tag_name": "cosφ",
        "expression": "kW_total / kVA_total",
        "unit": "",
        "range_min": 0,
        "range_max": 1
      },
      {
        "tag_name": "SEI",
        "expression": "kWh_total / production_count",
        "unit": "kWh/sp",
        "range_min": 0,
        "range_max": 1000
      }
    ],
    "command_whitelist": []
  },
  {
    "device_id": "vfd-compressor-01",
    "description": "Biến tần máy nén khí 1",
    "protocol": "ModbusTCP",
    "connection": {
      "host": "192.168.10.51",
      "port": 502,
      "slave_id": 1
    },
    "ct_ratio": 1,
    "pt_ratio": 1,
    "poll_cycle_ms": 2000,
    "registers": [
      {
        "tag_name": "frequency_hz",
        "address": 1000,
        "function_code": "FC03",
        "data_type": "uint16",
        "scale_factor": 0.01,
        "unit": "Hz",
        "deadband": 0.1,
        "range_min": 0,
        "range_max": 60
      }
    ],
    "virtual_tag_expressions": [],
    "command_whitelist": [
      {
        "tag_name": "frequency_hz",
        "register_address": 1001,
        "function_code": "FC06",
        "min_value": 0,
        "max_value": 50,
        "description": "Setpoint tần số biến tần — tối đa 50Hz"
      }
    ]
  }
]
```

---

## Chiến lược Testing

| Module | Loại test | Trọng tâm |
|---|---|---|
| 2A-1: Contracts | Compile-time | Interface không break — type compatibility |
| 2A-2: DeviceTemplate | Unit | Validation rules, hot-reload atomic swap, NCalc pre-compile lỗi syntax |
| 2A-3: ProtocolAdapter | Unit (Mock device) | Timeout → Bad quality, CRC retry, WriteQueue Bus Arbitration ordering |
| 2A-4: PollingEngine | Unit | Deadband filter, Stale detection, PeriodicTimer không drift |
| 2A-5: EdgeRuleEngine | Unit | CT/PT normalization, NCalc expression evaluation, Quality propagation |
| 2A-6: QualityChecker | Unit | Range/Stuck/RoC/Stale detection, transition event |
| 2A-7: SparkplugEncoder | Unit | IsNull=true cho Bad/Stale, DBIRTH schema đúng spec, seq wrap-around |
| 2A-8: LocalBuffer | Unit + Integration | Enqueue/Replay ordering, prune 72h, tmpfs → SQLite flush, graceful shutdown không mất dữ liệu |
| 2A-9: AdminService | Unit | NTP drift alert threshold, Health Check aggregation |
| 2A-10: CommandHandler | Unit | JWT validation, whitelist reject, range reject, Audit Log append-only |
| 2A-11: Host | Integration | Startup order, graceful shutdown sequence, feature flag bật/tắt |

> **Mock Data Mode** (Module 2A-9) cho phép chạy toàn bộ pipeline end-to-end trong CI/CD pipeline mà không cần thiết bị Modbus thực — sinh dữ liệu giả lập theo profile cấu hình.

---

## Lộ trình triển khai theo giai đoạn

### Giai đoạn 1 (G1) — Nền tảng thu thập

**Module kích hoạt:** 2A-1, 2A-2, 2A-3 (Modbus RTU/TCP), 2A-4, 2A-5 (CT/PT norm + virtual tags cơ bản), 2A-6, 2A-7, 2A-8, 2A-9, 2A-11

**Feature flags:** `CommandHandler:Enabled=false`, `ConfigChannel:Enabled=false`

**Kết quả:** Gateway thu thập 5–50 điểm đo, publish Sparkplug B lên EMQX OT (co-located), buffer 72h. Read-Only hoàn toàn.

### Giai đoạn 2 (G2) — Mở rộng & Config từ xa

**Module bổ sung kích hoạt:** Config Channel receiver (subscribe topic CONFIG trong 2A-9/2A-11), hot-reload `DeviceTemplateRepository`

**Feature flags:** `ConfigChannel:Enabled=true`

**Kết quả:** Business Server có thể cập nhật template (polling cycle, deadband, virtual tag expression) mà không cần SSH vào Gateway. Vẫn Read-Only hoàn toàn với thiết bị trường.

### Giai đoạn 3 (G3) — Command Channel & Fleet OTA

**Module bổ sung kích hoạt:** 2A-10 (CommandHandler), OTA Update Client trong 2A-9, BACnet/IP Adapter trong 2A-3 (nếu cần)

**Feature flags:** `CommandHandler:Enabled=true` (sau khi đủ điều kiện tiên quyết)

**Kết quả:** Business Server có thể gửi lệnh điều khiển (setpoint VFD, cắt tải) với đầy đủ audit trail. Fleet OTA quản lý firmware nhiều Gateway từ xa.

---

## Thiết kế UI/UX — Gateway Management Console

### Tổng quan & Bối cảnh

Gateway Management Console là giao diện web quản trị cục bộ chạy trên chính Gateway (ASP.NET Core host), truy cập qua LAN OT bằng trình duyệt — không phụ thuộc Business Server (Tầng 3). Đây là công cụ dành cho **kỹ sư vận hành và kỹ thuật bảo trì tại hiện trường**, không phải developer. Thiết kế hướng đến hệ thống chạy 24/7 trong môi trường công nghiệp, ưu tiên quan sát nhanh, tránh thao tác sai, ổn định với 50–200 thiết bị.

**Backend phục vụ UI:** ASP.NET Core (trong `EMS.Gateway.AdminService` — Module 2A-9) expose REST API + SignalR hub. UI là ứng dụng web single-page, không reload trang khi cập nhật realtime.

---

### Nguyên tắc UI/UX công nghiệp (bất biến)

| Nguyên tắc | Biểu hiện cụ thể |
|---|---|
| **Visibility** | Trạng thái Gateway / MQTT / từng device luôn hiển thị — không cần click vào menu mới biết |
| **Deterministic** | Mọi action đều có feedback rõ ràng: thành công / thất bại / đang xử lý. Không có trạng thái mơ hồ |
| **Fail-safe** | Validate input phía client trước khi submit. Action nguy hiểm (restart, reboot, delete) bắt buộc confirm dialog 2 bước |
| **Minimal** | Không decoration thừa. Ưu tiên bảng và badge trạng thái. Mỗi màn hình giải quyết đúng một nhiệm vụ |
| **Resilient** | UI tiếp tục hiển thị dữ liệu cũ (stale) khi mất kết nối WebSocket, không crash. Badge "Offline" thay cho spinner vô hạn |

**Quy ước màu sắc — áp dụng nhất quán toàn bộ UI:**

```
● Xanh lá  (#22C55E) — OK / Connected / Good quality
● Đỏ       (#EF4444) — Lỗi / Disconnected / Bad quality
● Vàng     (#EAB308) — Cảnh báo / Stale / Degraded
● Xám      (#6B7280) — Offline / Không có dữ liệu / Disabled
● Xanh dương (#3B82F6) — Đang xử lý / Reconnecting / Loading
```

---

### Kiến trúc màn hình & Navigation Flow

```
┌─────────────────────────────────────────────────────────────────┐
│  SIDEBAR (cố định, luôn hiển thị)                               │
│  ┌─────────────────┐                                            │
│  │ 🏭 EMS Gateway  │  machine-id · firmware version            │
│  ├─────────────────┤                                            │
│  │ ■ Dashboard     │  ← Mặc định khi mở                        │
│  │ ■ Devices       │  ← Modbus / BACnet / MQTT devices          │
│  │ ■ Data Monitor  │  ← Realtime tag values                     │
│  │ ■ MQTT/Sparkplug│  ← Uplink status & debug                  │
│  │ ■ System Config │  ← Network · NTP · Feature flags           │
│  │ ■ Log Viewer    │  ← Structured log viewer                   │
│  │ ■ Maintenance   │  ← Restart · Reboot · OTA (G3)            │
│  ├─────────────────┤                                            │
│  │ Role: [Admin]   │                                            │
│  │ [Logout]        │                                            │
│  └─────────────────┘                                            │
└─────────────────────────────────────────────────────────────────┘

Navigation Flow:
Dashboard ──► Devices ──► [Add/Edit Device Form]
          │             └► [Test Connection Dialog]
          ├──► Data Monitor ──► [Filter by Device]
          ├──► MQTT/Sparkplug ──► [Payload Inspector]
          ├──► System Config ──► [NTP · Network · Flags]
          ├──► Log Viewer ──► [Filter · Search · Export]
          └──► Maintenance ──► [Confirm Dialog → Action]
```

**Sidebar badge realtime** (cập nhật qua SignalR, không reload):
- `Devices`: badge đỏ hiển thị số device lỗi — ví dụ `Devices [3]`
- `MQTT/Sparkplug`: badge màu theo trạng thái kết nối
- `Log Viewer`: badge đỏ khi có log ERROR mới chưa đọc

---

### Màn hình 1 — Dashboard

**Mục tiêu:** Kỹ sư nhìn vào màu biết ngay tình trạng hệ thống trong 3–5 giây.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  DASHBOARD                                          [Last update: 14:32:01]│
├──────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐           │
│  │ GATEWAY STATUS  │  │  MQTT UPLINK    │  │   DEVICES       │           │
│  │                 │  │                 │  │                 │           │
│  │   ● RUNNING     │  │  ● CONNECTED    │  │  ●  45 / OK     │           │
│  │                 │  │                 │  │  ●   3 / ERROR  │           │
│  │  Uptime: 12d 4h │  │ Broker: 10.0.1.5│  │  ●   2 / STALE │           │
│  │  Firmware: 1.1.0│  │ Last pub: 0.3s  │  │                 │           │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘           │
│                                                                            │
│  ┌───────────────────────────────┐  ┌───────────────────────────────────┐ │
│  │  SYSTEM RESOURCES             │  │  ACTIVE ALERTS                    │ │
│  │                               │  │                                   │ │
│  │  CPU  [████████░░░░] 64%      │  │  ● [ERR] meter-main-01            │ │
│  │  RAM  [██████░░░░░░] 48%      │  │    Modbus timeout × 5  14:31:55   │ │
│  │  Disk [███░░░░░░░░░] 22%      │  │  ● [WARN] NTP drift: 4.2s         │ │
│  │  Buffer: 1,240 records        │  │    Threshold: 5s       14:30:10   │ │
│  │  Buffer fill: 0.8% / 72h      │  │  ● [WARN] vfd-comp-02             │ │
│  │                               │  │    Tag quality STALE   14:29:44   │ │
│  └───────────────────────────────┘  └───────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

**Yêu cầu kỹ thuật màn hình Dashboard:**
- 3 Status Card cập nhật qua **SignalR** — không polling REST
- `ACTIVE ALERTS` là danh sách cuộn, tối đa 10 alert gần nhất; click vào alert → navigate đến màn hình liên quan
- CPU/RAM/Disk lấy từ `GET /health/detail` poll 10 giây một lần (không cần realtime sub-second)
- Buffer fill % = `pending_records / max_capacity_72h` — giúp kỹ sư biết sắp đầy buffer
- `Last update` timestamp: nếu > 5 giây không cập nhật → toàn bộ Dashboard border chuyển vàng, badge "Connection Lost"

---

### Màn hình 2 — Device Management

**Mục tiêu:** Quản lý toàn bộ thiết bị field (Modbus RTU/TCP, BACnet/IP, MQTT Native). Hỗ trợ 50–200 thiết bị không lag.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  DEVICE MANAGEMENT                    [+ Add Device]  [⬆ Import JSON]    │
├──────────────────────────────────────────────────────────────────────────┤
│  Filter: [All ▼] [Protocol: All ▼] [Status: All ▼]  🔍 [Search...]       │
├────┬──────────────────┬───────────┬──────────┬───────┬────────┬──────────┤
│ #  │ Device ID        │ Protocol  │ Endpoint │ Status│Latency │ Actions  │
├────┼──────────────────┼───────────┼──────────┼───────┼────────┼──────────┤
│ 1  │ meter-main-01    │ Modbus RTU│ COM1 / 1 │ ● OK  │  28ms  │ ✎ 🔌 🗑 │
│ 2  │ meter-line-a-01  │ Modbus TCP│10.0.1.11 │ ● OK  │  12ms  │ ✎ 🔌 🗑 │
│ 3  │ vfd-compressor-01│ Modbus TCP│10.0.1.51 │ ● ERR │  ---   │ ✎ 🔌 🗑 │
│    │                  │           │          │ 5 err │        │         │
│ 4  │ sensor-temp-b    │ MQTT Sub  │factory/..│ ● OK  │  --    │ ✎    🗑 │
│ 5  │ hvac-bldg-01     │ BACnet/IP │10.0.1.80 │ ● STALE│  ---  │ ✎ 🔌 🗑 │
├────┴──────────────────┴───────────┴──────────┴───────┴────────┴──────────┤
│  Hiển thị 1–50 / 127 thiết bị          [< Prev]  [Page 1/3]  [Next >]   │
└──────────────────────────────────────────────────────────────────────────┘
```

**Icons hành động:**
- `✎` Edit — mở Edit Device Form (slide-in panel, không navigate trang)
- `🔌` Test Connection — ping/test poll ngay lập tức, hiện kết quả inline
- `🗑` Delete — confirm dialog 2 bước: "Bạn có chắc xóa [device_id]?" → [Hủy] [Xóa]

**Add / Edit Device Form (slide-in panel từ phải):**

```
┌────────────────────────────────────────────────────────┐
│  ADD DEVICE                                    [✕ Đóng]│
├────────────────────────────────────────────────────────┤
│  Device ID *        [meter-line-b-01            ]      │
│  Description        [Đồng hồ dây chuyền B       ]      │
│                                                        │
│  Protocol *         [Modbus TCP          ▼]            │
│                                                        │
│  --- Modbus TCP settings ---                           │
│  Host *             [192.168.10.52       ]             │
│  Port               [502                ]              │
│  Slave ID *         [1                  ]              │
│  Poll Cycle (ms) *  [5000               ]  min: 500    │
│                                                        │
│  CT Ratio           [200                ]              │
│  PT Ratio           [1                  ]              │
│                                                        │
│  [▶ Test Connection]   ← kết quả: ● OK  12ms           │
│                                                        │
│  ⚠ Chú ý: Thêm device sẽ có hiệu lực ngay sau khi lưu │
│                                                        │
│  [Hủy]                              [💾 Lưu & Áp dụng]│
└────────────────────────────────────────────────────────┘
```

**Validation rules phía client (trước khi submit):**
- `device_id`: required, unique, chỉ `[a-z0-9-]`, tối đa 64 ký tự
- `poll_cycle_ms`: số nguyên ≥ 500 — hiển thị cảnh báo đỏ ngay khi nhập < 500
- `ct_ratio`, `pt_ratio`: số thực > 0
- `host`: validate IP hoặc hostname hợp lệ
- Form không cho submit khi còn field lỗi — nút "Lưu & Áp dụng" disable màu xám

**Yêu cầu kỹ thuật màn hình Devices:**
- Bảng dùng **virtual scrolling** (chỉ render DOM cho rows đang hiển thị) — đảm bảo không lag với 200 thiết bị
- Cột `Status` và `Latency` cập nhật realtime qua **SignalR** — không reload bảng
- Filter `Protocol` và `Status` lọc client-side — không call API lại
- `Import JSON`: upload file `devices.json`, validate schema trước khi apply; hiển thị diff "X thiết bị mới, Y thiết bị thay đổi, Z thiết bị bị xóa" → confirm trước khi apply

---

### Màn hình 3 — Data Monitor (Realtime Tag Values)

**Mục tiêu:** Xem giá trị tag realtime để debug / kiểm tra kết nối thiết bị tại hiện trường.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  DATA MONITOR (REALTIME)          ● Live  [⏸ Pause]  [⬇ Export CSV]      │
├──────────────────────────────────────────────────────────────────────────┤
│  Device: [meter-main-01  ▼]   Show: [All tags ▼]   Quality: [All ▼]      │
├────────────────────┬─────────────┬──────┬─────────┬───────────┬──────────┤
│ Tag Name           │ Value       │ Unit │ Quality │ Timestamp │ Δ Change │
├────────────────────┼─────────────┼──────┼─────────┼───────────┼──────────┤
│ kW_total           │  842.30     │ kW   │ ● Good  │ 14:32:01  │ ▲ +2.1  │← flash xanh
│ kWh_total          │ 12,483.50   │ kWh  │ ● Good  │ 14:32:01  │ ▲ +0.01 │
│ cosφ               │   0.921     │      │ ● Good  │ 14:32:01  │ —       │
│ V_L1               │  221.4      │ V    │ ● Good  │ 14:32:00  │ —       │
│ I_L1               │   38.2      │ A    │ ● Good  │ 14:32:01  │ ▲ +0.3  │
│ THD_alert          │    0        │      │ ● Good  │ 14:32:01  │ —       │
│ SEI                │    5.23     │kWh/sp│ ● Stale │ 14:31:05  │ —       │← row vàng nhạt
│ freq_vfd_01        │    ---      │ Hz   │ ● Bad   │ 14:31:55  │ ---     │← row đỏ nhạt
├────────────────────┴─────────────┴──────┴─────────┴───────────┴──────────┤
│  8 tags hiển thị  (6 Good · 1 Stale · 1 Bad)                             │
└──────────────────────────────────────────────────────────────────────────┘
```

**Hành vi highlight khi dữ liệu thay đổi:**
- Ô `Value` flash nền xanh nhạt trong 800ms khi giá trị vừa được cập nhật
- Cột `Δ Change`: hiển thị delta so với lần trước, mũi tên ▲▼ kèm giá trị
- Row có `Quality = Stale` → nền vàng nhạt toàn row
- Row có `Quality = Bad` → nền đỏ nhạt toàn row, value hiển thị `---`

**Chế độ Chart (toggle Table ↔ Chart):**

```
[■ Table View]  [📈 Chart View]
─────────────────────────────────────────────────────────
Tag: [kW_total ▼]   Range: [5 phút ▼]

    kW
 900 ┤                        ╭──╮
 850 ┤          ╭─╮     ╭─────╯  ╰──
 800 ┤╭─────────╯ ╰─────╯
 750 ┤╯
     └──────────────────────────────── time
    14:27      14:29      14:31  14:32
```

- Chart dùng **canvas-based rendering** (không SVG với 200 points/giây) để tránh lag
- Tối đa hiển thị 5 phút dữ liệu trong buffer — không query InfluxDB (Gateway không có InfluxDB)
- Nút `Pause` dừng update realtime, giữ nguyên chart để quan sát — resume khi bấm lại

**Yêu cầu kỹ thuật:**
- Dữ liệu đẩy qua **SignalR hub** `TagValueUpdated` event — không polling REST
- Bảng dùng virtual scrolling với max 200 tags hiển thị
- Filter `Device` thay đổi → subscribe SignalR group mới, hủy subscribe group cũ — không reload trang
- `Export CSV`: export snapshot hiện tại tất cả tag của device đang xem, kèm timestamp

---

### Màn hình 4 — MQTT / Sparkplug B

**Mục tiêu:** Kiểm tra trạng thái uplink và debug payload khi cần.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  MQTT / SPARKPLUG B UPLINK                                                │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  CONNECTION STATUS                                                         │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  ● CONNECTED                                                        │   │
│  │  Broker     : emqx-ot.factory.local : 1883                         │   │
│  │  Client ID  : gw-line-a-001                                        │   │
│  │  TLS        : Disabled  (G1/G2)  |  mTLS : Enabled (G3)            │   │
│  │  Last DDATA : 0.3s ago  (14:32:01.421)                             │   │
│  │  Last DBIRTH: 12d ago   (on startup)                               │   │
│  │  Publish rate: 42 msg/s                                            │   │
│  │  Total published: 1,482,043 messages                               │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  CONFIGURATION  [✎ Edit — Admin only]                                     │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Broker Host   [emqx-ot.factory.local    ]   Port [1883  ]         │   │
│  │  Use TLS       [ ] Enabled                                         │   │
│  │  CA Cert Path  [/etc/ems-gateway/ca.crt  ]                         │   │
│  │  Client Cert   [/etc/ems-gateway/gw.crt  ]                         │   │
│  │  Client Key    [/etc/ems-gateway/gw.key  ]                         │   │
│  │  QoS Level     [1 — At least once  ▼]                              │   │
│  │                              [Test Connection]  [💾 Lưu & Restart] │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  PAYLOAD INSPECTOR  [🔴 Stop capture]  [🗑 Clear]                         │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  [14:32:01.421] DDATA  spBv1.0/factory-hanoi/meter-main-01/DDATA  │   │
│  │  Metrics: 8  |  seq: 142                                           │   │
│  │  ┌──────────────┬───────────┬──────────────────────────────────┐  │   │
│  │  │ Tag          │ Value     │ Quality / Note                   │  │   │
│  │  ├──────────────┼───────────┼──────────────────────────────────┤  │   │
│  │  │ kW_total     │ 842.30    │ ● Good                           │  │   │
│  │  │ kWh_total    │ 12483.50  │ ● Good                           │  │   │
│  │  │ freq_vfd_01  │ (null)    │ ● Bad — ModbusTimeout            │  │   │
│  │  └──────────────┴───────────┴──────────────────────────────────┘  │   │
│  │                                                                    │   │
│  │  [14:32:00.218] DDATA  spBv1.0/factory-hanoi/vfd-comp-01/DDATA   │   │
│  │  ...                                                               │   │
│  └────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────┘
```

**Yêu cầu kỹ thuật màn hình MQTT:**
- Config section chỉ hiển thị dạng read-only với role Operator — icon `✎ Edit` ẩn
- "Lưu & Restart" → confirm dialog: "Thay đổi broker config sẽ restart MQTT client, có thể mất < 1 phút dữ liệu nếu buffer chưa flush. Tiếp tục?" → [Hủy] [Xác nhận]
- **Payload Inspector** subscribe SignalR event `SparkplugMessagePublished` — hiển thị realtime. Nút Stop/Start capture. Buffer tối đa 50 message gần nhất trong UI — không lưu server-side
- Payload Inspector decode Protobuf → human-readable table (không hiển thị raw binary) — dùng `Eclipse.Tahu.Protobuf` để decode phía server, gửi JSON xuống UI
- Cột `is_null=true` metric hiển thị value là `(null)` màu đỏ kèm PropertySet reason

---

### Màn hình 5 — System Configuration

**Mục tiêu:** Cấu hình hạ tầng hệ thống — chỉ Admin được chỉnh sửa.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  SYSTEM CONFIGURATION                              [Role: Admin required] │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  ▼ NETWORK                                                                 │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Interface  eth0                                                    │   │
│  │  IP Mode    [● Static  ○ DHCP]                                     │   │
│  │  IP Address [192.168.10.100   ]   Subnet [255.255.255.0]           │   │
│  │  Gateway    [192.168.10.1     ]   DNS    [192.168.10.1  ]          │   │
│  │                                              [💾 Apply Network]    │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  ▼ TIME SYNC (NTP)                                                         │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  NTP Server   [10.0.1.5 (Business Server)  ]                       │   │
│  │  Current Time : 2024-11-15  14:32:01  UTC+7                        │   │
│  │  NTP Status   : ● Synced  |  Drift: +0.42s                         │   │
│  │  Alert threshold: [5] giây                                         │   │
│  │                                [🔄 Force Sync Now]  [💾 Save]      │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  ▼ LOGGING                                                                 │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Log Level   [Information ▼]  (Debug chỉ bật khi debug — tốn disk)│   │
│  │  Retention   [7 ngày ▼]                                            │   │
│  │                                                       [💾 Save]    │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  ▼ FEATURE FLAGS                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Config Channel (G2)   [● Enabled  ○ Disabled]                     │   │
│  │  Command Handler (G3)  [○ Enabled  ● Disabled]  ⚠ Xem điều kiện G3│   │
│  │  Mock Data Mode        [○ Enabled  ● Disabled]                     │   │
│  │                                                       [💾 Save]    │   │
│  └────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────┘
```

**Yêu cầu kỹ thuật:**
- Toàn bộ màn hình ẩn / read-only với role Operator; chỉ Admin mới thấy nút Save/Apply
- "Apply Network" → cảnh báo rõ: "Thay đổi IP có thể làm mất kết nối trình duyệt hiện tại. Truy cập lại tại IP mới: [192.168.10.100]" → countdown 5 giây → Apply
- Feature Flag "Command Handler (G3)" khi bật → hiện thêm dialog checklist điều kiện tiên quyết (6 tháng vận hành, whitelist, operator override hardware...) — phải tick đủ mới cho bật
- "Force Sync Now" → gọi `chronyc makestep`, hiển thị kết quả ngay trong 3 giây

---

### Màn hình 6 — Log Viewer

**Mục tiêu:** Tìm kiếm và phân tích log nhanh tại hiện trường mà không cần SSH.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  LOG VIEWER                                    [⬇ Export Log]  [🗑 Clear] │
├──────────────────────────────────────────────────────────────────────────┤
│  Level: [All ▼] [INFO][WARN][ERROR]   Module: [All ▼]   🔍 [Search...  ] │
│  Time range: [Last 1 hour ▼]          [● Live tail  ○ Historical]         │
├──────────────────────────────────────────────────────────────────────────┤
│  Timestamp           │ Level │ Module       │ Message                      │
├──────────────────────┼───────┼──────────────┼──────────────────────────────┤
│  14:32:01.421        │ INFO  │ SparkplugEnc │ DDATA published seq=142      │
│  14:31:55.003        │ ERROR │ ProtocolAdapt│ Modbus timeout device=meter- │
│                      │       │              │ main-01 retry=3/3            │
│  14:31:55.001        │ WARN  │ QualityCheck │ Tag freq_vfd_01 → Bad (Modbustimeout)│
│  14:31:10.882        │ INFO  │ PollingEngine│ Poll cycle completed 50 devs │
│  14:30:10.001        │ WARN  │ AdminService │ NTP drift 4.2s > threshold 5s│
│  ...                                                                        │
├──────────────────────────────────────────────────────────────────────────┤
│  Hiển thị 1–100 / 4,821 entries               [Load more ↓]              │
└──────────────────────────────────────────────────────────────────────────┘
```

**Yêu cầu kỹ thuật:**
- **Live tail mode**: log mới đẩy qua SignalR `LogEntryAdded` event, prepend lên đầu danh sách — không reload
- **Historical mode**: query `GET /api/logs?level=ERROR&module=ProtocolAdapter&from=...&to=...` với server-side pagination 100 entries/page
- Search full-text theo `message` field — debounce 300ms, query server-side
- Log entry có `device_id` trong message → render thành link → click navigate đến Device Management row đó
- `Export Log`: export NDJSON hoặc CSV cho khoảng thời gian đang filter — max 10,000 dòng/export
- Module filter: `PollingEngine`, `ProtocolAdapter`, `EdgeRuleEngine`, `QualityChecker`, `SparkplugEncoder`, `LocalBuffer`, `AdminService`, `CommandHandler`

---

### Màn hình 7 — Maintenance

**Mục tiêu:** Thực hiện các thao tác vận hành nguy hiểm với đầy đủ safeguard — tránh thao tác sai ngoài hiện trường.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  MAINTENANCE                                       [Role: Admin required] │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  SERVICE CONTROL                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Gateway Service   ● Running  Uptime: 12d 4h 32m                   │   │
│  │  [🔄 Restart Service]   ← restart process, không reboot hardware   │   │
│  │                                                                    │   │
│  │  ⚠ Restart service sẽ gián đoạn thu thập 10–30 giây.              │   │
│  │  Local buffer đảm bảo không mất dữ liệu.                          │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  SYSTEM REBOOT                                                             │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  [⚡ Reboot System]   ← reboot toàn bộ máy Gateway                │   │
│  │                                                                    │   │
│  │  ⚠ Reboot sẽ gián đoạn thu thập 1–3 phút.                         │   │
│  │  Local buffer đảm bảo dữ liệu được replay khi khởi động lại.      │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  SOFTWARE UPDATE (OTA)  [G3]                                               │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  Current version : 1.1.0                                           │   │
│  │  Available       : 1.2.0  (released 2024-11-14)  [📋 Changelog]   │   │
│  │  [⬆ Update to 1.2.0]                                               │   │
│  │                                                                    │   │
│  │  ⚠ Update sẽ restart service, mất ~30 giây. Rollback tự động      │   │
│  │  nếu version mới không khởi động được trong 120 giây.             │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│                                                                            │
│  CONFIG MANAGEMENT                                                         │
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │  [⬇ Export devices.json]   ← download toàn bộ device template     │   │
│  │  [⬇ Export appsettings]    ← download system config (redacted)    │   │
│  │  [⬆ Import devices.json]   ← bulk import devices                  │   │
│  └────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────┘
```

**Confirm Dialog 2 bước cho mọi action nguy hiểm:**

```
┌──────────────────────────────────────────────────────┐
│  ⚠ XÁC NHẬN RESTART SERVICE                          │
├──────────────────────────────────────────────────────┤
│                                                       │
│  Hành động này sẽ:                                   │
│  • Dừng thu thập dữ liệu trong ~15 giây              │
│  • Flush buffer trước khi dừng                       │
│  • Tự động khởi động lại và tiếp tục thu thập        │
│                                                       │
│  Nhập "RESTART" để xác nhận: [              ]        │
│                                                       │
│             [Hủy bỏ]    [✓ Xác nhận]                 │
└──────────────────────────────────────────────────────┘
```

> Yêu cầu nhập text xác nhận (không chỉ click OK) cho các action: Restart Service, Reboot System, Software Update — để tránh bấm nhầm trên màn hình cảm ứng ngoài hiện trường.

---

### Thiết kế Component tái sử dụng

#### Status Badge

```
Dùng nhất quán trên toàn bộ UI:

● Good / Connected / Running   → nền xanh nhạt,  chữ xanh đậm,  dot xanh
● Error / Disconnected         → nền đỏ nhạt,    chữ đỏ đậm,    dot đỏ nhấp nháy (animation 1s)
● Warning / Stale / Degraded   → nền vàng nhạt,  chữ vàng đậm,  dot vàng
● Offline / Disabled / N/A     → nền xám nhạt,   chữ xám,       dot xám
● Reconnecting / Loading       → dot xanh dương xoay (spinner nhỏ)

Kích thước: badge nhỏ (trong bảng) và badge lớn (trong Status Card Dashboard)
```

#### Tag Quality Indicator (Data Monitor)

```
Dùng trên cột Quality trong Data Monitor:
● Good   → ● xanh
● Stale  → ● vàng + tooltip "Không cập nhật từ [timestamp]"
● Bad    → ● đỏ + tooltip "[reason từ PropertySet]"

Row background:
- Good  : transparent
- Stale : rgba(234, 179, 8, 0.08)   ← vàng rất nhạt
- Bad   : rgba(239, 68, 68, 0.08)   ← đỏ rất nhạt
```

#### Confirm Dialog Component

```
Props:
- title: string
- description: string[]     ← danh sách hành động sẽ xảy ra
- confirmText: string       ← text user phải nhập ("RESTART", "REBOOT", "DELETE")
- dangerLevel: 'warn' | 'danger'
  - warn   → icon ⚠ màu vàng, nút xác nhận màu vàng
  - danger → icon 🔴 màu đỏ,  nút xác nhận màu đỏ
```

#### Virtual Table Component

```
Yêu cầu: hoạt động mượt với 200 rows, update realtime từng cell
Kỹ thuật:
- Render chỉ rows trong viewport + 10 rows buffer trên/dưới
- Cell update: thay đổi chỉ DOM node của cell thay đổi — không re-render toàn row
- Sort/Filter: thực hiện client-side trên data array, không call API
- Row height cố định (44px) để virtual scroll tính chính xác offset
```

---

### Công nghệ đề xuất

#### Blazor Server vs React — Phân tích trade-off

| Tiêu chí | Blazor Server | React + SignalR |
|---|---|---|
| Cùng stack .NET | ✅ Không cần team frontend riêng | ❌ Cần thêm JS/TS skill |
| Realtime latency | ✅ SignalR tích hợp sẵn, ít boilerplate | ✅ SignalR client JS hoạt động tốt |
| Hiệu năng với 200 devices | ⚠ Server giữ circuit/state mỗi client — tốn RAM | ✅ State hoàn toàn client-side |
| Offline/reconnect | ⚠ Mất circuit → reload trang, mất state UI | ✅ Tự reconnect SignalR, giữ state |
| Virtual table 200 rows | ⚠ Khó implement virtualization đúng trong Blazor | ✅ Thư viện (TanStack Virtual) sẵn có |
| Deployment | ✅ Một binary .NET duy nhất | ⚠ Cần serve static file riêng (hoặc embed) |
| Debug tại hiện trường | ✅ .NET tooling quen thuộc | ⚠ Cần browser dev tools |
| Production risk 24/7 | ⚠ Server circuit chết khi gateway restart → mọi client mất kết nối | ✅ Client tự reconnect độc lập |

**Khuyến nghị: React (hoặc Svelte) + SignalR + ASP.NET Core API**

Lý do quyết định:
1. **200 devices với realtime update** là yêu cầu nặng cho Blazor Server — RAM circuit cost nhân với số client đang xem
2. **Gateway restart** là sự kiện bình thường (maintenance, OTA) — Blazor Server mất toàn bộ circuit state, client phải reload trang thủ công. React + SignalR auto-reconnect trong suốt
3. **Virtual scrolling** với React + TanStack Virtual đã được kiểm chứng production với hàng nghìn rows

> Nếu team chỉ có .NET, dùng **Blazor WebAssembly** (không phải Blazor Server) — không có circuit state trên server, hoạt động như React về mặt resilience. Trade-off: bundle size lớn hơn (~10MB lần đầu tải).

#### SignalR — Có nên dùng không?

**Có — bắt buộc cho các luồng sau:**

| Luồng | Lý do dùng SignalR |
|---|---|
| Dashboard status card | Cập nhật < 1 giây, polling REST quá lãng phí |
| Data Monitor tag values | Mỗi poll cycle 5 giây có thể có 200 × 10 tags = 2,000 updates — cần stream |
| Device table Status/Latency | Update per-cell mà không reload bảng |
| Log Viewer live tail | Log event-driven, push tự nhiên hơn pull |
| Payload Inspector | Debug realtime, không có ý nghĩa khi poll |

**Không cần SignalR (dùng REST) cho:**
- Load cấu hình ban đầu (device list, MQTT config, system info)
- Submit form (save config, add device)
- Export log, export CSV

---

### Phân quyền (RBAC)

| Tính năng | Admin | Operator |
|---|---|---|
| Xem Dashboard, Data Monitor, Log Viewer | ✅ | ✅ |
| Xem Device list, MQTT status | ✅ | ✅ |
| Payload Inspector (debug) | ✅ | ✅ |
| Add / Edit / Delete device | ✅ | ❌ |
| Sửa MQTT config | ✅ | ❌ |
| System Config (Network, NTP, Flags) | ✅ | ❌ |
| Restart Service / Reboot | ✅ | ❌ |
| OTA Update (G3) | ✅ | ❌ |
| Export config JSON | ✅ | ❌ |
| Import config JSON | ✅ | ❌ |

- Session timeout: 8 giờ (phù hợp ca làm việc công nghiệp)
- Không có "Remember me" — môi trường nhiều người dùng chung thiết bị
- UI ẩn hoàn toàn các nút/form mà Operator không có quyền — không hiển thị disabled (tránh nhầm lẫn)

---

### Các lỗi UI phổ biến trong hệ thống công nghiệp & Cách tránh

| Lỗi phổ biến | Hậu quả | Cách tránh trong thiết kế này |
|---|---|---|
| Không phân biệt "Offline" với "Không có dữ liệu" | Kỹ sư không biết thiết bị bị mất hay chưa bao giờ có dữ liệu | Màu xám = Offline (có kết nối nhưng DDEATH), màu đỏ = Bad (có kết nối, có dữ liệu nhưng lỗi). Timestamp rõ ràng |
| Spinner vô hạn khi mất WebSocket | UI treo, kỹ sư không biết app sống hay chết | Timeout 5s → hiển thị banner "Connection Lost" + timestamp mất kết nối + nút Reconnect thủ công |
| Reload toàn bộ bảng khi 1 cell thay đổi | Flicker, mất scroll position với 200 rows | Virtual table update per-cell, không re-render toàn bảng |
| Không có confirm dialog | Restart/Reboot nhầm ngoài hiện trường | Confirm dialog 2 bước + nhập text xác nhận |
| Hiển thị raw value mà không có đơn vị | Kỹ sư không biết 842 là kW hay kWh hay A | Cột Unit bắt buộc trên Data Monitor, tooltip trên mọi giá trị |
| Config form không validate realtime | Submit rồi mới biết lỗi — gây gián đoạn | Validate per-field ngay khi blur, nút Submit disable khi còn lỗi |
| Log không có module filter | 1,000 dòng log không tìm được lỗi | Filter bắt buộc theo Level + Module + Search text |
| Không hiển thị "Stale" — chỉ hiển thị giá trị cũ | Kỹ sư tin tưởng giá trị đã cũ 10 phút | Row highlight vàng + timestamp rõ ràng + tooltip stale_since |
| Action nguy hiểm không có context | Kỹ sư không biết restart ảnh hưởng gì | Confirm dialog liệt kê cụ thể hậu quả — không chỉ "Bạn có chắc?" |

---

### Tích hợp với Module 2A-9 (AdminService)

UI Gateway Management Console được phục vụ bởi **`EMS.Gateway.AdminService`** (Module 2A-9) — không phải module riêng:

| API | Mục đích | Ghi chú |
|---|---|---|
| `GET /health` | Dashboard status cards | Polling 5s |
| `GET /health/detail` | CPU/RAM/Disk, per-device status | Polling 10s |
| `GET /api/devices` | Device list | REST |
| `POST /api/devices` | Add device | REST + hot-reload |
| `PUT /api/devices/{id}` | Edit device | REST + hot-reload |
| `DELETE /api/devices/{id}` | Delete device | REST + hot-reload |
| `POST /api/devices/{id}/test` | Test connection | REST, timeout 5s |
| `GET /api/mqtt/status` | MQTT connection info | REST |
| `PUT /api/mqtt/config` | Update MQTT config | REST — Admin only |
| `GET /api/logs` | Log viewer historical | REST + pagination |
| `GET /api/config/system` | System config | REST |
| `PUT /api/config/system` | Update system config | REST — Admin only |
| `POST /api/maintenance/restart` | Restart service | REST — Admin only |
| `POST /api/maintenance/reboot` | Reboot system | REST — Admin only |
| `GET /api/config/export` | Export devices.json | File download |
| `POST /api/config/import` | Import devices.json | File upload + validate |
| **SignalR Hub `/hubs/gateway`** | | |
| Event `GatewayStatusChanged` | Dashboard card update | Push |
| Event `DeviceStatusChanged` | Device table row update | Push |
| Event `TagValueUpdated` | Data Monitor cell update | Push |
| Event `SparkplugMessagePublished` | Payload Inspector | Push |
| Event `LogEntryAdded` | Log Viewer live tail | Push |
| Event `AlertRaised` | Dashboard active alerts | Push |

---

---

## Phân tích & Phản biện Yêu cầu Bổ sung

Phần này đánh giá từng yêu cầu trong tài liệu bổ sung, phân loại thành ba nhóm: **Chấp nhận & Bổ sung** (advantage rõ ràng, tích hợp vào thiết kế), **Chấp nhận có điều kiện** (hợp lý nhưng cần giới hạn phạm vi), và **Bác bỏ có lý do** (over-engineering hoặc mâu thuẫn với nguyên tắc kiến trúc hiện có).

---

### Nhóm I — Hệ thống & Vận hành

#### ✅ I.1 — Bus Arbitration RS485 (Chấp nhận — đã có, bổ sung làm rõ)

**Đánh giá:** Đã thiết kế đầy đủ trong Module 2A-3 với `WriteQueue` lock-free Channel + single-threaded arbitration loop. Yêu cầu bổ sung nhắc đến "ngắt Poll không gây hỏng frame" — đây là điểm chi tiết quan trọng cần ghi rõ hơn về inter-frame gap.

**Bổ sung vào Module 2A-3:** Arbitration loop phải tôn trọng **Modbus inter-frame silence** (3.5 character times = ~1.75ms @ 9600 baud, ~0.36ms @ 38400 baud) giữa lệnh Poll và Write để không hỏng frame đang transmit. Nếu một Poll frame đã bắt đầu phát → phải chờ hoàn thành response hoặc timeout trước khi chèn Write.

---

#### ✅ I.2 — Embedded MQTT Broker (`MQTTnet.Server`) — Đánh giá lại: Chấp nhận có phạm vi rõ ràng

**Đánh giá lại sau phân tích kịch bản G3 tách máy:**

Đánh giá ban đầu bác bỏ yêu cầu này vì nhầm lẫn vai trò. Sau khi phân tích kỹ, embedded `MQTTnet.Server` giải quyết **một gap kiến trúc thực sự** mà `LocalBuffer` không cover được, xuất hiện trong kịch bản **G3 tách máy + có thiết bị MQTT Native (Luồng A)**.

**Phân tích gap:**

Với kiến trúc hiện tại, thiết bị MQTT Native Luồng A publish lên **EMQX OT Broker (Tầng 2B)**, và `MqttNativeAdapter` của Gateway **subscribe từ EMQX OT** để nhận dữ liệu đó:

```
[Thiết bị MQTT Native]
     │ publish topic sensor/data
     ▼
[EMQX OT — Tầng 2B, server riêng]   ← nếu server này down?
     │
     ▼  ← Gateway subscribe từ đây
[MqttNativeAdapter — Gateway 2A]     ← mất luồng nhận hoàn toàn
     │ (không có gì để buffer — chưa nhận được)
     ▼
LocalBuffer                          ← không giúp được gì
```

Khi EMQX OT (Tầng 2B) down trong kịch bản G3: Gateway **mất khả năng nhận dữ liệu từ thiết bị MQTT Native**, dù Gateway hardware vẫn chạy tốt. `LocalBuffer` chỉ bảo vệ phần **sau khi đã nhận** — không bảo vệ được phần **nhận vào**. Đây là gap thực sự.

**So sánh với giao thức Modbus/BACnet:**

| Giao thức | EMQX OT down | Ảnh hưởng thu thập |
|---|---|---|
| Modbus RTU/TCP | Không liên quan — poll qua RS485/TCP trực tiếp | ✅ Không ảnh hưởng |
| BACnet/IP | Không liên quan — poll qua UDP trực tiếp | ✅ Không ảnh hưởng |
| MQTT Native (Luồng A) — kiến trúc hiện tại | Gateway subscribe EMQX OT để nhận | ❌ **Mất luồng nhận** |
| MQTT Native (Luồng A) — embedded broker | Thiết bị publish thẳng vào Gateway | ✅ Không ảnh hưởng |

**Giải pháp: Embedded `MQTTnet.Server` với vai trò tách biệt**

Không phải thay thế EMQX OT — hai thành phần có vai trò hoàn toàn khác nhau:

```
[Thiết bị MQTT Native Luồng A]
     │ publish topic sensor/data (mạng OT LAN)
     ▼
[MQTTnet.Server — embedded trong Gateway 2A process]
     │ in-process dispatch — không qua TCP stack
     │ độc lập hoàn toàn với EMQX OT (Tầng 2B)
     ▼
[MqttNativeAdapter subscriber — cùng process]
     │ RawTagBatch
     ▼
[Pipeline M04→M05→M06→M07 — bình thường]
     │ Sparkplug B payload
     ▼
[IMqttPublisher → EMQX OT (Tầng 2B)]
     ├─ Connected → publish trực tiếp
     └─ Disconnected → LocalBuffer SQLite 72h → replay khi reconnect
```

**Phạm vi áp dụng:**

| Kịch bản | Embedded Broker cần thiết? | Lý do |
|---|---|---|
| G1/G2 co-located, không có thiết bị MQTT Native | ❌ Không cần | EMQX OT cùng máy, không có gap |
| G1/G2 co-located, có thiết bị MQTT Native | ⚠ Tùy chọn | EMQX OT cùng máy — gap nhỏ, EMQX OT restart nhanh |
| **G3 tách máy, có thiết bị MQTT Native** | **✅ Bắt buộc** | **Gap thực sự — mất nhận khi EMQX OT down** |
| G3 tách máy, không có thiết bị MQTT Native | ❌ Không cần | Modbus/BACnet không phụ thuộc EMQX OT |

**Trade-off so với EMQX OT:**

| Tiêu chí | Embedded `MQTTnet.Server` (nhận từ thiết bị) | EMQX OT (uplink lên Tầng 2B) |
|---|---|---|
| Vai trò | South-facing: nhận từ thiết bị MQTT trường | North-facing: bridge lên Tầng 3 |
| ACL | Đơn giản — chỉ whitelist IP thiết bị trường | Đầy đủ — mTLS, RBAC |
| Bridge logic | Không cần — `MqttNativeAdapter` đọc in-process | EMQX built-in bridge OT→IT |
| Management API | Không cần — nội bộ Gateway | EMQX HTTP API cho BridgeMonitor |
| RAM footprint | ~10–20MB (MQTTnet.Server rất nhẹ) | ~100–200MB (EMQX) |
| Restart ảnh hưởng | Cùng process với firmware — restart cùng nhau | Độc lập với firmware |

**Bổ sung vào Module 2A-3 (`ProtocolAdapter`) và Module 2A-11 (`Host`):**

```csharp
// Trong EMS.Gateway.Host — chỉ kích hoạt khi có ít nhất 1 device MqttNative
if (templates.Any(t => t.Protocol == ProtocolType.MqttNative))
{
    services.AddSingleton<IEmbeddedMqttBroker, EmbeddedMqttBrokerService>();
    // MQTTnet.Server bind port 1884 (khác port 1883 của EMQX OT nếu co-located)
    // Chỉ listen trên interface OT LAN — không expose ra ngoài
}
```

Config bổ sung trong `appsettings.json`:
```json
"EmbeddedMqttBroker": {
  "Enabled": true,           // auto-enable khi có MqttNative device
  "Port": 1884,              // khác EMQX OT port 1883
  "BindInterface": "eth0",   // chỉ OT LAN, không bind 0.0.0.0
  "AllowedClientIpRanges": ["192.168.10.0/24"]  // whitelist IP thiết bị
}
```

`DeviceTemplate` cho thiết bị MQTT Native Luồng A cập nhật `broker_host`:
```json
"connection": {
  "broker_host": "localhost",  // thiết bị connect vào embedded broker của Gateway
  "broker_port": 1884,         // port embedded broker
  "subscribe_topic": "factory/zone-b/temperature"
}
```

**Điều chỉnh tóm tắt phân loại I.2:**

| Kịch bản | Quyết định |
|---|---|
| G1/G2 co-located | Không bắt buộc — EMQX OT cùng máy đã đủ |
| G3 tách máy + thiết bị MQTT Native | **Bắt buộc** — gap thực sự trong khả năng nhận |
| G3 tách máy + chỉ Modbus/BACnet | Không cần |

> **Nguyên tắc bất biến sau bổ sung:** Embedded `MQTTnet.Server` là **South-facing broker** (nhận từ thiết bị trường). EMQX OT (Tầng 2B) là **North-facing broker** (aggregation + bridge lên Tầng 3). Hai vai trò độc lập — một cái down không kéo cái kia.

---

#### ✅ I.3 — LWT (Last Will and Testament) cho thiết bị MQTT Native (Chấp nhận — bổ sung vào Module 2A-3 & 2A-6)

**Đánh giá:** Yêu cầu hoàn toàn đúng và là gap thực sự trong thiết kế hiện tại. `stale_timeout_ms` phát hiện thiết bị MQTT mất kết nối chậm (phải đợi hết timeout). LWT cho phép phát hiện ngay lập tức khi TCP session đứt — không cần chờ. **Bổ sung.**

**Bổ sung vào Module 2A-3 (`MqttNativeAdapter`):**
```
Device Template — thêm field LWT:
{
  "protocol": "MqttNative",
  "connection": {
    "lwt_topic": "factory/zone-b/status",   ← topic LWT thiết bị đăng ký
    "lwt_payload_offline": "offline",        ← payload khi offline
    "lwt_payload_online":  "online"          ← payload khi online (retained)
  }
}

MqttNativeAdapter subscribe lwt_topic song song với data topic.
Khi nhận LWT payload = lwt_payload_offline:
  → Phát DeviceUnresponsiveEvent ngay lập tức (không chờ stale_timeout_ms)
  → TagDatabase: tất cả tag của device → Quality = Bad, reason = "LWT_Offline"
  → Không cần chờ stale detection timer
```

**Bổ sung vào Domain Events (Module 2A-1):** Thêm `MqttDeviceLwtOfflineEvent` và `MqttDeviceLwtOnlineEvent` — phân biệt với `DeviceUnresponsiveEvent` (Modbus timeout) để UI hiển thị lý do chính xác.

---

#### ✅ I.4 — Command ACK Tracker cho thiết bị MQTT (Chấp nhận — bổ sung vào Module 2A-10)

**Đánh giá:** Đúng. Thiết bị MQTT nhận lệnh bất đồng bộ — không như Modbus FC06 có response frame đồng bộ trong cùng TCP connection. Cần cơ chế theo dõi ACK riêng. **Bổ sung vào Module 2A-10 (G3).**

**Bổ sung vào Module 2A-10 (`CommandHandler`):**
- Thêm `IMqttCommandAckTracker` interface:
  - `TrackCommand(dcmd_id, device_id, ack_topic, timeout_ms)` — đăng ký chờ ACK
  - `ReceiveAck(topic, payload)` — `MqttNativeAdapter` gọi khi nhận message trên `ack_topic`
  - Timeout → `CommandResult.AckTimeout` → ghi Audit Log + NACK về Business Server
- Device Template bổ sung:
  - `"command_topic_template": "factory/{device_id}/cmd"` — topic để publish lệnh
  - `"command_ack_topic": "factory/{device_id}/cmd/ack"` — topic chờ ACK
  - `"command_ack_timeout_ms": 5000` — timeout chờ ACK (mặc định 5 giây)
- Audit Log ghi thêm: `ack_received_at_utc_ns`, `ack_payload`, `ack_result`

---

#### ✅ I.5 — Zero-Allocation: `ArrayPool<byte>`, `Span<T>`, `Utf8JsonReader` (Chấp nhận — bổ sung kỹ thuật)

**Đánh giá:** `ArrayPool<byte>` đã ghi trong Module 2A-8. Nhưng `Utf8JsonReader` trên `ReadOnlySpan<byte>` và **Multi-tag Payload parsing** cho MQTT Native là gap thực sự — hiện tại thiết kế dùng `System.Text.Json` deserialization object thông thường, allocate object mỗi message. Với thiết bị MQTT publish 100 msg/s, đây là GC pressure đáng kể.

**Bổ sung vào Module 2A-3 (`MqttNativeAdapter`):**
- Parse payload JSON bằng `Utf8JsonReader` trực tiếp trên `ReadOnlySpan<byte>` từ `MqttApplicationMessage.Payload` — zero object allocation
- **Multi-tag Payload** trong Device Template:
  ```json
  "payload_mappings": [
    { "tag_name": "temperature", "json_path": "$.sensors.temp",    "data_type": "float32" },
    { "tag_name": "humidity",    "json_path": "$.sensors.humidity", "data_type": "float32" },
    { "tag_name": "status_code", "json_path": "$.status",           "data_type": "uint16"  }
  ]
  ```
  Một message JSON trích xuất nhiều tag mà không allocate intermediate object
- `ArrayPool<byte>` bắt buộc khi cần buffer tạm trong parsing — trả pool sau khi xong

---

#### ✅ I.6 — Burst/Storm Protection & Throttling (Chấp nhận — bổ sung vào Module 2A-3)

**Đánh giá:** Gap thực sự. Thiết bị MQTT giá rẻ đôi khi publish liên tục khi có sự kiện (ví dụ: cảnh báo nhiệt độ cao → flood message). Nếu không có rate-limit, `IInternalEventBus` Channel bị flood → backpressure kéo chậm toàn bộ pipeline → OOM tiềm ẩn.

**Bổ sung vào Module 2A-3 (`MqttNativeAdapter`):**
- **Token Bucket Rate Limiter** per device (dùng `System.Threading.RateLimiting.TokenBucketRateLimiter` .NET 7+):
  ```
  Device Template:
  "rate_limit": {
    "max_messages_per_second": 10,
    "burst_size": 20
  }
  ```
- Khi vượt rate limit:
  - **Drop DDATA** (metric value update) — ưu tiên thấp
  - **Giữ lại DBIRTH/DDEATH/LWT** — không bao giờ drop, dù rate limit bị vượt
  - Ghi metric `gateway_mqtt_dropped_messages_total` (Prometheus counter per device)
  - Phát `MqttMessageDroppedEvent` lên `IInternalEventBus` để AdminService log warning

---

#### ✅ I.7 — Rate-Limited Replay khi Buffer xả (Chấp nhận — bổ sung vào Module 2A-8)

**Đánh giá:** Đúng và quan trọng. Hiện tại `ReplayAsync()` xả toàn bộ buffer khi reconnect — nếu buffer có 50,000 records sau 72h mất mạng, flood LAN OT ngay lập tức có thể gây nghẽn hoặc sập EMQX OT. **Bổ sung.**

**Bổ sung vào Module 2A-8 (`LocalBuffer`):**
- `ReplayAsync()` dùng `System.Threading.RateLimiting.FixedWindowRateLimiter` để kiểm soát tốc độ xả:
  ```json
  "LocalBuffer": {
    "ReplayRateLimitPerSecond": 500,   ← mặc định 500 msg/s
    "ReplayBurstSize": 1000            ← burst ban đầu
  }
  ```
- Trong khi replay: vẫn tiếp tục nhận và enqueue message mới (không block pipeline đang chạy)
- Ưu tiên: DBIRTH/DDEATH replay trước DDATA — để Tầng 3 biết device state trước khi nhận data

---

#### ✅ I.8 — Software + Hardware Watchdog 2 lớp (Chấp nhận — làm rõ, đã có một phần)

**Đánh giá:** Hardware Watchdog (`/dev/watchdog`) đã có trong Module 2A-9. Nhưng **Software Watchdog** (health check channel nội bộ) chưa được thiết kế rõ. Đây là lớp bảo vệ quan trọng: nếu một Worker Service bị deadlock nhưng OS vẫn chạy → Hardware Watchdog không biết.

**Bổ sung vào Module 2A-9 (`AdminService`):**
```
Software Watchdog (nội bộ process):
  Mỗi Worker Service quan trọng (PollingEngine, SparkplugEncoder, LocalBuffer)
  phải gửi heartbeat lên SoftwareWatchdog Channel mỗi chu kỳ T giây.
  AdminService kiểm tra: nếu bất kỳ Worker nào không gửi heartbeat trong 2T giây
  → Log CRITICAL → Gọi IHostApplicationLifetime.StopApplication()
  → Systemd auto-restart process
  → Hardware Watchdog reset board nếu systemd restart cũng thất bại
```
- Interface bổ sung: `ISoftwareWatchdog` — `ReportAlive(workerId)`, `IsAnyWorkerUnhealthy()`
- Mỗi Worker Service gọi `ReportAlive(nameof(PollingEngine))` sau mỗi poll cycle hoàn thành

---

#### ✅ I.9 — Remote Diagnostics: `dotnet-dump` / `dotnet-trace` (Chấp nhận có điều kiện)

**Đánh giá:** Hữu ích cho môi trường production có memory leak nghi ngờ mà không thể SSH vào Gateway. Tuy nhiên cần giới hạn chặt: chỉ Admin mới trigger được, và cần cảnh báo rõ ràng về performance impact của `dotnet-trace`.

**Bổ sung vào Module 2A-9 (`AdminService`):**
- API `POST /api/diagnostics/dump` — trigger `dotnet-dump collect` → lưu file `.dmp` vào `/tmp/ems-diag/`
- API `POST /api/diagnostics/trace?duration=30` — trigger `dotnet-trace collect` trong N giây → lưu `.nettrace`
- API `GET /api/diagnostics/files` — list file diagnostics có sẵn để download
- API `GET /api/diagnostics/files/{filename}` — download file (qua HTTP chunked transfer)
- **Giới hạn bắt buộc:**
  - Chỉ Admin role mới gọi được
  - `dotnet-trace` tự động dừng sau tối đa 60 giây — tránh fill disk
  - Auto-cleanup file diagnostics sau 1 giờ
  - Cảnh báo UI: "Trace collection có thể ảnh hưởng hiệu năng trong thời gian thu thập"

---

#### ✅ I.10 — MQTT Uplink HA: Multi-Broker Failover (Chấp nhận — bổ sung vào Module 2A-7 & 2A-8)

**Đánh giá:** Hợp lý cho G3 khi EMQX OT triển khai cluster hoặc có backup node. Không nên phức tạp hóa G1/G2.

**Bổ sung vào Module 2A-7 (`SparkplugEncoder`) và config:**
```json
"MqttBroker": {
  "Endpoints": [
    { "Host": "10.0.1.10", "Port": 1883, "Priority": 1 },
    { "Host": "10.0.1.11", "Port": 1883, "Priority": 2 }
  ],
  "FailoverTimeoutSeconds": 10
}
```
- `IMqttPublisher` thử Endpoint có Priority cao nhất trước
- Sau N lần fail → chuyển sang Endpoint Priority kế tiếp
- Khi Endpoint primary phục hồi → tự switch lại (re-probe mỗi 60 giây)
- Local buffer 72h đảm bảo không mất dữ liệu trong thời gian failover

---

#### ✅ I.11 — Stateful Edge Rule Engine: Totalizer & Time-based Functions (Chấp nhận — bổ sung vào Module 2A-5)

**Đánh giá:** Gap quan trọng. Hiện tại `EdgeRuleEngine` là pure stateless function — không tính được Totalizer (∑ kWh cộng dồn), running average, hay rate-of-change dựa trên lịch sử. Đây là yêu cầu thực tế của EMS.

**Bổ sung vào Module 2A-5 (`EdgeRuleEngine`):**
- Thêm `IEdgeStateStore` — in-memory state store per virtual tag, persist vào SQLite (cùng file với LocalBuffer, khác table) khi flush:
  - Tránh mất state khi restart (Totalizer không bị reset về 0)
  - Persist mỗi 60 giây (không mỗi poll — tránh flash wear)
- Custom NCalc functions đăng ký qua `EvaluateFunction` callback:
  - `TOTALIZER(tag_name)` — cộng dồn giá trị theo thời gian: `∫ value × dt`
  - `ROLLING_AVG(tag_name, window_seconds)` — trung bình trượt trong N giây
  - `RATE(tag_name)` — tốc độ thay đổi: `(current - previous) / dt`
  - `ELAPSED_ON(tag_name, threshold)` — thời gian tag > threshold (giây)
- State được isolate per virtual tag — không share state giữa các expression để tránh side effect
- Khi hot-reload config xóa một virtual tag → state của tag đó bị xóa sạch

---

#### ✅ I.12 — OS Security Hardening: Non-root + `setcap` (Chấp nhận — bổ sung vào deploy artifacts)

**Đánh giá:** Best practice bắt buộc cho production. Không ảnh hưởng đến module code, thuộc về `deploy/` artifacts.

**Bổ sung vào `deploy/` (systemd unit + setup script):**
```ini
# /etc/systemd/system/ems-gateway.service
[Service]
User=ems-gateway
Group=ems-gateway
# Thêm group dialout cho RS485 serial port
SupplementaryGroups=dialout

# Không cần root để bind port > 1024
# Nếu cần port < 1024 (ví dụ health check port 80):
# ExecStartPre=/sbin/setcap 'cap_net_bind_service=+ep' /usr/bin/ems-gateway
```
```bash
# setup.sh
useradd --system --no-create-home --shell /sbin/nologin ems-gateway
usermod -aG dialout ems-gateway
# setcap chỉ khi cần bind port đặc quyền
setcap cap_net_bind_service=+ep /usr/bin/ems-gateway
```
- `/dev/watchdog` cần quyền đặc biệt: thêm udev rule cho group `ems-gateway` có thể ghi `/dev/watchdog`

---

#### ❌ I.13 — JWT Validate tại Gateway (Bác bỏ — đã có, không phải yêu cầu mới)

**Đánh giá:** Đã thiết kế đầy đủ trong Module 2A-10, mục "Công nghệ & Design Pattern": `Microsoft.IdentityModel.Tokens` validate JWT với **local public key** — không call mạng. Yêu cầu bổ sung này là duplicate của thiết kế hiện tại. Không bổ sung thêm.

---

### Nhóm II — UI/UX

#### ✅ II.1 — Tag Quality Display với màu + icon + tooltip (Chấp nhận — làm phong phú thêm)

**Đánh giá:** Thiết kế hiện tại đã có color coding. Bổ sung **icon + text label** để đáp ứng yêu cầu color-blind safe. Cập nhật component `Tag Quality Indicator`.

**Cập nhật component `Tag Quality Indicator`:**
```
● [✔ Good]   → icon ✔ xanh + text "Good"    + tooltip: "Last updated: [timestamp]"
● [⚠ Stale]  → icon ⚠ vàng + text "Stale"   + tooltip: "No update since: [stale_since]"
● [✖ Bad]    → icon ✖ đỏ   + text "Bad"     + tooltip: "Reason: [PropertySet.reason]"

Không dùng màu đơn độc — luôn kết hợp Màu + Icon + Text
```

---

#### ✅ II.2 — Buffer & Queue Visualization Widget (Chấp nhận — bổ sung vào Dashboard)

**Đánh giá:** Thiết kế Dashboard hiện tại đã có "Buffer fill %" nhưng chỉ dạng text. Bổ sung widget trực quan hơn.

**Bổ sung vào Dashboard — thay thế dòng buffer hiện tại:**
```
┌─────────────────────────────────────────────────────┐
│  LOCAL BUFFER STATUS                                 │
│                                                      │
│  RAM (tmpfs)  [████░░░░░░░░] 34%  142 records        │
│  Disk (SQLite)[█░░░░░░░░░░░]  8%  1,240 records      │
│  Replay rate  ─────────────  500 msg/s  (active)     │
│  Estimated drain time: ~2 min 28 sec                 │
└─────────────────────────────────────────────────────┘
```
- Progress bar đổi màu: < 50% xanh, 50–80% vàng, > 80% đỏ nhấp nháy
- "Estimated drain time" tính từ `pending_records / replay_rate`
- Khi không có replay: hiển thị "Idle — connected"

---

#### ✅ II.3 — Deadband & RoC Visualization (Chấp nhận — bổ sung vào Data Monitor)

**Đánh giá:** Rất hữu ích khi kỹ sư cần điều chỉnh deadband — hiện tại không thể thấy được "bao nhiêu giá trị bị lọc bỏ". Bổ sung mini-sparkline vào Data Monitor.

**Bổ sung vào Data Monitor — khi expand một row:**
```
▼ kW_total  |  842.30 kW  |  ● Good  |  14:32:01

  Raw (polling):    [──────╮╭──────────╮╭───────]  840–846 kW
  Forwarded (deadband=0.5): [────────────────────]  842.3 kW (ít thay đổi)
  Deadband filter: Đã lọc 23/30 samples (76.7%)
  RoC hiện tại: +0.3 kW/s  (limit: 500 kW/s)  ● OK
```
- Raw value vẽ bằng mini canvas sparkline (50px × 20px)
- Forwarded value là đường thẳng hơn (sau deadband)
- Số "Đã lọc X/Y samples" giúp kỹ sư biết deadband đang hoạt động đúng hay quá aggressive

---

#### ✅ II.4 — NTP Drift & Watchdog Status trên Dashboard (Chấp nhận — bổ sung)

**Đánh giá:** NTP Drift đã có trong Prometheus metrics nhưng chưa hiển thị trên Dashboard. Bổ sung vào card System Resources.

**Cập nhật card System Resources:**
```
┌────────────────────────────────────────┐
│  SYSTEM RESOURCES                      │
│  CPU  [████████░░░░] 64%               │
│  RAM  [██████░░░░░░] 48%               │
│  Disk [███░░░░░░░░░] 22%               │
│                                        │
│  NTP Drift    ● +0.42s  (limit: 5s)   │
│  HW Watchdog  ● Active  (30s heartbeat)│
│  SW Watchdog  ● All workers healthy    │
└────────────────────────────────────────┘
```
- NTP Drift: xanh < 2s, vàng 2–5s, đỏ > 5s
- HW Watchdog: xanh = đang ghi heartbeat; đỏ = không ghi được `/dev/watchdog`
- SW Watchdog: xanh = tất cả worker alive; đỏ = worker nào đó không heartbeat

---

#### ✅ II.5 — Sparkplug B Lifecycle Details (Chấp nhận — bổ sung vào màn hình MQTT)

**Đánh giá:** Thêm thông tin NBIRTH/DBIRTH sequence, LWT status vào màn hình MQTT — hữu ích để debug.

**Bổ sung vào màn hình MQTT/Sparkplug — thêm section "Sparkplug B Lifecycle":**
```
SPARKPLUG B LIFECYCLE
┌──────────────────────────────────────────────────────┐
│  Node State   : ● ONLINE (NBIRTH published)          │
│  Seq number   : 142  (wrap: 0–255, auto reset)       │
│  Last NBIRTH  : 2024-11-03 08:12:44 (12d ago)        │
│  Last DBIRTH  : 2024-11-15 14:20:01 (12 min ago)     │
│    → Re-published after config reload (G2)            │
│  LWT registered: ● Yes  (will topic: gw/status/lwt)  │
│  DDEATH sent  : Never   (uptime clean)                │
└──────────────────────────────────────────────────────┘
```

---

#### ✅ II.6 — Visual JSONPath Tester / Payload Mapper (Chấp nhận — màn hình debug mới)

**Đánh giá:** Rất thực tiễn. Kỹ sư không cần viết code để kiểm tra `value_json_path` của MQTT device. Bổ sung như một tab trong màn hình MQTT/Sparkplug.

**Bổ sung tab "JSONPath Tester" trong màn hình MQTT:**
```
┌──────────────────────────────────────────────────────────────────┐
│  JSONPATH TESTER                                  [Tab: Debug]   │
├──────────────────────────────────────────────────────────────────┤
│  Paste JSON payload:                                             │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ {"sensors":{"temp":42.3,"humidity":65},"status":1}        │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  JSONPath expression:  [$.sensors.temp              ]           │
│                                                                  │
│  Kết quả:  ✔  42.3  (float)                                     │
│            ↳ Tương đương tag: temperature = 42.3                │
│                                                                  │
│  Multi-tag test:                                                 │
│  $.sensors.temp     → 42.3   ✔                                  │
│  $.sensors.humidity → 65     ✔                                  │
│  $.status           → 1      ✔ (uint16)                         │
│  $.nonexistent      → NULL   ✖ Path not found                   │
│                                                                  │
│  [📋 Copy as device_template config]                             │
└──────────────────────────────────────────────────────────────────┘
```
- Xử lý hoàn toàn client-side (JavaScript JSONPath library) — không gọi API
- Nút "Copy as device_template config" tạo snippet JSON sẵn sàng paste vào template

---

#### ✅ II.7 — Topic Subscription Explorer (Chấp nhận — màn hình debug mới)

**Đánh giá:** Hữu ích khi debug nhiều thiết bị MQTT đang push vào Gateway. Giúp kỹ sư thấy ngay topic nào đang có traffic và topic nào im lặng.

**Bổ sung tab "Topic Explorer" trong màn hình MQTT:**
```
TOPIC SUBSCRIPTION EXPLORER       ● Live  [⏸ Pause]
┌──────────────────────────────────────────────────────────────┐
│ factory/                                                      │
│  ├─ zone-a/                                                   │
│  │   ├─ temperature    ● 3 msg/s   Last: 14:32:01  [42.3°C] │
│  │   └─ pressure       ● 1 msg/s   Last: 14:31:58  [1.02bar]│
│  ├─ zone-b/                                                   │
│  │   ├─ temperature    ● 1 msg/s   Last: 14:31:45  [38.1°C] │
│  │   └─ status         ● Idle      Last: 14:28:10  [online] │
│  └─ compressor-01/                                            │
│      └─ telemetry      ✖ No data   Last: never               │
└──────────────────────────────────────────────────────────────┘
```
- Tree được build từ danh sách `subscribe_topic` trong device templates
- Rate và last message cập nhật qua SignalR event `MqttMessageReceived`
- Topic không có traffic > 5 phút → hiển thị "Idle" màu vàng
- Topic không bao giờ nhận được message → "No data" màu đỏ — giúp phát hiện config sai topic

---

#### ✅ II.8 — NCalc Debugger (Chấp nhận — màn hình debug mới)

**Đánh giá:** Cực kỳ thực tiễn. Kỹ sư điều chỉnh expression OEE/SEI thường xuyên — không có debugger thì phải deploy lại config để biết có đúng không.

**Bổ sung tab "Expression Debugger" trong màn hình Devices hoặc Data Monitor:**
```
NCALC EXPRESSION DEBUGGER
┌──────────────────────────────────────────────────────────────┐
│  Device context: [meter-main-01  ▼]  (dùng data thực hiện tại)│
│                                                              │
│  Expression:                                                 │
│  [kW_total / kVA_total                                     ] │
│                                                              │
│  Available tags (meter-main-01):                             │
│  kW_total = 842.30  (Good)    kVA_total = 914.50  (Good)    │
│  kWh_total = 12483.5 (Good)  cosφ = 0.921 (Good, virtual)  │
│                                                              │
│  [▶ Evaluate]                                                │
│                                                              │
│  Kết quả:   0.9208  ✔                                       │
│  Quality propagation: Good (tất cả inputs = Good)           │
│  Thời gian evaluate: 0.08ms                                 │
│                                                              │
│  ⚠ Syntax error test:                                       │
│  [kW_total / 0] → NaN ✖ — sẽ tạo Quality=Bad               │
│                                                              │
│  [💾 Save as virtual tag "cosφ_calc" to template]           │
└──────────────────────────────────────────────────────────────┘
```
- Evaluate gọi API `POST /api/debug/evaluate-expression` — server dùng NCalc với TagDatabase snapshot thực
- Hiển thị quality propagation rõ ràng
- Gợi ý tự động tên tag available từ device context đang chọn

---

#### ✅ II.9 — Hex Dump / Frame Sniffer (Chấp nhận có giới hạn)

**Đánh giá:** Hữu ích để debug dây tín hiệu RS485 bị nhiễu hoặc CRC error. Tuy nhiên cần giới hạn: chỉ capture một khoảng thời gian ngắn, không stream liên tục (tốn CPU).

**Bổ sung tab "Frame Sniffer" trong Log Viewer:**
- API `POST /api/diagnostics/modbus-capture?device_id=X&duration_seconds=30` — bật capture cho device X trong 30 giây
- Hiển thị từng frame TX/RX theo hex: `[TX] 01 03 00 00 00 0A C5 CD` | `[RX] 01 03 14 ...`
- Highlight byte CRC, Function Code, Slave ID bằng màu sắc khác nhau
- Tự động tắt sau `duration_seconds` — không để capture vô hạn

---

#### ✅ II.10 — Message Rate Monitor (Chấp nhận — bổ sung vào Dashboard hoặc MQTT screen)

**Đánh giá:** Cần thiết khi có Event Storm từ thiết bị MQTT. Bổ sung widget nhỏ.

**Bổ sung vào màn hình MQTT/Sparkplug:**
```
MESSAGE RATE (realtime)
┌──────────────────────────────────────┐
│  Ingress (từ thiết bị MQTT):  42/s   │
│  Processed (sau filter):      18/s   │
│  Dropped (rate limit):         0/s   │
│  Egress (publish Sparkplug B): 18/s  │
│                                      │
│     42 ┤ ╭╮   ╭──╮                  │
│     20 ┤─╯╰───╯  ╰── (5 phút)       │
└──────────────────────────────────────┘
```

---

#### ✅ II.11 — Mobile-First & Touch Targets ≥ 48×48px (Chấp nhận — cập nhật Design System)

**Đánh giá:** Đúng với bối cảnh Industrial PC màn hình cảm ứng hoặc Tablet IP65 ngoài hiện trường. Bổ sung vào Design System.

**Cập nhật Nguyên tắc UI/UX:**
- Tất cả button, icon-button, dropdown trigger: tối thiểu **48×48px** touch target
- Khoảng cách giữa các element tương tác: tối thiểu **8px** để tránh chạm nhầm
- Không dùng **hover-only** interaction — tooltip và action menu phải accessible qua tap/click
- Layout responsive: breakpoint tối thiểu **1024px** (tablet landscape) vẫn hiển thị đủ thông tin quan trọng
- Font size tối thiểu **14px** cho nội dung bảng — đọc được ở khoảng cách tay với

---

#### ✅ II.12 — i18n Localization (Chấp nhận — kiến trúc frontend)

**Đánh giá:** Hợp lý cho môi trường FDI. Không ảnh hưởng backend. Bổ sung vào kiến trúc frontend.

**Bổ sung vào Công nghệ đề xuất:**
- Dùng **i18next** (React) hoặc tương đương — resource file JSON tách biệt khỏi code
- Hỗ trợ ban đầu: Tiếng Việt (`vi`) và Tiếng Anh (`en`)
- Language switch không cần reload trang — runtime switch
- Mọi string hiển thị (label, tooltip, error message, confirm dialog) đều qua i18n key — không hard-code text
- Số liệu và ngày giờ: dùng `Intl.NumberFormat` và `Intl.DateTimeFormat` theo locale

---

#### ✅ II.13 — Bulk Actions (Chấp nhận — bổ sung vào Device Management)

**Đánh giá:** Hữu ích khi quản lý > 50 thiết bị.

**Bổ sung vào màn hình Device Management:**
```
☑ [Chọn tất cả]   3 thiết bị đã chọn
[Bulk Actions ▼]
  ├─ Pause polling (tạm dừng poll)
  ├─ Resume polling
  ├─ Set poll cycle... (nhập ms cho tất cả)
  └─ Delete selected... (confirm dialog)
```
- Checkbox multi-select trên mỗi row
- Bulk action hiển thị summary: "Áp dụng cho 3 thiết bị: meter-main-01, vfd-comp-01, sensor-temp-b"
- Confirm dialog cho Delete và Set poll cycle

---

#### ✅ II.14 — Manual Override / Simulate (Chấp nhận có điều kiện — chỉ Admin)

**Đánh giá:** Hữu ích để test alarm rule tại Tầng 3 mà không cần tạo điều kiện thực. Cần ghi flag `IsSimulated=true` rõ ràng trong Sparkplug B PropertySet để Tầng 3 không nhầm với dữ liệu thực.

**Bổ sung vào Data Monitor (chỉ Admin role):**
- Nút "Override" trên row của tag: mở dialog nhập giá trị giả lập
- Giá trị override được inject vào `EnrichedTagBatch` tại `EdgeRuleEngine` với flag `IsSimulated=true`
- Sparkplug B Encoder thêm `PropertySet: {"is_simulated": true}` vào metric
- Banner cảnh báo màu vàng toàn màn hình khi có tag đang override: "⚠ SIMULATION ACTIVE — X tags đang ở chế độ giả lập. Dữ liệu tại Tầng 3 có thể không chính xác."
- Override tự động hết hạn sau 10 phút (cấu hình) — không để kỹ sư quên

---

#### ✅ II.15 — G3 Safe-Mode Banner & Whitelist Display (Chấp nhận — bổ sung vào toàn bộ UI)

**Đánh giá:** Yêu cầu an toàn bắt buộc khi bật Command Handler G3.

**Bổ sung — Global Banner khi `CommandHandler:Enabled=true`:**
```
┌──────────────────────────────────────────────────────────────────┐
│ 🔴 WRITE MODE ACTIVE (G3) — Gateway có thể gửi lệnh điều khiển  │
│ Whitelist: vfd-compressor-01[freq_hz] · pump-01[on_off]          │
│                                       [Xem đầy đủ] [Tắt G3...]  │
└──────────────────────────────────────────────────────────────────┘
```
- Banner dính cố định phía trên sidebar — không thể dismiss
- Màu đỏ nhạt, icon 🔴 — không thể bỏ qua
- Link "Xem đầy đủ" → modal hiển thị toàn bộ whitelist với register address và giới hạn vật lý

---

#### ✅ II.16 — Audit Log Viewer độc lập (Chấp nhận — màn hình mới, G3)

**Đánh giá:** Cần thiết để kỹ sư vận hành tại hiện trường kiểm tra lệnh điều khiển đã được thực hiện mà không cần truy cập Business Server (Tầng 3).

**Bổ sung màn hình "Audit Log" (hiển thị trong Sidebar khi G3 active):**
```
AUDIT LOG — COMMAND HISTORY                    [⬇ Export NDJSON]
┌──────────────────────────────────────────────────────────────────────┐
│ Filter: [All ▼]  Device: [All ▼]  Result: [All ▼]  🔍 [dcmd_id...] │
├───────────────┬──────────────┬──────────────┬────────┬───────────────┤
│ Timestamp     │ Device       │ Command      │ Value  │ Result        │
├───────────────┼──────────────┼──────────────┼────────┼───────────────┤
│ 14:30:22.001  │ vfd-comp-01  │ frequency_hz │ 45.0Hz │ ● ACK         │
│ 14:28:05.445  │ vfd-comp-01  │ frequency_hz │ 50.0Hz │ ● ACK         │
│ 14:15:33.002  │ pump-01      │ on_off       │ 0      │ ✖ AckTimeout  │
│ 13:50:11.880  │ vfd-comp-02  │ frequency_hz │ 48.0Hz │ ✖ Whitelist   │
│               │              │              │        │   Rejected    │
├───────────────┴──────────────┴──────────────┴────────┴───────────────┤
│ 4 lệnh hiển thị (3 thành công · 1 timeout · 1 rejected)              │
└──────────────────────────────────────────────────────────────────────┘
```
- Đọc trực tiếp từ file `.ndjson` local (không từ PostgreSQL Tầng 3) — hoạt động khi Business Server down
- Export NDJSON để đối chiếu với Audit Trail PostgreSQL tại Business Server qua `dcmd_id`
- Giải thích rõ từng `Result`: ACK (thiết bị xác nhận), AckTimeout (thiết bị không phản hồi), WhitelistRejected (địa chỉ không được phép), RangeExceeded, JwtInvalid

---

### Tóm tắt phân loại

| # | Yêu cầu | Quyết định | Vị trí bổ sung |
|---|---|---|---|
| I.1 | Bus Arbitration inter-frame gap | ✅ Bổ sung | Module 2A-3 |
| I.2 | Embedded MQTTnet.Server | ✅ Bắt buộc (G3 + MQTT Native) / Không cần (G1/G2 hoặc chỉ Modbus) | Module 2A-3, 2A-11 |
| I.3 | LWT cho MQTT device | ✅ Bổ sung | Module 2A-3, 2A-1 |
| I.4 | Command ACK Tracker MQTT | ✅ Bổ sung | Module 2A-10 |
| I.5 | Zero-allocation + Multi-tag payload | ✅ Bổ sung | Module 2A-3 |
| I.6 | Burst/Storm Protection Token Bucket | ✅ Bổ sung | Module 2A-3 |
| I.7 | Rate-Limited Replay | ✅ Bổ sung | Module 2A-8 |
| I.8 | Software Watchdog 2 lớp | ✅ Bổ sung | Module 2A-9 |
| I.9 | Remote Diagnostics dump/trace | ✅ Có điều kiện | Module 2A-9 |
| I.10 | MQTT HA Multi-Broker Failover | ✅ Bổ sung | Module 2A-7 |
| I.11 | Stateful EdgeRuleEngine Totalizer | ✅ Bổ sung | Module 2A-5 |
| I.12 | OS Hardening non-root + setcap | ✅ Bổ sung | deploy/ artifacts |
| I.13 | JWT validate tại Gateway | ❌ Duplicate | Đã có Module 2A-10 |
| II.1 | Tag Quality icon + text color-blind | ✅ Bổ sung | UI Component |
| II.2 | Buffer Widget trực quan | ✅ Bổ sung | Dashboard |
| II.3 | Deadband & RoC Visualization | ✅ Bổ sung | Data Monitor |
| II.4 | NTP + Watchdog trên Dashboard | ✅ Bổ sung | Dashboard |
| II.5 | Sparkplug B Lifecycle Details | ✅ Bổ sung | MQTT Screen |
| II.6 | JSONPath Tester / Payload Mapper | ✅ Bổ sung | MQTT Screen tab |
| II.7 | Topic Subscription Explorer | ✅ Bổ sung | MQTT Screen tab |
| II.8 | NCalc Debugger | ✅ Bổ sung | Data Monitor tab |
| II.9 | Hex Dump / Frame Sniffer | ✅ Có giới hạn | Log Viewer tab |
| II.10 | Message Rate Monitor | ✅ Bổ sung | MQTT Screen |
| II.11 | Mobile-First Touch Targets | ✅ Bổ sung | Design System |
| II.12 | i18n Localization | ✅ Bổ sung | Frontend Architecture |
| II.13 | Bulk Actions | ✅ Bổ sung | Device Management |
| II.14 | Manual Override/Simulate | ✅ Có điều kiện | Data Monitor |
| II.15 | G3 Safe-Mode Banner | ✅ Bổ sung | Global UI |
| II.16 | Audit Log Viewer | ✅ Bổ sung | Màn hình mới (G3) |

> **Tổng kết:** 27/28 yêu cầu được chấp nhận (trong đó 4 có điều kiện/giới hạn, I.2 được đánh giá lại từ bác bỏ thành chấp nhận có phạm vi rõ ràng), 1 duplicate (I.13 — đã có).

---

*Phiên bản 1.4 — Đánh giá lại I.2 Embedded MQTTnet.Server: chấp nhận là **bắt buộc** trong kịch bản G3 tách máy có thiết bị MQTT Native — giải quyết gap "mất khả năng nhận dữ liệu từ thiết bị MQTT khi EMQX OT down" mà LocalBuffer không cover được. Làm rõ hai vai trò tách biệt: Embedded Broker = South-facing (nhận từ thiết bị trường), EMQX OT = North-facing (bridge lên Tầng 3). Bổ sung config `EmbeddedMqttBroker` và điều kiện auto-enable trong Host.*  
*Phiên bản 1.3 — Phân tích & phản biện 28 yêu cầu bổ sung: bổ sung LWT cho MQTT device (2A-3), Command ACK Tracker (2A-10), Multi-tag Zero-Allocation Payload (2A-3), Token Bucket Storm Protection (2A-3), Rate-Limited Replay (2A-8), Software Watchdog 2 lớp (2A-9), Remote Diagnostics API (2A-9), MQTT HA Failover (2A-7), Stateful EdgeRuleEngine Totalizer/RollingAvg (2A-5), OS Hardening deploy artifacts; bổ sung UI: Buffer Widget, Deadband Visualization, JSONPath Tester, Topic Explorer, NCalc Debugger, Hex Sniffer, Bulk Actions, Manual Override, G3 Banner, Audit Log Viewer.*  
*Phiên bản 1.0 — Tài liệu phân rã Tầng 2A (IIoT Gateway) · Solution .NET 8 firmware biên · Độc lập với Tầng 2B và Tầng 3 · Sparkplug B native (Eclipse.Tahu.Protobuf) · NCalc Virtual Tag Edge Engine · Bus Arbitration WriteQueue · Áp dụng cho nhà máy SME, On-premise Việt Nam.*  
*`[G3]` = Tính năng Giai đoạn 3 — không bắt buộc ở triển khai ban đầu.*
