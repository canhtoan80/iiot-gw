# Production Deployment Guide: EMS IIoT Gateway (Tier 2A)

This document provides comprehensive instructions for deploying the EMS IIoT Gateway in industrial environments. It is designed for engineers implementing the system on-site, ensuring 24/7 reliability, security, and maintainability.

---

## (A) Overall Deployment Architecture

The gateway acts as the secure bridge between the **Field Layer** (OT) and the **Business Server** (IT).

### Architecture Diagram (ASCII)
```text
  FIELD LAYER (OT)            EDGE LAYER (Tier 2A)           BUSINESS SERVER (IT)
+------------------+        +-----------------------+        +--------------------+
| Energy Meters    | RS-485 | [Modbus RTU Adapter]  |        |                    |
| (Modbus RTU)     |------->| [Polling Engine    ]  |        |    EMQX Broker     |
+------------------+        | [Edge Rule Engine  ]  |  MQTT  |  (Sparkplug B Hub) |
                            | [Quality Checker   ]  | (TLS)  |                    |
+------------------+  LAN   | [Sparkplug Encoder ] |------->|         +          |
| PLCs / Sensors   |------->| [Local Buffer (SQL)]  |  (4G)  |   Business App     |
| (Modbus TCP)     |        | [Admin/Watchdog    ]  |        |                    |
+------------------+        +-----------------------+        +--------------------+
                                        ^
                                        | (Localhost)
                                  +------------+
                                  | Mosquitto  |
                                  | (Bridge)   |
                                  +------------+
```

### Data Flow
1. **Southbound:** Data is polled via RS-485 (RTU) or Ethernet (TCP).
2. **Processing:** Values are normalized, quality-checked, and encoded into Sparkplug B.
3. **Persistence:** Payloads are buffered in SQLite (72h retention) to prevent loss during outages.
4. **Northbound:** Data is published to a local Mosquitto broker, which bridges to the remote EMQX broker via TLS.

---

## (B) Packaging & Build (DevOps)

### Build Strategy
We use **.NET Self-Contained Publishing** to eliminate dependency on a pre-installed .NET runtime on the field IPC.

**Build Command:**
```bash
dotnet publish src/EMS.Gateway.Host/EMS.Gateway.Host.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -o ./publish
```

### Packaging: .deb vs. Docker
- **Recommendation:** **.deb Package** for primary deployment.
- **Reasoning:** In low-power edge IPCs, native systemd services offer lower overhead, easier access to hardware (serial ports, watchdog), and simpler integration with existing Linux monitoring tools.

---

## (C) Linux Installation (System Engineer)

### Service Configuration (systemd)
Create `/etc/systemd/system/ems-gateway.service`:

```ini
[Unit]
Description=EMS IIoT Gateway Service
After=network.target mosquitto.service
Wants=mosquitto.service

[Service]
Type=notify
WorkingDirectory=/opt/ems-gateway
ExecStart=/opt/ems-gateway/EMS.Gateway.Host
Restart=always
RestartSec=10
User=ems-admin
Group=ems-admin

# Security Hardening
PrivateTmp=true
NoNewPrivileges=true

# Resource Limits
MemoryMax=512M
CPUQuota=50%

# Hardware Watchdog Integration
WatchdogSec=30

[Install]
WantedBy=multi-user.target
```

### Log Rotation
Configure `/etc/logrotate.d/ems-gateway`:
```text
/var/log/ems-gateway/*.log {
    daily
    rotate 7
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
}
```

---

## (D) Network Configuration (Network Engineer)

### 4G/LTE Setup (M2M SIM)
Use `nmcli` for persistent 4G connection:
```bash
nmcli connection add type gsm ifname cdc-wdm0 con-name m2m-connection apn m2m.apn.name
nmcli connection modify m2m-connection gsm.home-only yes
nmcli connection up m2m-connection
```

### Firewall Rules (ufw)
```bash
ufw default deny incoming
ufw allow ssh # Limited to VPN range if possible
ufw allow 8080/tcp # Local Admin UI
ufw allow out 8883/tcp # MQTT TLS to Cloud
ufw allow out 53/udp # DNS
ufw allow out 123/udp # NTP
ufw enable
```

---

## (E) MQTT Sparkplug B Configuration

### Mosquitto Bridge Config (`/etc/mosquitto/conf.d/bridge.conf`)
```text
connection cloud-bridge
address business-server.com:8883
remote_clientid gateway-001
bridge_cafile /etc/ems-gateway/certs/ca.crt
bridge_certfile /etc/ems-gateway/certs/gw.crt
bridge_keyfile /etc/ems-gateway/certs/gw.key
topic spBv1.0/# out 1
topic spBv1.0/+/DCMD/gateway-001 in 1
cleansession false
notifications true
start_type automatic
```

### Reconnect Strategy
- **Firmware Level:** Managed by MQTTnet with exponential backoff (1s -> 2s -> 4s ... 30s max).
- **Network Level:** Hardware watchdog resets the system if the "Cloud Connected" heartbeats fail for > 15 minutes.

---

## (F) Modbus Field Wiring (Field Engineer)

### RS-485 Best Practices
- **Wiring:** Use Shielded Twisted Pair (STP). Connect A(+), B(-), and Signal Ground (GND).
- **Termination:** Enable 120Ω resistor on the **last** device of the bus.
- **Biasing:** Ensure the gateway or first device provides fail-safe biasing.

### Timing Tuning
- **Inter-frame Delay:** 3.5 character times (min).
- **Timeout:** 500ms - 1000ms depending on cable length and electromagnetic interference.
- **Retry:** Max 3 attempts before flagging `TagQuality.Bad`.

---

## (G) Security Hardening (Security Engineer)

1. **Physical Security:** Disable USB boot and BIOS/UEFI console if possible.
2. **SSH Hardening:**
   - Use SSH Keys only (`PasswordAuthentication no`).
   - Change default port 22 to a non-standard port.
3. **User Privileges:** Run the gateway process under a dedicated `ems-admin` user with restricted sudo access.
4. **Certificate Management:** Automated renewal via `step-ca` or similar ACME client with a 30-day expiry check.

---

## (H) Reliability & Fault Handling

| Failure | Response Strategy |
|---|---|
| **MQTT Outage** | Switch to Local Buffer (SQLite). Max 72 hours storage. |
| **Power Loss** | `tmpfs` buffer lost; last 5s of data. System auto-restarts on power return. |
| **Process Hang** | Systemd `WatchdogSec` triggers restart. Hardware watchdog triggers reset. |
| **CRC/Modbus Error** | Immediate retry. Persistent errors flag tags as `Bad`. |

---

## (I) Monitoring & Logging

- **Local Logs:** `/var/log/ems-gateway/app-.log` (Structured JSON).
- **Health Endpoint:** `GET http://localhost:8080/health/detail`.
- **Metrics:** Prometheus endpoint on port 9090 (CPU, RAM, Buffer Fill %, MQTT Status).

---

## (J) OTA / Update Strategy

1. **Preparation:** Download bundle to `/tmp/ems-update/`.
2. **Verification:** Check SHA-256 and Ed25519 signature.
3. **Execution:**
   - Stop service: `systemctl stop ems-gateway`.
   - Backup current: `cp -r /opt/ems-gateway /opt/ems-gateway.bak`.
   - Replace binaries.
   - Start service: `systemctl start ems-gateway`.
4. **Rollback:** If health check fails within 5 minutes, restore `/opt/ems-gateway.bak` and restart.

---

## (K) Implementation Checklist

### Pre-Deployment
- [ ] Verify 4G signal strength (RSSI > -75dBm).
- [ ] Check RS-485 termination (120Ω on last device).
- [ ] Validate Gateway certificates against Cloud CA.
- [ ] Sync system clock via NTP.

### Post-Deployment
- [ ] Confirm `DBIRTH` received on EMQX dashboard.
- [ ] Verify data quality is `Good` for all critical tags.
- [ ] Test manual power cycle and verify auto-start.

---

## (L) Test Plan (QA)

1. **Network Chaos Test:** Disconnect 4G for 1 hour; verify SQLite buffer fills and replays correctly upon reconnection.
2. **Field Simulation:** Disconnect Modbus cable; verify tags transition to `Bad` with reason "Timeout".
3. **Recovery Test:** Kill the process (`kill -9`); verify systemd restarts it within 10 seconds.
4. **Load Test:** Poll 100 devices at 1s intervals; monitor CPU usage (should be < 30%).
