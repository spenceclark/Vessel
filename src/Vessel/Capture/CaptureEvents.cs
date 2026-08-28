using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>One pre-serialized SSE frame: a named event plus its single-line JSON payload.</summary>
public sealed record SseEvent(string Name, string Json);

/// <summary>
/// D5 — the SSE broadcast hub. Each subscriber (one per open <c>/vessel/api/events</c>
/// connection) gets its own bounded, drop-oldest channel so a stalled browser can never
/// back-pressure the request path or the writer. With zero subscribers, every publish
/// method is a single dictionary emptiness check — near-zero cost on the hot path.
/// </summary>
public sealed class CaptureEvents
{
    private const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<long, Channel<SseEvent>> _subscribers = new();
    private long _nextSubscriberId;

    public CaptureSubscription Subscribe()
    {
        long id = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _subscribers[id] = channel;
        return new CaptureSubscription(this, id, channel.Reader);
    }

    internal void Unsubscribe(long id) => _subscribers.TryRemove(id, out _);

    /// <summary>Emitted at handler entry, once the backend/tags are resolved (request path).</summary>
    public void Started(long seq, string startedAt, string method, string path, string backend, string[] tags)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("started", JsonSerializer.Serialize(
            new StartedEvent(seq, startedAt, method, path, backend, tags), EventsJsonContext.Default.StartedEvent));
    }

    /// <summary>
    /// Post-Phase-4 addition (ui-spec.md §9.1, phase-3.md D5) — emitted once the request
    /// body is fully read and its <c>model</c> field parsed off the request path (a
    /// background task; see <see cref="CaptureContext.EmitRequestReadyIfParseable"/>).
    /// Exists so an in-flight row can show the real model within milliseconds of
    /// dispatch instead of only after completion.
    /// </summary>
    public void RequestReady(long seq, string model)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("request_ready", JsonSerializer.Serialize(
            new RequestReadyEvent(seq, model), EventsJsonContext.Default.RequestReadyEvent));
    }

    /// <summary>Emitted on the first-response-byte mark of streamed responses (request path).</summary>
    public void FirstToken(long seq, double ttftMs)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("first_token", JsonSerializer.Serialize(
            new FirstTokenEvent(seq, ttftMs), EventsJsonContext.Default.FirstTokenEvent));
    }

    /// <summary>
    /// Emitted by the writer after the row is inserted (background). <paramref name="row"/>
    /// is null when the writer dropped the batch (resilience path) — the UI clears the
    /// in-flight entry rather than showing it as complete.
    /// </summary>
    public void Completed(long seq, Summary? row)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("completed", JsonSerializer.Serialize(
            new CompletedEvent(seq, row), EventsJsonContext.Default.CompletedEvent));
    }

    private void Publish(string name, string json)
    {
        var evt = new SseEvent(name, json);
        foreach (Channel<SseEvent> channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt); // drop-oldest mode never blocks
        }
    }
}

/// <summary>One SSE connection's subscription; disposing unregisters it from the hub.</summary>
public sealed class CaptureSubscription : IDisposable
{
    private readonly CaptureEvents _hub;
    private readonly long _id;

    internal CaptureSubscription(CaptureEvents hub, long id, ChannelReader<SseEvent> reader)
    {
        _hub = hub;
        _id = id;
        Reader = reader;
    }

    public ChannelReader<SseEvent> Reader { get; }

    public void Dispose() => _hub.Unsubscribe(_id);
}

internal sealed record StartedEvent(long Seq, string StartedAt, string Method, string Path, string Backend, string[] Tags);

internal sealed record RequestReadyEvent(long Seq, string Model);

internal sealed record FirstTokenEvent(long Seq, double TtftMs);

internal sealed record CompletedEvent(long Seq, Summary? Row);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(RequestReadyEvent))]
[JsonSerializable(typeof(FirstTokenEvent))]
[JsonSerializable(typeof(CompletedEvent))]
internal sealed partial class EventsJsonContext : JsonSerializerContext;
