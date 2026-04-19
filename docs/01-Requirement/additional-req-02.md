Dưới góc nhìn của một chuyên gia từng vận hành hàng ngàn node edge devices thực tế, nơi mà điện nhà máy có thể sập bất cứ lúc nào và mạng OT thì nhiễu loạn, tôi xin đóng góp **6 yêu cầu bổ sung mang tính "sống còn" cuối cùng** để hệ thống đạt cảnh giới "Bullet-proof" (Chống đạn).

Dưới đây là phần bổ sung định dạng Markdown thô để anh dễ dàng chèn vào tài liệu:

---

## Các Yêu cầu Bổ sung Tối ưu hóa (Cập nhật cho v1.7)

### Nhóm I — Tối ưu Thu thập & An toàn Dữ liệu (Protocol & Storage)

#### 1. Thuật toán Gom cụm thanh ghi Modbus (Modbus Register Coalescing)
* **Vấn đề:** Nếu cấu hình đọc 10 thanh ghi Modbus nằm rải rác (ví dụ: `40001`, `40003`, `40010`), việc gửi 3 lệnh Modbus Read (FC03) riêng biệt sẽ bị overhead bởi thời gian trễ của bus (baud rate) và inter-frame gap. 
* **Yêu cầu bổ sung vào Module 2A-3 (Protocol Adapter):** Bắt buộc implement thuật toán **Register Coalescing**. 
    * Trước khi đưa vào lịch poll, hệ thống phải gom các thanh ghi có địa chỉ gần nhau (khoảng cách gap < N words, mặc định N = 10) thành **một lệnh đọc khối lượng lớn duy nhất** (Block Read).
    * Sau khi nhận byte array về, Gateway tự cắt (slice) ra thành các tag tương ứng. Điều này giúp tăng tốc độ poll lên gấp 3-5 lần, giảm thiểu nguy cơ collision trên đường truyền RS485.

#### 2. Chiến lược Tự phục hồi Database khi bị hỏng (SQLite Corruption Auto-Recovery)
* **Vấn đề:** Trong môi trường công nghiệp SME, mất điện đột ngột (Hard Power Loss) thường xuyên xảy ra trước khi UPS kịp can thiệp. Mặc dù SQLite chạy WAL mode trên `tmpfs` rất an toàn, nhưng rủi ro file `buffer.db` trên eMMC bị corrupt (hỏng block sector) vẫn hiện hữu. Nếu file `.db` hỏng, Module 2A-8 sẽ quăng exception liên tục và Gateway rơi vào trạng thái "Crash Loop" (khởi động lại vô tận).
* **Yêu cầu bổ sung vào Module 2A-8 (Local Buffer):** * Catch ngoại lệ `SqliteException` với mã lỗi `SQLITE_CORRUPT`.
    * Khi phát hiện corrupt: Ngay lập tức đổi tên file lỗi thành `buffer_corrupted_[timestamp].db`, xóa file `.db-wal` / `.db-shm` cũ, và **tạo mới một file SQLite trắng** để hệ thống tiếp tục hoạt động. 
    * Chấp nhận mất phần dữ liệu đệm cũ để bảo vệ tiến trình thu thập dữ liệu hiện tại, đồng thời bắn cảnh báo `AlertRaised` lên Dashboard và ghi Log CRITICAL.

### Nhóm II — Vận hành, Bảo mật & Triển khai (Ops, Security & Deployment)

#### 3. Quản lý vòng đời Chứng chỉ tự động (Automated mTLS Cert Lifecycle)
* **Vấn đề:** Kết nối Bridge giữa Mosquitto và EMQX OT sử dụng mTLS (`bridge_certfile`). Các chứng chỉ này thường có hạn (1 năm - 3 năm). Nếu quên gia hạn, Gateway sẽ đột ngột mất kết nối uplink (hàng loạt) vào một ngày đẹp trời. Quản lý thủ công qua OTA cho hàng trăm Gateway là cơn ác mộng.
* **Yêu cầu bổ sung vào Nhóm 4 (AdminService):** * Gateway phải tích hợp một agent (như `step-ca` client hoặc `certbot`) chạy ngầm.
    * Tự động renew chứng chỉ mTLS với Tầng 3 (Business Server) trước khi hết hạn 30 ngày qua giao thức ACME (hoặc API nội bộ).
    * Sau khi renew thành công, tự động trigger lệnh `systemctl reload mosquitto` để nhận chứng chỉ mới mà không làm rớt kết nối hiện tại.

#### 4. Kiến trúc Cập nhật Firmware Phân vùng kép (A/B Partition OTA)
* **Vấn đề:** Kịch bản "tải firmware về, dừng dịch vụ, copy đè, chạy lại" (in-place update) có rủi ro tạo ra "cục gạch" (brick) nếu mất điện đúng lúc đang copy file, hoặc version mới có lỗi logic làm crash process liên tục.
* **Yêu cầu bổ sung vào Module 2A-9 (OTA Update Client):** * Không dùng in-place update. Yêu cầu áp dụng kiến trúc **A/B Dual-Bank Partitioning** (có thể tích hợp RAUC hoặc Mender client).
    * Firmware mới được ghi vào phân vùng B. Gateway reboot sang phân vùng B để chạy thử.
    * **Health-Check Boot:** Nếu phân vùng B chạy và không kết nối được MQTT trong vòng 5 phút, Watchdog OS tự động rollback (đổi cờ bootloader) khởi động lại về phân vùng A (phiên bản cũ) để đảm bảo Gateway luôn sống.

### Nhóm III — Cải tiến UI/UX (Gateway Console)

#### 5. Trải nghiệm Thiết lập Ban đầu (Setup Wizard / OOBE)
* **Vấn đề:** Kỹ sư mang Gateway (mới bóc hộp) ra hiện trường lắp. Việc bắt họ phải vào từng tab System Config, MQTT Config, Network Config để cấu hình rất dễ thiếu sót bước.
* **Yêu cầu bổ sung (Màn hình mới):** Thêm màn hình **Out-Of-Box Experience (OOBE) Wizard**.
    * Khi truy cập Gateway lần đầu (hoặc sau khi Factory Reset), UI cưỡng chế hiển thị 4 bước: 
        1. Đổi mật khẩu Admin mặc định.
        2. Cấu hình IP tĩnh (LAN OT).
        3. Cấu hình IP/Port của EMQX OT Broker.
        4. Kiểm tra đồng bộ thời gian (NTP Sync).
    * Phải hoàn thành Wizard mới được truy cập Dashboard. Giúp chuẩn hóa quy trình triển khai tủ điện.

#### 6. Công cụ Chẩn đoán Mạng (Network Diagnostics Tool)
* **Vấn đề:** Màn hình Device Management báo Modbus TCP "Timeout". Kỹ sư không biết là do đứt cáp mạng, do sai IP, hay do PLC bị treo. Việc phải SSH vào OS Linux để gõ lệnh ping/telnet là rào cản với kỹ thuật viên bảo trì.
* **Yêu cầu bổ sung vào Tab "Maintenance":** Thêm sub-tab **Network Diagnostics**.
    * Cung cấp các nút UI để thực thi lệnh OS cơ bản: `Ping (ICMP)`, `Traceroute`, và `Telnet/TCP Ping` tới một IP/Port bất kỳ.
    * Kết quả console được stream trực tiếp lên UI qua SignalR. Kỹ sư có thể đứng ở tủ điện, dùng màn hình cảm ứng để Ping thẳng tới IP của Biến tần/PLC, xác định lỗi vật lý ngay lập tức.