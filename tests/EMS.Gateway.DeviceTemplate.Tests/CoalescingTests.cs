using EMS.Gateway.Contracts;
using EMS.Gateway.DeviceTemplate.Coalescing;
using FluentAssertions;
using Xunit;

namespace EMS.Gateway.DeviceTemplate.Tests;

public class CoalescingTests
{
    [Fact]
    public void Coalescing_ScatteredRegisters_ShouldResultInOneBlock_WhenGapIsSmall()
    {
        // Arrange
        var registers = new List<RegisterDefinitionDto>
        {
            CreateRegister("T1", 100),
            CreateRegister("T2", 105),
            CreateRegister("T3", 110)
        };

        // Act
        var blocks = RegisterCoalescingBuilder.Build(registers, maxGapWords: 10);

        // Assert
        blocks.Should().HaveCount(1);
        blocks[0].StartAddress.Should().Be(100);
        blocks[0].Count.Should().Be(12); // 110 + 2 (float32 size) - 100
        blocks[0].Tags.Should().HaveCount(3);
    }

    [Fact]
    public void Coalescing_ShouldSplitBlocks_WhenGapExceedsMaxGap()
    {
        // Arrange
        var registers = new List<RegisterDefinitionDto>
        {
            CreateRegister("T1", 100),
            CreateRegister("T2", 120) // Gap = 20 - 2 = 18 > 10
        };

        // Act
        var blocks = RegisterCoalescingBuilder.Build(registers, maxGapWords: 10);

        // Assert
        blocks.Should().HaveCount(2);
        blocks[0].StartAddress.Should().Be(100);
        blocks[1].StartAddress.Should().Be(120);
    }

    [Fact]
    public void Coalescing_ShouldSplitBlocks_WhenMaxRegistersPerBlockIsReached()
    {
        // Arrange
        var registers = new List<RegisterDefinitionDto>
        {
            CreateRegister("T1", 100),
            CreateRegister("T2", 150)
        };

        // Act
        var blocks = RegisterCoalescingBuilder.Build(registers, maxGapWords: 100, maxRegistersPerBlock: 40);

        // Assert
        blocks.Should().HaveCount(2); // (150+2) - 100 = 52 > 40
    }

    private static RegisterDefinitionDto CreateRegister(string name, int address)
    {
        return new RegisterDefinitionDto(name, address, "FC03", "float32", 1.0, "unit", 0, 0, 100, 0, 0, false);
    }
}
