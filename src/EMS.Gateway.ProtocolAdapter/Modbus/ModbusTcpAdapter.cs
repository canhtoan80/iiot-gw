using System.Buffers.Binary;
using System.Threading.Channels;
using EMS.Gateway.Contracts;
using FluentModbus;
using Polly;
using Polly.Retry;

namespace EMS.Gateway.ProtocolAdapter.Modbus;

/// <summary>
/// Adapter for Modbus TCP devices.
/// </summary>
public class ModbusTcpAdapter : IProtocolAdapter, IDisposable
{
    private readonly ModbusTcpClient _client;
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly byte _unitIdentifier;
    private readonly Channel<WriteCommand> _writeQueue;
    private readonly AsyncRetryPolicy _retryPolicy;
    private bool _disposed;

    public ModbusTcpAdapter(string ipAddress, int port, byte unitIdentifier)
    {
        _ipAddress = ipAddress;
        _port = port;
        _unitIdentifier = unitIdentifier;
        _client = new ModbusTcpClient();
        _writeQueue = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromMilliseconds(50));
    }

    public async Task<RawTagBatch> PollAsync(DeviceTemplateDto device, IReadOnlyList<CoalescedBlockDto> coalescedBlocks, CancellationToken ct)
    {
        var tags = new List<RawTagDto>();
        long pollTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000;

        try
        {
            EnsureConnected();

            // Arbitration: Process all pending writes first
            while (_writeQueue.Reader.TryRead(out var writeCmd))
            {
                await ExecuteWriteInternal(writeCmd, ct);
            }

            foreach (var block in coalescedBlocks)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var data = await _retryPolicy.ExecuteAsync(async () =>
                    {
                        return await Task.Run(() => _client.ReadHoldingRegisters<byte>(_unitIdentifier, block.StartAddress, block.Count).ToArray());
                    });

                    foreach (var tag in block.Tags)
                    {
                        var rawValue = ParseValue(data.ToArray(), tag.Offset, tag.DataType);
                        tags.Add(new RawTagDto(tag.TagName, rawValue, pollTimestamp, TagQuality.Good, null));
                    }
                }
                catch (Exception ex)
                {
                    foreach (var tag in block.Tags)
                    {
                        tags.Add(new RawTagDto(tag.TagName, null, pollTimestamp, TagQuality.Bad, $"Poll failed: {ex.Message}"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Device level failure
            foreach (var reg in device.Registers)
            {
                tags.Add(new RawTagDto(reg.TagName, null, pollTimestamp, TagQuality.Bad, $"Connection failed: {ex.Message}"));
            }
        }

        return new RawTagBatch(device.DeviceId, tags, pollTimestamp);
    }

    public async Task<bool> WriteAsync(string deviceId, int registerAddress, object value, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        var command = new WriteCommand(registerAddress, value, tcs);
        
        if (await _writeQueue.Writer.WaitToWriteAsync(ct))
        {
            await _writeQueue.Writer.WriteAsync(command, ct);
            return await tcs.Task;
        }
        
        return false;
    }

    private async Task ExecuteWriteInternal(WriteCommand cmd, CancellationToken ct)
    {
        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await Task.Run(() => {
                    if (cmd.Value is bool b)
                    {
                        _client.WriteSingleCoil(_unitIdentifier, cmd.Address, b);
                    }
                    else if (cmd.Value is ushort u16)
                    {
                        _client.WriteSingleRegister(_unitIdentifier, cmd.Address, u16);
                    }
                    else if (cmd.Value is float f32)
                    {
                        byte[] bytes = new byte[4];
                        BinaryPrimitives.WriteSingleBigEndian(bytes, f32);
                        ushort[] registers = {
                            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)),
                            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2))
                        };
                        _client.WriteMultipleRegisters(_unitIdentifier, cmd.Address, registers);
                    }
                });
            });
            cmd.CompletionSource.SetResult(true);
        }
        catch (Exception ex)
        {
            cmd.CompletionSource.SetException(ex);
        }
    }

    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(_ipAddress), _port), ModbusEndianness.BigEndian);
        }
    }

    private double? ParseValue(byte[] data, int offset, string dataType)
    {
        int byteOffset = offset * 2;
        var span = data.AsSpan(byteOffset);

        return dataType.ToLowerInvariant() switch
        {
            "uint16" => BinaryPrimitives.ReadUInt16BigEndian(span),
            "int16" => BinaryPrimitives.ReadInt16BigEndian(span),
            "float32" => BinaryPrimitives.ReadSingleBigEndian(span),
            "uint32" => BinaryPrimitives.ReadUInt32BigEndian(span),
            "int32" => BinaryPrimitives.ReadInt32BigEndian(span),
            "bool" => span[0] != 0 || span[1] != 0 ? 1.0 : 0.0,
            _ => null
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public record WriteCommand(int Address, object Value, TaskCompletionSource<bool> CompletionSource);
