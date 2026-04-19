using System.Threading.Channels;
using System.Buffers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.LocalBuffer;

public record LocalBufferOptions(
    string SqlitePath = "buffer.db",
    string TmpfsMountPath = "tmpfs",
    int SyncIntervalMs = 5000,
    int RetentionHours = 72,
    int ChannelCapacity = 10000,
    int ReplayRateLimitPerSecond = 500,
    int ReplayBurstSize = 1000
);

public class LocalBufferService : BackgroundService, ILocalBuffer
{
    private readonly ILogger<LocalBufferService> _logger;
    private readonly LocalBufferOptions _options;
    private readonly Channel<SparkplugPayloadDto> _channel;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly IInternalEventBus _eventBus;
    private readonly string _connectionString;

    private long _pendingCount = 0;

    public LocalBufferService(
        ILogger<LocalBufferService> logger,
        IOptions<LocalBufferOptions> options,
        IMqttPublisher mqttPublisher,
        IInternalEventBus eventBus)
    {
        _logger = logger;
        _options = options.Value;
        _mqttPublisher = mqttPublisher;
        _eventBus = eventBus;

        _channel = Channel.CreateBounded<SparkplugPayloadDto>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _connectionString = $"Data Source={_options.SqlitePath};";
        
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS buffer (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id       TEXT NOT NULL,
                message_type    INTEGER NOT NULL,
                seq_number      INTEGER NOT NULL,
                timestamp_utc_ns INTEGER NOT NULL,
                payload         BLOB NOT NULL,
                enqueued_at     INTEGER NOT NULL,
                UNIQUE(device_id, seq_number, timestamp_utc_ns)
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp ON buffer(timestamp_utc_ns);
        ";
        command.ExecuteNonQuery();
        
        // Initial count
        command.CommandText = "SELECT COUNT(*) FROM buffer";
        _pendingCount = (long)command.ExecuteScalar()!;
    }

    public async Task EnqueueAsync(SparkplugPayloadDto payload, CancellationToken ct)
    {
        if (await _channel.Writer.WaitToWriteAsync(ct))
        {
            await _channel.Writer.WriteAsync(payload, ct);
        }
    }

    public async Task ReplayAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting buffer replay...");
        
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            if (!_mqttPublisher.IsConnected) break;

            var batch = new List<SparkplugPayloadDto>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, device_id, message_type, seq_number, timestamp_utc_ns, payload FROM buffer ORDER BY timestamp_utc_ns ASC LIMIT 100";
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    batch.Add(new SparkplugPayloadDto(
                        reader.GetString(1),
                        (SparkplugMessageType)reader.GetInt32(2),
                        (byte[])reader.GetValue(5),
                        reader.GetInt64(4),
                        reader.GetInt32(3)
                    ));
                }
            }

            if (batch.Count == 0) break;

            foreach (var payload in batch)
            {
                if (await _mqttPublisher.PublishAsync(payload, ct))
                {
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM buffer WHERE device_id = @deviceId AND seq_number = @seq AND timestamp_utc_ns = @ts";
                    deleteCmd.Parameters.AddWithValue("@deviceId", payload.DeviceId);
                    deleteCmd.Parameters.AddWithValue("@seq", payload.SeqNumber);
                    deleteCmd.Parameters.AddWithValue("@ts", payload.TimestampUtcNs);
                    await deleteCmd.ExecuteNonQueryAsync(ct);
                    Interlocked.Decrement(ref _pendingCount);
                }
                else
                {
                    // MQTT failed again, stop replay
                    return;
                }
                
                // Simplified rate limit
                await Task.Delay(1000 / _options.ReplayRateLimitPerSecond, ct);
            }
        }
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        long cutoff = DateTimeOffset.UtcNow.AddHours(-_options.RetentionHours).ToUnixTimeSeconds();
        command.CommandText = "DELETE FROM buffer WHERE enqueued_at < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff);
        int deleted = await command.ExecuteNonQueryAsync(ct);
        if (deleted > 0)
        {
            _logger.LogInformation("Pruned {Count} old records from buffer", deleted);
            // Update count
            command.CommandText = "SELECT COUNT(*) FROM buffer";
            _pendingCount = (long)await command.ExecuteScalarAsync(ct)!;
        }
    }

    public long GetPendingCount() => _pendingCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Local Buffer Service starting...");

        // Start consumer loop
        _ = Task.Run(() => ConsumerLoop(stoppingToken), stoppingToken);

        // Prune timer
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            await PruneAsync(stoppingToken);
        }
    }

    private async Task ConsumerLoop(CancellationToken ct)
    {
        var batch = new List<SparkplugPayloadDto>();
        var lastFlush = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await _channel.Reader.WaitToReadAsync(ct))
                {
                    while (_channel.Reader.TryRead(out var payload))
                    {
                        batch.Add(payload);
                        if (batch.Count >= 100) break;
                    }
                }

                if (batch.Count > 0 && (DateTimeOffset.UtcNow - lastFlush).TotalMilliseconds >= _options.SyncIntervalMs || batch.Count >= 100)
                {
                    await FlushBatchToSqlite(batch);
                    batch.Clear();
                    lastFlush = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in buffer consumer loop");
            }
        }
        
        // Final flush
        if (batch.Count > 0) await FlushBatchToSqlite(batch);
    }

    private async Task FlushBatchToSqlite(List<SparkplugPayloadDto> batch)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        
        try
        {
            foreach (var payload in batch)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT OR IGNORE INTO buffer (device_id, message_type, seq_number, timestamp_utc_ns, payload, enqueued_at)
                    VALUES (@deviceId, @type, @seq, @ts, @payload, @enqueuedAt)
                ";
                command.Parameters.AddWithValue("@deviceId", payload.DeviceId);
                command.Parameters.AddWithValue("@type", (int)payload.MessageType);
                command.Parameters.AddWithValue("@seq", payload.SeqNumber);
                command.Parameters.AddWithValue("@ts", payload.TimestampUtcNs);
                command.Parameters.AddWithValue("@payload", payload.ProtobufPayload);
                command.Parameters.AddWithValue("@enqueuedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await command.ExecuteNonQueryAsync();
                Interlocked.Increment(ref _pendingCount);
            }
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush batch to SQLite");
            await transaction.RollbackAsync();
        }
    }
}
