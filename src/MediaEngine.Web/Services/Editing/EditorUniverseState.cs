using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The things of one kind that the rail has read so far, for the search that is typed now.</summary>
public sealed class UniverseKindList
{
    public IReadOnlyList<EditorUniverseThingDto> Items { get; internal set; } = [];

    /// <summary>How many things of the kind match the search (more than <see cref="Items"/> while the rest is still to be paged in).</summary>
    public int Total { get; internal set; }

    public bool HasMore { get; internal set; }

    public bool Loading { get; internal set; }

    /// <summary>True once the first page has been asked for, so a dropdown that was never opened shows nothing stale.</summary>
    public bool Loaded { get; internal set; }

    public string? Search { get; internal set; }

    /// <summary>Counts every read, so an answer that arrives after a newer search was typed is ignored.</summary>
    internal int Generation { get; set; }
}

/// <summary>
/// What the rail of the Universe editor knows (universal editor v4, D10 and WP11): the Universe, its kinds with how many things each
/// holds, and for each kind the things read so far, searched and paged. Everything comes from the Engine, so a kind added later needs no
/// editor change. Read-only: choosing a thing is done by the editor's flow, adding one is staged in the pending list.
/// </summary>
public sealed class EditorUniverseState
{
    public const int PageSize = 40;

    private readonly IEngineApiClient _api;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, UniverseKindList> _lists = new(StringComparer.Ordinal);
    private int _generation;

    public EditorUniverseState(IEngineApiClient api, ILogger? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>Raised whenever the rail should be drawn again.</summary>
    public event Action? Changed;

    /// <summary>The Universe's Wikidata ID; null while the editor is not on a Universe or one of its things.</summary>
    public string? Qid { get; private set; }

    /// <summary>The Universe's own editor id; <see cref="Guid.Empty"/> until the first read has answered.</summary>
    public Guid UniverseId { get; private set; }

    public string UniverseName { get; private set; } = string.Empty;

    public bool Loading { get; private set; }

    /// <summary>The first read failed: the rail then offers only the Universe itself.</summary>
    public bool Failed { get; private set; }

    /// <summary>The kinds the rail shows, in the Engine's order. A kind the Universe does not use yet (Concepts) is left out until it has a thing.</summary>
    public IReadOnlyList<EditorUniverseKindDto> Kinds { get; private set; } = [];

    /// <summary>True while the editor is on a Universe: the Universe kinds are drawn in the header instead of Season and Item.</summary>
    public bool Visible => Qid is not null;

    /// <summary>The things read so far for a kind (empty and not loaded when its dropdown was never opened).</summary>
    public UniverseKindList ListFor(string kindKey) =>
        _lists.TryGetValue(kindKey, out var list) ? list : new UniverseKindList();

    /// <summary>Reads the kinds and their counts for a Universe, once; asking again for the same Universe changes nothing.</summary>
    public async Task EnsureAsync(string qid, CancellationToken ct = default)
    {
        if (string.Equals(Qid, qid, StringComparison.OrdinalIgnoreCase) && (Loading || Kinds.Count > 0 || Failed))
        {
            return;
        }

        Reset();
        Qid = qid;
        await ReadKindsAsync(ct);
    }

    /// <summary>Reads everything again after a save: the counts, and the lists the person had open.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Qid is not { } qid)
        {
            return;
        }

        var open = _lists.Where(pair => pair.Value.Loaded).Select(pair => (pair.Key, pair.Value.Search)).ToList();
        _lists.Clear();
        await ReadKindsAsync(ct);
        foreach (var (key, search) in open)
        {
            await OpenListAsync(key, search, ct);
        }
    }

    /// <summary>Forgets the Universe (the editor closed or moved to something that is not part of one).</summary>
    public void Reset()
    {
        _generation++;
        Qid = null;
        UniverseId = Guid.Empty;
        UniverseName = string.Empty;
        Loading = false;
        Failed = false;
        Kinds = [];
        _lists.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reads the first page of a kind, for the search typed in its dropdown (null or empty lists them all).</summary>
    public async Task OpenListAsync(string kindKey, string? search, CancellationToken ct = default)
    {
        if (Qid is not { } qid)
        {
            return;
        }

        var generation = _generation;
        var list = ListFor(kindKey);
        _lists[kindKey] = list;
        var mine = ++list.Generation;
        list.Loading = true;
        list.Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Changed?.Invoke();
        try
        {
            var page = await _api.GetEditorUniverseThingsAsync(qid, kindKey, list.Search, 0, PageSize, ct);
            if (generation != _generation || mine != list.Generation)
            {
                return;
            }

            if (page is not null)
            {
                list.Items = page.Things;
                list.Total = page.Total;
                list.HasMore = page.HasMore;
                ApplyHeader(page);
            }

            list.Loaded = true;
        }
        catch (OperationCanceledException)
        {
            // The dropdown was closed or the editor left while the list was loading; nothing to show or report.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The Universe editor could not list the {Kind} of {UniverseQid}", kindKey, qid);
        }
        finally
        {
            if (generation == _generation && mine == list.Generation)
            {
                list.Loading = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Pages in the next part of a kind's list.</summary>
    public async Task LoadMoreAsync(string kindKey, CancellationToken ct = default)
    {
        if (Qid is not { } qid || !_lists.TryGetValue(kindKey, out var list) || list.Loading || !list.HasMore)
        {
            return;
        }

        var generation = _generation;
        var mine = ++list.Generation;
        list.Loading = true;
        Changed?.Invoke();
        try
        {
            var page = await _api.GetEditorUniverseThingsAsync(qid, kindKey, list.Search, list.Items.Count, PageSize, ct);
            if (generation != _generation || mine != list.Generation || page is null)
            {
                return;
            }

            list.Items = [.. list.Items, .. page.Things.Where(thing => list.Items.All(known => known.Id != thing.Id))];
            list.Total = page.Total;
            list.HasMore = page.HasMore;
        }
        catch (OperationCanceledException)
        {
            // The dropdown was closed while the next part was loading.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The Universe editor could not page in more {Kind} of {UniverseQid}", kindKey, qid);
        }
        finally
        {
            if (generation == _generation && mine == list.Generation)
            {
                list.Loading = false;
                Changed?.Invoke();
            }
        }
    }

    private async Task ReadKindsAsync(CancellationToken ct)
    {
        if (Qid is not { } qid)
        {
            return;
        }

        var generation = _generation;
        Loading = true;
        Failed = false;
        Changed?.Invoke();
        try
        {
            var kinds = await _api.GetEditorUniverseThingsAsync(qid, null, null, 0, 1, ct);
            if (generation != _generation)
            {
                return;
            }

            if (kinds is null)
            {
                Failed = true;
            }
            else
            {
                ApplyHeader(kinds);
            }
        }
        catch (OperationCanceledException)
        {
            // The editor was left while the kinds were loading.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The Universe editor could not read the kinds of {UniverseQid}", qid);
            if (generation == _generation)
            {
                Failed = true;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                Loading = false;
                Changed?.Invoke();
            }
        }
    }

    private void ApplyHeader(EditorUniverseThingsDto page)
    {
        UniverseId = page.UniverseId;
        UniverseName = page.UniverseName;
        Kinds = page.Kinds.Where(kind => kind.Available).ToList();
    }
}
