using System.Text.Json;
using EMS.Gateway.Contracts;
using FluentAssertions;
using Moq;
using Xunit;

namespace EMS.Gateway.EdgeRuleEngine.Tests;

public class EdgeRuleEngineTests
{
    private readonly Mock<IDeviceTemplateRepository> _templateRepoMock = new();
    private readonly Mock<IEdgeStateStore> _stateStoreMock = new();

    [Fact]
    public void Process_CtPtNormalization_ShouldCalculateCorrectly()
    {
        // Arrange
        var engine = new EMS.Gateway.EdgeRuleEngine.EdgeRuleEngine(_templateRepoMock.Object, _stateStoreMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, ct: 200, pt: 1, scale: 0.1);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);

        var rawBatch = new RawTagBatch(deviceId, new List<RawTagDto> {
            new("T1", 100, 123456, TagQuality.Good, null)
        }, 123456);

        // Act
        var result = engine.ProcessAsync(rawBatch);

        // Assert
        result.Tags[0].PhysicalValue.Should().Be(2000.0); // 100 * 200 * 1 * 0.1
    }

    [Fact]
    public void Process_VirtualTag_ShouldCalculateExpression()
    {
        // Arrange
        var engine = new EMS.Gateway.EdgeRuleEngine.EdgeRuleEngine(_templateRepoMock.Object, _stateStoreMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId);
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);
        engine.PrecompileExpressions(new[] { device });

        var rawBatch = new RawTagBatch(deviceId, new List<RawTagDto> {
            new("V", 230, 123456, TagQuality.Good, null),
            new("I", 10, 123456, TagQuality.Good, null)
        }, 123456);

        // Act
        var result = engine.ProcessAsync(rawBatch);

        // Assert
        var pTag = result.Tags.FirstOrDefault(t => t.TagName == "P");
        pTag.Should().NotBeNull();
        pTag!.PhysicalValue.Should().Be(2300.0); // 230 * 10
    }

    [Fact]
    public void Process_DivisionByZero_ShouldReturnBadQuality()
    {
        // Arrange
        var engine = new EMS.Gateway.EdgeRuleEngine.EdgeRuleEngine(_templateRepoMock.Object, _stateStoreMock.Object);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, virtualExpr: "100 / X");
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);
        engine.PrecompileExpressions(new[] { device });

        var rawBatch = new RawTagBatch(deviceId, new List<RawTagDto> {
            new("X", 0, 123456, TagQuality.Good, null)
        }, 123456);

        // Act
        var result = engine.ProcessAsync(rawBatch);

        // Assert
        var vtTag = result.Tags.FirstOrDefault(t => t.IsVirtual);
        vtTag!.Quality.Should().Be(TagQuality.Bad);
        vtTag.QualityReason.Should().Be("NaN_DivisionByZero");
    }

    [Fact]
    public void Totalizer_ShouldAccumulate()
    {
        // Arrange
        var stateStore = new EMS.Gateway.EdgeRuleEngine.EdgeStateStore();
        var engine = new EMS.Gateway.EdgeRuleEngine.EdgeRuleEngine(_templateRepoMock.Object, stateStore);
        var deviceId = "dev1";
        var device = CreateDevice(deviceId, virtualExpr: "TOTALIZER(P, dt)");
        _templateRepoMock.Setup(x => x.GetTemplate(deviceId)).Returns(device);
        engine.PrecompileExpressions(new[] { device });

        var rawBatch = new RawTagBatch(deviceId, new List<RawTagDto> {
            new("P", 100, 123456, TagQuality.Good, null)
        }, 123456);

        // Act & Assert
        // Cycle 1: dt=1s, p=100 -> total=100
        engine.ProcessAsync(rawBatch).Tags.First(t => t.IsVirtual).PhysicalValue.Should().Be(100.0);
        // Cycle 2: dt=1s, p=100 -> total=200
        engine.ProcessAsync(rawBatch).Tags.First(t => t.IsVirtual).PhysicalValue.Should().Be(200.0);
    }

    private static DeviceTemplateDto CreateDevice(string id, double ct = 1, double pt = 1, double scale = 1, string? virtualExpr = null)
    {
        var conn = JsonDocument.Parse("{}").RootElement;
        return new DeviceTemplateDto(id, "desc", ProtocolType.ModbusTCP, conn, ct, pt, 1000, 60,
            new List<RegisterDefinitionDto> { 
                new("T1", 100, "FC03", "float32", scale, "unit", 0, 0, 100000, 0, 0, false),
                new("V", 101, "FC03", "float32", 1, "V", 0, 0, 1000, 0, 0, false),
                new("I", 102, "FC03", "float32", 1, "A", 0, 0, 1000, 0, 0, false),
                new("P", 103, "FC03", "float32", 1, "W", 0, 0, 1000, 0, 0, false),
                new("X", 104, "FC03", "float32", 1, "U", 0, 0, 1000, 0, 0, false)
            },
            new List<VirtualTagExpressionDto> { 
                new("P", virtualExpr ?? "V * I", "W", 0, 100000) 
            },
            new List<CommandWhitelistEntryDto>(),
            null, null, null);
    }
}
