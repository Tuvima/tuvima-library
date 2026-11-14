using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Web.Components.Settings.LibraryActivity;

/// <summary>
/// The window behind the Activity page's Working now row (spec library-activity D34): of every title in progress it shows only as
/// many as fit across the row, never more than <see cref="MaxShown"/>, and when more titles are in progress than fit it rotates
/// through them. A tile, once shown, stays at least <see cref="MinimumHold"/> even when the Engine's newest list no longer holds it
/// (its last known state is shown). It has no timer of its own: the row calls <see cref="Update"/> with each snapshot and the
/// measured number that fit, and <see cref="Tick"/> about once a second while <see cref="NeedsTick"/> is true.
/// Titles are keyed by <see cref="IngestionMediaGroupDto.GroupId"/>.
/// </summary>
public sealed class WorkingNowRotation
{
    /// <summary>The most tiles the row ever shows at once.</summary>
    public const int MaxShown = 10;

    /// <summary>How long a tile stays on screen at least before it can make way for another title.</summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock;
    private readonly List<Slot> _slots = [];
    private List<IngestionMediaGroupDto> _pool = [];
    private int _capacity = MaxShown;
    private int _cursor;

    public WorkingNowRotation(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The tiles to draw, in the order they sit across the row.</summary>
    public IReadOnlyList<IngestionMediaGroupDto> Shown => _slots.Select(slot => slot.Item).ToList();

    /// <summary>How many titles the Engine lists as in progress right now.</summary>
    public int Total => _pool.Count;

    /// <summary>How many tiles the row aims to show: the number that fit across, at most <see cref="MaxShown"/>.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// True while something can change without a new snapshot: more titles than fit (so the row rotates) or a finished tile that is
    /// only held for its minimum time. The row ticks about once a second while this is true.
    /// </summary>
    public bool NeedsTick => _pool.Count > _capacity || _slots.Any(slot => !slot.InPool);

    /// <summary>Takes the Engine's newest list and the number of tiles that fit. Returns true when the tiles on screen changed.</summary>
    public bool Update(IEnumerable<IngestionMediaGroupDto> snapshot, int fit)
    {
        _pool = snapshot.GroupBy(item => item.GroupId).Select(group => group.First()).ToList();
        _capacity = Math.Clamp(fit, 1, MaxShown);
        return Reconcile();
    }

    /// <summary>Lets tiles whose time is up make way. Returns true when the tiles on screen changed.</summary>
    public bool Tick() => Reconcile();

    private bool Reconcile()
    {
        var now = _clock.GetUtcNow();
        var before = Signature();
        var poolById = _pool.ToDictionary(item => item.GroupId);

        foreach (var slot in _slots)
        {
            if (poolById.TryGetValue(slot.Item.GroupId, out var fresh))
            {
                slot.Item = fresh;
                slot.InPool = true;
            }
            else
            {
                // No longer in progress: keep its last known state until its hold is up.
                slot.InPool = false;
            }
        }

        // Titles that finished and served their hold leave; a narrower row drops the tiles that no longer fit.
        _slots.RemoveAll(slot => !slot.InPool && now - slot.ShownAt >= MinimumHold);
        if (_slots.Count > _capacity)
        {
            _slots.RemoveRange(_capacity, _slots.Count - _capacity);

            // The tiles that were dropped were never seen for long: the title after the last one still shown is next in line.
            var lastShown = _slots[^1].Item.GroupId;
            var at = _pool.FindIndex(item => item.GroupId == lastShown);
            if (at >= 0)
            {
                _cursor = (at + 1) % _pool.Count;
            }
        }

        // More titles than fit: every tile that has had its hold makes way, in the same place, for the next title in line.
        if (_pool.Count > _capacity)
        {
            for (var index = 0; index < _slots.Count; index++)
            {
                if (now - _slots[index].ShownAt < MinimumHold)
                {
                    continue;
                }

                if (NextWaiting() is { } next)
                {
                    _slots[index] = new Slot(next, now);
                }
            }
        }

        while (_slots.Count < _capacity && NextWaiting() is { } waiting)
        {
            _slots.Add(new Slot(waiting, now));
        }

        return !string.Equals(before, Signature(), StringComparison.Ordinal);
    }

    // The next title in the list, from where the last one was taken, that is not already on screen; the list is walked as a ring.
    private IngestionMediaGroupDto? NextWaiting()
    {
        var shown = _slots.Select(slot => slot.Item.GroupId).ToHashSet();
        for (var step = 0; step < _pool.Count; step++)
        {
            var index = (_cursor + step) % _pool.Count;
            if (!shown.Contains(_pool[index].GroupId))
            {
                _cursor = (index + 1) % _pool.Count;
                return _pool[index];
            }
        }

        return null;
    }

    private string Signature() => string.Join('|', _slots.Select(slot =>
        $"{slot.Item.GroupId:N}:{slot.Item.UpdatedAt:O}:{slot.Item.Availability}:{slot.Item.StatusLabel}:{slot.Item.CurrentGateKey}"));

    private sealed class Slot(IngestionMediaGroupDto item, DateTimeOffset shownAt)
    {
        public IngestionMediaGroupDto Item { get; set; } = item;
        public DateTimeOffset ShownAt { get; } = shownAt;
        public bool InPool { get; set; } = true;
    }
}
