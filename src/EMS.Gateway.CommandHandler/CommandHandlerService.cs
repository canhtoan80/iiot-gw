using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using EMS.Gateway.Contracts;
using System.Text.Json;

namespace EMS.Gateway.CommandHandler;

public class CommandHandlerService : BackgroundService, ICommandHandler
{
    private readonly ILogger<CommandHandlerService> _logger;
    private readonly IDeviceTemplateRepository _templateRepo;
    private readonly IProtocolAdapter _protocolAdapter;
    private readonly IInternalEventBus _eventBus;
    private readonly IMqttPublisher _mqttPublisher;

    private const string AuditLogPath = "/var/log/ems-gateway/audit.ndjson";

    public CommandHandlerService(
        ILogger<CommandHandlerService> logger,
        IDeviceTemplateRepository templateRepo,
        IProtocolAdapter protocolAdapter,
        IInternalEventBus eventBus,
        IMqttPublisher mqttPublisher)
    {
        _logger = logger;
        _templateRepo = templateRepo;
        _protocolAdapter = protocolAdapter;
        _eventBus = eventBus;
        _mqttPublisher = mqttPublisher;
    }

    public async Task<CommandResult> HandleAsync(CommandRequestDto request, CancellationToken ct)
    {
        _logger.LogInformation("Processing command {DcmdId} for device {DeviceId}", request.DcmdId, request.DeviceId);

        // 1. JWT Validation (Placeholder for real implementation)
        if (!ValidateJwt(request.JwtToken))
        {
            await LogAuditAsync(request, "RejectedJwtInvalid");
            return CommandResult.RejectedJwtInvalid;
        }

        // 2. Whitelist & Range Check
        var whitelist = _templateRepo.GetCommandWhitelist(request.DeviceId);
        var entry = whitelist.FirstOrDefault(e => e.RegisterAddress == request.RegisterAddress);
        
        if (entry == null)
        {
            await LogAuditAsync(request, "RejectedWhitelist");
            return CommandResult.RejectedWhitelist;
        }

        double val = Convert.ToDouble(request.Value);
        if (val < entry.MinValue || val > entry.MaxValue)
        {
            await LogAuditAsync(request, "RejectedRangeExceeded");
            return CommandResult.RejectedRangeExceeded;
        }

        // 3. Pre-execution Audit
        await LogAuditAsync(request, "Executing");

        // 4. Execute Write
        try
        {
            bool success = await _protocolAdapter.WriteAsync(request.DeviceId, request.RegisterAddress, request.Value, ct);
            var result = success ? CommandResult.Accepted : CommandResult.DeviceError;
            
            await LogAuditAsync(request, result.ToString());
            
            // Send ACK back to Mosquitto
            await SendAckAsync(request.DcmdId, request.DeviceId, result, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute write for command {DcmdId}", request.DcmdId);
            await LogAuditAsync(request, "DeviceError");
            return CommandResult.DeviceError;
        }
    }

    private bool ValidateJwt(string token)
    {
        // Real implementation would use JwtSecurityTokenHandler and a public key
        return !string.IsNullOrEmpty(token);
    }

    private async Task LogAuditAsync(CommandRequestDto request, string status)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow,
            dcmd_id = request.DcmdId,
            device_id = request.DeviceId,
            register = request.RegisterAddress,
            value = request.Value,
            status = status
        };

        string json = JsonSerializer.Serialize(entry);
        _logger.LogInformation("AUDIT: {Entry}", json);

        try
        {
            var dir = Path.GetDirectoryName(AuditLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // Append to file logic (simplified)
            // await File.AppendAllLinesAsync(AuditLogPath, new[] { json });
        }
        catch { /* Ignore logging errors to avoid blocking command */ }
    }

    private async Task SendAckAsync(string dcmdId, string deviceId, CommandResult result, CancellationToken ct)
    {
        // Placeholder for sending ACK payload back via MQTT
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command Handler Service starting...");

        // Subscribe to DCMD events from MQTT (bridged via InternalEventBus)
        await foreach (var @event in _eventBus.SubscribeAsync<CommandReceivedEvent>(stoppingToken))
        {
            // In a real implementation, this would trigger HandleAsync
            // with a request deserialized from the MQTT message
        }
    }
}
