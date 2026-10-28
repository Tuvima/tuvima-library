using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One ticked item's current value (or values) for a field, and why it cannot take a change when it cannot.</summary>
public sealed record BulkFieldItem(Guid EntityId, IReadOnlyList<string> Values, string? LockedReason)
{
    public string Text => Values.Count == 0 ? string.Empty : Values[0];

    public bool IsLocked => LockedReason is not null;
}

/// <summary>
/// How one field stands across the ticked items (spec D14): the shared value when they agree, "Mixed, leave unchanged" when they do
/// not, and which items cannot take a change. Built from each item's own read, so staging can work out every item's new value.
/// </summary>
public sealed class BulkFieldView
{
    public BulkFieldView(string key, string label, bool isList, IReadOnlyList<string> operations, IReadOnlyList<BulkFieldItem> items)
    {
        Key = key;
        Label = label;
        IsList = isList;
        Operations = operations;
        Items = items;
        var first = items.Count > 0 ? items[0].Values : [];
        IsMixed = items.Any(item => !BatchEditFieldCatalog.SameItems(item.Values, first));
        Shared = IsMixed ? [] : first;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsList { get; }

    /// <summary>The operations the field accepts; empty means a provider fact, shown but never changed.</summary>
    public IReadOnlyList<string> Operations { get; }

    public IReadOnlyList<BulkFieldItem> Items { get; }

    public bool IsProviderFact => Operations.Count == 0;

    /// <summary>True when the ticked items do not all hold the same value.</summary>
    public bool IsMixed { get; }

    /// <summary>What every item holds when they agree; empty when mixed or when they all hold nothing.</summary>
    public IReadOnlyList<string> Shared { get; }

    public string SharedText => Shared.Count == 0 ? string.Empty : Shared[0];

    /// <summary>The items a change would reach: the ones the field is not locked for.</summary>
    public IEnumerable<BulkFieldItem> Eligible => IsProviderFact ? [] : Items.Where(item => !item.IsLocked);

    public IReadOnlyList<BulkFieldItem> Locked => [.. Items.Where(item => item.IsLocked)];

    /// <summary>True when any ticked item has a value staged for this field.</summary>
    public bool HasStaged(PendingEditList pending) => Items.Any(item => pending.TryGetField(item.EntityId, Key, out _));

    /// <summary>
    /// How the items will stand once the staged changes are saved: each item's staged value in place of its own. The panel shows this, so
    /// what was typed is what the box holds, and Mixed means the items will still disagree.
    /// </summary>
    public BulkFieldView WithStaged(PendingEditList pending)
    {
        if (!HasStaged(pending))
        {
            return this;
        }

        var items = Items.Select(item =>
        {
            if (!pending.TryGetField(item.EntityId, Key, out var staged))
            {
                return item;
            }

            // An empty staged value sends the item back to the provider's value, which the panel cannot know; it shows as empty.
            IReadOnlyList<string> values = IsList
                ? BatchEditFieldCatalog.ParseList(staged)
                : string.IsNullOrWhiteSpace(staged) ? [] : [staged.Trim()];
            return item with { Values = values };
        }).ToList();
        return new BulkFieldView(Key, Label, IsList, Operations, items);
    }

    /// <summary>"Drama (2), Comedy (1), and 3 more": the values the ticked items disagree on, most common first.</summary>
    public string MixedHint()
    {
        var counts = new Dictionary<string, (string Value, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in Items.SelectMany(item => item.Values.Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            counts[value] = counts.TryGetValue(value, out var seen) ? (seen.Value, seen.Count + 1) : (value, 1);
        }

        var ordered = counts.Values.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = ordered.Take(3).Select(entry => $"{entry.Value} ({entry.Count})").ToList();
        if (shown.Count == 0)
        {
            return string.Empty;
        }

        var more = ordered.Count - shown.Count;
        return more > 0 ? $"{string.Join(", ", shown)}, and {more} more" : string.Join(", ", shown);
    }
}

/// <summary>
/// Reads how the ticked items stand on every field the bulk panel can change, one item at a time so each item's own value is known
/// (an "add a genre" needs every item's current genres). Reads are kept until <see cref="Invalidate"/>, so ticking one more row reads
/// only that row. Nothing here changes anything.
/// </summary>
public sealed class BulkEditFields
{
    private const int MaxParallelReads = 4;

    private readonly IEngineApiClient _api;
    private readonly Dictionary<Guid, ItemRead> _reads = [];
    private int _version;

    public BulkEditFields(IEngineApiClient api)
    {
        _api = api;
    }

    private sealed record ItemRead(IReadOnlyList<LibraryBatchFieldState> Fields, string? UnavailableReason);

    public IReadOnlyList<BulkFieldView> Fields { get; private set; } = [];

    /// <summary>Items that cannot be edited at all, with the Engine's reason; they are left out of every change.</summary>
    public IReadOnlyDictionary<Guid, string> Unavailable { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>True when the Engine says this person may not change these items: the panel shows the fields read-only.</summary>
    public bool Forbidden { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>A plain sentence when the fields could not be read; null otherwise.</summary>
    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>Forgets what was read (after a save), so the next read shows the saved values.</summary>
    public void Invalidate()
    {
        _reads.Clear();
        _version++;
    }

    /// <summary>Reads the items not read yet and works out how the whole set stands.</summary>
    public async Task LoadAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var version = ++_version;
        var missing = ids.Where(id => !_reads.ContainsKey(id)).Distinct().ToList();
        Error = null;
        if (missing.Count > 0)
        {
            Loading = true;
            Changed?.Invoke();
            var fetched = await ReadAsync(missing, ct).ConfigureAwait(false);
            if (version != _version)
            {
                // A later tick, a save or a close has replaced this answer.
                return;
            }

            Forbidden = fetched.Forbidden;
            Error = fetched.Error;
            foreach (var (id, read) in fetched.Reads)
            {
                _reads[id] = read;
            }
        }

        Loading = false;
        Apply(ids);
        Changed?.Invoke();
    }

    private sealed record Fetched(Dictionary<Guid, ItemRead> Reads, bool Forbidden, string? Error);

    private async Task<Fetched> ReadAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var reads = new Dictionary<Guid, ItemRead>();
        var forbidden = false;
        string? error = null;
        using var gate = new SemaphoreSlim(MaxParallelReads);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var outcome = await _api.PreviewLibraryBatchEditAsync(new LibraryBatchEditPreviewRequest { EntityIds = [id] }, ct).ConfigureAwait(false);
                lock (reads)
                {
                    if (outcome.IsOk)
                    {
                        var preview = outcome.Value!;
                        reads[id] = new ItemRead(
                            preview.Fields,
                            preview.UnavailableItems.FirstOrDefault(item => item.EntityId == id)?.Reason);
                    }
                    else if (outcome.Status == LibraryBatchEditOutcomeStatus.Forbidden)
                    {
                        forbidden = true;
                    }
                    else
                    {
                        error = outcome.Message ?? "Couldn't load the fields. Close this and try again.";
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Fetched(reads, forbidden, error);
    }

    private void Apply(IReadOnlyList<Guid> ids)
    {
        var present = ids.Where(_reads.ContainsKey).ToList();
        var unavailable = new Dictionary<Guid, string>();
        foreach (var id in present)
        {
            if (_reads[id].UnavailableReason is { } reason)
            {
                unavailable[id] = reason;
            }
        }

        Unavailable = unavailable;
        Fields = Build(present.Where(id => !unavailable.ContainsKey(id)).ToList(), _reads.ToDictionary(pair => pair.Key, pair => pair.Value.Fields));
    }

    /// <summary>Works out how the items stand on each field from every item's own read. Pure, so it can be tested without an Engine.</summary>
    public static IReadOnlyList<BulkFieldView> Build(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, IReadOnlyList<LibraryBatchFieldState>> reads)
    {
        var views = new List<BulkFieldView>();
        var template = ids.Where(reads.ContainsKey).Select(id => reads[id]).FirstOrDefault();
        if (template is null)
        {
            return views;
        }

        foreach (var field in template)
        {
            var isList = string.Equals(field.Kind, LibraryBatchFieldKinds.List, StringComparison.Ordinal);
            var items = new List<BulkFieldItem>();
            foreach (var id in ids.Where(reads.ContainsKey))
            {
                var state = reads[id].FirstOrDefault(candidate => string.Equals(candidate.Key, field.Key, StringComparison.OrdinalIgnoreCase));
                if (state is null)
                {
                    continue;
                }

                var values = isList
                    ? (IReadOnlyList<string>)state.Values
                    : string.IsNullOrWhiteSpace(state.Value) ? [] : [state.Value.Trim()];
                var locked = state.Operations.Count == 0
                    ? state.LockedItems.Select(item => item.Reason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason)) ?? "This can't be changed here."
                    : state.LockedItems.FirstOrDefault(item => item.EntityId == id)?.Reason;
                items.Add(new BulkFieldItem(id, values, locked));
            }

            views.Add(new BulkFieldView(field.Key, field.Label, isList, field.Operations, items));
        }

        return views;
    }
}
