# Danh sách Yêu cầu Bổ sung cho Kiến trúc EMS IIoT Gateway (Tầng 2A)

Tài liệu này tổng hợp các yêu cầu bổ sung chuyên sâu nhằm đảm bảo hệ thống IIoT Gateway đạt chuẩn production (industrial-grade), có khả năng chạy 24/7 tại hiện trường, và đáp ứng tốt sự dịch chuyển sang giao thức MQTT phía SOUTH.

---

## I. Yêu cầu Hệ thống & Vận hành (System Architecture)

### 1. Xử lý Giao thức & Kết nối Field (SOUTH Layer)
* **Cơ chế phân xử Bus (Bus Arbitration) cho RS485:** Đảm bảo luồng lệnh Write (ưu tiên cao) ngắt lệnh Poll mà không gây collision hoặc hỏng frame Modbus trên bus half-duplex.
* **Topology MQTT Broker phía Edge:** Khuyến nghị nhúng trực tiếp (embed) MQTT Broker (như `MQTTnet.Server`) vào trong firmware .NET 8 của Gateway để giảm dependency hạ tầng và tối ưu zero-network-hop cho luồng dữ liệu.
* **Quản lý trạng thái vòng đời qua LWT:** Bắt buộc subscribe topic Last Will and Testament (LWT) của thiết bị MQTT trường để phát hiện đứt mạng tức thời, thay vì chỉ dựa vào `stale_timeout_ms` như Modbus.
* **Định tuyến lệnh điều khiển (Southbound Command Routing):** Bổ sung cấu hình topic template và cơ chế **Command ACK Tracker** cho thiết bị MQTT để xác nhận lệnh điều khiển bất đồng bộ thành công trước khi ghi Audit Log.

### 2. Tối ưu Hiệu năng & Tài nguyên
* **Quản lý Memory & Zero-Allocation:** Áp dụng chặt chẽ `ArrayPool<byte>`, `Span<T>` trong việc parse byte array và Protobuf encoding để tránh Garbage Collector (GC) spike.
* **Zero-Allocation JSON Parsing:** Bắt buộc dùng `Utf8JsonReader` trên `ReadOnlySpan<byte>` để quét trực tiếp binary payload của thiết bị MQTT, kết hợp hỗ trợ cấu hình *Multi-tag Payload* để trích xuất nhiều biến từ một message mà không bị cấp phát Object rác.
* **Bảo vệ Flash-wear & Giới hạn I/O:** Sử dụng RAM Disk (`tmpfs`) làm bộ đệm ghi trung gian trước khi flush xuống eMMC/SSD qua SQLite WAL để tăng tuổi thọ ổ đĩa phần cứng.
* **Kiểm soát bão sự kiện (Burst/Storm Protection & Throttling):** Áp dụng rate-limit (Token Bucket) tại MQTT Native Adapter. Nếu message rate quá cao, ưu tiên drop DDATA và giữ lại trạng thái Birth/Death/LWT để tránh OOM crash.
* **Giới hạn tốc độ xả Buffer (Rate-Limited Replay):** Áp dụng Backpressure khi Gateway có mạng trở lại, xả dữ liệu từ SQLite lên Broker Tầng 2B với tốc độ kiểm soát được (vd: 1000 msg/s) để tránh làm nghẽn LAN OT hoặc sập EMQX.

### 3. Vận hành, Tính ổn định & Chẩn đoán
* **Cơ chế Watchdog 2 lớp:** Bắt buộc có Software Watchdog (health check channel) và Hardware Watchdog (thông qua `/dev/watchdog` của Linux) để tự động hard-reset nếu OS hoặc .NET runtime bị treo.
* **Kiểm soát đồng bộ thời gian (NTP Drift):** Giám sát liên tục qua chronyd/systemd-timesyncd. Có kịch bản fallback rõ ràng (annotate `Stale`) nếu độ lệch thời gian vượt quá ngưỡng 5 giây.
* **Tính nguyên tử khi Hot-Reload Cấu hình:** Sử dụng cơ chế thread-safe (atomic swap với `ImmutableDictionary`) khi nhận cấu hình mới để các tác vụ đang poll không bị crash do state mutation.
* **Cơ sở hạ tầng Chẩn đoán từ xa:** Cho phép AdminService trigger `dotnet-dump` hoặc `dotnet-trace` từ xa để lấy snapshot bộ nhớ (.dmp file) khi nghi ngờ có memory leak tại hiện trường.
* **High Availability cho MQTT Uplink:** Hỗ trợ cấu hình danh sách nhiều Broker URLs phía Tầng 2B để tự động fail-over khi một node EMQX gặp sự cố.
* **State Machine cho Edge Rule Engine:** Bổ sung cơ chế lưu State cục bộ cho NCalc, cho phép tính toán các hàm phụ thuộc thời gian (ví dụ: Totalizer cộng dồn) thay vì chỉ stateless.

### 4. Bảo mật (Security)
* **Bảo mật Payload Điều khiển (G3):** Validate JWT signature của Tầng 3 trực tiếp ngay tại Gateway bằng public key cục bộ trước khi xử lý lệnh, chống giả mạo ngay cả khi EMQX bị compromise.
* **Bảo mật mức OS (Hardening):** Chạy Gateway service dưới quyền non-root (vd: `ems-gateway`). Cấp quyền cụ thể bằng `setcap` (bind port) và group `dialout` (RS485) thay vì dùng quyền root.

---

## II. Yêu cầu Thiết kế UI/UX (Gateway Management Console)

### 1. Quan sát trạng thái & Monitor
* **Hiển thị Tag Quality Native:** Bắt buộc hiển thị rõ Quality (`Good`, `Bad`, `Stale`) của Sparkplug B bằng màu sắc, icon và tooltip phân biệt rõ mất mạng vật lý hay lỗi cấu hình.
* **Visualization cho Buffer & Queue:** Widget riêng trên Dashboard hiển thị % dung lượng RAM/Disk đã dùng, số record pending và trạng thái xả buffer.
* **Bảng điều khiển Deadband & RoC:** Hiển thị trực quan Raw Value vs Forwarded Value (sau khi lọc Deadband) bằng mini-sparkline để đánh giá độ nhiễu.
* **Trạng thái đồng bộ phần cứng:** Trực quan hóa độ lệch thời gian NTP Drift và tình trạng Ping của Hardware Watchdog trên Dashboard.
* **Trạng thái vòng đời MQTT:** Hiển thị chi tiết chu kỳ Sparkplug B (Sequence number, thời điểm NBIRTH/DBIRTH cuối cùng, trạng thái LWT đăng ký thành công).

### 2. Giao diện gỡ lỗi (Debugging Tools)
* **Visual JSONPath Tester (Payload Mapper):** Công cụ trích xuất JSON trực quan; kỹ sư dán chuỗi JSON thô và gõ JSONPath để test highlight giá trị ngay trên trình duyệt mà không cần lưu cấu hình.
* **Topic Subscription Explorer:** Công cụ vẽ "Cây thư mục Topic" lắng nghe realtime, giúp nhìn tổng quan toàn bộ luồng dữ liệu MQTT trường đang đổ vào Gateway.
* **NCalc Debugger:** Màn hình nháp biểu thức toán học, thử nghiệm realtime với data thực tế trước khi lưu để tránh crash config.
* **Hex Dump / Frame Sniffer:** Tab riêng trong Log Viewer để đọc luồng byte thô TX/RX theo mã Hex, phục vụ bắt lỗi dây tín hiệu Modbus (A/B) hoặc nhiễu.
* **Giao diện Giám sát Tải (Message Rate):** Biểu đồ realtime hiển thị Ingress/Egress Message Rate và tỉ lệ Dropped messages để theo dõi Event Storm.

### 3. Vận hành & An toàn công nghiệp
* **Thiết kế Mobile-First & Touch Targets:** Giao diện Responsive dùng cho màn hình cảm ứng IPC hoặc Tablet. Nút bấm tối thiểu 48x48px, không phụ thuộc vào thao tác hover chuột.
* **An toàn cho người mù màu (Color-Blind Safe):** Mọi trạng thái hệ thống phải dùng tổ hợp Màu sắc + Icon + Text (vd: `[✔ Good]`, `[✖ Bad]`).
* **Đa ngôn ngữ (Localization - i18n):** Kiến trúc UI phải hỗ trợ chuyển ngữ (Anh/Việt) linh hoạt phục vụ chuyên gia ngoại quốc/FDI.
* **Thao tác hàng loạt (Bulk Actions):** Cung cấp Checkbox xử lý nhiều thiết bị cùng lúc (pause, delete, change poll cycle) để tiết kiệm thời gian vận hành.
* **Ghi đè giá trị (Manual Override/Simulate):** Nút thao tác ép giá trị giả lập trên Data Monitor (chèn cờ `IsSimulated=true`) để kỹ sư dễ dàng test rule cảnh báo Tầng 3.
* **Cảnh báo Safe-Mode & Whitelist (G3):** Banner tĩnh nổi bật toàn màn hình khi bật tính năng Write (G3), hiển thị danh sách Whitelist cho phép điều khiển.
* **Audit Log Viewer độc lập:** Màn hình riêng xem `.ndjson` của các lệnh điều khiển: Ai ra lệnh, lúc nào, giá trị bao nhiêu và Gateway có ACK hay không.