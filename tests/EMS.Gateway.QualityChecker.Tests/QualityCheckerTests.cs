using System.Text.Json;
using EMS.Gateway.Contracts;
using FluentAssertions;
using Moq;
using Xunit;

namespace EMS.Gateway.QualityChecker.Tests;

public class QualityCheckerTests
{
    private readonly Mock<IDeviceTemplateRepository> _templateRepoMock = new();
    private readonly Mock<IInternalEventBus> _eventBusMock = new();

    [Fact]
    public void Check_NullValue_ShouldReturnBadQuality()
    {
        // Arrange
        var checker = new EMS.Gateway.QualityChecker.QualityChecker(_templateRepoMock.Object, _eventBusMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);

        var batch = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", null, "unit", TagQuality.Good, null, 123456, false, false)
        }, 123456);

        // Act
        var result = checker.CheckAsync(batch);

        // Assert
        result.Tags[0].Quality.Should().Be(TagQuality.Bad);
        result.Tags[0].QualityReason.Should().Be("NullOrNaN");
    }

    [Fact]
    public void Check_RangeExceeded_ShouldReturnBadQuality()
    {
        // Arrange
        var checker = new EMS.Gateway.QualityChecker.QualityChecker(_templateRepoMock.Object, _eventBusMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, min: 0, max: 100);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);

        var batch = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", 150, "unit", TagQuality.Good, null, 123456, false, false)
        }, 123456);

        // Act
        var result = checker.CheckAsync(batch);

        // Assert
        result.Tags[0].Quality.Should().Be(TagQuality.Bad);
        result.Tags[0].QualityReason.Should().Contain("RangeExceeded");
    }

    [Fact]
    public void Check_StuckValue_ShouldReturnBadQuality_AfterTimeout()
    {
        // Arrange
        var checker = new EMS.Gateway.QualityChecker.QualityChecker(_templateRepoMock.Object, _eventBusMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, stuckTimeoutMs: 1000);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);

        var t1 = 1000000000L; // 1s
        var t2 = 2500000000L; // 2.5s (> 1000ms stuck timeout)

        var batch1 = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", 50, "unit", TagQuality.Good, null, t1, false, false)
        }, t1);
        var batch2 = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", 50, "unit", TagQuality.Good, null, t2, false, false)
        }, t2);

        // Act
        checker.CheckAsync(batch1);
        var result = checker.CheckAsync(batch2);

        // Assert
        result.Tags[0].Quality.Should().Be(TagQuality.Bad);
        result.Tags[0].QualityReason.Should().Be("StuckValue");
    }

    [Fact]
    public void Check_TransitionGoodToBad_ShouldPublishEvent()
    {
        // Arrange
        var checker = new EMS.Gateway.QualityChecker.QualityChecker(_templateRepoMock.Object, _eventBusMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, min: 0, max: 100);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);

        var batch1 = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", 50, "unit", TagQuality.Good, null, 123456, false, false)
        }, 123456);
        var batch2 = new EnrichedTagBatch(deviceId, new List<EnrichedTagDto> {
            new("T1", 150, "unit", TagQuality.Good, null, 123457, false, false)
        }, 123457);

        // Act
        checker.CheckAsync(batch1);
        checker.CheckAsync(batch2);

        // Assert
        _eventBusMock.Verify(x => x.PublishAsync(It.IsAny<TagQualityDegradedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DeviceTemplateDto CreateDevice(string id, double min = -1000000, double max = 1000000, int stuckTimeoutMs = 0)
    {
        var conn = JsonDocument.Parse("{}").RootElement;
        return new DeviceTemplateDto(id, "desc", ProtocolType.ModbusTCP, conn, 1, 1, 1000, 60,
            new List<RegisterDefinitionDto> { 
                new("T1", 100, "FC03", "float32", 1, "unit", 0, min, max, stuckTimeoutMs, 0, false) 
            },
            new List<VirtualTagExpressionDto>(),
            new List<CommandWhitelistEntryDto>(),
            null, null, null);
    }
}
