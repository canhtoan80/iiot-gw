using System.Collections.Concurrent;
using System.Threading.Channels;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.Host;

public class InternalEventBus : IInternalEventBus
{
    private readonly ConcurrentDictionary<Type, object> _channels = new();

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : IDomainEvent
    {
        var channel = GetOrCreate<TEvent>();
        return channel.Writer.WriteAsync(domainEvent, ct).AsTask();
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct)
        where TEvent : IDomainEvent
    {
        var channel = GetOrCreate<TEvent>();
        return channel.Reader.ReadAllAsync(ct);
    }

    private Channel<TEvent> GetOrCreate<TEvent>()
    {
        return (Channel<TEvent>)_channels.GetOrAdd(typeof(TEvent),
            _ => Channel.CreateBounded<TEvent>(
                new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait }));
    }
}
