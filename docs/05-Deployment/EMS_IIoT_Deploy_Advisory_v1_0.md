# Tư vấn Chiến lược Triển khai — Hệ thống EMS IIoT On-premise
> Góc nhìn: Chuyên gia IIoT · System Architecture  
> Tập trung: Hiệu năng · Ổn định · Dễ bảo trì & Nâng cấp  
> Phạm vi: Toàn bộ 5 tầng — từ Gateway đến Application Layer  
> Phiên bản: 1.0

---

## Tóm tắt điều hành

Kiến trúc 5 tầng được thiết kế tốt về mặt nguyên tắc. Tuy nhiên **khoảng cách lớn nhất trong tài liệu hiện tại nằm ở tầng Deploy** — tức là làm thế nào để đưa phần mềm đã code lên phần cứng thực, vận hành liên tục 24/7, và nâng cấp an toàn mà không gây gián đoạn. Tài liệu này lấp đầy khoảng cách đó.

**3 vấn đề cốt lõi cần giải quyết:**

| # | Vấn đề | Hệ quả nếu bỏ qua |
|---|---|---|
| 1 | Không có chuẩn hóa môi trường deploy | Gateway tại xưởng A khác cấu hình xưởng B → bug khó tái hiện |
| 2 | Không có pipeline CI/CD | Update firmware bằng tay → rủi ro human error, không rollback được |
| 3 | Không có observability xuyên tầng | Khi lỗi xảy ra, không biết lỗi ở tầng nào → MTTR cao |

---

## Phần I — Đánh giá Tổng thể Kiến trúc Deployment Hiện tại

### Điểm mạnh đã thiết kế đúng

**Separation of concerns rõ ràng theo tầng:**
- Tầng 2A (Gateway firmware .NET) hoàn toàn độc lập — poll thiết bị kể cả khi Tầng 3 down
- Tầng 2B (EMQX OT + Mosquitto) là infrastructure thuần — không có business logic
- Tầng 3 (Business Server) chứa toàn bộ analytics — layered monolith phù hợp SME
- Buffer 72h tại hai lớp (Gateway LocalBuffer + EMQX Store-and-Forward) đảm bảo không mất dữ liệu

**Nguyên tắc security đúng đắn:**
- DMZ one-way OT→IT — không có kết nối ngược mặc định
- mTLS tại bridge — xác thực hai chiều
- JWT validate tại Gateway (last enforcer) — không tin Business Server tuyệt đối

**Thực dụng phù hợp SME:**
- Layered Monolith thay vì Microservices — đội IT 1–2 người vận hành được
- Cold Standby thay vì Active-Passive — không cần chuyên môn HA infrastructure

### Các Gap cần bổ sung

| Gap | Mức độ | Phần xử lý trong tài liệu này |
|---|---|---|
| Không có chuẩn hóa môi trường (IaC) | Cao | Phần II |
| Không có CI/CD pipeline cho firmware | Cao | Phần III |
| Chiến lược deploy Tầng 3 chưa rõ | Cao | Phần IV |
| Observability chưa xuyên tầng | Trung bình | Phần V |
| Backup & DR chưa được định nghĩa | Cao | Phần VI |
| Network topology chưa chi tiết | Trung bình | Phần VII |

---

## Phần II — Chuẩn hóa Môi trường (Infrastructure as Code)

### Vấn đề hiện tại

Thiết kế v1.8 mô tả rất chi tiết *phần mềm* nhưng phần *cài đặt OS và cấu hình hệ thống* còn rải rác trong nhiều chỗ (`deploy/setup.sh`, `deploy/hardening.sh`, systemd unit files). Không có một quy trình chuẩn duy nhất để tạo ra một Gateway mới từ phần cứng trắng.

**Hệ quả thực tế:** Kỹ sư cài thủ công → mỗi Gateway một chút khác nhau → bug xuất hiện tại Gateway A nhưng không tái hiện tại Gateway B → mất hàng giờ debug.

### Giải pháp: Ansible Playbook cho toàn bộ fleet

Ansible được chọn vì: agentless (không cần cài agent trên Gateway), YAML dễ đọc cho đội IT SME, idempotent (chạy lại không gây hại), không cần internet (chạy từ máy tính quản trị trong mạng IT).

```
ems-deploy/
├── inventory/
│   ├── production.yml          ← danh sách tất cả host: gateway, iiot-server, biz-server
│   └── staging.yml             ← môi trường test
├── group_vars/
│   ├── all.yml                 ← biến chung: NTP server, CA URL, log level
│   ├── gateways.yml            ← biến riêng cho tất cả Gateway
│   └── business_server.yml     ← biến riêng cho Business Server
├── host_vars/
│   ├── gw-line-a-001.yml       ← biến riêng: machine_id, device config path
│   └── gw-line-b-001.yml
├── roles/
│   ├── common/                 ← OS hardening dùng chung tất cả node
│   │   ├── tasks/main.yml      ← apt update, timezone, NTP, SSH key, disable root login
│   │   └── files/sysctl.conf   ← kernel params tối ưu
│   ├── gateway/                ← cài đặt Gateway firmware
│   │   ├── tasks/
│   │   │   ├── install.yml     ← copy binary, systemd unit, cert
│   │   │   ├── mosquitto.yml   ← cài + cấu hình Mosquitto
│   │   │   ├── mdns.yml        ← cài Avahi, set hostname
│   │   │   └── hardening.yml   ← USB lockdown, USB udev rules
│   │   ├── templates/
│   │   │   ├── ems-gateway.service.j2
│   │   │   ├── mosquitto.conf.j2
│   │   │   └── appsettings.json.j2   ← Jinja2 template với biến per-host
│   │   └── handlers/main.yml   ← restart services khi config thay đổi
│   ├── iiot_server/            ← cài đặt Tầng 2B (EMQX OT)
│   └── business_server/        ← cài đặt Tầng 3
└── playbooks/
    ├── deploy-gateway.yml      ← deploy hoặc update một Gateway
    ├── deploy-all.yml          ← deploy toàn bộ fleet
    ├── rotate-cert.yml         ← renew cert thủ công cho fleet
    └── rollback-gateway.yml    ← rollback firmware về version trước
```

**Ví dụ inventory/production.yml:**

```yaml
all:
  children:
    gateways:
      hosts:
        gw-line-a-001:
          ansible_host: 192.168.10.101
          machine_id: a1b2c3d4
          group_id: factory-hanoi
        gw-line-b-001:
          ansible_host: 192.168.10.102
          machine_id: e5f6g7h8
          group_id: factory-hanoi
    iiot_servers:
      hosts:
        iiot-server-01:
          ansible_host: 192.168.10.10
    business_servers:
      hosts:
        biz-server-01:
          ansible_host: 192.168.1.10
```

**Quy trình deploy Gateway mới (từ phần cứng trắng → hoạt động):**

```bash
# Từ máy tính quản trị trong mạng IT:
ansible-playbook playbooks/deploy-gateway.yml \
  --limit gw-line-a-001 \
  --extra-vars "firmware_version=1.2.0"

# Toàn bộ playbook tự động:
# 1. Cài OS dependencies (apt)
# 2. Tạo user ems-gateway (non-root)
# 3. Cài Mosquitto + cấu hình từ template
# 4. Copy firmware binary + appsettings.json (render từ Jinja2)
# 5. Copy systemd unit files
# 6. Request cert từ step-ca (Business Server)
# 7. USB lockdown + OS hardening
# 8. Cài Avahi mDNS
# 9. Bật và enable tất cả services
# 10. Verify: kiểm tra /health endpoint trả 200
# Thời gian: ~5 phút từ Ubuntu minimal install
```

**Nguyên tắc bất biến:** Mọi thay đổi cấu hình Gateway đều phải đi qua Ansible playbook hoặc Config Channel (G2). **Không SSH tay để sửa config** — nếu cần SSH thì sau đó phải cập nhật lại Ansible role.

---

## Phần III — CI/CD Pipeline cho Gateway Firmware

### Vấn đề hiện tại

Tài liệu v1.8 mô tả OTA Update Client nhưng chưa mô tả *làm thế nào để tạo ra firmware bundle* cần upload. Không có pipeline thì:
- Test chỉ chạy khi developer nhớ chạy
- Build artifact không nhất quán giữa các lần
- Không biết firmware version X chứa những thay đổi gì

### Kiến trúc CI/CD (Gitea + Gitea Actions hoặc GitHub Actions)

Chọn Gitea self-hosted vì: hoạt động on-premise (không cần internet), nhẹ hơn GitLab, phù hợp đội IT nhỏ.

```
Developer push code
      │
      ▼
┌─────────────────────────────────────────────────────────────────┐
│  CI Pipeline (trigger: push to any branch)                      │
│                                                                 │
│  Job 1: Build & Test (~3 phút)                                  │
│  ├── dotnet restore                                             │
│  ├── dotnet build --configuration Release                       │
│  ├── dotnet test (tất cả unit tests)                            │
│  └── dotnet test (integration tests với MockDataMode)           │
│                                                                 │
│  Job 2: Security Scan (~2 phút) [parallel với Job 1]           │
│  ├── dotnet-outdated (check outdated NuGet)                     │
│  └── trivy filesystem scan (CVE check trên dependencies)        │
└─────────────────────────────────────────────────────────────────┘
      │ (chỉ tiếp tục nếu tất cả pass)
      ▼
┌─────────────────────────────────────────────────────────────────┐
│  CD Pipeline (trigger: push to main/release branch)             │
│                                                                 │
│  Job 3: Package Firmware Bundle (~2 phút)                       │
│  ├── dotnet publish --runtime linux-x64 --self-contained        │
│  ├── Tạo firmware-{version}.tar.gz                              │
│  ├── Tính SHA-256 checksum                                      │
│  ├── Ký Ed25519 signature (private key trong CI secret)         │
│  └── Upload lên internal artifact server (Gitea Packages)       │
│                                                                 │
│  Job 4: Deploy to Staging (~5 phút)                             │
│  ├── Ansible playbook deploy-gateway.yml --limit staging        │
│  ├── Verify /health endpoint                                    │
│  ├── Chạy smoke test: poll 1 lần, verify DBIRTH published       │
│  └── Notify Zalo/Slack: "Staging deploy OK, version X.Y.Z"     │
└─────────────────────────────────────────────────────────────────┘
      │ (manual approval required cho production deploy)
      ▼
┌─────────────────────────────────────────────────────────────────┐
│  Production Deploy (trigger: manual approval)                   │
│                                                                 │
│  Job 5: Canary Deploy (10% → 50% → 100%)                       │
│  ├── Ansible: update gw-line-a-001 (10% fleet)                  │
│  ├── Wait 10 phút, monitor health metrics                       │
│  ├── Nếu OK: Ansible update 50% fleet                          │
│  ├── Wait 10 phút, monitor                                      │
│  └── Nếu OK: Ansible update 100%                               │
└─────────────────────────────────────────────────────────────────┘
```

**Gateway OTA Update flow:**

```
Gitea Packages
      │ firmware-1.2.0.tar.gz + .sha256 + .sig
      ▼
Internal HTTPS artifact server (nginx trên Business Server)
      │ GET /firmware/1.2.0/firmware.tar.gz
      ▼
Gateway OTA Client (Module 2A-9)
  1. Download bundle (verify SSL cert của internal server)
  2. Verify SHA-256 checksum
  3. Verify Ed25519 signature (public key embedded trong firmware)
  4. Execute ota-apply.sh (atomic rename x86 / RAUC ARM)
  5. systemctl restart ems-gateway
  6. Health check 120s → commit hoặc rollback
  7. Report kết quả về Business Server qua MQTT
```

**Semantic Versioning cho firmware:**

```
MAJOR.MINOR.PATCH-BUILD
  1.2.3-20241115

MAJOR: breaking change (thay đổi Sparkplug B schema)
MINOR: tính năng mới (thêm protocol adapter)
PATCH: bug fix, performance
BUILD: CI build number (tự động)

Gateway chỉ nhận update nếu:
  MAJOR không thay đổi (breaking change cần deploy thủ công với review)
  Hoặc có explicit override flag trong OTA payload
```

---

## Phần IV — Chiến lược Deploy Tầng 3 (Business Server)

### Đánh giá hiện trạng

Tầng 3 dùng Layered Monolith (Python FastAPI + Celery + InfluxDB + PostgreSQL) — lựa chọn đúng cho SME. Tuy nhiên **cách deploy chưa được định nghĩa**, dẫn đến rủi ro:
- Update Business Server gây downtime → mất dashboard trong giờ làm việc
- Không có cách rollback nhanh khi update lỗi
- InfluxDB và PostgreSQL data không được backup tự động

### Khuyến nghị: Docker Compose (không Kubernetes)

**Tại sao Docker Compose thay vì Kubernetes:**
- Kubernetes overhead quá lớn cho 1 máy, 1–2 người vận hành
- Docker Compose: 1 file, đơn giản, đủ cho scale của SME
- Khi cần scale trong tương lai: migrate sang Docker Swarm (minimal change) hoặc K3s

```yaml
# docker-compose.yml — Tầng 3 Business Server

version: "3.9"

services:
  # ── MQTT Broker (IT zone) ──────────────────────────────────────
  emqx-it:
    image: emqx/emqx:5.8
    container_name: emqx-it
    restart: unless-stopped
    ports:
      - "1884:1883"   # MQTT (IT zone, internal only)
      - "8883:8883"   # MQTT TLS (bridge from OT)
      - "18083:18083" # Dashboard (internal only)
    volumes:
      - emqx-data:/opt/emqx/data
      - ./config/emqx:/opt/emqx/etc
      - ./certs:/opt/emqx/etc/certs:ro
    networks:
      - ems-internal
    healthcheck:
      test: ["CMD", "emqx", "ping"]
      interval: 30s
      timeout: 10s
      retries: 3

  # ── Time-series Database ───────────────────────────────────────
  influxdb:
    image: influxdb:2.7
    container_name: influxdb
    restart: unless-stopped
    ports:
      - "127.0.0.1:8086:8086"  # chỉ bind localhost — không expose ra ngoài
    volumes:
      - influxdb-data:/var/lib/influxdb2
      - influxdb-config:/etc/influxdb2
      - ./backup/influxdb:/backup  # mount backup directory
    environment:
      DOCKER_INFLUXDB_INIT_MODE: setup
      DOCKER_INFLUXDB_INIT_USERNAME: ${INFLUXDB_ADMIN_USER}
      DOCKER_INFLUXDB_INIT_PASSWORD: ${INFLUXDB_ADMIN_PASSWORD}
      DOCKER_INFLUXDB_INIT_ORG: ems-factory
      DOCKER_INFLUXDB_INIT_BUCKET: energy-raw
      DOCKER_INFLUXDB_INIT_RETENTION: "8760h"  # 1 năm
    networks:
      - ems-internal
    healthcheck:
      test: ["CMD", "influx", "ping"]
      interval: 30s

  # ── Relational Database ────────────────────────────────────────
  postgres:
    image: postgres:16-alpine
    container_name: postgres
    restart: unless-stopped
    ports:
      - "127.0.0.1:5432:5432"  # chỉ bind localhost
    volumes:
      - postgres-data:/var/lib/postgresql/data
      - ./backup/postgres:/backup
      - ./init-sql:/docker-entrypoint-initdb.d:ro
    environment:
      POSTGRES_DB: ems
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    networks:
      - ems-internal
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
      interval: 30s

  # ── Task Queue ─────────────────────────────────────────────────
  redis:
    image: redis:7-alpine
    container_name: redis
    restart: unless-stopped
    command: redis-server --save 60 1 --loglevel warning --maxmemory 512mb --maxmemory-policy allkeys-lru
    volumes:
      - redis-data:/data
    networks:
      - ems-internal
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 30s

  # ── Business Server (FastAPI + Celery) ─────────────────────────
  ems-server:
    image: ems-business-server:${VERSION:-latest}
    container_name: ems-server
    restart: unless-stopped
    depends_on:
      influxdb: { condition: service_healthy }
      postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
      emqx-it: { condition: service_healthy }
    ports:
      - "127.0.0.1:8000:8000"  # FastAPI — exposed via nginx
    volumes:
      - ./config/server:/app/config:ro
      - ./certs:/app/certs:ro
      - server-logs:/app/logs
    environment:
      - INFLUXDB_URL=http://influxdb:8086
      - POSTGRES_DSN=postgresql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@postgres:5432/ems
      - REDIS_URL=redis://redis:6379/0
      - EMQX_BROKER=emqx-it:1884
    networks:
      - ems-internal
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8000/health"]
      interval: 30s

  # ── Celery Worker (Analytics) ──────────────────────────────────
  celery-worker:
    image: ems-business-server:${VERSION:-latest}
    container_name: celery-worker
    restart: unless-stopped
    command: celery -A ems.celery worker --loglevel=info --concurrency=4
    depends_on:
      - redis
      - postgres
      - influxdb
    volumes:
      - ./config/server:/app/config:ro
    environment:
      - POSTGRES_DSN=postgresql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@postgres:5432/ems
      - REDIS_URL=redis://redis:6379/0
      - INFLUXDB_URL=http://influxdb:8086
    networks:
      - ems-internal

  # ── Celery Beat (Scheduler) ────────────────────────────────────
  celery-beat:
    image: ems-business-server:${VERSION:-latest}
    container_name: celery-beat
    restart: unless-stopped
    command: celery -A ems.celery beat --loglevel=info --scheduler django_celery_beat.schedulers:DatabaseScheduler
    depends_on:
      - redis
      - postgres
    networks:
      - ems-internal

  # ── Reverse Proxy & TLS Termination ────────────────────────────
  nginx:
    image: nginx:1.25-alpine
    container_name: nginx
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./config/nginx:/etc/nginx/conf.d:ro
      - ./certs:/etc/nginx/certs:ro
      - ./static:/var/www/static:ro
    depends_on:
      - ems-server
    networks:
      - ems-internal
      - ems-external

  # ── Observability ──────────────────────────────────────────────
  prometheus:
    image: prom/prometheus:v2.50.0
    container_name: prometheus
    restart: unless-stopped
    ports:
      - "127.0.0.1:9090:9090"
    volumes:
      - ./config/prometheus:/etc/prometheus:ro
      - prometheus-data:/prometheus
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--storage.tsdb.retention.time=30d"
    networks:
      - ems-internal

  grafana:
    image: grafana/grafana:10.3.0
    container_name: grafana
    restart: unless-stopped
    ports:
      - "127.0.0.1:3000:3000"
    volumes:
      - grafana-data:/var/lib/grafana
      - ./config/grafana/dashboards:/etc/grafana/provisioning/dashboards:ro
      - ./config/grafana/datasources:/etc/grafana/provisioning/datasources:ro
    networks:
      - ems-internal

volumes:
  emqx-data:
  influxdb-data:
  influxdb-config:
  postgres-data:
  redis-data:
  server-logs:
  prometheus-data:
  grafana-data:

networks:
  ems-internal:
    driver: bridge
    internal: true   # không truy cập internet trực tiếp
  ems-external:
    driver: bridge   # chỉ nginx expose ra ngoài
```

### Zero-downtime Update cho Business Server

```bash
# update-server.sh — không có downtime cho người dùng

set -euo pipefail

NEW_VERSION=$1

echo "Pulling new image..."
docker pull ems-business-server:${NEW_VERSION}

echo "Running DB migrations..."
# Migrate trước khi update server — backward compatible migrations
docker run --rm \
  --network ems-internal \
  --env-file .env \
  ems-business-server:${NEW_VERSION} \
  python manage.py migrate --run-syncdb

echo "Rolling update ems-server..."
docker compose up -d --no-deps --scale ems-server=2 ems-server
# Nginx tự động load balance giữa old và new instance
sleep 30  # chờ new instance healthy

echo "Remove old instance..."
docker compose up -d --no-deps --scale ems-server=1 ems-server

echo "Rolling update celery-worker..."
docker compose up -d --no-deps celery-worker

echo "Done. Verifying..."
curl -f http://localhost/health || { echo "FAIL"; exit 1; }
echo "Deploy ${NEW_VERSION} successful"
```

**Database migration strategy:**
- Luôn dùng **expand-contract pattern**: thêm cột mới trước → deploy → xóa cột cũ sau
- Không bao giờ rename hoặc xóa cột trong cùng một migration với code change
- PostgreSQL migration: Alembic (Python) với versioned scripts
- InfluxDB schema change: thêm bucket mới, backfill, không sửa bucket cũ

---

## Phần V — Observability Xuyên Tầng

### Kiến trúc thu thập metrics thống nhất

```
┌─────────────────────────────────────────────────────────────────────────┐
│  OBSERVABILITY STACK                                                     │
│                                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  Prometheus Scrape Targets                                        │  │
│  │                                                                   │  │
│  │  Tầng 2A Gateway:                                                 │  │
│  │    - http://{gw-ip}:9090/metrics  (prometheus-net)               │  │
│  │    - Metrics: ntp_drift, buffer_pending, mqtt_connected,          │  │
│  │               emmc_wear, cert_expiry, poll_success_rate           │  │
│  │                                                                   │  │
│  │  Tầng 2B EMQX OT:                                                 │  │
│  │    - http://{iiot-server}:8081/api/v5/prometheus/stats            │  │
│  │    - Metrics: connections, message_rate, bridge_status            │  │
│  │                                                                   │  │
│  │  Tầng 3 Business Server:                                          │  │
│  │    - http://localhost:8000/metrics                                │  │
│  │    - Metrics: ingestion_lag, celery_queue_depth, api_latency      │  │
│  │                                                                   │  │
│  │  Infrastructure:                                                   │  │
│  │    - node_exporter: CPU/RAM/disk cho mọi server                   │  │
│  │    - postgres_exporter: DB connections, slow queries              │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                    │ scrape mỗi 15s                                      │
│                    ▼                                                      │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  Prometheus (Business Server)                                     │  │
│  │  Retention: 30 ngày · Alertmanager → Zalo OA                     │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                    │                                                      │
│                    ▼                                                      │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  Grafana Dashboards                                               │  │
│  │  - Fleet Overview: tất cả Gateway trong 1 view                   │  │
│  │  - Gateway Detail: per-device health                              │  │
│  │  - Business Server: ingestion pipeline status                     │  │
│  │  - Alerting: tích hợp với Prometheus Alertmanager                │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

### Grafana Dashboard ưu tiên triển khai

**Dashboard 1 — Fleet Health (màn hình 24/7 tại phòng IT):**

```
┌──────────────────────────────────────────────────────────────────────┐
│  EMS FLEET STATUS                              Last update: 14:32:01  │
├────────────┬──────────┬────────────┬──────────┬───────────┬──────────┤
│ Gateway    │ Status   │ MQTT       │ Buffer   │ eMMC      │ Cert     │
├────────────┼──────────┼────────────┼──────────┼───────────┼──────────┤
│ gw-line-a  │ ● Online │ ● Connected│ 0.8%     │ ● 34%     │ 87 days  │
│ gw-line-b  │ ● Online │ ● Connected│ 0.1%     │ ● 41%     │ 87 days  │
│ gw-comp    │ ⚠ Stale  │ ● Connected│ 12.3%    │ ⚠ 74%     │ 12 days  │
│ gw-hvac    │ ✖ Offline│ ✖ Lost     │ 89.2%    │ ● 22%     │ 87 days  │
└────────────┴──────────┴────────────┴──────────┴───────────┴──────────┘
```

**Dashboard 2 — Data Pipeline Latency:**
- End-to-end latency: thời điểm poll → thời điểm ghi vào InfluxDB
- Ingestion queue depth (EMQX IT broker)
- Celery task queue depth và processing time
- InfluxDB write success rate

**Dashboard 3 — Business Server Resources:**
- PostgreSQL: connections, query latency p95/p99, table size growth
- InfluxDB: write throughput, compaction status, disk usage
- Redis: memory usage, queue depth per Celery task type

### Alerting Rules (Prometheus Alertmanager → Zalo OA)

```yaml
# alertmanager-rules.yml

groups:
  - name: gateway_critical
    rules:
      - alert: GatewayOffline
        expr: gateway_mqtt_connected == 0
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "Gateway {{ $labels.machine_id }} mất kết nối MQTT > 5 phút"

      - alert: GatewayBufferAlmostFull
        expr: gateway_buffer_fill_percent > 80
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "Gateway {{ $labels.machine_id }} buffer {{ $value }}% — EMQX OT có thể down"

      - alert: GatewayEmmcWear
        expr: gateway_emmc_wear_percent > 70
        labels:
          severity: warning
        annotations:
          summary: "Gateway {{ $labels.machine_id }} eMMC đã dùng {{ $value }}% tuổi thọ"

      - alert: GatewayCertExpiringSoon
        expr: gateway_cert_expiry_days_remaining < 14
        labels:
          severity: critical
        annotations:
          summary: "Cert Gateway {{ $labels.machine_id }} hết hạn trong {{ $value }} ngày"

  - name: business_server
    rules:
      - alert: IngestionLagHigh
        expr: ems_ingestion_lag_seconds > 30
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "Ingestion lag {{ $value }}s — có thể bị overload hoặc InfluxDB chậm"

      - alert: PostgresConnectionsHigh
        expr: pg_stat_activity_count > 80
        labels:
          severity: warning
        annotations:
          summary: "PostgreSQL connections {{ $value }} — gần giới hạn pool"

      - alert: DiskUsageHigh
        expr: (node_filesystem_size_bytes - node_filesystem_free_bytes) / node_filesystem_size_bytes > 0.85
        labels:
          severity: critical
        annotations:
          summary: "Disk {{ $labels.mountpoint }} trên {{ $labels.instance }} đã dùng {{ $value | humanizePercentage }}"
```

### Structured Logging xuyên tầng

**Nguyên tắc:** Mỗi log entry phải có `trace_id` xuyên suốt từ poll đến InfluxDB để có thể trace một measurement cụ thể.

```json
// Tầng 2A — Gateway Log
{
  "ts": "2024-11-15T14:32:01.421Z",
  "level": "INFO",
  "machine_id": "gw-line-a-001",
  "firmware_version": "1.2.0",
  "module": "SparkplugEncoder",
  "device_id": "meter-main-01",
  "seq": 142,
  "trace_id": "gw-a1b2c3d4-seq142",
  "message": "DDATA published"
}

// Tầng 3 — Business Server Log
{
  "ts": "2024-11-15T14:32:01.856Z",
  "level": "INFO",
  "service": "ingestion",
  "device_id": "meter-main-01",
  "seq": 142,
  "trace_id": "gw-a1b2c3d4-seq142",  // cùng trace_id
  "lag_ms": 435,
  "message": "Written to InfluxDB"
}
```

`trace_id` format: `{machine_id}-seq{seq_number}` — đủ unique trong 72h buffer window, không cần UUID overhead.

---

## Phần VI — Backup & Disaster Recovery

### Chiến lược 3-2-1 adapted cho On-premise SME

```
3 copies of data:
  Copy 1: InfluxDB + PostgreSQL trên Business Server (primary)
  Copy 2: Daily backup file trên NAS nội bộ (hoặc USB HDD)
  Copy 3: Weekly backup encrypted trên Cloud Storage (S3-compatible)

2 different media:
  SSD Business Server + NAS HDD

1 offsite:
  Cloud (Backblaze B2 hoặc ViettelCloud — cost-effective, đủ cho SME)
```

### Backup schedule và retention

```bash
# /etc/cron.d/ems-backup

# InfluxDB: backup mỗi 6 giờ (dữ liệu quan trọng nhất)
0 */6 * * * root /opt/ems/scripts/backup-influxdb.sh

# PostgreSQL: backup mỗi ngày lúc 2:00 AM
0 2 * * * root /opt/ems/scripts/backup-postgres.sh

# Gateway config sync: sau mỗi Config Channel update (triggered by Ansible)
# (không cron — triggered event)
```

```bash
# backup-influxdb.sh
#!/bin/bash

BACKUP_DIR="/opt/ems/backup/influxdb"
DATE=$(date +%Y%m%d_%H%M)
RETENTION_DAYS=7

# InfluxDB backup API
influx backup "${BACKUP_DIR}/${DATE}" \
  --host http://localhost:8086 \
  --token "${INFLUXDB_TOKEN}"

# Compress
tar -czf "${BACKUP_DIR}/${DATE}.tar.gz" "${BACKUP_DIR}/${DATE}"
rm -rf "${BACKUP_DIR}/${DATE}"

# Prune old backups
find "${BACKUP_DIR}" -name "*.tar.gz" -mtime +${RETENTION_DAYS} -delete

# Upload to offsite (nếu network available)
rclone copy "${BACKUP_DIR}/${DATE}.tar.gz" b2:ems-backup/influxdb/ \
  --no-traverse --log-level NOTICE 2>/dev/null || true
  # || true: không fail cron nếu internet không có

echo "InfluxDB backup ${DATE} completed"
```

### RPO / RTO targets

| Thành phần | RPO (mất tối đa) | RTO (phục hồi trong) | Phương pháp |
|---|---|---|---|
| Gateway firmware | 0 (atomic OTA) | 2 phút (swap binary) | Atomic in-place + rollback script |
| Gateway config | 0 (Config Channel version) | 5 phút (Ansible re-apply) | Ansible playbook |
| InfluxDB data | 6 giờ | 30 phút | Restore từ backup |
| PostgreSQL data | 24 giờ | 20 phút | pg_restore |
| Business Server | 0 (container image) | 5 phút | `docker compose up` |
| EMQX config | 0 (config file trong git) | 10 phút | `docker compose up` |

**Disaster Recovery Runbook (lưu trong Wiki nội bộ):**

```
Kịch bản 1: Business Server SSD chết
  1. Thay SSD mới → Ubuntu install (~15 phút)
  2. Restore từ NAS: docker volumes + config files (~20 phút)
  3. docker compose up -d (~5 phút)
  4. Restore InfluxDB từ backup (~10 phút)
  5. Verify /health → OK
  Tổng: ~50 phút. Gateway vẫn chạy và buffer trong suốt thời gian này.

Kịch bản 2: Gateway bị hỏng phần cứng
  1. Lấy Cold Standby từ tủ
  2. Cắm vào switch, boot → OOBE Wizard xuất hiện
  3. Kỹ sư hoàn thành Wizard (5 phút)
  4. Ansible playbook deploy-gateway.yml (5 phút)
  5. Replay 72h buffer
  Tổng: ~10 phút. Không mất dữ liệu nếu trong 72h.
```

---

## Phần VII — Network Topology & Security Hardening

### Network Segmentation chi tiết

```
Internet (nếu cần offsite backup, VPN remote)
    │ WireGuard VPN (chỉ kỹ sư được cấp phép)
    │
┌───▼──────────────────────────────────────────────────────────────┐
│  MẠNG IT (VLAN 10 — 192.168.1.0/24)                              │
│                                                                   │
│  Business Server (192.168.1.10)                                   │
│    - Port 443: HTTPS Dashboard (nginx TLS)                        │
│    - Port 8443: step-ca internal CA                               │
│    - Port 9091: Prometheus scrape endpoint (internal only)        │
│    - Port 3000: Grafana (internal only)                           │
│                                                                   │
│  Firewall IT→OT: DENY tất cả chiều vào OT                        │
│  Firewall OT→IT: ALLOW chỉ port 8883 (MQTT TLS bridge)           │
└───────────────────────────┬──────────────────────────────────────┘
                            │ DMZ (port 8883 only, TLS only)
┌───────────────────────────▼──────────────────────────────────────┐
│  MẠNG OT (VLAN 20 — 192.168.10.0/24)                             │
│                                                                   │
│  IIoT Server (192.168.10.10) [Tầng 2B — G3]                      │
│    - Port 1884: EMQX OT (chỉ nhận từ Gateway trong VLAN 20)      │
│    - Port 18083: EMQX Dashboard (internal OT only)               │
│    - Port 9090: Prometheus metrics (scrape từ Business Server)    │
│                                                                   │
│  Gateway Line A (192.168.10.101) [Tầng 2A]                        │
│    - Port 8080: AdminUI + Health endpoint                         │
│    - Port 9090: Prometheus metrics                                │
│    - Port 1883: Mosquitto local (loopback only, không expose)     │
│                                                                   │
│  Gateway Line B (192.168.10.102)                                  │
│    ...                                                            │
│                                                                   │
│  Firewall OT: DENY Internet access (OT không được ra internet)   │
│  Firewall OT: DENY cross-Gateway traffic (isolation)             │
└───────────────────────────┬──────────────────────────────────────┘
                            │ RS485 / Modbus TCP / BACnet
┌───────────────────────────▼──────────────────────────────────────┐
│  THIẾT BỊ TRƯỜNG (Tầng 1)                                         │
│  Smart meter · PLC · VFD · BACnet controller                     │
│  Subnet riêng nếu có nhiều tủ điện: 192.168.10.128/25            │
└──────────────────────────────────────────────────────────────────┘
```

### Firewall rules (iptables / nftables trên Linux gateway)

```bash
# /etc/nftables.d/ems-ot-rules.conf
# Áp dụng trên IIoT Server (Tầng 2B) nếu làm luôn vai trò firewall

table inet filter {
  chain forward {
    type filter hook forward priority 0; policy drop;

    # Cho phép OT → IT (MQTT Bridge chỉ)
    iifname "eth-ot" oifname "eth-it" tcp dport 8883 accept
    iifname "eth-ot" oifname "eth-it" ct state established,related accept

    # Chặn IT → OT (không có chiều ngược trực tiếp)
    iifname "eth-it" oifname "eth-ot" drop

    # Log dropped packets để audit
    log prefix "EMS-FIREWALL-DROP: " drop
  }
}
```

### TLS Certificate Hierarchy

```
Root CA (step-ca trên Business Server)
  │ Tự ký, 10 năm
  │ Private key: offline, chỉ dùng để ký Intermediate CA
  │
  ├── Intermediate CA "EMS-OT-CA" (2 năm)
  │     │ Dùng để ký cert cho Gateway và EMQX OT
  │     │
  │     ├── gw-line-a-001.factory.local (90 ngày, auto-renew)
  │     ├── gw-line-b-001.factory.local (90 ngày, auto-renew)
  │     └── emqx-ot.factory.local (1 năm)
  │
  └── Intermediate CA "EMS-IT-CA" (2 năm)
        │ Dùng để ký cert cho Business Server services
        │
        ├── ems-dashboard.factory.local (1 năm)
        └── emqx-it.factory.local (1 năm)
```

---

## Phần VIII — Lộ trình Triển khai Thực tế (Giai đoạn → Giai đoạn)

### G1 — Triển khai ban đầu (Tuần 1–2)

**Mục tiêu:** Hệ thống hoạt động, dữ liệu vào InfluxDB, dashboard cơ bản.

```
Ngày 1–2: Chuẩn bị
  □ Cài Ubuntu 22.04 LTS trên Business Server
  □ Clone repo ems-deploy, cấu hình inventory
  □ Cài step-ca, tạo Root CA và Intermediate CA
  □ Tạo cert cho EMQX IT và Business Server

Ngày 3–4: Deploy Business Server
  □ docker compose up (EMQX IT + InfluxDB + PostgreSQL + Redis)
  □ Verify tất cả container healthy
  □ Deploy ems-server (FastAPI) + celery
  □ Verify /health API trả 200

Ngày 5–6: Deploy Gateway đầu tiên (Line A)
  □ Flash Ubuntu 22.04 lên Industrial PC
  □ ansible-playbook deploy-gateway.yml --limit gw-line-a-001
  □ OOBE Wizard: đổi mật khẩu, IP, MQTT broker, NTP
  □ Verify Mosquitto bridge connected → EMQX OT → EMQX IT
  □ Verify DBIRTH nhận được tại Business Server
  □ Verify DDATA vào InfluxDB

Ngày 7–8: Đấu nối thiết bị
  □ Cắm RS485 vào đồng hồ đo tổng
  □ Cấu hình devices.json (slave ID, register map, CT/PT ratio)
  □ Kiểm tra Data Monitor: tag quality = Good
  □ Verify Shadow Bill đầu tiên

Ngày 9–10: Monitoring setup
  □ Prometheus scrape config
  □ Grafana Fleet Health dashboard
  □ Test alert: ngắt MQTT → verify Zalo nhận alert trong 5 phút
  □ Backup script chạy thử
```

### G2 — Mở rộng (Tháng 2–3)

```
  □ Deploy thêm Gateway cho các dây chuyền
  □ Bật ConfigChannel (feature flag)
  □ Fleet OTA: test update firmware 1 Gateway trước
  □ Auto cert renewal: verify step ca renew hoạt động
  □ Canary deploy pipeline trong CI/CD
  □ Grafana dashboard đầy đủ cho tất cả Gateway
  □ Ansible role cho tất cả Gateway (không còn setup thủ công)
```

### G3 — Production-grade (Tháng 4+)

```
  □ Tách Tầng 2B ra server riêng (nếu > 5 Gateway)
  □ Active-Passive cho Business Server (Keepalived)
  □ OpenTelemetry tracing
  □ Command Channel (sau 6 tháng ổn định)
  □ Fleet OTA canary rollout tự động
  □ Offsite backup tự động lên Cloud
```

---

## Phần IX — Ma trận Rủi ro & Giảm thiểu

| Rủi ro | Xác suất | Impact | Giảm thiểu |
|---|---|---|---|
| Gateway firmware crash loop sau OTA | Trung bình | Cao | Health check 120s + rollback tự động |
| eMMC hỏng đột ngột | Thấp | Rất cao | eMMC wear monitor + Cold Standby sẵn sàng |
| SQLite corrupt sau mất điện | Trung bình | Trung bình | Corruption auto-recovery + tmpfs buffer |
| Cert hết hạn gây mất bridge | Trung bình | Cao | Auto-renew 30 ngày trước + alert 14 ngày |
| Business Server disk full | Trung bình | Cao | Alert tại 85% + InfluxDB retention policy |
| Config sai gây 100% timeout | Trung bình | Cao | Grace Period 5 phút + auto-rollback |
| Ransomware qua USB | Thấp | Rất cao | USB storage lockdown |
| NTP drift làm sai Shadow Bill | Thấp | Cao | NTP watchdog + RTC sync mỗi 15 phút |
| Human error khi SSH tay | Cao | Trung bình | Ansible-only policy + auditd SSH log |

---

## Phần X — Checklist Vận hành Hằng ngày

### Dashboard kiểm tra sáng (5 phút, trước 8:00)

```
□ Fleet Status: tất cả Gateway ● Online?
□ Buffer fill: không có Gateway > 20%?
□ Cert expiry: không có cert < 30 ngày?
□ eMMC wear: không có Gateway > 70%?
□ Backup: backup đêm qua thành công?
□ Alerts: không có unresolved CRITICAL alert?
```

### Maintenance window hằng tuần (Thứ 7, 22:00–23:00)

```
□ Kiểm tra NuGet package updates (dotnet-outdated)
□ docker compose pull → kiểm tra image mới
□ Xem xét Prometheus alerts tuần qua
□ Verify backup restore (1 lần/tháng)
□ Test Cold Standby (1 lần/quý): bật Gateway backup, verify hoạt động
```

---

## Tổng kết

Kiến trúc 5 tầng hiện tại có nền tảng vững chắc. Ba bổ sung quan trọng nhất để đạt production-grade:

**1. Ansible IaC (ưu tiên cao nhất)** — Thiếu cái này thì mọi thiết kế phần mềm tốt đến đâu cũng bị phá vỡ bởi "configuration drift" sau 3–6 tháng vận hành. Mỗi Gateway một chút khác nhau là nguyên nhân số 1 của bug khó tái hiện trong môi trường industrial.

**2. Docker Compose cho Tầng 3 với zero-downtime update** — Không có container thì update Business Server = downtime = kỹ sư làm ngoài giờ. Với Docker Compose + rolling update script, update trở thành việc bình thường trong giờ làm việc.

**3. Observability xuyên tầng với `trace_id`** — Khi kỹ sư nhận báo cáo "dữ liệu bị sai lúc 14:32", họ cần trace ngay: sai tại Gateway (tag quality Bad?), sai tại bridge (message dropped?), hay sai tại ingestion (parse lỗi?)? Không có `trace_id` thống nhất thì tìm bug mất hàng giờ.

---

*Tài liệu này là lớp Deploy bổ sung cho EMS_IIoTGateway_Tier2A_Module_Decomposition_v1.8 và EMS_On-premise_SME_Thiet_ke_He_thong_v2.4. Không thay thế hai tài liệu trên — bổ sung layer cuối cùng từ code đến production.*
