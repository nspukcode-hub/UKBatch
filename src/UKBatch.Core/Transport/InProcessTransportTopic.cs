using System.Collections.Concurrent;
using System.Threading.Channels;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Transport;

/// <summary>
/// Per-topic registry of subscriber channels. Each subscriber owns a private
/// <see cref="Channel{T}"/>; <see cref="PublishToAll"/> fans out to every active subscriber
/// non-blockingly.
/// </summary>
internal sealed class InProcessTransportTopic
{
    private readonly ConcurrentDictionary<Guid, Channel<JobMessage>> _subscribers = new();

    /// <summary>Adds a new subscriber channel; the returned channel is unbounded for the in-process MVP.</summary>
    public Channel<JobMessage> AddSubscriber(Guid id)
    {
        var ch = Channel.CreateUnbounded<JobMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = ch;
        return ch;
    }

    /// <summary>Removes a subscriber channel and completes it.</summary>
    public bool RemoveSubscriber(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
            return true;
        }
        return false;
    }

    /// <summary>Broadcasts a message to every current subscriber.</summary>
    public void PublishToAll(JobMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var ch in _subscribers.Values)
        {
            ch.Writer.TryWrite(message);
        }
    }
}
