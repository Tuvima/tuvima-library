using System.Collections.Concurrent;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Dashboard-side limit on starting a pairing (<c>oauth/device_authorization</c>).
/// The Engine limits sign-in and pairing attempts per caller address, and every app arrives from the
/// Dashboard's own address, so without this one noisy device could use up the Engine's whole allowance and
/// block everyone's pairing and the Dashboard's own sign-in. Each app address gets a small share, and the
/// total is held below the Engine's limit (10 per minute) so that room is always left for human sign-in.
/// </summary>
public sealed class NativeAppPairingThrottle(TimeProvider clock)
{
    public const int PerAddressPerMinute = 4;
    public const int TotalPerMinute = 8;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> perAddress = new();
    private readonly Queue<DateTimeOffset> total = new();
    private readonly object gate = new();

    public NativeAppPairingThrottle() : this(TimeProvider.System)
    {
    }

    /// <summary>Returns false when this address (or the door as a whole) has used up its share for now.</summary>
    public bool TryAcquire(string address)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Trim(total, now);
            if (total.Count >= TotalPerMinute)
            {
                return false;
            }

            var mine = perAddress.GetOrAdd(address, _ => new Queue<DateTimeOffset>());
            Trim(mine, now);
            if (mine.Count >= PerAddressPerMinute)
            {
                return false;
            }

            mine.Enqueue(now);
            total.Enqueue(now);

            // Forget addresses that have gone quiet so the table cannot grow without bound.
            if (perAddress.Count > 1024)
            {
                foreach (var key in perAddress.Where(p => Trim(p.Value, now) == 0).Select(p => p.Key).ToList())
                {
                    perAddress.TryRemove(key, out _);
                }
            }

            return true;
        }
    }

    private static int Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window)
        {
            queue.Dequeue();
        }

        return queue.Count;
    }
}
