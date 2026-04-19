using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EMS.Gateway.Contracts;
using EMS.Gateway.DeviceTemplate.Coalescing;
using EMS.Gateway.DeviceTemplate.ConfigHistory;
using EMS.Gateway.DeviceTemplate.Validators;
using FluentValidation;

namespace EMS.Gateway.DeviceTemplate;

/// <summary>
/// Implementation of the device template repository with hot-reload and coalescing.
/// </summary>
public class DeviceTemplateRepository : IDeviceTemplateRepository
{
    private const string ConfigPath = "/etc/ems-gateway/devices.json";
    private const string BackupPath = "/etc/ems-gateway/devices.json.bak";
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(5);

    private readonly string _configPath;
    private readonly string _backupPath;

    private ImmutableDictionary<string, DeviceTemplateDto> _templates = ImmutableDictionary<string, DeviceTemplateDto>.Empty;
    private ImmutableDictionary<string, IReadOnlyList<CoalescedBlockDto>> _coalescedBlocks = ImmutableDictionary<string, IReadOnlyList<CoalescedBlockDto>>.Empty;
    
    private readonly IInternalEventBus _eventBus;
    private readonly DeviceTemplateDtoValidator _validator;

    private CancellationTokenSource? _rollbackCts;

    public DeviceTemplateRepository(IInternalEventBus eventBus, string? configPath = null, string? backupPath = null)
    {
        _eventBus = eventBus;
        _validator = new DeviceTemplateDtoValidator();
        _configPath = configPath ?? ConfigPath;
        _backupPath = backupPath ?? BackupPath;
    }

    public DeviceTemplateDto? GetTemplate(string deviceId)
    {
        return _templates.TryGetValue(deviceId, out var template) ? template : null;
    }

    public IReadOnlyList<DeviceTemplateDto> GetAllTemplates()
    {
        return _templates.Values.ToImmutableList();
    }

    public IReadOnlyList<CommandWhitelistEntryDto> GetCommandWhitelist(string deviceId)
    {
        return _templates.TryGetValue(deviceId, out var template) ? template.CommandWhitelist : Array.Empty<CommandWhitelistEntryDto>();
    }

    public async Task<ReloadResult> ReloadAsync(string newConfigJson)
    {
        try
        {
            // Cancel any existing pending rollback
            await CommitConfigAsync();

            // 1. Parse and validate
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var newTemplatesList = JsonSerializer.Deserialize<List<DeviceTemplateDto>>(newConfigJson, options);
            
            if (newTemplatesList == null)
            {
                return new ReloadResult(false, "Invalid JSON format.", null);
            }

            // Check for unique device IDs
            if (newTemplatesList.GroupBy(x => x.DeviceId).Any(g => g.Count() > 1))
            {
                return new ReloadResult(false, "Duplicate Device IDs found in configuration.", null);
            }

            foreach (var template in newTemplatesList)
            {
                var validationResult = await _validator.ValidateAsync(template);
                if (!validationResult.IsValid)
                {
                    return new ReloadResult(false, $"Validation failed for device {template.DeviceId}: {string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage))}", null);
                }
            }

            // 2. Save backup of current if exists
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, _backupPath, true);
            }

            // 3. Pre-compile NCalc expressions (already validated in validator, but we could cache them here if needed)
            // In this implementation, we just ensure they are valid.

            // 4. Build CoalescedBlocks
            var newBlocks = new Dictionary<string, IReadOnlyList<CoalescedBlockDto>>();
            var newTemplates = new Dictionary<string, DeviceTemplateDto>();
            
            foreach (var template in newTemplatesList)
            {
                var blocks = RegisterCoalescingBuilder.Build(template.Registers);
                newBlocks[template.DeviceId] = blocks;
                newTemplates[template.DeviceId] = template;
            }

            // 5. Atomic swap
            Interlocked.Exchange(ref _templates, newTemplates.ToImmutableDictionary());
            Interlocked.Exchange(ref _coalescedBlocks, newBlocks.ToImmutableDictionary());

            // Save new config to disk
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(_configPath, newConfigJson);

            string hash = ComputeHash(newConfigJson);

            // 6. Publish Event
            await _eventBus.PublishAsync(new ConfigReloadRequestedEvent(hash, DateTimeOffset.UtcNow));

            // Log history
            await ConfigHistoryLogger.LogAsync("RELOAD", hash, "system", true);

            // Start auto-rollback timer
            StartRollbackTimer(DefaultGracePeriod);

            return new ReloadResult(true, null, hash);
        }
        catch (Exception ex)
        {
            await ConfigHistoryLogger.LogAsync("RELOAD", "N/A", "system", false);
            return new ReloadResult(false, $"Reload failed: {ex.Message}", null);
        }
    }

    public async Task<bool> RollbackToBackupAsync()
    {
        if (!File.Exists(_backupPath)) return false;

        try
        {
            string backupJson = await File.ReadAllTextAsync(_backupPath);
            
            // We use a internal reload that doesn't trigger another backup or rollback timer
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var newTemplatesList = JsonSerializer.Deserialize<List<DeviceTemplateDto>>(backupJson, options);
            if (newTemplatesList == null) return false;

            var newBlocks = new Dictionary<string, IReadOnlyList<CoalescedBlockDto>>();
            var newTemplates = new Dictionary<string, DeviceTemplateDto>();
            foreach (var template in newTemplatesList)
            {
                newBlocks[template.DeviceId] = RegisterCoalescingBuilder.Build(template.Registers);
                newTemplates[template.DeviceId] = template;
            }

            Interlocked.Exchange(ref _templates, newTemplates.ToImmutableDictionary());
            Interlocked.Exchange(ref _coalescedBlocks, newBlocks.ToImmutableDictionary());
            
            await File.WriteAllTextAsync(_configPath, backupJson);
            string hash = ComputeHash(backupJson);

            await _eventBus.PublishAsync(new ConfigRolledBackEvent("Auto/Manual Rollback", 0, Array.Empty<string>(), hash, DateTimeOffset.UtcNow));
            await ConfigHistoryLogger.LogAsync("ROLLBACK", hash, "system", true);
            
            return true;
        }
        catch
        {
            await ConfigHistoryLogger.LogAsync("ROLLBACK", "N/A", "system", false);
        }
        
        return false;
    }

    public Task CommitConfigAsync()
    {
        if (_rollbackCts != null)
        {
            _rollbackCts.Cancel();
            _rollbackCts.Dispose();
            _rollbackCts = null;
            ConfigHistoryLogger.LogAsync("COMMIT", "N/A", "system", true).Wait();
        }
        return Task.CompletedTask;
    }

    private void StartRollbackTimer(TimeSpan gracePeriod)
    {
        _rollbackCts = new CancellationTokenSource();
        var token = _rollbackCts.Token;

        Task.Delay(gracePeriod, token).ContinueWith(async t =>
        {
            if (!t.IsCanceled)
            {
                await RollbackToBackupAsync();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Gets coalesced blocks for a device.
    /// </summary>
    public IReadOnlyList<CoalescedBlockDto> GetCoalescedBlocks(string deviceId)
    {
        return _coalescedBlocks.TryGetValue(deviceId, out var blocks) ? blocks : Array.Empty<CoalescedBlockDto>();
    }

    private static string ComputeHash(string input)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
