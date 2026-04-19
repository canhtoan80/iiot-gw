### Tổng quan Dự án Hệ thống EMS On-premise (Phiên bản 2.1)
Dự án Hệ thống Quản lý Năng lượng (EMS) On-premise được thiết kế chuyên biệt cho các nhà máy vừa và nhỏ (SME) tại Việt Nam (quy mô 50–300 điểm đo). Dự án tập trung vào tính thực dụng, tối ưu chi phí và khả năng vận hành tinh gọn, áp dụng lộ trình triển khai 3 giai đoạn để mang lại hiệu quả cao nhất với nguồn lực IT giới hạn (1-2 người).

### Mục tiêu dự án
* **Tối ưu hóa chi phí điện năng:** Khắc phục tình trạng điện chiếm 15–35% giá thành sản xuất bằng cách tiết kiệm tổng hợp 15–25% chi phí (qua tối ưu TOU, cắt giảm đỉnh phụ tải, và giảm lãng phí vô hình). Thời gian hoàn vốn dự kiến nhanh, chỉ từ 18–30 tháng.
* **Hiển thị thời gian thực (Real-time Visibility):** Cung cấp khả năng giám sát sâu sát đến từng mức dây chuyền, khu vực thay vì chỉ dựa vào thông số hóa đơn tổng vào cuối tháng.
* **Hỗ trợ quản trị & Tuân thủ:** Xây dựng nền tảng dữ liệu minh bạch, tin cậy để phục vụ đánh giá KPI năng lượng theo ca kíp, hỗ trợ kế toán quản trị phân bổ giá thành, cũng như cung cấp báo cáo theo tiêu chuẩn ISO 50001, ESG và Scope 2.

### Những thành phần quan trọng
* **Kiến trúc 4 tầng chuẩn công nghiệp:** Phân tách rõ rệt gồm Tầng thiết bị trường (Field Layer), Tầng biên (Edge Layer/Mạng OT), Tầng nền tảng (Platform Layer/Mạng IT), và Tầng ứng dụng (Application Layer).
* **Hạ tầng Hai máy chủ (On-premise):** * **IIoT Server (Edge):** Đặt tại xưởng (mạng OT), chịu trách nhiệm thu thập dữ liệu, xử lý hệ số nhân CT/PT tại nguồn, tích hợp Watchdog phần cứng và bộ đệm (Local buffer 72h) chạy cơ chế Store-and-Forward để chống mất dữ liệu khi rớt mạng.
    * **Business Server (Platform):** Đặt tại mạng IT, lưu trữ kép bằng InfluxDB (cho dữ liệu chuỗi thời gian) và PostgreSQL (cho nghiệp vụ, cấu hình) trên ổ cứng SSD Enterprise (DWPD ≥ 1.0), kết hợp Analytics Core để xử lý các tác vụ phân tích nền.
* **Bảo mật & Luồng dữ liệu:** Dữ liệu được đẩy một chiều (One-way TLS push) qua tường lửa DMZ từ OT lên IT. Ở các giai đoạn đầu, hệ thống thiết lập cơ chế 100% Read-Only (không có Control Plane điều khiển ngược) để đảm bảo an toàn tuyệt đối.
* **Đồng bộ & Dự phòng cốt lõi:** Bắt buộc đồng bộ thời gian (NTP) khắt khe cho IIoT Server để đảm bảo tính chính xác của dữ liệu đối soát hóa đơn; áp dụng cơ chế thiết bị dự phòng nguội (Cold Standby) nhằm tối ưu chi phí và vận hành thay vì kiến trúc HA (High Availability) phức tạp.

### Những chức năng quan trọng
* **Quản lý TOU & Đối soát Shadow Bill:** Tính toán chi phí năng lượng thời gian thực theo 3 khung giờ biểu giá EVN và tự động đối soát với dữ liệu AMR của điện lực nhằm phát hiện sai lệch trước thời hạn thanh toán.
* **Giám sát Real-time & Sơ đồ SLD động:** Cập nhật thông số (kW, kWh, cosφ) liên tục mỗi 1-5 giây. Biểu diễn trạng thái mạng lưới trực quan ngay trên sơ đồ đơn tuyến (Single-Line Diagram).
* **Demand Response & Peak Shaving:** Giám sát, dự báo đỉnh phụ tải (Peak Demand) và ra cảnh báo sớm để vận hành viên chủ động cắt tải tĩnh (ở Giai đoạn 1 và 2), tiến tới tự động hóa (ở Giai đoạn 3) nhằm giảm chi phí Demand charge.
* **Phân tích Loss Balance & Load Profiling:** Vẽ biểu đồ tiêu thụ định kỳ (15 phút), phát hiện các điểm tiêu thụ điện ảo (phantom load) khi xưởng nghỉ và đối chiếu chênh lệch đồng hồ tổng-nhánh để tìm ra thất thoát vô hình trên đường dây.
* **Quản lý ca kíp động (Shift Scheduling) & Phân bổ chi phí:** Áp dụng dữ liệu điện năng vào lịch làm việc thực tế linh hoạt (ca 8h, ca 12h) để tính toán chính xác chỉ số hiệu quả (SEI, kWh/sản phẩm) và tự động phân bổ chi phí năng lượng cho từng dây chuyền.
* **Cảnh báo, Power Quality & CBM cơ bản:** Cảnh báo tức thì qua Zalo/Email về tình trạng quá tải hoặc hệ số cosφ thấp. Theo dõi chất lượng điện (THD%, mất cân bằng pha) và xu hướng tăng dòng điện bất thường so với mức cơ sở (baseline) để bộ phận bảo trì can thiệp sớm.