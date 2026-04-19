using System.Text.Json;

namespace EMS.Gateway.DeviceTemplate.ConfigHistory;

/// <summary>
/// Logger for configuration change history.
/// </summary>
public class ConfigHistoryLogger
{
    private const string LogPath = "/var/log/ems-gateway/config-history.ndjson";
    private const int MaxEntries = 30;

    /// <summary>
    /// Logs a configuration change event.
    /// </summary>
    public static async Task LogAsync(string eventType, string configHash, string changedBy, bool success)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = eventType,
            config_hash = configHash,
            changed_by = changedBy,
            success = success
        };

        string json = JsonSerializer.Serialize(entry);

        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Simple rotation: read all, keep last N, write back
            List<string> lines = new List<string>();
            if (File.Exists(LogPath))
            {
                lines = (await File.ReadAllLinesAsync(LogPath)).ToList();
            }

            lines.Add(json);

            if (lines.Count > MaxEntries)
            {
                lines = lines.Skip(lines.Count - MaxEntries).ToList();
            }

            await File.WriteAllLinesAsync(LogPath, lines);
        }
        catch
        {
            // Fail silently or log to console as per requirements
            Console.WriteLine($"Failed to log config history: {json}");
        }
    }
}
