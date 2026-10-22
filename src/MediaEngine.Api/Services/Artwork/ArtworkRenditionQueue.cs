using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MediaEngine.Api.Services.Artwork;

/// <summary>
/// Pictures whose missing copies should be made in the background. Small and bounded: when it is full or the
/// picture is already waiting, the request is dropped (the next request for the picture asks again).
/// </summary>
public sealed class ArtworkRenditionQueue
{
    private const int Capacity = 256;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Capacity)
    {
        // Wait (not DropWrite): a full queue makes TryWrite return false, so the picture is not left marked as
        // waiting forever and the next request for it can queue it again.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    /// <summary>Queues a picture. False when it is already waiting or the queue is full.</summary>
    public bool TryEnqueue(Guid assetId)
    {
        if (!_pending.TryAdd(assetId, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(assetId))
        {
            return true;
        }

        _pending.TryRemove(assetId, out _);
        return false;
    }

    /// <summary>Whether the picture is waiting to be worked on (or being worked on right now).</summary>
    public bool IsPending(Guid assetId) => _pending.ContainsKey(assetId);

    /// <summary>Marks a picture as no longer waiting, so a later request can queue it again.</summary>
    public void MarkDone(Guid assetId) => _pending.TryRemove(assetId, out _);

    /// <summary>The queued pictures, in order, until <paramref name="ct"/> is cancelled.</summary>
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
