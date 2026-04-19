## Module 2A-8 — `EMS.Gateway.LocalBuffer`

```
You are a senior .NET 8 engineer with expertise in embedded storage and reliability engineering.
Modules 2A-1 through 2A-7 are complete.

## Task
Implement `EMS.Gateway.LocalBuffer` — the reliability layer ensuring zero data loss during
MQTT outages. Buffers up to 72 hours of Sparkplug B payloads. This is a Worker Service (.NET 8).

## Project references
- EMS.Gateway.Contracts
- NuGet: Microsoft.Data.Sqlite

## Class to implement: LocalBufferService : ILocalBuffer, IHostedService

### Architecture: Two-layer write path (Flash-Wear protection)
```
Producer (SparkplugEncoder)
    ↓ EnqueueAsync
System.Threading.Channels.BoundedChannel<byte[]>  (RAM, capacity=10000)
    ↓ consumer loop (single Task)
tmpfs RAM buffer (/dev/shm/ems-buffer/)
    ↓ flush every sync_interval_ms (default 5000ms)
SQLite WAL mode (eMMC/SSD: /var/lib/ems-gateway/buffer.db)
```

### SQLite schema
```sql
CREATE TABLE IF NOT EXISTS buffer (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       TEXT NOT NULL,
    seq_number      INTEGER NOT NULL,
    timestamp_utc_ns INTEGER NOT NULL,
    payload         BLOB NOT NULL,
    enqueued_at     INTEGER NOT NULL,  -- Unix epoch seconds
    UNIQUE(device_id, seq_number, timestamp_utc_ns) -- deduplication key
);
CREATE INDEX IF NOT EXISTS idx_timestamp ON buffer(timestamp_utc_ns);
```

### EnqueueAsync
```csharp
// Use ArrayPool<byte> for payload bytes (zero GC allocation)
// Bounded channel: BoundedChannelFullMode.Wait (backpressure — don't drop)
// Channel consumer loop: batch write to SQLite (batch size = 100 or 500ms timeout)
// WAL checkpoint triggered by flush timer, not per-write
```

### ReplayAsync (rate-limited)
```csharp
// ONLY called when MqttConnectionRestoredEvent received
// Rate limit: FixedWindowRateLimiter at ReplayRateLimitPerSecond (default 500 msg/s)
// Read SQLite ORDER BY timestamp_utc_ns ASC, batch size 100
// Publish via IMqttPublisher — on fail: circuit breaker (3 consecutive fails → stop, wait)
// Delete successfully published records from SQLite
// Priority: replay DBIRTH messages before DDATA messages
```

### PruneAsync (every 15 minutes)
```csharp
// DELETE FROM buffer WHERE enqueued_at < (UtcNow - RetentionHours * 3600)
// Default RetentionHours = 72
```

### SQLite Corruption Auto-Recovery
```csharp
private async Task ExecuteWithCorruptionRecoveryAsync(Func<Task> operation)
{
    try { await operation(); }
    catch (SqliteException ex) when (
        ex.SqliteErrorCode == 11 ||  // SQLITE_CORRUPT
        ex.SqliteErrorCode == 26 ||  // SQLITE_NOTADB
        ex.SqliteErrorCode == 10)    // SQLITE_IOERR
    {
        // 1. SqliteConnection.ClearAllPools() — release all file handles
        // 2. File.Move(dbPath, $"{dbPath}.corrupted_{epoch}") — rename
        // 3. File.Delete(walPath), File.Delete(shmPath) — cleanup WAL
        // 4. InitializeDatabaseAsync() — create fresh empty DB
        // 5. Publish BufferCorruptedEvent with estimated lost records
        // 6. Log CRITICAL
        // 7. Reset _pendingCount = 0
    }
}
```

### Graceful shutdown (IHostApplicationLifetime)
```
1. Stop accepting new Enqueue requests
2. Drain Channel: wait for consumer loop to process all pending items
3. Flush remaining tmpfs → SQLite (WAL checkpoint)
4. Close SQLite connection cleanly
5. Signal completion
TimeoutStopSec=60 in systemd unit
```

## Configuration (IOptions<LocalBufferOptions>)
```csharp
public record LocalBufferOptions(
    string SqlitePath,            // /var/lib/ems-gateway/buffer.db
    string TmpfsMountPath,        // /dev/shm/ems-buffer
    int SyncIntervalMs,           // 5000
    int RetentionHours,           // 72
    int ChannelCapacity,          // 10000
    int ReplayRateLimitPerSecond, // 500
    int ReplayBurstSize           // 1000
);
```

## Unit tests required
1. Enqueue → SQLite: payload persisted after sync_interval
2. Replay: records replayed in timestamp order (ascending)
3. Replay: rate limiter enforces max messages per second
4. Prune: records older than RetentionHours deleted
5. Replay: DBIRTH replayed before DDATA for same device
6. Corruption recovery: SQLITE_CORRUPT → renamed file, fresh DB created, event published
7. Corruption recovery: recovery continues polling (no crash loop)
8. Graceful shutdown: all Channel items flushed before exit (no data loss)
9. Deduplication: same (device_id, seq, timestamp) not inserted twice

## Deliverable
Complete LocalBufferService. ArrayPool<byte> used. WAL mode enabled. Recovery tested.
```

---