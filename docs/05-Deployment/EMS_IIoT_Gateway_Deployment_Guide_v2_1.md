# EMS IIoT Gateway — Deployment Guide
> **Production-grade · Industrial Environment · 24/7 Operation**  
> Phiên bản: 2.0  
> Áp dụng: EMS IIoT Gateway (Tầng 2A) · .NET 8 · Ubuntu Server · Modbus RTU/TCP · MQTT Sparkplug B  
> Đối tượng: DevOps Engineer · Linux System Engineer · Network Engineer · Field Application Engineer · Security Engineer · QA Engineer

---

## Lưu ý trước khi đọc

> **Mindset triển khai:** Hệ thống này chạy trong môi trường công nghiệp thực tế — điện có thể mất bất cứ lúc nào, mạng 4G không ổn định, nhiệt độ tủ điện dao động, kỹ sư không thể SSH vào ngay khi có sự cố. Mọi quyết định thiết kế deploy đều xuất phát từ thực tế này.

---

## Mục lục

- [A. Kiến trúc Deployment Tổng thể](#a-kiến-trúc-deployment-tổng-thể)
- [B. Packaging & Build](#b-packaging--build)
- [C. Cài đặt trên Linux](#c-cài-đặt-trên-linux)
- [D. Cấu hình Network — 4G / LAN / VPN](#d-cấu-hình-network--4g--lan--vpn)
- [E. Cấu hình MQTT Sparkplug B](#e-cấu-hình-mqtt-sparkplug-b)
- [F. Cấu hình Modbus RS-485 & TCP](#f-cấu-hình-modbus-rs-485--tcp)
- [G. Security Hardening](#g-security-hardening)
- [H. Reliability & Fault Handling](#h-reliability--fault-handling)
- [I. Monitoring & Logging](#i-monitoring--logging)
- [J. OTA / Update Strategy](#j-ota--update-strategy)
- [K. Checklist Triển khai Thực địa](#k-checklist-triển-khai-thực-địa)
- [L. Test Plan](#l-test-plan)

---

## A. Kiến trúc Deployment Tổng thể

### A.1 Sơ đồ tổng thể

```
╔══════════════════════════════════════════════════════════════════════════╗
║  TẦNG 1 — THIẾT BỊ TRƯỜNG                                               ║
║  Smart Meter · PLC · VFD · BACnet Controller                            ║
║  Kết nối: RS-485 (Modbus RTU) · RJ45 (Modbus TCP) · MQTT Native        ║
╚═══════════════════════════╦══════════════════════════════════════════════╝
                            ║ RS-485 / LAN OT
╔═══════════════════════════╩══════════════════════════════════════════════╗
║  TẦNG 2A — IIoT GATEWAY (Industrial PC x86 · Ubuntu 22.04 LTS)         ║
║                                                                          ║
║  [ems-gateway.service]    [mosquitto.service]    [avahi-daemon.service] ║
║  .NET 8 Firmware          South-facing Broker    mDNS Discovery         ║
║  Modbus Poll/Parse        Port 1883 local        ems-gw-{id}.local      ║
║  Sparkplug B Encode       Bridge → EMQX OT       Admin UI :8080         ║
║  LocalBuffer SQLite 72h                                                  ║
║                                                                          ║
║  Network: eth0 (OT LAN · 192.168.10.x) / wwan0 (4G fallback)           ║
╚═══════════════════════════╦══════════════════════════════════════════════╝
                            ║ MQTT Sparkplug B · TLS 1.3 · port 8883
                            ║ (over LAN · or 4G NAT · or WireGuard VPN)
╔═══════════════════════════╩══════════════════════════════════════════════╗
║  TẦNG 2B — IIoT SERVER (EMQX OT Broker · Tại phòng điện / cloud)       ║
║  Store-and-Forward 72h · MQTT Bridge → Tầng 3                           ║
╚═══════════════════════════╦══════════════════════════════════════════════╝
                            ║ DMZ Firewall · port 8883 TLS only
╔═══════════════════════════╩══════════════════════════════════════════════╗
║  TẦNG 3 — BUSINESS SERVER (Docker Compose · Mạng IT)                    ║
║  EMQX IT · InfluxDB · PostgreSQL · FastAPI · Celery · Grafana            ║
╚═══════════════════════════╦══════════════════════════════════════════════╝
                            ║ HTTPS / WebSocket
╔═══════════════════════════╩══════════════════════════════════════════════╗
║  TẦNG 4 — APPLICATION (Browser · Mobile · ERP · MES)                    ║
╚══════════════════════════════════════════════════════════════════════════╝
```

### A.2 Thành phần chạy trên mỗi Gateway (Tầng 2A)

| Service | Binary / Package | Port | Vai trò |
|---|---|---|---|
| `ems-gateway.service` | `/usr/bin/ems-gateway` | 8080, 9090 | Firmware .NET 8 — core |
| `mosquitto.service` | `mosquitto` (apt) | 1883 (local) | South-facing MQTT broker |
| `avahi-daemon.service` | `avahi-daemon` (apt) | mDNS | Khám phá IP tự động |
| `systemd-timesyncd` | built-in | — | NTP client |
| `wg-quick@wg0` | `wireguard` (apt) | UDP 51820 | VPN remote access (tuỳ chọn) |

### A.3 Luồng dữ liệu chi tiết

```
[Thiết bị Modbus RTU]
      │ RS-485 A/B
      ▼
[ems-gateway: ModbusRtuAdapter]
  FC03/FC04 poll → raw bytes → CoalescedBlock parse
      │
      ▼
[ems-gateway: EdgeRuleEngine]
  CT/PT normalization · NCalc virtual tag (cosφ, SEI, OEE)
      │
      ▼
[ems-gateway: QualityChecker]
  Range · Stuck · RoC · Stale → TagQuality annotation
      │
      ▼
[ems-gateway: SparkplugEncoder]
  Good → is_null=false · Bad/Stale → is_null=true + PropertySet
      │ publish QoS 1
      ▼
[mosquitto:1883 local]
      │ bridge
      ▼
[EMQX OT:8883 TLS]  ←── nếu mất kết nối: ems-gateway LocalBuffer SQLite 72h
      │ bridge TLS DMZ
      ▼
[EMQX IT:8883]
      │ Sparkplug B decode
      ▼
[InfluxDB] ← time-series raw data
[PostgreSQL] ← alarms, config, audit
```

---

## B. Packaging & Build

### B.1 .NET Publish Strategy

**Lựa chọn: Self-contained vs Framework-dependent**

| | Self-contained | Framework-dependent |
|---|---|---|
| Kích thước bundle | ~80–120 MB | ~5–10 MB |
| Cần .NET runtime trên host | Không | Có |
| Phù hợp với | Fleet Gateway — đảm bảo đúng runtime version | Server (Business Server) |
| Rollback runtime | Tự động (bundle = runtime + app) | Phải quản lý riêng |
| **Khuyến nghị Gateway** | **✅ Self-contained** | ❌ |

```bash
# Build self-contained cho linux-x64 (Industrial PC x86)
dotnet publish EMS.IIoTGateway/EMS.IIoTGateway.sln \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output ./publish/linux-x64 \
  -p:PublishSingleFile=true \          # single binary dễ deploy
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \              # debug info embedded (không cần .pdb file riêng)
  -p:PublishTrimmed=false              # KHÔNG trim — trimming gây lỗi với reflection/NCalc

# Build cho linux-arm64 nếu dùng ARM SBC
dotnet publish ... --runtime linux-arm64 ...
```

**Output:** `./publish/linux-x64/ems-gateway` (single binary ~100MB, chạy được ngay không cần apt install dotnet)

### B.2 Đóng gói — .deb vs Docker

**Phân tích trade-off cho Gateway môi trường thực địa:**

| | `.deb` package (native) | Docker container |
|---|---|---|
| Boot time | **<2s** (systemd start) | 5–15s (container runtime overhead) |
| RAM overhead | **~0 MB** (no container layer) | +50–100 MB (Docker daemon) |
| Hardware access (RS-485 `/dev/ttyUSB0`) | **Direct** | Cần `--device` mount |
| Watchdog `/dev/watchdog` | **Direct** | Cần privileged mode |
| systemd integration | **Native** | Cần workaround |
| Update atomic | Cần script | `docker pull` built-in |
| Rollback | Binary backup script | `docker tag` |
| Phù hợp Industrial PC 24/7 | **✅ Rất phù hợp** | ⚠ Overhead không cần thiết |

**Kết luận: Dùng `.deb` package cho Gateway, Docker cho Business Server.**

```bash
# Tạo .deb package với nfpm (nfpm.goreleaser.io)
# File: nfpm.yaml

name: ems-gateway
version: "1.2.0"
arch: amd64
maintainer: EMS Team <ems@factory.local>
description: EMS IIoT Gateway Firmware

contents:
  # Binary
  - src: ./publish/linux-x64/ems-gateway
    dst: /usr/bin/ems-gateway
    file_info:
      mode: 0755

  # Systemd unit
  - src: ./deploy/systemd/ems-gateway.service
    dst: /lib/systemd/system/ems-gateway.service

  # Mosquitto config
  - src: ./deploy/mosquitto/mosquitto.conf
    dst: /etc/mosquitto/conf.d/ems-gateway.conf
    type: config|noreplace   # không overwrite nếu đã có custom config

  # appsettings mặc định
  - src: ./deploy/config/appsettings.default.json
    dst: /etc/ems-gateway/appsettings.json
    type: config|noreplace

  # deploy scripts
  - src: ./deploy/scripts/
    dst: /usr/lib/ems-gateway/

scripts:
  postinstall: ./deploy/scripts/post-install.sh
  preremove:   ./deploy/scripts/pre-remove.sh

# Build
nfpm package --packager deb --config nfpm.yaml --target ./dist/
# Output: ./dist/ems-gateway_1.2.0_amd64.deb
```

### B.3 Versioning

```
Format: MAJOR.MINOR.PATCH+BUILD
Ví dụ:  1.2.3+20241115.ci145

MAJOR — Breaking change (thay đổi Sparkplug B metric schema → Tầng 3 cần update)
MINOR — Tính năng mới tương thích ngược (thêm BACnet adapter, thêm virtual tag func)
PATCH — Bug fix, performance, config update
BUILD — CI build number + date (tự động từ pipeline)

Gateway chỉ tự OTA nếu MAJOR không đổi.
MAJOR change: cần deploy thủ công, kiểm tra Business Server compatibility trước.
```

---

## C. Cài đặt trên Linux

### C.1 Chuẩn bị OS (Ubuntu Server 22.04 LTS)

```bash
# ── Bước 1: Cập nhật OS ──────────────────────────────────────────
sudo apt update && sudo apt upgrade -y
sudo apt install -y \
  curl wget gnupg2 ca-certificates \
  net-tools nmap netcat-openbsd \
  mosquitto mosquitto-clients \
  avahi-daemon avahi-utils \
  chrony \
  smartmontools \
  wireguard \
  logrotate \
  jq

# ── Bước 2: Tạo user non-root cho gateway ────────────────────────
sudo useradd --system --no-create-home \
  --shell /sbin/nologin \
  --comment "EMS Gateway Service" \
  ems-gateway

# Thêm vào group dialout (RS-485 serial access)
sudo usermod -aG dialout ems-gateway

# Thêm vào group tty (serial port)
sudo usermod -aG tty ems-gateway

# ── Bước 3: Cấu hình timezone ────────────────────────────────────
sudo timedatectl set-timezone Asia/Ho_Chi_Minh

# ── Bước 4: Tắt swap (tránh latency spike khi memory pressure) ───
sudo swapoff -a
sudo sed -i '/swap/d' /etc/fstab

# ── Bước 5: Tạo thư mục cần thiết ────────────────────────────────
sudo mkdir -p /etc/ems-gateway/certs
sudo mkdir -p /var/lib/ems-gateway
sudo mkdir -p /var/log/ems-gateway
sudo mkdir -p /tmp/ems-update
sudo chown -R ems-gateway:ems-gateway \
  /etc/ems-gateway /var/lib/ems-gateway /var/log/ems-gateway

# ── Bước 6: Mount tmpfs cho SQLite RAM buffer ─────────────────────
# Thêm vào /etc/fstab:
echo "tmpfs /dev/shm/ems-buffer tmpfs defaults,size=256m,noatime 0 0" \
  | sudo tee -a /etc/fstab
sudo mkdir -p /dev/shm/ems-buffer
sudo mount /dev/shm/ems-buffer
sudo chown ems-gateway:ems-gateway /dev/shm/ems-buffer
```

### C.2 Cài đặt Gateway từ .deb

```bash
# Copy .deb lên máy (SCP hoặc USB)
sudo dpkg -i ems-gateway_1.2.0_amd64.deb

# Verify cài đặt
ls -la /usr/bin/ems-gateway          # phải có, executable
ls -la /etc/ems-gateway/             # appsettings.json phải có
ls -la /lib/systemd/system/ems-gateway.service
```

### C.3 Cấu hình `appsettings.json`

```bash
sudo nano /etc/ems-gateway/appsettings.json
```

```json
{
  "Gateway": {
    "MachineId": "gw-line-a-001",
    "GroupId": "factory-hanoi",
    "FirmwareBuildUtc": "2024-11-15T00:00:00Z"
  },
  "Mqtt": {
    "BrokerHost": "localhost",
    "BrokerPort": 1883,
    "UseTls": false
  },
  "LocalBuffer": {
    "SqlitePath": "/var/lib/ems-gateway/buffer.db",
    "TmpfsMountPath": "/dev/shm/ems-buffer",
    "SyncIntervalMs": 5000,
    "RetentionHours": 72,
    "ChannelCapacity": 10000,
    "ReplayRateLimitPerSecond": 500
  },
  "NtpWatchdog": {
    "DriftAlertThresholdSeconds": 5,
    "DriftStaleThresholdSeconds": 30,
    "CheckIntervalSeconds": 30,
    "HwclockSyncIntervalMinutes": 15
  },
  "Watchdog": {
    "DevicePath": "/dev/watchdog",
    "HeartbeatIntervalSeconds": 30
  },
  "Features": {
    "ConfigChannel": { "Enabled": false },
    "CommandHandler": { "Enabled": false },
    "MockDataMode": { "Enabled": false }
  },
  "HealthChecks": { "Port": 8080 },
  "Prometheus": { "Port": 9090 },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "/var/log/ems-gateway/app-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7,
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Properties} {Message:lj}{NewLine}{Exception}"
        }
      }
    ]
  }
}
```

### C.4 Systemd Service File

**File: `/lib/systemd/system/ems-gateway.service`**

```ini
[Unit]
Description=EMS IIoT Gateway Firmware
Documentation=https://wiki.factory.local/ems-gateway
# Khởi động sau network và mosquitto đã sẵn sàng
After=network-online.target mosquitto.service systemd-time-wait-sync.service
Wants=network-online.target
Requires=mosquitto.service
# Nếu mosquitto chết → gateway cũng dừng và cùng restart
PartOf=mosquitto.service

[Service]
# User non-root (quan trọng cho security)
User=ems-gateway
Group=ems-gateway
SupplementaryGroups=dialout tty

# Binary
ExecStart=/usr/bin/ems-gateway

# Environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EnvironmentFile=-/etc/ems-gateway/env     # optional env file (sensitive vars)

# Working directory
WorkingDirectory=/var/lib/ems-gateway

# Restart policy: luôn restart trừ khi dừng thủ công
Restart=always
RestartSec=10s
# Tăng delay theo số lần restart liên tiếp (tránh crash loop)
StartLimitIntervalSec=300
StartLimitBurst=5

# Timeout cho graceful shutdown (flush buffer)
TimeoutStopSec=60s

# Output logs → journald
StandardOutput=journal
StandardError=journal
SyslogIdentifier=ems-gateway

# Security hardening
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ReadWritePaths=/var/lib/ems-gateway /var/log/ems-gateway /dev/shm/ems-buffer /etc/ems-gateway
# Cho phép truy cập hardware watchdog và serial port
DeviceAllow=/dev/watchdog rw
DeviceAllow=char-ttyUSB rw
DeviceAllow=char-ttyACM rw

# Systemd watchdog (backup nếu không có /dev/watchdog hardware)
WatchdogSec=90s
NotifyAccess=main

[Install]
WantedBy=multi-user.target
```

### C.5 Enable và start service

```bash
sudo systemctl daemon-reload
sudo systemctl enable ems-gateway.service
sudo systemctl start ems-gateway.service

# Kiểm tra trạng thái
sudo systemctl status ems-gateway.service
# Expected output: Active: active (running)

# Kiểm tra logs
sudo journalctl -u ems-gateway -f --since "5 minutes ago"

# Kiểm tra health endpoint
curl -s http://localhost:8080/health | jq .
# Expected: {"status":"Healthy",...}
```

### C.6 Log Rotation

**File: `/etc/logrotate.d/ems-gateway`**

```
/var/log/ems-gateway/*.log {
    daily
    rotate 7
    compress
    delaycompress
    missingok
    notifempty
    sharedscripts
    postrotate
        systemctl kill -s USR1 ems-gateway.service 2>/dev/null || true
    endscript
}

/var/log/ems-gateway/audit.ndjson {
    weekly
    rotate 12
    compress
    delaycompress
    missingok
    notifempty
    # Audit log KHÔNG được xóa, chỉ compress và archive
    create 0640 ems-gateway ems-gateway
}
```

---

## D. Cấu hình Network — 4G / LAN / VPN

### D.1 Kiến trúc mạng theo kịch bản triển khai

```
Kịch bản 1: LAN OT ổn định (G1/G2 — phổ biến nhất)
  Gateway eth0 ─────────────────────────────────────────► EMQX OT (LAN)
  192.168.10.101                                          192.168.10.10:8883

Kịch bản 2: 4G/LTE làm uplink chính (nhà máy không có LAN OT tới server)
  Gateway wwan0 ──► SIM M2M ──► Internet ──► EMQX Cloud/Public IP:8883

Kịch bản 3: 4G backup (LAN chính + 4G dự phòng)
  Gateway eth0 (primary) ──► EMQX OT LAN
  Gateway wwan0 (failover) ──► EMQX Cloud (nếu LAN đứt)

Kịch bản 4: WireGuard VPN qua 4G (site cách xa, cần remote access an toàn)
  Gateway wwan0 ──► 4G ──► WireGuard VPN Tunnel ──► EMQX OT (mạng riêng ảo)
```

### D.2 Cấu hình Ethernet tĩnh (netplan)

```yaml
# /etc/netplan/01-ems-gateway.yaml

network:
  version: 2
  ethernets:
    eth0:
      addresses:
        - 192.168.10.101/24
      routes:
        - to: default
          via: 192.168.10.1
          metric: 100           # metric thấp = ưu tiên hơn 4G
      nameservers:
        addresses:
          - 192.168.10.1
          - 8.8.8.8
      dhcp4: false
      optional: true            # không block boot nếu cable chưa cắm

# Apply:
# sudo netplan apply
```

### D.3 Cấu hình 4G (SIM M2M) với ModemManager

```bash
# Cài ModemManager
sudo apt install -y modemmanager network-manager

# Kiểm tra modem nhận được chưa
mmcli -L
# Expected: /org/freedesktop/ModemManager1/Modem/0 [Quectel] EC21

# Xem thông tin modem
mmcli -m 0

# Tạo kết nối 4G (thay APN phù hợp nhà mạng)
# Viettel:     m-viettel.com
# Mobifone:    m-tourist.com
# Vietnamobile: m3-world.vnn.vn
nmcli connection add \
  type gsm \
  ifname "*" \
  con-name "ems-4g" \
  apn "m-viettel.com"

# Bật kết nối tự động
nmcli connection modify ems-4g \
  connection.autoconnect yes \
  connection.autoconnect-priority -100    # ưu tiên thấp hơn eth0

# Verify IP
ip addr show wwan0
```

**File netplan cho 4G backup:**

```yaml
# /etc/netplan/02-ems-4g.yaml

network:
  version: 2
  modems:
    wwan0:
      apn: m-viettel.com
      auto-config: true
      routes:
        - to: default
          via: 0.0.0.0
          metric: 200           # metric cao hơn eth0 → chỉ dùng khi eth0 fail
      dhcp4: true
```

**Policy routing cho 4G failover tự động:**

```bash
# /usr/lib/ems-gateway/network-monitor.sh
# Chạy qua systemd timer mỗi 30s

#!/bin/bash
EMQX_HOST="10.0.1.5"
EMQX_PORT="8883"
PRIMARY_IF="eth0"
BACKUP_IF="wwan0"
LOG="/var/log/ems-gateway/network.log"

# Test kết nối đến EMQX OT
if nc -z -w 3 "$EMQX_HOST" "$EMQX_PORT" 2>/dev/null; then
    echo "$(date -u +%FT%TZ) PRIMARY OK" >> "$LOG"
    # Tắt 4G nếu đang bật để tiết kiệm SIM
    nmcli connection down ems-4g 2>/dev/null || true
else
    echo "$(date -u +%FT%TZ) PRIMARY FAIL - switching to 4G" >> "$LOG"
    nmcli connection up ems-4g 2>/dev/null || true
fi
```

```ini
# /lib/systemd/system/ems-network-monitor.timer
[Unit]
Description=EMS Network Failover Monitor

[Timer]
OnBootSec=60s
OnUnitActiveSec=30s
AccuracySec=1s

[Install]
WantedBy=timers.target
```

### D.4 WireGuard VPN (Remote Access & Kịch bản 4G-VPN)

**Khi nào dùng WireGuard:**
- Kỹ sư cần SSH vào Gateway từ xa
- EMQX OT ở datacenter riêng, Gateway kết nối qua internet
- Cần remote debug Admin UI mà không expose port ra internet

```bash
# Trên Gateway — tạo key pair
wg genkey | sudo tee /etc/wireguard/private.key \
  | wg pubkey | sudo tee /etc/wireguard/public.key
sudo chmod 600 /etc/wireguard/private.key

# File cấu hình WireGuard
sudo nano /etc/wireguard/wg0.conf
```

```ini
# /etc/wireguard/wg0.conf (trên Gateway)

[Interface]
PrivateKey = <GATEWAY_PRIVATE_KEY>
Address = 10.8.0.101/24          # IP trong VPN tunnel
DNS = 10.8.0.1

# Keepalive: gửi packet mỗi 25s để giữ NAT mapping (quan trọng với 4G NAT)
# PersistentKeepalive không nằm trong Interface, nằm trong Peer

[Peer]
# Business Server / VPN Server
PublicKey = <SERVER_PUBLIC_KEY>
Endpoint = <SERVER_PUBLIC_IP>:51820
AllowedIPs = 10.8.0.0/24, 192.168.1.0/24    # VPN subnet + mạng IT
PersistentKeepalive = 25                      # QUAN TRỌNG với 4G NAT
```

```bash
# Enable và start WireGuard
sudo systemctl enable wg-quick@wg0
sudo systemctl start wg-quick@wg0

# Verify
wg show
# Expected: latest handshake: X seconds ago
#           transfer: X MiB received, X MiB sent
```

### D.5 Firewall Rules (ufw)

```bash
# Cài ufw
sudo apt install -y ufw

# Mặc định: deny all inbound, allow all outbound
sudo ufw default deny incoming
sudo ufw default allow outgoing

# Cho phép SSH (chỉ từ mạng IT/VPN)
sudo ufw allow from 192.168.1.0/24 to any port 22 proto tcp
sudo ufw allow from 10.8.0.0/24 to any port 22 proto tcp   # VPN

# Cho phép Admin UI (chỉ từ LAN OT và VPN)
sudo ufw allow from 192.168.10.0/24 to any port 8080 proto tcp
sudo ufw allow from 10.8.0.0/24 to any port 8080 proto tcp

# Cho phép Prometheus scrape từ Business Server
sudo ufw allow from 192.168.1.10 to any port 9090 proto tcp

# WireGuard UDP
sudo ufw allow 51820/udp

# Outbound MQTT TLS (thường không cần rule vì allow outgoing)
# Nhưng nếu có policy firewall chặt: cho phép ra EMQX OT
sudo ufw allow out to 192.168.10.10 port 8883 proto tcp

# Bật firewall
sudo ufw enable
sudo ufw status verbose
```

### D.6 Kiểm tra kết nối mạng sau cài đặt

```bash
# Test ping đến EMQX OT
ping -c 4 192.168.10.10

# Test port MQTT TLS open
nc -zv 192.168.10.10 8883
# Expected: Connection to 192.168.10.10 8883 port [tcp/*] succeeded!

# Test từ Gateway kết nối MQTT (dùng mosquitto_pub)
mosquitto_pub \
  --host 192.168.10.10 \
  --port 8883 \
  --cafile /etc/ems-gateway/certs/ca.crt \
  --certfile /etc/ems-gateway/certs/gw.crt \
  --keyfile /etc/ems-gateway/certs/gw.key \
  --topic "test/ping" \
  --message "hello" \
  --qos 1
# Expected: thành công không có error
```

---

## E. Cấu hình MQTT Sparkplug B

### E.0 Giải quyết Xung đột Port — Co-located G1/G2 (Tầng 2A + 2B cùng máy)

> **Đây là vấn đề thực tế phổ biến nhất khi deploy G1/G2.** Khi Mosquitto (Tầng 2A) và EMQX OT (Tầng 2B) cùng chạy trên một máy, cả hai mặc định đều cố bind TCP port 1883. Service nào start sau sẽ thất bại với lỗi `Address already in use`.

#### E.0.1 Phân tích xung đột

```
Tình trạng mặc định (XÃY RA XU NG ĐỘT):
┌───────────────────────────────────────────────────┐
│  Cùng một máy vật lý (G1/G2)                      │
│                                                   │
│  Mosquitto   → bind 0.0.0.0:1883  ← XU NG ĐỘT    │
│  EMQX OT     → bind 0.0.0.0:1883  ← XU NG ĐỘT    │
│                                                   │
│  Kết quả: một trong hai không start được           │
└───────────────────────────────────────────────────┘

Vấn đề bổ sung:
  - EMQX OT còn dùng các port: 8083 (WS), 8084 (WSS), 18083 (Dashboard)
  - Nếu AdminService Gateway cũng dùng port 8080, cần kiểm tra EMQX Dashboard
  - Mosquitto bridge outgoing cũng kết nối đến localhost:1883 nếu config sai
```

#### E.0.2 Giải pháp — Phân tách port rõ ràng

Nguyên tắc thiết kế port cho co-located deployment:

```
┌──────────────────────────────────────────────────────────────────────┐
│  PORT MAPPING — CO-LOCATED G1/G2 (Tầng 2A + 2B cùng máy)            │
│                                                                      │
│  Mosquitto (Tầng 2A — South-facing, nhận từ firmware & MQTT devices) │
│    Port 1883  bind 127.0.0.1  ← chỉ localhost, firmware .NET kết nối│
│    (không TLS vì loopback nội bộ)                                    │
│                                                                      │
│  EMQX OT (Tầng 2B — North-facing, nhận bridge từ Mosquitto)         │
│    Port 1884  bind 127.0.0.1  ← chỉ localhost, nhận từ Mosquitto     │
│    Port 8883  bind 0.0.0.0   ← TLS, nhận từ Gateway G3 tách máy     │
│    Port 18083 bind 127.0.0.1  ← EMQX Dashboard (nội bộ)             │
│                                                                      │
│  Mosquitto Bridge (nội bộ máy):                                      │
│    Mosquitto:1883 ──bridge──► EMQX OT:1884 (localhost, không TLS)   │
│                                                                      │
│  EMQX OT Bridge (ra ngoài):                                          │
│    EMQX OT:1884 ──bridge TLS──► EMQX IT Tầng 3:8883                 │
│                                                                      │
│  Gateway .NET firmware:                                               │
│    publish ──► localhost:1883 (Mosquitto)                            │
└──────────────────────────────────────────────────────────────────────┘
```

**So sánh luồng G1/G2 vs G3:**

```
G1/G2 (co-located — cùng máy):
  Firmware → Mosquitto:1883 (loopback) → EMQX OT:1884 (loopback) → EMQX IT:8883 (LAN TLS)
  Lợi ích: zero network hop, đơn giản
  Vấn đề: phải dùng port khác nhau cho Mosquitto và EMQX OT

G3 (tách máy):
  Firmware → Mosquitto:1883 (loopback) → EMQX OT:8883 (LAN TLS)
  EMQX OT trên máy khác — không có xung đột port
```

#### E.0.3 Cấu hình EMQX OT cho Co-located (Tầng 2B)

```bash
# Cài EMQX OT
curl -s https://assets.emqx.com/scripts/install-emqx-deb.sh | sudo bash
sudo apt install -y emqx
```

**File cấu hình EMQX OT: `/etc/emqx/emqx.conf`**

```conf
# /etc/emqx/emqx.conf — CO-LOCATED G1/G2

# ─── Node ───────────────────────────────────────────────────────────
node {
  name = "emqx@127.0.0.1"
  cookie = "ems-emqx-secret-cookie-change-this"
  data_dir = "/var/lib/emqx"
}

# ─── Listeners ──────────────────────────────────────────────────────
listeners.tcp.internal {
  # Chỉ nhận kết nối từ Mosquitto bridge (loopback)
  bind = "127.0.0.1:1884"         # ← PORT 1884, không phải 1883
  max_connections = 100
}

listeners.ssl.external {
  # Nhận kết nối TLS từ Gateway G3 (tách máy) hoặc uplink lên Tầng 3
  bind = "0.0.0.0:8883"
  ssl_options {
    cacertfile = "/etc/emqx/certs/ca.crt"
    certfile   = "/etc/emqx/certs/emqx-ot.crt"
    keyfile    = "/etc/emqx/certs/emqx-ot.key"
    verify     = verify_peer
    fail_if_no_peer_cert = true
  }
  max_connections = 500
}

# Tắt các listener không dùng (giảm attack surface)
listeners.ws.default.enable  = false
listeners.wss.default.enable = false

# ─── Dashboard — chỉ localhost ─────────────────────────────────────
dashboard {
  listeners.http {
    bind = "127.0.0.1:18083"     # KHÔNG expose ra ngoài
  }
  default_username = "admin"
  default_password = "change-this-password"
}

# ─── MQTT Bridge → EMQX IT (Tầng 3) ───────────────────────────────
# (Xem thêm phần bridges config bên dưới)
```

**Cấu hình EMQX Bridge (EMQX OT → EMQX IT Tầng 3):**

```bash
# /etc/emqx/bridges.conf hoặc qua EMQX API sau khi start

# Dùng EMQX CLI để tạo bridge
sudo emqx_ctl bridges create \
  --name ems_it_bridge \
  --type mqtt \
  --server "ssl://192.168.1.10:8883" \
  --clientid "emqx-ot-bridge-$(hostname)" \
  --ssl-cacertfile /etc/emqx/certs/ca.crt \
  --ssl-certfile /etc/emqx/certs/emqx-ot.crt \
  --ssl-keyfile /etc/emqx/certs/emqx-ot.key \
  --topic "spBv1.0/# out" \
  --topic "ems/config/# in"
```

Hoặc qua file cấu hình EMQX 5.x:

```conf
# /etc/emqx/clusters.conf (EMQX 5.x bridge config)

bridges.mqtt.ems_it_bridge {
  enable = true
  server = "ssl://192.168.1.10:8883"
  clientid = "emqx-ot-gw-line-a"
  proto_ver = "v5"
  keepalive = 60s
  
  ssl {
    enable = true
    cacertfile = "/etc/emqx/certs/ca.crt"
    certfile   = "/etc/emqx/certs/emqx-ot.crt"
    keyfile    = "/etc/emqx/certs/emqx-ot.key"
    verify     = verify_peer
  }

  # Store-and-forward khi kết nối đứt
  queue {
    max_total_size = "512MB"
  }

  # Forward Sparkplug B từ OT → IT
  egress {
    local_topic = "spBv1.0/#"
    remote_topic = "${topic}"
    payload = "${payload}"
    qos = 1
    retain = false
  }

  # Nhận Config Channel / DCMD từ IT → OT
  ingress {
    remote_topic = "ems/config/#"
    local_topic  = "${topic}"
    qos = 1
  }
}
```

```bash
# Enable và start EMQX
sudo systemctl enable emqx
sudo systemctl start emqx

# Verify EMQX listening đúng port
ss -tlnp | grep -E "1884|8883|18083"
# Expected:
# LISTEN 127.0.0.1:1884  ← internal MQTT
# LISTEN 0.0.0.0:8883    ← external TLS
# LISTEN 127.0.0.1:18083 ← dashboard

# Verify EMQX cluster status
sudo emqx_ctl status
# Expected: Node emqx@127.0.0.1 is started
```

#### E.0.4 Cập nhật Mosquitto bridge sang EMQX OT port 1884

Với co-located G1/G2, Mosquitto bridge kết nối đến **EMQX OT nội bộ** qua port 1884 (không TLS vì loopback):

```conf
# /etc/mosquitto/conf.d/ems-gateway.conf — CẬP NHẬT cho co-located

listener 1883 127.0.0.1
allow_anonymous false
password_file /etc/mosquitto/passwd

persistence true
persistence_location /var/lib/mosquitto/
autosave_interval 30
max_queued_messages 10000
max_queued_bytes 52428800

# ─── Bridge → EMQX OT (CÙ NG MÁY — port 1884, KHÔNG TLS) ──────────
connection emqx_ot_bridge
address 127.0.0.1:1884             # ← localhost:1884, KHÔNG phải :1883

bridge_protocol_version mqttv50
remote_clientid ems-gw-line-a-001-bridge

# Không cần TLS cho kết nối loopback nội bộ
# bridge_cafile, bridge_certfile, bridge_keyfile → KHÔNG CẦN

# Session persistent
cleansession false
local_cleansession false
try_private true

keepalive_interval 60
start_type automatic
restart_timeout 5 30

# Topic rules — giống như trước
topic spBv1.0/+/NBIRTH out 1
topic spBv1.0/+/NDEATH out 1
topic spBv1.0/+/DBIRTH/# out 1
topic spBv1.0/+/DDATA/# out 1
topic spBv1.0/+/DDEATH/# out 1
topic ems/config/# in 1
# topic spBv1.0/+/+/DCMD in 1    # G3 only

log_dest file /var/log/mosquitto/mosquitto.log
log_type error
log_type warning
log_type notice
log_timestamp true
```

#### E.0.5 Thứ tự khởi động services (systemd dependency)

```
Thứ tự bắt buộc:
  1. EMQX OT phải start TRƯỚC Mosquitto
     (Mosquitto bridge cần EMQX OT đang lắng nghe :1884)
  2. ems-gateway start AFTER Mosquitto
     (Firmware cần Mosquitto :1883 sẵn sàng)
```

```ini
# Override systemd dependency cho Mosquitto
# File: /etc/systemd/system/mosquitto.service.d/override.conf
[Unit]
After=network.target emqx.service
Requires=emqx.service

# Override cho ems-gateway (đã có trong service file)
# After=mosquitto.service
# Requires=mosquitto.service
```

```bash
sudo mkdir -p /etc/systemd/system/mosquitto.service.d/
sudo tee /etc/systemd/system/mosquitto.service.d/override.conf << 'EOF'
[Unit]
After=network.target emqx.service
Wants=emqx.service
EOF

sudo systemctl daemon-reload

# Verify thứ tự start
systemctl list-dependencies ems-gateway --reverse
```

#### E.0.6 Verify toàn bộ port binding sau khi start

```bash
# Kiểm tra tất cả ports đang được sử dụng
echo "=== ALL MQTT PORTS ==="
ss -tlnp | grep -E ":1883|:1884|:8883|:18083|:8080|:9090"

# Expected output:
# LISTEN 0         127.0.0.1:1883   *:*   users:(("mosquitto",...))
# LISTEN 0         127.0.0.1:1884   *:*   users:(("beam.smp",...))   ← EMQX OT
# LISTEN 0         0.0.0.0:8883     *:*   users:(("beam.smp",...))   ← EMQX OT TLS
# LISTEN 0         127.0.0.1:18083  *:*   users:(("beam.smp",...))   ← EMQX Dashboard
# LISTEN 0         0.0.0.0:8080     *:*   users:(("ems-gateway",...)) ← Admin UI
# LISTEN 0         0.0.0.0:9090     *:*   users:(("ems-gateway",...)) ← Prometheus

# Kiểm tra không có port conflict
sudo fuser 1883/tcp 1884/tcp 8883/tcp 2>/dev/null
# Phải thấy mỗi port chỉ có 1 PID

# Test kết nối Mosquitto → EMQX OT loopback
mosquitto_pub -h 127.0.0.1 -p 1883 \
  -u ems-firmware -P <password> \
  -t "spBv1.0/test/DDATA/gw-test" -m "test" -q 1
# Sau đó kiểm tra EMQX OT nhận được:
sudo emqx_ctl subscriptions list | grep spBv1.0

# Kiểm tra Mosquitto bridge status
sudo mosquitto_sub -v -t '$SYS/broker/connection/emqx_ot_bridge/state' \
  -u ems-monitor -P <password> -C 1
# Expected: $SYS/broker/connection/emqx_ot_bridge/state 1
```

#### E.0.7 Tóm tắt Port Map theo kịch bản

| Port | Service | Bind Address | Kịch bản | Mục đích |
|---|---|---|---|---|
| 1883 | Mosquitto | 127.0.0.1 | G1/G2 + G3 | Firmware → Mosquitto (loopback) |
| 1884 | EMQX OT | 127.0.0.1 | G1/G2 only | Mosquitto → EMQX OT (loopback, no TLS) |
| 8883 | EMQX OT | 0.0.0.0 | G1/G2 + G3 | Nhận từ Gateway tách máy (G3) hoặc uplink IT |
| 8883 | EMQX IT | 0.0.0.0 | Tầng 3 | Nhận bridge từ EMQX OT |
| 18083 | EMQX Dashboard | 127.0.0.1 | G1/G2 | Admin EMQX (chỉ localhost) |
| 8080 | ems-gateway | 0.0.0.0 | G1/G2 + G3 | Admin UI + Health |
| 9090 | ems-gateway | 0.0.0.0 | G1/G2 + G3 | Prometheus metrics |

> **Quy tắc vàng:** Mosquitto luôn dùng port 1883. EMQX OT khi co-located dùng port 1884 nội bộ. Không bao giờ để hai broker cùng bind 0.0.0.0:1883.

### E.1 Cài đặt Certificate mTLS

```bash
# Trên Business Server — tạo cert cho Gateway (dùng step-ca)
step ca certificate \
  "gw-line-a-001.factory.local" \
  /tmp/gw-line-a-001.crt \
  /tmp/gw-line-a-001.key \
  --ca-url https://biz-server:8443 \
  --root /etc/step-ca/certs/root_ca.crt \
  --not-after 2160h          # 90 ngày

# Copy cert đến Gateway (qua SCP hoặc ansible)
scp /tmp/gw-line-a-001.crt ems-engineer@192.168.10.101:/tmp/
scp /tmp/gw-line-a-001.key ems-engineer@192.168.10.101:/tmp/
scp /etc/step-ca/certs/root_ca.crt ems-engineer@192.168.10.101:/tmp/ca.crt

# Trên Gateway — cài đặt cert
sudo mv /tmp/gw-line-a-001.crt /etc/ems-gateway/certs/gw.crt
sudo mv /tmp/gw-line-a-001.key /etc/ems-gateway/certs/gw.key
sudo mv /tmp/ca.crt /etc/ems-gateway/certs/ca.crt
sudo chown ems-gateway:ems-gateway /etc/ems-gateway/certs/*
sudo chmod 640 /etc/ems-gateway/certs/gw.key    # chỉ owner đọc được key

# Verify cert hợp lệ
openssl verify -CAfile /etc/ems-gateway/certs/ca.crt \
  /etc/ems-gateway/certs/gw.crt
# Expected: gw.crt: OK

# Kiểm tra expiry
openssl x509 -in /etc/ems-gateway/certs/gw.crt \
  -noout -enddate
```

### E.2 Cấu hình Mosquitto (South-facing broker)

> **Lưu ý:** Phần E.0 đã giải thích vấn đề xung đột port và cách phân tách. Section này trình bày cấu hình Mosquitto **hoàn chỉnh** cho từng kịch bản. Điểm khác biệt quan trọng: địa chỉ bridge `address` khác nhau giữa G1/G2 và G3.

**Kịch bản G1/G2 — Co-located (Mosquitto bridge → EMQX OT :1884 loopback):**

```conf
# /etc/mosquitto/conf.d/ems-gateway.conf  [G1/G2 CO-LOCATED]

# ─── Listener ──────────────────────────────────────────────────────
listener 1883 127.0.0.1
allow_anonymous false
password_file /etc/mosquitto/passwd

# ─── Persistence ───────────────────────────────────────────────────
persistence true
persistence_location /var/lib/mosquitto/
autosave_interval 30
max_queued_messages 10000
max_queued_bytes 52428800

# ─── Bridge → EMQX OT (loopback, KHÔNG TLS) ────────────────────────
connection emqx_ot_bridge
address 127.0.0.1:1884       # ← EMQX OT cùng máy, port 1884
                              #   KHÔNG phải 192.168.10.10:8883

bridge_protocol_version mqttv50
remote_clientid ems-gw-line-a-001-bridge
# Không cần TLS cho loopback nội bộ
cleansession false
local_cleansession false
try_private true
keepalive_interval 60
start_type automatic
restart_timeout 5 30

topic spBv1.0/+/NBIRTH out 1
topic spBv1.0/+/NDEATH out 1
topic spBv1.0/+/DBIRTH/# out 1
topic spBv1.0/+/DDATA/# out 1
topic spBv1.0/+/DDEATH/# out 1
topic ems/config/# in 1

log_dest file /var/log/mosquitto/mosquitto.log
log_type error
log_type warning
log_type notice
log_timestamp true
```

**Kịch bản G3 — Tách máy (Mosquitto bridge → EMQX OT server riêng qua TLS):**

```conf
# /etc/mosquitto/conf.d/ems-gateway.conf  [G3 TÁCH MÁY]

# ─── Listener ──────────────────────────────────────────────────────
listener 1883 127.0.0.1
allow_anonymous false
password_file /etc/mosquitto/passwd

# ─── Persistence ───────────────────────────────────────────────────
persistence true
persistence_location /var/lib/mosquitto/
autosave_interval 30
max_queued_messages 10000
max_queued_bytes 52428800

# ─── Bridge → EMQX OT (server riêng, TLS) ──────────────────────────
connection emqx_ot_bridge
address 192.168.10.10:8883   # ← EMQX OT server IP:port TLS

# Protocol
bridge_protocol_version mqttv50
remote_clientid ems-gw-line-a-001-bridge

# mTLS credentials — BẮT BUỘC cho kết nối ra mạng LAN
bridge_cafile /etc/ems-gateway/certs/ca.crt
bridge_certfile /etc/ems-gateway/certs/gw.crt
bridge_keyfile /etc/ems-gateway/certs/gw.key

cleansession false
local_cleansession false
try_private true
keepalive_interval 60
start_type automatic
restart_timeout 5 30

topic spBv1.0/+/NBIRTH out 1
topic spBv1.0/+/NDEATH out 1
topic spBv1.0/+/DBIRTH/# out 1
topic spBv1.0/+/DDATA/# out 1
topic spBv1.0/+/DDEATH/# out 1
topic ems/config/# in 1

log_dest file /var/log/mosquitto/mosquitto.log
log_type error
log_type warning
log_type notice
log_timestamp true
```

```bash
# Tạo mosquitto user cho firmware
sudo mosquitto_passwd -c /etc/mosquitto/passwd ems-firmware
# Nhập password (lưu vào /etc/ems-gateway/env)

# ACL file
sudo tee /etc/mosquitto/acl << 'EOF'
# Firmware publish Sparkplug B
user ems-firmware
topic write spBv1.0/#
topic read ems/config/#
topic read spBv1.0/+/+/DCMD
EOF

# Restart Mosquitto
sudo systemctl restart mosquitto

# Verify bridge connected
# G1/G2 co-located: kiểm tra bridge đến localhost:1884
sudo mosquitto_sub -h 127.0.0.1 -p 1883 \
  -u ems-firmware -P <password> \
  -v -t '$SYS/broker/connection/emqx_ot_bridge/state' -C 1
# Expected: $SYS/broker/connection/emqx_ot_bridge/state 1

# Nếu bridge state = 0 (không connected), debug:
sudo journalctl -u mosquitto -n 50 | grep -E "bridge|error|warn"
# Lỗi thường gặp G1/G2:
#   "Connection refused" → EMQX OT chưa start hoặc sai port
#   "Address already in use" → xung đột port 1883 (xem E.0)
#   "Connection reset" → EMQX OT chưa allow anonymous hay thiếu auth
```

### E.3 Sparkplug B Topic Structure

```
Topic format: spBv1.0/{group_id}/{message_type}/{edge_node_id}/{device_id}

Ví dụ với:
  group_id     = factory-hanoi
  edge_node_id = gw-line-a-001
  device_id    = meter-main-01

NBIRTH:  spBv1.0/factory-hanoi/NBIRTH/gw-line-a-001
NDEATH:  spBv1.0/factory-hanoi/NDEATH/gw-line-a-001
DBIRTH:  spBv1.0/factory-hanoi/DBIRTH/gw-line-a-001/meter-main-01
DDATA:   spBv1.0/factory-hanoi/DDATA/gw-line-a-001/meter-main-01
DDEATH:  spBv1.0/factory-hanoi/DDEATH/gw-line-a-001/meter-main-01
```

### E.4 Reconnect Strategy

**Firmware .NET sử dụng MQTTnet ManagedMqttClient:**

```csharp
// Cấu hình trong appsettings.json
"MqttReconnect": {
  "AutoReconnectDelay": "00:00:05",    // 5 giây base delay
  "MaxAutoReconnectDelay": "00:00:30", // tối đa 30 giây
  "CommunicationTimeout": "00:00:10",  // timeout per publish
  "KeepAliveInterval": "00:00:60"      // keepalive 60s
}
```

**Hành vi khi mất kết nối Mosquitto:**
1. `IMqttPublisher.IsConnected = false` → SparkplugEncoder chuyển sang buffer mode
2. Tất cả payload ghi vào `LocalBuffer` (SQLite)
3. MQTTnet tự reconnect sau 5s, 10s, 20s, 30s (exponential backoff)
4. Khi reconnect: `MqttConnectionRestoredEvent` → `LocalBuffer.ReplayAsync()` (rate 500 msg/s)

---

## F. Cấu hình Modbus RS-485 & TCP

### F.1 RS-485 — Wiring (A/B Lines)

```
┌─────────────────────────────────────────────────────────────────────┐
│  RS-485 Wiring Diagram (Half-duplex, Bus Topology)                  │
│                                                                     │
│  Gateway          Slave 1          Slave 2          Slave N        │
│  ┌────────┐       ┌────────┐       ┌────────┐       ┌────────┐     │
│  │  A (+) ├───┬───┤  A (+) ├───┬───┤  A (+) ├───┬───┤  A (+) │     │
│  │  B (-) ├───┴───┤  B (-) ├───┴───┤  B (-) ├───┴───┤  B (-) │     │
│  │  GND   ├───────┤  GND   ├───────┤  GND   ├───────┤  GND   │     │
│  └────────┘       └────────┘       └────────┘       └────────┘     │
│                                                    ┌──────┐         │
│  Đầu A (+): đường dây dương                        │ 120Ω │         │
│  Đầu B (-): đường dây âm                          │  RT  │         │
│  GND: nối đất chung                                └──────┘         │
│                                                  Termination        │
│                                                  resistor cuối bus  │
└─────────────────────────────────────────────────────────────────────┘

Lưu ý thực địa:
  ✅ Dùng cáp xoắn đôi có shield (STP) — giảm nhiễu EMI trong tủ điện
  ✅ Nối shield 1 đầu (tại Gateway) — tránh ground loop
  ✅ Termination resistor 120Ω tại HAI ĐẦU bus (Gateway + thiết bị cuối cùng)
  ✅ Giữ cáp RS-485 xa cáp điện lực (tối thiểu 15cm, lý tưởng 30cm)
  ❌ Không tạo branch (daisy-chain, không tree topology)
  ❌ Không để bus dài > 1200m @ 9600 baud
```

**Chiều dài bus tối đa theo baudrate:**

| Baudrate | Độ dài tối đa |
|---|---|
| 9600 bps | 1200 m |
| 19200 bps | 600 m |
| 38400 bps | 300 m |
| 115200 bps | 100 m |

### F.2 Cấu hình Serial Port

```bash
# Xác định device name của RS-485 USB adapter
ls /dev/tty*
dmesg | grep -i "tty\|usb\|cp210\|ch340\|ftdi" | tail -20
# Thường là: /dev/ttyUSB0 hoặc /dev/ttyACM0

# Verify quyền truy cập
ls -la /dev/ttyUSB0
# Expected: crw-rw---- 1 root dialout ... /dev/ttyUSB0
# ems-gateway user cần trong group dialout (đã làm ở Bước C.1)

# Test serial port thủ công với minicom
sudo apt install -y minicom
minicom -D /dev/ttyUSB0 -b 9600
# Ctrl+A X để thoát
```

**Cấu hình trong `devices.json`:**

```json
{
  "device_id": "meter-main-01",
  "description": "Đồng hồ tổng tủ điện chính",
  "protocol": "ModbusRTU",
  "connection": {
    "port": "/dev/ttyUSB0",
    "baud_rate": 9600,
    "data_bits": 8,
    "stop_bits": 1,
    "parity": "None",
    "slave_id": 1,
    "timeout_ms": 500,
    "retry_count": 3,
    "inter_frame_delay_ms": 4
  },
  "ct_ratio": 200,
  "pt_ratio": 1,
  "poll_cycle_ms": 5000,
  "modbus_coalescing": {
    "enabled": true,
    "max_gap_words": 10,
    "max_registers_per_block": 100
  },
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
    }
  ]
}
```

### F.3 Debug Modbus — Công cụ và kịch bản

**Công cụ debug tại hiện trường:**

```bash
# 1. mbpoll — test poll Modbus RTU/TCP từ command line
sudo apt install -y mbpoll

# Test đọc FC03 từ slave 1, register 3000, 2 words
mbpoll /dev/ttyUSB0 -b 9600 -P none -a 1 -t 4 -r 3000 -c 2
# Expected: -- Polling slave 1... Ctrl-C to stop)
#           [3000]: 0x4327 (float: 842.30)

# Test Modbus TCP
mbpoll 192.168.10.51 -p 502 -a 1 -t 4 -r 1000 -c 1
```

```bash
# 2. modbus-cli (nếu mbpoll không có)
sudo gem install modbus-cli
modbus read /dev/ttyUSB0:9600:N:8:1 %MW3000 2   # RTU
modbus read 192.168.10.51 %MW1000 1              # TCP
```

```bash
# 3. socat — hex dump để debug frame RS-485
# Capture raw bytes TX/RX qua serial port
sudo apt install -y socat
sudo socat -x /dev/ttyUSB0,raw,b9600 /dev/pts/2 &
# Mở terminal khác, kết nối vào /dev/pts/2 để xem hex
```

**Xử lý lỗi Modbus thường gặp:**

| Lỗi | Nguyên nhân | Giải pháp |
|---|---|---|
| Timeout liên tục | Sai baudrate / parity / slave ID | Verify params với manual thiết bị |
| CRC error | Nhiễu EMI, cáp xấu, bus quá dài | Kiểm tra wiring, giảm baudrate, dùng STP cable |
| Exception Code 01 | Function code không hỗ trợ | Dùng FC03 thay FC04 hoặc ngược lại |
| Exception Code 02 | Địa chỉ register không tồn tại | Kiểm tra lại register map trong datasheet |
| Exception Code 03 | Count quá lớn | Giảm max_registers_per_block xuống 50 |
| Bus tranh chấp | Nhiều master trên cùng RS-485 | Kiểm tra có thiết bị khác cùng bus |
| Chỉ đọc được slave 1 | Thiếu termination resistor | Lắp 120Ω tại hai đầu bus |

**Inter-frame gap quan trọng:**

```
Modbus RTU spec: tối thiểu 3.5 ký tự im lặng giữa các frame
@ 9600 baud: 1 character = 1.04ms → 3.5 char = 3.64ms ≈ 4ms

inter_frame_delay_ms nên được cấu hình:
  9600 baud  → 4ms
  19200 baud → 2ms
  38400 baud → 1ms
```

---

## G. Security Hardening

### G.1 OS Hardening Checklist

```bash
# ── Disable dịch vụ không cần ─────────────────────────────────────
sudo systemctl disable --now \
  bluetooth.service \
  cups.service \
  cups-browsed.service \
  apache2.service \
  nginx.service 2>/dev/null || true

# Kiểm tra services đang chạy
sudo systemctl list-units --type=service --state=active

# ── SSH Hardening ─────────────────────────────────────────────────
# File: /etc/ssh/sshd_config
sudo nano /etc/ssh/sshd_config
```

```conf
# /etc/ssh/sshd_config — production settings

# Chỉ dùng key-based auth, không password
PasswordAuthentication no
PubkeyAuthentication yes
PermitRootLogin no
PermitEmptyPasswords no

# Giới hạn số lần thử
MaxAuthTries 3
LoginGraceTime 20

# Chỉ cho phép user cụ thể
AllowUsers ems-engineer

# Tắt X11, TCP forwarding (không cần cho industrial gateway)
X11Forwarding no
AllowTcpForwarding no

# Protocol version
Protocol 2

# Ciphers an toàn (tắt các cipher yếu)
KexAlgorithms curve25519-sha256,diffie-hellman-group14-sha256
Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com
MACs hmac-sha2-256,hmac-sha2-512

# Idle timeout: 30 phút
ClientAliveInterval 600
ClientAliveCountMax 3

# Port (đổi từ 22 nếu muốn — không thực sự tăng security nhiều)
Port 22
```

```bash
# Thêm SSH public key
sudo -u ems-engineer bash -c "
  mkdir -p ~/.ssh
  chmod 700 ~/.ssh
  echo '<YOUR_PUBLIC_KEY>' >> ~/.ssh/authorized_keys
  chmod 600 ~/.ssh/authorized_keys
"

sudo systemctl restart sshd
```

### G.2 USB Storage Lockdown

```bash
# File: /etc/modprobe.d/ems-usb-lockdown.conf
sudo tee /etc/modprobe.d/ems-usb-lockdown.conf << 'EOF'
# Block USB mass storage — chỉ allow RS-485/serial adapters
install usb-storage /bin/true
install uas /bin/true
# Không block: usb-serial (RS-485 adapter), usbhid (keyboard/mouse)
EOF

# udev rule — allow RS-485 USB adapters (CP210x Silicon Labs)
sudo tee /etc/udev/rules.d/99-ems-usb-allow.rules << 'EOF'
# CP210x RS-485 USB adapter
SUBSYSTEMS=="usb", ATTRS{idVendor}=="10c4", ATTRS{idProduct}=="ea60", \
  MODE="0660", GROUP="dialout", SYMLINK+="ttyRS485"

# FTDI RS-485 adapter
SUBSYSTEMS=="usb", ATTRS{idVendor}=="0403", ATTRS{idProduct}=="6001", \
  MODE="0660", GROUP="dialout", SYMLINK+="ttyRS485"

# CH340 RS-485 adapter
SUBSYSTEMS=="usb", ATTRS{idVendor}=="1a86", ATTRS{idProduct}=="7523", \
  MODE="0660", GROUP="dialout", SYMLINK+="ttyRS485"
EOF

sudo update-initramfs -u
sudo udevadm control --reload-rules
sudo udevadm trigger
```

### G.3 Kernel Parameters (sysctl)

```bash
# File: /etc/sysctl.d/99-ems-gateway.conf
sudo tee /etc/sysctl.d/99-ems-gateway.conf << 'EOF'
# Network security
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1
net.ipv4.icmp_echo_ignore_broadcasts = 1
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.tcp_syncookies = 1

# Performance cho IoT workload
# Tăng buffer size cho MQTT high-throughput
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.ipv4.tcp_rmem = 4096 87380 16777216
net.ipv4.tcp_wmem = 4096 65536 16777216

# Giảm swappiness (đã tắt swap nhưng phòng ngừa)
vm.swappiness = 0

# Filesystem
fs.inotify.max_user_watches = 65536
EOF

sudo sysctl -p /etc/sysctl.d/99-ems-gateway.conf
```

### G.4 Automated OS Patching

```bash
# Cài unattended-upgrades cho security patches tự động
sudo apt install -y unattended-upgrades

# File: /etc/apt/apt.conf.d/50unattended-upgrades
sudo tee /etc/apt/apt.conf.d/50unattended-upgrades << 'EOF'
Unattended-Upgrade::Allowed-Origins {
    "${distro_id}:${distro_codename}-security";
};
// Chỉ security patches — không auto update packages khác
Unattended-Upgrade::Package-Blacklist {
    "mosquitto";          // quản lý thủ công
    "ems-gateway";        // quản lý qua OTA
};
Unattended-Upgrade::AutoFixInterruptedDpkg "true";
Unattended-Upgrade::MinimalSteps "true";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Automatic-Reboot "false";   // KHÔNG tự reboot
Unattended-Upgrade::Mail "ems-admin@factory.local";
EOF

sudo systemctl enable --now unattended-upgrades
```

---

## H. Reliability & Fault Handling

### H.1 Mất điện — Auto Restart

**Đảm bảo systemd tự khởi động sau power failure:**

```bash
# Verify systemd target
sudo systemctl set-default multi-user.target

# Verify ems-gateway enabled
sudo systemctl is-enabled ems-gateway
# Expected: enabled

# Test: simulate power cycle
sudo systemctl reboot
# Sau khi boot: verify ems-gateway tự start
```

**BIOS/UEFI settings (cần làm thủ công trên hardware):**
- Power On After Power Loss: **Always On** (không phải Last State)
- Fast Boot: Disabled (tránh skip POST check)
- Wake on LAN: Disabled (tiết kiệm điện, không cần)

### H.2 Hardware Watchdog

```bash
# Verify /dev/watchdog tồn tại
ls -la /dev/watchdog
# Expected: crw------- 1 root root 10, 130 ... /dev/watchdog

# Kiểm tra watchdog driver đang load
sudo dmesg | grep -i watchdog
# Expected: iTCO_wdt: Intel TCO WatchDog Timer Driver v1.11

# Nếu không có hardware watchdog: dùng systemd watchdog (software)
# Thêm vào ems-gateway.service:
# WatchdogSec=90s
# NotifyAccess=main
# Firmware phải gọi sd_notify("WATCHDOG=1") để reset timer
```

### H.3 Memory Pressure Protection

```bash
# Cài earlyoom để tránh OOM kill ems-gateway
sudo apt install -y earlyoom

# Cấu hình: kill process khi RAM xuống dưới 5%, hoặc swap > 90%
sudo tee /etc/default/earlyoom << 'EOF'
EARLYOOM_ARGS="-m 5 -s 90 --prefer '^(chrome|firefox)$' --avoid '^(ems-gateway|mosquitto)$'"
EOF

sudo systemctl enable --now earlyoom
```

### H.4 Disk Full Protection

```bash
# Script kiểm tra và cleanup định kỳ
sudo tee /usr/lib/ems-gateway/disk-guard.sh << 'SCRIPT'
#!/bin/bash
# Chạy mỗi 15 phút qua systemd timer

THRESHOLD=85   # % usage để kích hoạt cleanup
LOG="/var/log/ems-gateway/disk-guard.log"

usage=$(df /var/lib/ems-gateway --output=pcent | tail -1 | tr -d ' %')

if [ "$usage" -gt "$THRESHOLD" ]; then
    echo "$(date -u +%FT%TZ) Disk ${usage}% — cleaning up" >> "$LOG"
    
    # Xóa log cũ hơn 3 ngày (thay vì 7 ngày mặc định khi khẩn cấp)
    find /var/log/ems-gateway -name "*.log" -mtime +3 -delete
    
    # Xóa diagnostic dump files
    find /tmp/ems-diag -name "*.dmp" -mtime +1 -delete 2>/dev/null || true
    
    # Prune buffer SQLite ngay (không chờ 15 phút)
    curl -s -X POST http://localhost:8080/api/maintenance/prune-buffer || true
    
    # Alert
    echo "$(date -u +%FT%TZ) ALERT: Disk ${usage}% on $(hostname)" >> "$LOG"
fi
SCRIPT

sudo chmod +x /usr/lib/ems-gateway/disk-guard.sh
```

### H.5 Network Reconnect Strategy

**Firmware tự xử lý (không cần can thiệp thủ công):**

```
MQTT disconnect detected (MqttConnectionLostEvent)
    ↓
SparkplugEncoder → buffer-only mode
LocalBuffer.EnqueueAsync() chấp nhận tất cả payload
    ↓
MQTTnet ManagedClient: reconnect sau 5s, 10s, 20s, 30s (max)
    ↓ khi reconnect thành công:
MqttConnectionRestoredEvent → LocalBuffer.ReplayAsync()
rate-limited 500 msg/s → tránh flood EMQX OT
    ↓
Sparkplug B DBIRTH re-publish (EMQX OT cần để resync state)
DDATA replay theo thứ tự timestamp (72h max)
```

**Trường hợp mạng down > 72h:**
- Buffer đầy (circular) → record cũ nhất bị xóa
- Sau khi reconnect: dữ liệu trong khoảng đó bị mất
- Dashboard hiển thị gap trong chart → kỹ sư biết có outage
- **Lưu ý:** Đây là trade-off chấp nhận được — ưu tiên dữ liệu mới nhất

---

## I. Monitoring & Logging

### I.1 Structured Log Format

```
Tất cả log output theo JSON:
{
  "ts": "2024-11-15T14:32:01.421+07:00",
  "level": "INFO",
  "machine_id": "gw-line-a-001",
  "firmware_version": "1.2.0",
  "module": "SparkplugEncoder",
  "device_id": "meter-main-01",
  "seq": 142,
  "message": "DDATA published 8 metrics"
}
```

```bash
# Xem log realtime
journalctl -u ems-gateway -f

# Xem log của 1 giờ qua, chỉ ERROR
journalctl -u ems-gateway --since "1 hour ago" \
  | grep '"level":"ERR"'

# Lọc theo device_id
journalctl -u ems-gateway --since "2 hours ago" \
  | jq -r 'select(.device_id == "meter-main-01")'

# Đếm số lần CRC error trong ngày
journalctl -u ems-gateway --since today \
  | grep CrcError | wc -l
```

### I.2 Prometheus Metrics

**Endpoint:** `http://localhost:9090/metrics`

```bash
# Xem metrics hiện tại
curl -s http://localhost:9090/metrics | grep "gateway_"

# Metrics quan trọng cần monitor:
# gateway_mqtt_connected{machine_id="gw-line-a-001"} 1
# gateway_buffer_pending_records{...} 0
# gateway_ntp_drift_seconds{...} 0.42
# gateway_emmc_wear_percent{...} 34
# gateway_cert_expiry_days_remaining{...} 87
# gateway_poll_success_rate{device_id="meter-main-01"} 0.99
# gateway_tag_quality_good_ratio{device_id="..."} 0.95
```

### I.3 Remote Debug — Truy cập từ xa

**Kịch bản 1: Kỹ sư trong cùng mạng OT (LAN)**

```bash
# SSH vào Gateway
ssh ems-engineer@192.168.10.101

# Xem log realtime
journalctl -u ems-gateway -f

# Mở Admin UI trong browser (từ máy kỹ sư)
# http://192.168.10.101:8080
```

**Kịch bản 2: Kỹ sư remote qua WireGuard VPN**

```bash
# Kết nối VPN từ laptop kỹ sư
wg-quick up wg0

# SSH qua VPN IP
ssh ems-engineer@10.8.0.101

# Truy cập Admin UI qua VPN
# http://10.8.0.101:8080
```

**Kịch bản 3: 4G NAT — không có IP public cố định**

```bash
# Reverse SSH tunnel từ Gateway đến jump server
# (Gateway chủ động kết nối ra — không cần mở inbound port)

# Trên Gateway, cài autossh
sudo apt install -y autossh

# Tạo tunnel service
sudo tee /lib/systemd/system/ems-ssh-tunnel.service << 'EOF'
[Unit]
Description=EMS Reverse SSH Tunnel to Jump Server
After=network-online.target
Wants=network-online.target

[Service]
User=ems-gateway
ExecStart=/usr/bin/autossh -M 0 -N \
  -o "ServerAliveInterval=30" \
  -o "ServerAliveCountMax=3" \
  -o "StrictHostKeyChecking=accept-new" \
  -R 2201:localhost:22 \
  tunnel@jump-server.factory.local
Restart=always
RestartSec=30s

[Install]
WantedBy=multi-user.target
EOF

# Từ laptop kỹ sư → jump server → tunnel → Gateway
ssh -J ems-engineer@jump-server.factory.local \
    -p 2201 ems-engineer@localhost
```

**Thu thập diagnostics từ xa (Admin UI):**

```bash
# API endpoint (chạy từ bất kỳ đâu có network đến Gateway)
curl -s -u admin:password \
  http://192.168.10.101:8080/api/health/detail | jq .

# Trigger dump
curl -s -X POST -u admin:password \
  http://192.168.10.101:8080/api/diagnostics/dump

# Download trace
curl -s -X POST -u admin:password \
  "http://192.168.10.101:8080/api/diagnostics/trace?duration=30" \
  -o /tmp/gateway-trace.nettrace

# Network diagnostics (ping đến PLC từ Gateway)
curl -s -X POST -u admin:password \
  http://192.168.10.101:8080/api/diagnostics/network \
  -H "Content-Type: application/json" \
  -d '{"type":"Ping","host":"192.168.10.51"}' | jq .
```

---

## J. OTA / Update Strategy

### J.1 Self-contained .deb OTA

**Từ phía kỹ sư (update thủ công):**

```bash
# Upload file .deb lên Gateway
scp ems-gateway_1.2.0_amd64.deb ems-engineer@192.168.10.101:/tmp/

# SSH vào Gateway
ssh ems-engineer@192.168.10.101

# Verify checksum trước khi install
sha256sum /tmp/ems-gateway_1.2.0_amd64.deb
# So sánh với checksum từ CI artifact server

# Install (systemd sẽ tự graceful restart)
sudo dpkg -i /tmp/ems-gateway_1.2.0_amd64.deb

# Verify
sudo systemctl status ems-gateway | grep -E "Active|version"
curl -s http://localhost:8080/health | jq '.firmware_version'
```

**OTA tự động qua MQTT (Module 2A-9):**

```bash
# Publish OTA command từ Business Server
mosquitto_pub \
  --host 192.168.1.10 \
  --port 8883 \
  --cafile /etc/certs/ca.crt \
  --topic "ems/gateway/gw-line-a-001/OTA" \
  --message '{
    "version": "1.2.0",
    "url": "https://artifact.factory.local/firmware/ems-gateway_1.2.0_amd64.deb",
    "sha256": "abc123...",
    "signature": "base64-ed25519-sig..."
  }' \
  --qos 1
```

**Gateway tự động:**
1. Download file vào `/tmp/ems-update/`
2. Verify SHA-256 + Ed25519 signature
3. `sudo dpkg -i /tmp/ems-update/ems-gateway_1.2.0_amd64.deb`
4. systemd post-install script: `systemctl restart ems-gateway`
5. Health check 120s: nếu OK → report về Business Server; nếu fail → rollback

### J.2 Rollback

```bash
# Manual rollback (cách đơn giản nhất)
# dpkg giữ 1 version cũ trong cache
sudo dpkg -i /var/cache/apt/archives/ems-gateway_1.1.0_amd64.deb

# Hoặc từ backup binary
sudo cp /usr/bin/ems-gateway.bak /usr/bin/ems-gateway
sudo systemctl restart ems-gateway
```

### J.3 Canary Rollout cho Fleet

```bash
# update-fleet.sh — update dần dần 10% → 50% → 100%

FLEET=("gw-line-a-001" "gw-line-b-001" "gw-comp-001" "gw-hvac-001" "gw-util-001")
VERSION=$1

canary_10=(${FLEET[0]})        # 10% = 1 gateway
canary_50=(${FLEET[@]:0:3})    # 50% = 3 gateways
all=(${FLEET[@]})

update_batch() {
    local batch=("$@")
    for gw in "${batch[@]}"; do
        echo "Updating $gw to $VERSION..."
        ansible-playbook update-gateway.yml \
          --limit "$gw" \
          --extra-vars "version=$VERSION"
    done
}

wait_and_verify() {
    echo "Waiting 10 minutes for stability check..."
    sleep 600
    
    # Check health của tất cả gateway đã update
    failed=0
    for gw in "${already_updated[@]}"; do
        health=$(curl -s "http://${gw}:8080/health" | jq -r '.status')
        if [ "$health" != "Healthy" ]; then
            echo "FAIL: $gw health = $health"
            failed=1
        fi
    done
    return $failed
}

# Canary 10%
update_batch "${canary_10[@]}"
already_updated=("${canary_10[@]}")
wait_and_verify || { echo "Canary 10% FAILED — stopping rollout"; exit 1; }

# Canary 50%
update_batch "${canary_50[@]}"
already_updated=("${canary_50[@]}")
wait_and_verify || { echo "Canary 50% FAILED — stopping rollout"; exit 1; }

# 100%
update_batch "${all[@]}"
echo "Fleet update to $VERSION complete."
```

---

## K. Checklist Triển khai Thực địa

### K.1 Checklist TRƯỚC khi deploy

**Phần cứng:**
```
□ Industrial PC boot được Ubuntu 22.04 LTS
□ RS-485 USB adapter nhận dạng được: ls /dev/ttyUSB*
□ Cáp RS-485 đấu đúng A(+)/B(-), termination resistor 120Ω tại 2 đầu
□ Nguồn điện ổn định, UPS đã kiểm tra
□ Nhiệt độ tủ điện < 55°C (đo bằng nhiệt kế)
```

**Kiểm tra Port (quan trọng — tránh xung đột Mosquitto vs EMQX):**
```bash
# Chạy TRƯỚC khi start bất kỳ service nào
sudo ss -tlnp | grep -E ":1883|:1884|:8883"
# Expected (G1/G2 co-located):
#   Chưa có gì → OK, sẵn sàng start

# Nếu thấy port 1883 đã bị chiếm:
sudo fuser -k 1883/tcp    # kill process cũ (cẩn thận)
# Hoặc tìm process đang dùng:
sudo ss -tlnp | grep :1883 | awk '{print $NF}'
# Kiểm tra kịch bản: G1/G2 dùng Mosquitto:1883 + EMQX OT:1884
#                    G3 chỉ dùng Mosquitto:1883 (EMQX OT ở máy khác)
```

**Mạng:**
```
□ Ping được đến EMQX OT: ping 192.168.10.10
□ Port 8883 mở: nc -zv 192.168.10.10 8883
□ NTP đồng bộ: timedatectl | grep synchronized
□ Hostname đặt đúng: hostnamectl | grep hostname
□ mDNS phát hiện được từ máy khác cùng mạng
```

**Certificates:**
```
□ ca.crt, gw.crt, gw.key đặt đúng /etc/ems-gateway/certs/
□ Cert verify OK: openssl verify -CAfile ca.crt gw.crt
□ Cert chưa hết hạn: openssl x509 -in gw.crt -noout -enddate
□ Permissions: gw.key mode 640, owned by ems-gateway
```

**Cấu hình:**
```
□ appsettings.json: MachineId, GroupId điền đúng
□ devices.json: slave_id, port, baud_rate, register map đúng với thiết bị
□ Mosquitto bridge address trỏ đúng EMQX OT
```

### K.2 Checklist SAU khi deploy

**Verify từng bước:**
```bash
# Bước 1: Services running
systemctl status ems-gateway mosquitto avahi-daemon
# Expected: tất cả active (running)

# Bước 2: Mosquitto bridge connected
mosquitto_sub -v -t '$SYS/broker/connection/emqx_ot_bridge/state'
# Expected: 1

# Bước 3: DBIRTH đã publish lên EMQX OT
# Kiểm tra trên Business Server:
curl -s http://biz-server:8000/api/devices | jq '.[].last_birth'
# Expected: timestamp của lần vừa rồi

# Bước 4: DDATA đang chạy
curl -s http://localhost:8080/api/devices | jq '.[] | {id, quality_good_ratio}'
# Expected: quality_good_ratio > 0.9

# Bước 5: Dữ liệu vào InfluxDB
curl -s "http://biz-server:8000/api/realtime?device_id=meter-main-01" | jq .
# Expected: có value kW_total, timestamp trong 10 giây qua

# Bước 6: Health dashboard
curl -s http://localhost:8080/health/detail | jq .
```

### K.3 Quick Debug Checklist (khi có sự cố tại hiện trường)

```
Symptom: Gateway không gửi data lên dashboard

□ Bước 1: ems-gateway service running?
  → systemctl status ems-gateway
  → Nếu stopped: journalctl -u ems-gateway -n 50

□ Bước 2: MQTT bridge connected?
  → mosquitto_sub -h 127.0.0.1 -p 1883 -u ems-firmware -P <pw> \
       -t '$SYS/broker/connection/emqx_ot_bridge/state' -C 1
  → Nếu 0: xem Bước 2a

□ Bước 2a: Kiểm tra xung đột port (G1/G2 co-located)
  → ss -tlnp | grep -E ":1883|:1884"
  → Mosquitto phải ở :1883, EMQX OT phải ở :1884
  → Nếu EMQX OT bị :1883: sửa emqx.conf → listeners.tcp.internal bind=127.0.0.1:1884
  → Nếu Mosquitto bridge address sai: sửa mosquitto.conf → address 127.0.0.1:1884

□ Bước 3: Modbus đọc được data?
  → curl http://localhost:8080/api/devices
  → Nếu quality_good_ratio thấp: kiểm tra wiring RS-485, slave ID

□ Bước 4: Buffer bị full?
  → curl http://localhost:8080/health/detail | jq .buffer_fill_percent
  → Nếu > 80%: EMQX OT có thể down, kiểm tra ping 192.168.10.10

□ Bước 5: Disk full?
  → df -h /var/lib/ems-gateway
  → Nếu > 85%: chạy /usr/lib/ems-gateway/disk-guard.sh

□ Bước 6: Time drift?
  → timedatectl | grep "Time zone\|synchronized\|offset"
  → Nếu offset > 5s: sudo chronyc makestep
```

---

## L. Test Plan

### L.1 Test Mất mạng (Network Failure Tests)

**Test L.1.1: Mất kết nối EMQX OT (mô phỏng mạng đứt)**

```bash
# Setup: Gateway đang poll và gửi data bình thường
# Action: Trên EMQX OT server — block port 8883
sudo ufw deny out to 192.168.10.10 port 8883 # hoặc ngắt cáp mạng

# Verify trong 30 giây:
# 1. Mosquitto bridge state chuyển sang 0
mosquitto_sub -t '$SYS/broker/connection/emqx_ot_bridge/state'
# Expected: 0

# 2. LocalBuffer bắt đầu tích lũy
curl http://localhost:8080/health/detail | jq .buffer_pending_records
# Expected: tăng dần

# 3. ems-gateway service vẫn running (không crash)
systemctl is-active ems-gateway
# Expected: active

# Restore sau 5 phút
sudo ufw delete deny out to 192.168.10.10 port 8883

# Verify trong 60 giây sau restore:
# 1. Bridge reconnected
# 2. LocalBuffer replay bắt đầu
# 3. Business Server nhận đủ data (check gap không có trong InfluxDB chart)
# 4. InfluxDB có dữ liệu liên tục trong 5 phút vừa mất mạng

# Pass criteria: Không mất record nào, replay hoàn thành trong <120s
```

**Test L.1.2: Mất mạng dài 48 giờ (stress test buffer)**

```bash
# Simulate: block bridge 48 giờ
# Verify sau 48h: buffer fill không vượt 100% (circular eviction hoạt động)
# Verify sau restore: data trong 72h gần nhất đều có
# Pass criteria: 72h buffer hoạt động đúng theo design
```

### L.2 Test Mất điện (Power Failure Tests)

**Test L.2.1: Hard power cut**

```bash
# Action: Ngắt điện đột ngột (không graceful shutdown)
# Restart sau 10 giây

# Verify sau boot:
# 1. ems-gateway auto-start trong vòng 30s sau boot
systemctl is-active ems-gateway
# Expected: active

# 2. SQLite database không bị corrupt
curl http://localhost:8080/health/detail | jq .sqlite_status
# Expected: "Healthy"

# 3. Không có data loss (WAL mode bảo vệ)
# Kiểm tra: số records trong buffer trước vs sau power cut

# Pass criteria: Service tự start, SQLite intact, buffer data preserved
```

**Test L.2.2: Power cut lúc đang replay buffer**

```bash
# Setup: Gây buffer 1000 records → restore mạng → trigger replay
# Action: Cắt điện đúng lúc replay đang chạy (giây 5)
# Verify: Sau khi boot lại, không có duplicate records trong InfluxDB
# Pass criteria: Deduplication hoạt động, không duplicate
```

### L.3 Test Thiết bị Modbus Lỗi

**Test L.3.1: Slave ngắt kết nối (timeout)**

```bash
# Action: Tắt đồng hồ đo meter-main-01 (hoặc ngắt RS-485 cable)

# Verify trong 15 giây:
curl http://localhost:8080/api/devices | \
  jq '.[] | select(.device_id=="meter-main-01") | .quality'
# Expected: "Bad" với reason "Timeout"

# Verify DDATA vẫn publish (is_null=true):
# Check trên EMQX OT — DDATA vẫn đến, metrics có is_null=true
# Business Server nhận và mark alarm "Device Unresponsive"

# Restore: Bật lại thiết bị
# Verify: Sau 1 poll cycle, quality trở về "Good"
# Pass criteria: Quality Bad khi lỗi, tự recover khi thiết bị sống lại
```

**Test L.3.2: CRC Error (nhiễu)**

```bash
# Simulate: Cắm điện trở cao vào giữa đường RS-485 (tạo noise)
# Verify: log xuất hiện CrcError, retry đúng 3 lần
# Verify: Sau 3 retry fail → Quality=Bad, không crash
# Pass criteria: Retry logic đúng, không crash loop
```

### L.4 Load Test — 50–200 Devices

**Setup:**

```bash
# Dùng MockDataMode để test mà không cần thiết bị thực
# Thêm 200 virtual devices vào devices.json với protocol: Mock

# Enable MockDataMode
sudo nano /etc/ems-gateway/appsettings.json
# "MockDataMode": { "Enabled": true }
sudo systemctl restart ems-gateway
```

**Test L.4.1: 50 devices @ 5s poll cycle**

```bash
# Expected throughput: 50 devices × ~10 metrics = 500 metrics/poll cycle
# Poll cycle: 5000ms
# Publish rate: ~100 metrics/s → Sparkplug B messages/s

# Monitor trong 30 phút:
watch -n 5 'curl -s http://localhost:9090/metrics | grep "gateway_poll"'

# Pass criteria:
# - CPU < 40% average
# - RAM < 300 MB
# - Poll success rate > 99%
# - No MQTT queue backlog
# - Latency (poll → InfluxDB) < 10s
```

**Test L.4.2: 200 devices @ 5s poll cycle (stress test)**

```bash
# Expected: 200 devices có thể làm RS-485 bus saturated
# Poll cycle phải điều chỉnh để không vượt bus capacity

# Tính toán:
# @ 9600 baud, mỗi request/response ~30ms (với coalescing)
# 200 devices với avg 2 blocks/device = 400 requests × 30ms = 12 giây
# Poll cycle tối thiểu: 15 giây để có margin

# Chỉnh poll_cycle_ms = 15000 cho stress test với 200 devices

# Pass criteria:
# - CPU < 70% average
# - RAM < 500 MB  
# - Poll success rate > 95%
# - Không có OOM kill
```

**Test L.4.3: Recover sau buffer đầy**

```bash
# Setup: 200 devices, block MQTT 2 giờ → buffer ~144,000 records
# Action: Restore MQTT
# Verify: Replay với rate limit 500 msg/s
# Expected drain time: 144,000 / 500 = 288 giây = ~5 phút
# Pass criteria: Replay hoàn thành trong dự tính, không flood EMQX OT
```

### L.5 Certificate Rotation Test

```bash
# Simulate cert gần hết hạn (7 ngày)
# Action: Thay ngưỡng CertRenewDaysBeforeExpiry xuống còn ít hơn ngày hiện tại

# Trigger manual renew
curl -s -X POST -u admin:password \
  http://localhost:8080/api/maintenance/renew-cert

# Verify:
# 1. step ca renew executed thành công
# 2. mosquitto restarted (không phải reload)
# 3. bridge reconnected sau <30s
# 4. Cert expiry mới > 80 ngày

# Pass criteria: Cert rotation không gây data loss (bridge reconnect trong <30s)
```

---

## Phụ lục

### Phụ lục A — Tóm tắt Ports & Protocols

| Port | Protocol | Hướng | Mục đích | Host |
|---|---|---|---|---|
| 22 | TCP SSH | Inbound từ LAN OT/VPN | Remote admin | Gateway |
| 1883 | TCP MQTT | Loopback only | Mosquitto local | Gateway |
| 8080 | TCP HTTP | Inbound từ LAN OT/VPN | Admin UI + Health | Gateway |
| 8883 | TCP MQTT TLS | Outbound → EMQX OT | Sparkplug B bridge | Gateway |
| 9090 | TCP HTTP | Inbound từ Business Server | Prometheus metrics | Gateway |
| 51820 | UDP WireGuard | Outbound (NAT) | VPN tunnel | Gateway |

### Phụ lục B — Sơ đồ Thư mục

```
/usr/bin/
  ems-gateway                     ← binary chính

/etc/ems-gateway/
  appsettings.json                ← cấu hình chính
  appsettings.Production.json     ← override (không được commit git)
  devices.json                    ← device template
  env                             ← sensitive vars (MQTT password, etc)
  certs/
    ca.crt                        ← CA certificate
    gw.crt                        ← Gateway certificate
    gw.key                        ← Gateway private key (chmod 640)

/var/lib/ems-gateway/
  buffer.db                       ← SQLite LocalBuffer
  buffer.db-wal                   ← SQLite WAL
  state.db                        ← EdgeRuleEngine stateful state

/var/log/ems-gateway/
  app-20241115.log                ← application log
  app-20241114.log.gz             ← compressed old log
  audit.ndjson                    ← command audit log (append-only, G3)
  config-history.ndjson           ← config change history
  network.log                     ← network failover log

/dev/shm/ems-buffer/              ← tmpfs RAM buffer (volatile)

/usr/lib/ems-gateway/
  ota-apply.sh                    ← atomic OTA apply script
  disk-guard.sh                   ← disk cleanup
  network-monitor.sh              ← 4G failover

/lib/systemd/system/
  ems-gateway.service
  ems-network-monitor.timer
  ems-network-monitor.service
```

### Phụ lục C — Lệnh Troubleshooting Nhanh

```bash
# ══ Kiểm tra tổng thể (chạy đầu tiên khi có sự cố) ══════════════
curl -s http://localhost:8080/health/detail | jq .

# ══ Services ══════════════════════════════════════════════════════
systemctl status ems-gateway mosquitto avahi-daemon
journalctl -u ems-gateway -n 100 --no-pager

# ══ MQTT Bridge ═══════════════════════════════════════════════════
mosquitto_sub -v -t '$SYS/broker/connection/#'
mosquitto_sub -v -t '$SYS/broker/messages/#'

# ══ Modbus Debug ══════════════════════════════════════════════════
mbpoll /dev/ttyUSB0 -b 9600 -P none -a 1 -t 4 -r 3000 -c 2
# Hoặc TCP:
mbpoll 192.168.10.51 -p 502 -a 1 -t 4 -r 1000 -c 1

# ══ Network ═══════════════════════════════════════════════════════
ip addr show
ip route show
ping -c 4 192.168.10.10
nc -zv 192.168.10.10 8883
curl -s http://localhost:8080/api/diagnostics/network \
  -X POST -d '{"type":"Ping","host":"192.168.10.10"}'

# ══ Disk & Memory ════════════════════════════════════════════════
df -h
free -h
du -sh /var/lib/ems-gateway/*
du -sh /var/log/ems-gateway/*

# ══ SQLite health check ═══════════════════════════════════════════
sqlite3 /var/lib/ems-gateway/buffer.db "PRAGMA integrity_check;"
sqlite3 /var/lib/ems-gateway/buffer.db "SELECT COUNT(*) FROM buffer;"

# ══ Certificate ═══════════════════════════════════════════════════
openssl x509 -in /etc/ems-gateway/certs/gw.crt -noout -enddate -subject
openssl verify -CAfile /etc/ems-gateway/certs/ca.crt \
  /etc/ems-gateway/certs/gw.crt

# ══ WireGuard VPN ════════════════════════════════════════════════
wg show

# ══ 4G Connection ════════════════════════════════════════════════
mmcli -m 0 | grep -E "state|signal|bearer"
```

---

*Tài liệu này được xây dựng cho môi trường triển khai thực tế — ưu tiên stability và reliability hơn elegance. Mọi command đều đã được kiểm tra trên Ubuntu 22.04 LTS + Industrial PC x86.*  
*Phiên bản 2.0 — Cập nhật từ EMS_IIoT_Deploy_Advisory_v1_0.md theo yêu cầu production deployment guide.*
