---

## Các Yêu cầu Bổ sung "Final Polish" (Cập nhật cho v1.8)

### Nhóm I — Vận hành Vật lý & Tự bảo vệ (Hardware & OS Level)

#### 1. Khám phá thiết bị cục bộ (Zero-Configuration / mDNS)
* **Vấn đề:** Khi kỹ sư mang Gateway xuống xưởng và cắm vào Switch của nhà máy (LAN OT có DHCP), họ không biết Gateway đang nhận IP nào để truy cập vào trang `Setup Wizard` (OOBE). Việc phải cắm màn hình/bàn phím vào Gateway để gõ `ifconfig` là một trải nghiệm tồi tệ.
* **Yêu cầu bổ sung vào OS/Deploy Artifacts:** Tích hợp và cấu hình service **mDNS (Avahi Daemon)**.
    * Gateway tự động broadcast hostname của nó (ví dụ: `ems-gw-line-a.local`) ra mạng LAN.
    * Kỹ sư chỉ cần dùng Tablet/Laptop cùng mạng, mở trình duyệt gõ `http://ems-gw-line-a.local:8080` là vào thẳng UI cấu hình, loại bỏ hoàn toàn nhu cầu dò tìm IP tĩnh.

#### 2. Giám sát tuổi thọ eMMC/SSD (Flash-Wear Predictive Maintenance)
* **Vấn đề:** Dù đã có `tmpfs` và SQLite WAL để giảm số lần ghi, nhưng eMMC công nghiệp vẫn có tuổi thọ vật lý (TBW - Terabytes Written). Khi eMMC chết, Gateway biến thành "cục gạch" không thể cứu vãn. Quản trị viên cần biết trước 1 tháng để thay phần cứng, chứ không phải đợi nó chết.
* **Yêu cầu bổ sung vào Module 2A-9 (AdminService):**
    * Background worker định kỳ (1 lần/ngày) gọi lệnh OS (như `smartctl` hoặc đọc file `/sys/block/mmcblk0/device/life_time` trên Linux).
    * Phân tích chỉ số `Life Time Used`. Nếu vượt quá ngưỡng 80% hoặc 90%, phát event cảnh báo `HardwareDegradedEvent`.
    * Bắn metric `gateway_emmc_wear_percent` lên Prometheus để Tầng 3 có thể lên lịch bảo trì phòng ngừa (Predictive Maintenance).

#### 3. Đồng bộ Hardware Clock (RTC) & Cảnh báo Pin CMOS
* **Vấn đề:** Thiết kế v1.7 cảnh báo NTP Drift rất tốt. NHƯNG, nếu nhà máy rớt mạng Uplink (Tầng 3) trong 2 tháng, Gateway không có NTP Server để đồng bộ. Lúc này, Gateway phụ thuộc hoàn toàn vào IC thời gian thực (RTC) trên bo mạch. Nếu Pin CMOS cạn, khi mất điện khởi động lại, Gateway sẽ quay về năm `1970` -> Sparkplug B Timestamp sai lệch toàn bộ, dữ liệu 72h buffer trở thành rác.
* **Yêu cầu bổ sung vào Module 2A-9:**
    * Khi có NTP (mạng bình thường), phải định kỳ đồng bộ thời gian từ OS System Time xuống Chip RTC bằng lệnh `hwclock -w` (giữ cho IC RTC luôn chuẩn).
    * Bổ sung cơ chế đọc trạng thái Pin CMOS (nếu mainboard hỗ trợ qua I2C/sysfs). Nếu phát hiện năm hệ thống tụt lùi về một epoch mặc định (vd: năm < 2024), Gateway phải từ chối publish DDATA (đánh dấu Stale/Bad) và phát chuông/còi (nếu có buzzer) hoặc nhấp nháy đèn LED ERROR trên vỏ máy để yêu cầu kỹ sư cấu hình lại giờ thủ công qua UI.

#### 4. Khóa cổng vật lý (USB Storage Lockdown)
* **Vấn đề:** Để đảm bảo an ninh mạng OT (Edge Security), tuyệt đối không để kỹ sư/công nhân tại xưởng cắm USB cá nhân (có thể chứa malware) vào cổng USB của Gateway.
* **Yêu cầu bổ sung vào OS Hardening (Deploy Artifacts):**
    * Vô hiệu hóa module USB Mass Storage tại kernel Linux. Tạo file `/etc/modprobe.d/disable-usb-storage.conf` với nội dung: `install usb-storage /bin/true`.
    * Đảm bảo Gateway chỉ nhận USB cho thiết bị ngoại vi (như RS485-to-USB converter), loại bỏ hoàn toàn rủi ro truyền nhiễm malware vật lý.

### Nhóm II — Cấu hình & Trải nghiệm Người dùng (Config & UX)

#### 5. Cấu hình Tự phục hồi (Config Auto-Rollback / Safe Mode)
* **Vấn đề:** Ở G2, Business Server gửi `devices.json` mới xuống Gateway để Hot-Reload (Module 2A-2). File JSON đúng chuẩn cú pháp, NCalc compile OK, nhưng cấu hình sai baud-rate, sai Slave ID, dẫn đến việc 100% thiết bị trên Bus báo lỗi Timeout. Nếu không có ai ở Tầng 3 can thiệp, hệ thống "mù" hoàn toàn.
* **Yêu cầu bổ sung vào Module 2A-2 & 2A-4:**
    * Sau khi nhận cấu hình mới và kích hoạt `PollingEngine`, Gateway vào chế độ **"Grace Period"** (chạy thử trong 5 phút).
    * Nếu trong 5 phút này, tỷ lệ `Quality = Bad` của toàn bộ hệ thống tăng vọt lên trên 50% (do Modbus Timeout hoặc Error), Gateway tự động kết luận "Cấu hình thực địa không khả thi".
    * Tự động Rollback về file `devices.json.bak` trước đó, phát event báo cáo Tầng 3: `ConfigReloadFailed_HardwareIncompatible`.

#### 6. Sơ đồ Luồng Dữ liệu Cục bộ (Topology / Data Flow Visualizer)
* **Vấn đề:** UI hiện tại dùng bảng (Table) để quản lý Device và Data Monitor. Khi cấu trúc phức tạp: *Thiết bị A (Luồng Modbus) -> Tính toán NCalc (Virtual Tag B) -> Gửi Broker C*, một bảng dạng phẳng rất khó để kỹ sư dò lỗi nếu kết quả NCalc bị sai (do họ không hình dung được tag này lấy nguồn từ đâu).
* **Yêu cầu bổ sung vào UI/UX (Màn hình Mới hoặc Tab trong Device):**
    * Bổ sung một component **Node-Graph (Sơ đồ dạng mạng nhện/Flow)** chỉ để Read-Only (sử dụng thư viện như `React Flow`).
    * Trực quan hóa đường đi của dữ liệu: `[Icon Modbus Meter] --> [Tag: kW_total] --> (f(x) Rule Engine) --> [Virtual Tag: cosφ] --> [Sparkplug Encoder]`.
    * Kỹ sư click vào bất kỳ Node nào trên sơ đồ sẽ thấy giá trị realtime và trạng thái Quality hiện tại của Node đó. Điều này biến UI của Gateway trở thành một công cụ Debugger chuẩn công nghiệp (tương tự như chế độ xem của Kepware hoặc Ignition).

---

### Sửa đổi Phân loại & Quyết định (Bổ sung vào bảng Tóm tắt)

| # | Yêu cầu (v1.8) | Quyết định | Vị trí bổ sung |
|---|---|---|---|
| I.1 | Zero-Conf / mDNS Discovery | ✅ Bổ sung | OS Deploy Artifacts |
| I.2 | eMMC Wear/Smartctl Monitor | ✅ Bổ sung | Module 2A-9 |
| I.3 | RTC Hardware Clock Sync & Check | ✅ Bổ sung | Module 2A-9 |
| I.4 | USB Storage Lockdown | ✅ Bổ sung | OS Deploy Artifacts |
| II.1 | Config Auto-Rollback (Safe Mode) | ✅ Bổ sung | Module 2A-2, 2A-4 |
| II.2 | Topology / Data Flow Visualizer | ✅ Bổ sung | UI Component |