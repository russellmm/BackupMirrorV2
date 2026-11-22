using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace BackupMirror.Service.Runtime;

/// <summary>Fan-out of live events to every connected dashboard (Server-Sent Events).</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly JsonSerializerOptions _json;

    public EventHub(JsonSerializerOptions json) => _json = json;

    public int SubscriberCount => _subscribers.Count;

    public void Publish(string type, object payload)
    {
        if (_subscribers.IsEmpty) return;
        var frame = $"event: {type}\ndata: {JsonSerializer.Serialize(payload, _json)}\n\n";
        foreach (var ch in _subscribers.Values)
            ch.Writer.TryWrite(frame); // bounded + DropOldest: a slow client never blocks syncing
    }

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch))
            ch.Writer.TryComplete();
    }
}
