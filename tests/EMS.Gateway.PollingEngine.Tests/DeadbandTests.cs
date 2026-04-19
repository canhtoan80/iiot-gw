using System.Text.Json;
using EMS.Gateway.Contracts;
using EMS.Gateway.ProtocolAdapter;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EMS.Gateway.PollingEngine.Tests;

public class DeadbandTests
{
    private readonly Mock<IDeviceTemplateRepository> _templateRepoMock = new();
    private readonly Mock<ProtocolAdapterFactory> _adapterFactoryMock;
    private readonly Mock<IInternalEventBus> _eventBusMock = new();
    private readonly Mock<IProtocolAdapter> _adapterMock = new();

    public DeadbandTests()
    {
        _adapterFactoryMock = new Mock<ProtocolAdapterFactory>(_eventBusMock.Object);
        _adapterFactoryMock.Setup(x => x.Create(It.IsAny<DeviceTemplateDto>())).Returns(_adapterMock.Object);
    }

    [Fact]
    public async Task ApplyDeadband_ValueWithinDeadband_ShouldNotPublish()
    {
        // Arrange
        var engine = new PollingEngine(
            NullLogger<PollingEngine>.Instance,
            _templateRepoMock.Object,
            _adapterFactoryMock.Object,
            _eventBusMock.Object);

        var device = CreateDevice("dev1", 1.0); // 1.0 deadband
        var rawBatch1 = CreateRawBatch("dev1", 10.0);
        var rawBatch2 = CreateRawBatch("dev1", 10.5); // within deadband

        // Act
        // Access private method via reflection for testing or make it internal and use [InternalsVisibleTo]
        // For simplicity in this demo, I'll use a wrapper or just test via the main loop if possible
        // Let's use reflection to call the private method for surgical testing
        var method = typeof(PollingEngine).GetMethod("ApplyDeadbandAndPublish", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        await (Task)method!.Invoke(engine, new object[] { device, rawBatch1, CancellationToken.None })!;
        _eventBusMock.Verify(x => x.PublishAsync(It.IsAny<MetricsBatchReadyEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _eventBusMock.Invocations.Clear();

        await (Task)method!.Invoke(engine, new object[] { device, rawBatch2, CancellationToken.None })!;

        // Assert
        _eventBusMock.Verify(x => x.PublishAsync(It.IsAny<MetricsBatchReadyEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyDeadband_ValueOutsideDeadband_ShouldPublish()
    {
        // Arrange
        var engine = new PollingEngine(
            NullLogger<PollingEngine>.Instance,
            _templateRepoMock.Object,
            _adapterFactoryMock.Object,
            _eventBusMock.Object);

        var device = CreateDevice("dev1", 1.0);
        var rawBatch1 = CreateRawBatch("dev1", 10.0);
        var rawBatch2 = CreateRawBatch("dev1", 11.5); // outside deadband

        var method = typeof(PollingEngine).GetMethod("ApplyDeadbandAndPublish", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(engine, new object[] { device, rawBatch1, CancellationToken.None })!;
        _eventBusMock.Invocations.Clear();
        await (Task)method!.Invoke(engine, new object[] { device, rawBatch2, CancellationToken.None })!;

        // Assert
        _eventBusMock.Verify(x => x.PublishAsync(It.IsAny<MetricsBatchReadyEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DeviceTemplateDto CreateDevice(string id, double deadband)
    {
        var conn = JsonDocument.Parse("{}").RootElement;
        return new DeviceTemplateDto(id, "desc", ProtocolType.ModbusTCP, conn, 1, 1, 1000, 60,
            new List<RegisterDefinitionDto> { 
                new("T1", 100, "FC03", "float32", 1.0, "unit", deadband, 0, 100, 0, 0, false) 
            },
            new List<VirtualTagExpressionDto>(),
            new List<CommandWhitelistEntryDto>(),
            null, null, null);
    }

    private static RawTagBatch CreateRawBatch(string deviceId, double value, TagQuality quality = TagQuality.Good)
    {
        return new RawTagBatch(deviceId, new List<RawTagDto> {
            new("T1", value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000, quality, null)
        }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000);
    }
}
