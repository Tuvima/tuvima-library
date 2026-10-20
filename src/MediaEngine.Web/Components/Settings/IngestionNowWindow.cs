using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The small rotating window behind the Now panel: at most <see cref="MaxRows"/> of the items the Engine says are being
/// worked on, most recently active first. The page never lists them all, and rows must not flicker, so:
/// a row, once shown, stays for at least <see cref="MinimumDwell"/> even when a newer snapshot no longer holds it (its last
/// known state is shown); a row keeps its place on screen while it is being worked on; and a row that has finished and
/// served its time is replaced, in the same place, by the most recently active item that is waiting. Rows rotate as items
/// finish (spec D13), never just because a newer item is waiting. Rows are keyed by <see cref="IngestionMediaGroupDto.GroupId"/>.
/// It has no timer of its own: the page calls <see cref="Update"/> with each snapshot and <see cref="Tick"/> about once a
/// second while <see cref="NeedsTick"/> is true.
/// </summary>
public sealed class IngestionNowWindow
{
    /// <summary>The most rows the Now panel ever shows.</summary>
    public const int MaxRows = 10;

    /// <summary>The number of rows the panel aims for while at least that many items are being worked on (it shows fewer when fewer are in flight).</summary>
    public const int MinRows = 5;

    /// <summary>How long a row stays on screen at least, whatever the Engine says next.</summary>
    public static readonly TimeSpan MinimumDwell = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _clock;
    private readonly List<Slot> _slots = [];
    private IReadOnlyList<IngestionMediaGroupDto> _latest = [];

    public IngestionNowWindow(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The rows to show, in the order they sit on screen.</summary>
    public IReadOnlyList<IngestionMediaGroupDto> Rows => _slots.Select(slot => slot.Item).ToList();

    /// <summary>
    /// True while something could still change without a new snapshot: a finished row is only held for its minimum time.
    /// The page ticks about once a second while this is true and stops when it is not.
    /// </summary>
    public bool NeedsTick
    {
        get
        {
            return _slots.Any(slot => !slot.InSnapshot);
        }
    }

    /// <summary>Takes the Engine's newest list of in-flight items. Returns true when the rows on screen changed.</summary>
    public bool Update(IEnumerable<IngestionMediaGroupDto> snapshot)
    {
        _latest = snapshot.GroupBy(item => item.GroupId).Select(group => group.First()).ToList();
        return Reconcile();
    }

    /// <summary>Lets rows whose time is up leave. Returns true when the rows on screen changed.</summary>
    public bool Tick() => Reconcile();

    private bool Reconcile()
    {
        var now = _clock.GetUtcNow();
        var before = Signature();
        var latestById = _latest.ToDictionary(item => item.GroupId);

        foreach (var slot in _slots)
        {
            if (latestById.TryGetValue(slot.Item.GroupId, out var fresh))
            {
                slot.Item = fresh;
                slot.InSnapshot = true;
            }
            else
            {
                // Not in the newest snapshot: keep its last known state until its time is up.
                slot.InSnapshot = false;
            }
        }

        var shown = _slots.Select(slot => slot.Item.GroupId).ToHashSet();
        var waiting = _latest
            .Where(item => !shown.Contains(item.GroupId))
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
        var next = 0;

        // Rows that have left the snapshot and served their time make way: the next waiting item takes the same place,
        // or, when nothing waits, the rows below close up.
        var index = 0;
        while (index < _slots.Count)
        {
            var slot = _slots[index];
            if (!slot.InSnapshot && now - slot.ShownAt >= MinimumDwell)
            {
                if (next < waiting.Count)
                {
                    _slots[index] = new Slot(waiting[next++], now);
                    index++;
                }
                else
                {
                    _slots.RemoveAt(index);
                }

                continue;
            }

            index++;
        }

        while (_slots.Count < MaxRows && next < waiting.Count)
        {
            _slots.Add(new Slot(waiting[next++], now));
        }

        return !string.Equals(before, Signature(), StringComparison.Ordinal);
    }

    private string Signature() => string.Join('|', _slots.Select(slot =>
        $"{slot.Item.GroupId:N}:{slot.Item.UpdatedAt:O}:{slot.Item.Availability}:{slot.Item.StatusLabel}:{slot.Item.CurrentGateKey}"));

    private sealed class Slot(IngestionMediaGroupDto item, DateTimeOffset shownAt)
    {
        public IngestionMediaGroupDto Item { get; set; } = item;
        public DateTimeOffset ShownAt { get; } = shownAt;
        public bool InSnapshot { get; set; } = true;
    }
}
