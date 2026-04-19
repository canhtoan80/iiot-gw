using EMS.Gateway.Contracts;

namespace EMS.Gateway.DeviceTemplate.Coalescing;

/// <summary>
/// Static builder for coalescing registers into efficient Modbus blocks.
/// </summary>
public static class RegisterCoalescingBuilder
{
    private const int DefaultMaxGapWords = 10;
    private const int DefaultMaxRegistersPerBlock = 100;

    /// <summary>
    /// Builds coalesced blocks for a device based on its register definitions.
    /// </summary>
    public static IReadOnlyList<CoalescedBlockDto> Build(IReadOnlyList<RegisterDefinitionDto> registers, 
        int maxGapWords = DefaultMaxGapWords, 
        int maxRegistersPerBlock = DefaultMaxRegistersPerBlock)
    {
        if (registers.Count == 0) return Array.Empty<CoalescedBlockDto>();

        var sortedRegisters = registers.OrderBy(r => r.Address).ToList();
        var blocks = new List<CoalescedBlockDto>();

        int blockStart = sortedRegisters[0].Address;
        var currentBlockTags = new List<(string TagName, int Offset, string DataType)>();
        int lastAddress = sortedRegisters[0].Address;
        int lastSize = GetWordCount(sortedRegisters[0].DataType);

        currentBlockTags.Add((sortedRegisters[0].TagName, 0, sortedRegisters[0].DataType));

        for (int i = 1; i < sortedRegisters.Count; i++)
        {
            var current = sortedRegisters[i];
            int currentSize = GetWordCount(current.DataType);
            int lastBlockEnd = lastAddress + lastSize;

            if ((current.Address - lastBlockEnd) <= maxGapWords &&
                (current.Address + currentSize - blockStart) <= maxRegistersPerBlock)
            {
                // Extend current block
                currentBlockTags.Add((current.TagName, current.Address - blockStart, current.DataType));
                lastAddress = current.Address;
                lastSize = currentSize;
            }
            else
            {
                // Close current block
                blocks.Add(new CoalescedBlockDto(blockStart, lastAddress + lastSize - blockStart, currentBlockTags));

                // Start new block
                blockStart = current.Address;
                currentBlockTags = new List<(string TagName, int Offset, string DataType)>();
                currentBlockTags.Add((current.TagName, 0, current.DataType));
                lastAddress = current.Address;
                lastSize = currentSize;
            }
        }

        // Close last block
        blocks.Add(new CoalescedBlockDto(blockStart, lastAddress + lastSize - blockStart, currentBlockTags));

        return blocks;
    }

    private static int GetWordCount(string dataType)
    {
        return dataType.ToLowerInvariant() switch
        {
            "bool" or "uint16" or "int16" => 1,
            "float32" or "uint32" or "int32" => 2,
            "float64" or "uint64" or "int64" => 4,
            _ => 1 // Default to 1 word
        };
    }
}
