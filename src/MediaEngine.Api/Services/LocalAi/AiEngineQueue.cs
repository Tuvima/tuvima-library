using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.LocalAi;

/// <summary>
/// The queue in front of one engine. It has a fixed number of slots; requests that cannot start at once wait and are
/// served in caller-kind priority order (a person, ingestion, Library background, plugin), first come first served within
/// a kind. A person's arrival asks running Library background and plugin work on this engine to step aside.
/// </summary>
internal sealed class AiEngineQueue
{
    private const int KindCount = 4;

    /// <summary>Capacity for an engine that has no limit today (Tesseract).</summary>
    public const int Unlimited = int.MaxValue;

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly LinkedList<Waiter>[] _waiting;
    private readonly List<AiEngineSlot> _active = [];

    public AiEngineQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _waiting = new LinkedList<Waiter>[KindCount];
        for (var i = 0; i < KindCount; i++)
        {
            _waiting[i] = new LinkedList<Waiter>();
        }
    }

    /// <summary>How many requests are running now.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>How many requests are waiting for a slot.</summary>
    public int WaitingCount
    {
        get
        {
            lock (_sync)
            {
                return _waiting.Sum(list => list.Count);
            }
        }
    }

    /// <summary>
    /// Announces that a request of <paramref name="kind"/> has arrived. A person's arrival cancels running Library
    /// background and plugin work on this engine (never ingestion). Call before waiting for a slot.
    /// </summary>
    public void Arrive(AiCallerKind kind)
    {
        if (kind == AiCallerKind.Person)
        {
            Preempt(AiCallerKind.LibraryBackground, AiCallerKind.Plugin);
        }
    }

    /// <summary>Asks running requests of the given kinds to step aside; returns how many were asked.</summary>
    public int Preempt(params AiCallerKind[] kinds)
    {
        List<AiEngineSlot> targets;
        lock (_sync)
        {
            targets = _active.Where(slot => kinds.Contains(slot.Kind) && !slot.WasPreempted).ToList();
        }

        foreach (var slot in targets)
        {
            slot.Preempt();
        }

        return targets.Count;
    }

    /// <summary>
    /// Takes a slot, waiting if none is free. With <paramref name="refuseIfKindActive"/>, returns null at once when another
    /// request of the same kind is already running (the Library background rule "one background inference at a time").
    /// </summary>
    public async Task<AiEngineSlot?> AcquireAsync(
        AiCallerKind kind,
        bool refuseIfKindActive,
        CancellationToken waitToken)
    {
        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_sync)
        {
            if (refuseIfKindActive && _active.Any(slot => slot.Kind == kind))
            {
                return null;
            }

            if (_active.Count < _capacity)
            {
                return StartSlotLocked(kind);
            }

            waiter = new Waiter(kind);
            node = _waiting[(int)kind].AddLast(waiter);
        }

        using var registration = waitToken.Register(() =>
        {
            lock (_sync)
            {
                if (node.List is null)
                {
                    return; // Already handed a slot (or removed); nothing to cancel.
                }

                node.List.Remove(node);
            }

            waiter.Completion.TrySetCanceled(waitToken);
        });

        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private AiEngineSlot StartSlotLocked(AiCallerKind kind)
    {
        var slot = new AiEngineSlot(this, kind);
        _active.Add(slot);
        return slot;
    }

    internal void Release(AiEngineSlot slot)
    {
        var granted = new List<(Waiter Waiter, AiEngineSlot Slot)>();
        lock (_sync)
        {
            if (!_active.Remove(slot))
            {
                return;
            }

            while (_active.Count < _capacity && TryDequeueLocked(out var next))
            {
                granted.Add((next, StartSlotLocked(next.Kind)));
            }
        }

        foreach (var (waiter, nextSlot) in granted)
        {
            if (!waiter.Completion.TrySetResult(nextSlot))
            {
                // The waiter gave up at the same moment; pass the slot on.
                nextSlot.Dispose();
            }
        }
    }

    private bool TryDequeueLocked(out Waiter waiter)
    {
        for (var i = 0; i < KindCount; i++)
        {
            var first = _waiting[i].First;
            if (first is null)
            {
                continue;
            }

            _waiting[i].RemoveFirst();
            waiter = first.Value;
            return true;
        }

        waiter = null!;
        return false;
    }

    private sealed class Waiter(AiCallerKind kind)
    {
        public AiCallerKind Kind { get; } = kind;

        public TaskCompletionSource<AiEngineSlot?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>One running request's place on an engine. Dispose to give the place to the next request in line.</summary>
internal sealed class AiEngineSlot : IDisposable
{
    private readonly AiEngineQueue _owner;
    private readonly CancellationTokenSource _preempt = new();
    private int _released;

    internal AiEngineSlot(AiEngineQueue owner, AiCallerKind kind)
    {
        _owner = owner;
        Kind = kind;
    }

    public AiCallerKind Kind { get; }

    /// <summary>Cancelled when a person's arrival (or app traffic, for Library background work) asks this request to step aside.</summary>
    public CancellationToken PreemptToken => _preempt.Token;

    public bool WasPreempted => _preempt.IsCancellationRequested;

    public void Preempt()
    {
        try
        {
            _preempt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request finished while the stop request was on its way; nothing is left to stop.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _owner.Release(this);
        _preempt.Dispose();
    }
}
