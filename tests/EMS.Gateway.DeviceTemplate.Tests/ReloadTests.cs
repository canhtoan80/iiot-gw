using System.Text.Json;
using EMS.Gateway.Contracts;
using FluentAssertions;
using Moq;
using Xunit;

namespace EMS.Gateway.DeviceTemplate.Tests;

public class ReloadTests : IDisposable
{
    private readonly Mock<IInternalEventBus> _eventBusMock = new();
    private readonly string _testConfigPath = Path.Combine(Path.GetTempPath(), "devices_test.json");
    private readonly string _testBackupPath = Path.Combine(Path.GetTempPath(), "devices_test.json.bak");

    public ReloadTests()
    {
        if (File.Exists(_testConfigPath)) File.Delete(_testConfigPath);
        if (File.Exists(_testBackupPath)) File.Delete(_testBackupPath);
    }

    public void Dispose()
    {
        if (File.Exists(_testConfigPath)) File.Delete(_testConfigPath);
        if (File.Exists(_testBackupPath)) File.Delete(_testBackupPath);
    }

    [Fact]
    public async Task Reload_ValidConfig_ShouldSucceedAndSwap()
    {
        // Arrange
        var repo = new DeviceTemplateRepository(_eventBusMock.Object, _testConfigPath, _testBackupPath);
        var devices = new List<DeviceTemplateDto> { CreateValidDevice("device-1") };
        string json = JsonSerializer.Serialize(devices);

        // Act
        var result = await repo.ReloadAsync(json);

        // Assert
        result.Success.Should().BeTrue();
        repo.GetTemplate("device-1").Should().NotBeNull();
        File.Exists(_testConfigPath).Should().BeTrue();
        _eventBusMock.Verify(x => x.PublishAsync(It.IsAny<ConfigReloadRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reload_InvalidConfig_ShouldFailAndNotSwap()
    {
        // Arrange
        var repo = new DeviceTemplateRepository(_eventBusMock.Object, _testConfigPath, _testBackupPath);
        string json = "invalid-json";

        // Act
        var result = await repo.ReloadAsync(json);

        // Assert
        result.Success.Should().BeFalse();
        repo.GetAllTemplates().Should().BeEmpty();
    }

    [Fact]
    public async Task Rollback_ShouldRestoreBackup()
    {
        // Arrange
        var repo = new DeviceTemplateRepository(_eventBusMock.Object, _testConfigPath, _testBackupPath);
        
        // 1. Load initial good config
        var devices1 = new List<DeviceTemplateDto> { CreateValidDevice("device-1") };
        await repo.ReloadAsync(JsonSerializer.Serialize(devices1));
        await repo.CommitConfigAsync(); // Stop auto-rollback

        // 2. Load second good config (this will backup device-1)
        var devices2 = new List<DeviceTemplateDto> { CreateValidDevice("device-2") };
        await repo.ReloadAsync(JsonSerializer.Serialize(devices2));
        await repo.CommitConfigAsync();

        // Act
        var rollbackResult = await repo.RollbackToBackupAsync();

        // Assert
        rollbackResult.Should().BeTrue();
        repo.GetTemplate("device-1").Should().NotBeNull();
        repo.GetTemplate("device-2").Should().BeNull();
    }

    private static DeviceTemplateDto CreateValidDevice(string id)
    {
        var connectionJson = "{}";
        var jsonElement = JsonDocument.Parse(connectionJson).RootElement;
        
        return new DeviceTemplateDto(
            id, "desc", ProtocolType.ModbusTCP, jsonElement, 1.0, 1.0, 1000, 60,
            new List<RegisterDefinitionDto> { new("T1", 100, "FC03", "float32", 1.0, "unit", 0, 0, 100, 0, 0, false) },
            new List<VirtualTagExpressionDto>(),
            new List<CommandWhitelistEntryDto>(),
            null, null, null
        );
    }
}
