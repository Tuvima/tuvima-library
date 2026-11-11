using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Which family the editor's left column shows (universal editor v3 D3): "This show", "This album", "This series", or nothing.</summary>
public enum EditorFamilyKind
{
    /// <summary>A film, a standalone book or song, a person, a collection or a Universe: the column is hidden and the tools use the full width.</summary>
    None,
    Show,
    Album,
    Series,
}

/// <summary>The parts of the family the editor header draws on their own: the fixed tile (the show, album or series) and the dropdowns under the title.</summary>
public enum EditorFamilyRailPart
{
    All,
    Tile,
    Selects,
}

/// <summary>
/// The family of the item being edited (universal editor v3 WP2, D3; v5 reads it for the header's navigation row): this show, album or series and only that, as show &gt; seasons
/// &gt; episodes (plus files with no episode), album &gt; tracks, or series &gt; books or issues. The shape (the root and its seasons, albums
/// or series) comes from the Engine's navigator read; the files under each parent are paged in as they are opened or scrolled to, from the
/// Engine's owned-children read, each with its matched state and the reason it is not matched. Nothing here is saved or staged: switching is
/// the <see cref="EditSessionFlow"/>'s job, and every item's staged changes stay in the session's pending list.
/// </summary>
public sealed class EditorFamilyState
{
    /// <summary>How many files one read brings in.</summary>
    public const int PageSize = 50;

    private readonly IEngineApiClient _api;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, Node> _nodes = [];
    private Guid _rootId;
    private Guid _requestedFor;
    private int _version;
    private CancellationTokenSource? _cts;

    public EditorFamilyState(IEngineApiClient api, ILogger logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>Which family is shown; <see cref="EditorFamilyKind.None"/> while loading an unknown item and for any item without one.</summary>
    public EditorFamilyKind Kind { get; private set; }

    /// <summary>True when the family is a folder of Other videos: a plain list of videos with named sections, never Show, Season, Episode or Book (item 17).</summary>
    public bool IsVideoFolder { get; private set; }

    /// <summary>True while the shape of the family is being read (the column shows row skeletons; the right side is usable at once).</summary>
    public bool Loading { get; private set; }

    /// <summary>The column is drawn: a family exists, or it is still being read.</summary>
    public bool Visible => Kind != EditorFamilyKind.None || Loading;

    public event Action? Changed;

    /// <summary>Forgets the family (the editor closed).</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _version++;
        _nodes.Clear();
        _rootId = Guid.Empty;
        _requestedFor = Guid.Empty;
        Kind = EditorFamilyKind.None;
        IsVideoFolder = false;
        Loading = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Makes the tree show the family of <paramref name="entityId"/> with that item in view. An item already in the tree only opens the
    /// parents above it; a new family is read. <paramref name="knownParents"/> are ids the editor already knows sit above the item
    /// (the page's trail, the Engine's scope chain), so their files are paged in without waiting for the person to open them.
    /// </summary>
    public async Task EnsureAsync(Guid entityId, IEnumerable<Guid>? knownParents = null, bool skipFamily = false)
    {
        if (entityId == Guid.Empty || skipFamily)
        {
            if (Kind != EditorFamilyKind.None || Loading || _nodes.Count > 0)
            {
                Reset();
            }

            return;
        }

        var parents = (knownParents ?? []).Where(id => id != Guid.Empty && id != entityId).ToList();
        if (_nodes.Count > 0 && (_nodes.ContainsKey(entityId) || parents.Any(_nodes.ContainsKey)) && Kind != EditorFamilyKind.None)
        {
            foreach (var parent in parents.Where(_nodes.ContainsKey))
            {
                await OpenAsync(parent);
            }

            if (_nodes.TryGetValue(entityId, out var known))
            {
                await OpenAncestorsAsync(known);
            }

            Changed?.Invoke();
            return;
        }

        if (_requestedFor == entityId)
        {
            return;
        }

        _requestedFor = entityId;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var version = ++_version;
        Loading = true;
        Changed?.Invoke();

        MediaEditorNavigatorDto? navigator;
        try
        {
            navigator = await _api.GetMediaEditorNavigatorAsync(entityId, structureOnly: true, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The family of {EntityId} could not be read for the editor", entityId);
            navigator = null;
        }

        if (version != _version)
        {
            return;
        }

        Loading = false;
        _nodes.Clear();
        if (navigator is null)
        {
            // No family: the Engine does not know the id as a library item (a person, a collection, a Universe), or it could not be reached.
            // The right side never waits on this, so the column simply stays out of the way.
            Kind = EditorFamilyKind.None;
            IsVideoFolder = false;
            _rootId = Guid.Empty;
            Changed?.Invoke();
            return;
        }

        Build(navigator);
        if (Kind == EditorFamilyKind.None)
        {
            Changed?.Invoke();
            return;
        }

        Changed?.Invoke();
        await OpenAsync(_rootId);
        foreach (var parent in parents.Where(_nodes.ContainsKey))
        {
            await OpenAsync(parent);
        }

        if (_nodes.TryGetValue(entityId, out var current))
        {
            await OpenAncestorsAsync(current);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Shows a video series in the order the person has arranged it while the Match tool is open (universal editor v3.1): the videos of each
    /// list take the given order, number labels follow it, and a video moved into another section sits under that section. Only the tree
    /// changes; nothing is saved, and the Engine's own order comes back when the family is read again.
    /// </summary>
    public void ApplyArrangement(IEnumerable<(Guid ParentId, IReadOnlyList<Guid> OrderedIds)> lists)
    {
        var changed = false;
        foreach (var (parentId, ids) in lists)
        {
            if (!_nodes.TryGetValue(parentId, out var parent))
            {
                continue;
            }

            var present = ids.Where(id => _nodes.TryGetValue(id, out var node) && !node.IsParent).ToList();
            foreach (var id in present)
            {
                var leaf = _nodes[id];
                if (leaf.ParentId is { } old && old != parentId && _nodes.TryGetValue(old, out var oldParent))
                {
                    oldParent.Leaves.Remove(id);
                }

                leaf.ParentId = parentId;
            }

            var rest = parent.Leaves.Where(id => !present.Contains(id) && _nodes.TryGetValue(id, out var node) && node.ParentId == parentId).ToList();
            parent.Leaves.Clear();
            parent.Leaves.AddRange(present);
            parent.Leaves.AddRange(rest);
            for (var i = 0; i < present.Count; i++)
            {
                _nodes[present[i]].Detail = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reads the family again (after a save or a match committed), keeping what was open.</summary>
    public async Task RefreshAsync(Guid entityId)
    {
        if (Kind == EditorFamilyKind.None)
        {
            return;
        }

        var open = _nodes.Values.Where(node => node.Expanded).Select(node => node.Id).ToList();
        _requestedFor = Guid.Empty;
        _nodes.Clear();
        Kind = EditorFamilyKind.None;
        IsVideoFolder = false;
        await EnsureAsync(entityId, open);
    }

    /// <summary>
    /// Takes deleted items out of the tree at once (universal editor v4 D13): each id leaves its parent's list, a deleted parent takes
    /// everything under it, and a parent's count drops so "Show more" stays true. Nothing is read from the Engine; the next refresh agrees.
    /// </summary>
    public void Remove(IEnumerable<Guid> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var changed = false;
        foreach (var id in entityIds.Distinct())
        {
            changed |= RemoveNode(id);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool RemoveNode(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node) || id == _rootId)
        {
            // The root (the show, album or series itself) is never taken out of its own tree; the editor closes instead.
            return false;
        }

        foreach (var child in node.Parents.Concat(node.Leaves).ToList())
        {
            RemoveNode(child);
        }

        if (node.ParentId is { } parentId && _nodes.TryGetValue(parentId, out var parent))
        {
            parent.Parents.Remove(id);
            if (parent.Leaves.Remove(id) && parent.Total > 0)
            {
                parent.Total--;
            }
        }

        _nodes.Remove(id);
        return true;
    }

    /// <summary>The trail that ends at an item in the tree, outermost first, for switching to it; null when the item is not in the tree.</summary>
    public IReadOnlyList<EditTrailItem>? TrailTo(Guid entityId)
    {
        if (!_nodes.TryGetValue(entityId, out var node))
        {
            return null;
        }

        var trail = new List<EditTrailItem>();
        for (var current = node; current is not null; current = current.ParentId is { } parent && _nodes.TryGetValue(parent, out var up) ? up : null)
        {
            trail.Insert(0, new EditTrailItem(current.Id, current.Title, current.Kind, current.ImageUrl));
        }

        return trail;
    }

    /// <summary>Whether the tree says an item is matched: true or false for an item it lists, null for one it does not know.</summary>
    public bool? IsMatched(Guid entityId) => _nodes.TryGetValue(entityId, out var node) ? node.Matched : null;

    /// <summary>The season, album or show an item sits under in the tree, or null when the tree does not know the item.</summary>
    public Guid? ParentOf(Guid entityId) => _nodes.TryGetValue(entityId, out var node) ? node.ParentId : null;

    /// <summary>Opens a parent if it is closed (no-op otherwise).</summary>
    public async Task OpenAsync(Guid parentId)
    {
        if (!_nodes.TryGetValue(parentId, out var node) || !node.IsParent)
        {
            return;
        }

        node.Expanded = true;
        if (node.PagesLoaded == 0 && !node.Loading && !node.Failed)
        {
            await LoadPageAsync(node);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Every matched file under a parent (a show's or a season's episodes), reading each page that is not read yet. Used by "Fetch all" in the
    /// Artwork tool; capped so a very long show cannot hold the editor up. Empty when the tree does not know the parent.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LeavesUnderAsync(Guid parentId, int max = 300)
    {
        var found = new List<Guid>();
        if (!_nodes.ContainsKey(parentId))
        {
            return found;
        }

        var pending = new Stack<Guid>([parentId]);
        while (pending.Count > 0 && found.Count < max)
        {
            var node = _nodes[pending.Pop()];
            while ((node.PagesLoaded == 0 || node.HasMore) && !node.Loading && !node.Failed)
            {
                var before = node.PagesLoaded;
                await LoadPageAsync(node);
                if (node.PagesLoaded == before)
                {
                    break;
                }
            }

            foreach (var leafId in node.Leaves)
            {
                var leaf = _nodes[leafId];
                if (leaf.Matched && found.Count < max)
                {
                    found.Add(leaf.Id);
                }
            }

            foreach (var child in node.Parents)
            {
                pending.Push(child);
            }
        }

        return found;
    }

    // The navigator's parents become tree nodes; the files under them arrive page by page.
    private void Build(MediaEditorNavigatorDto navigator)
    {
        _nodes.Clear();
        _rootId = Guid.Empty;
        Kind = EditorFamilyKind.None;
        IsVideoFolder = VideoFolderWording.IsVideoFolderMediaType(navigator.MediaType);
        var root = navigator.Nodes.FirstOrDefault(node => node.IsRoot);
        if (!navigator.Enabled || root is null || root.IsLeaf
            || string.Equals(navigator.MediaType, "Movies", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Kind = navigator.MediaType switch
        {
            "TV" => EditorFamilyKind.Show,
            "Music" => EditorFamilyKind.Album,
            _ => EditorFamilyKind.Series,
        };
        _rootId = root.EntityId;
        foreach (var node in navigator.Nodes.Where(node => node.IsRoot || !node.IsLeaf))
        {
            _nodes[node.EntityId] = new Node
            {
                Id = node.EntityId,
                ParentId = node.IsRoot ? null : node.ParentNodeId,
                Kind = KindOf(navigator.MediaType, node),
                Title = string.IsNullOrWhiteSpace(node.Title) ? node.Label : node.Title,
                Detail = null,
                Matched = !string.Equals(node.MatchState, "unmatched", StringComparison.OrdinalIgnoreCase),
                ImageUrl = string.IsNullOrWhiteSpace(node.ArtworkUrl) ? null : EngineImageProxyPath.ToBrowserUrl(node.ArtworkUrl, null),
                IsParent = true,
                Expanded = node.IsRoot,
            };
        }

        // Seasons (or albums, series) sit under their parent in the order the Engine gave.
        foreach (var node in _nodes.Values.Where(node => node.ParentId is not null))
        {
            if (_nodes.TryGetValue(node.ParentId!.Value, out var parent))
            {
                parent.Parents.Add(node.Id);
            }
            else
            {
                node.ParentId = _rootId;
                _nodes[_rootId].Parents.Add(node.Id);
            }
        }

        // Keep the Engine's order for the parents under each parent.
        var order = navigator.Nodes.Select((node, index) => (node.EntityId, index)).ToDictionary(pair => pair.EntityId, pair => pair.index);
        foreach (var node in _nodes.Values)
        {
            node.Parents.Sort((left, right) => order.GetValueOrDefault(left).CompareTo(order.GetValueOrDefault(right)));
        }
    }

    private static string KindOf(string mediaType, MediaEditorNavigatorNodeDto node) => node.NodeKind switch
    {
        "series" when mediaType == "TV" => EditTrailKinds.Show,
        "season" => EditTrailKinds.Season,
        "episode" => EditTrailKinds.Episode,
        "album" => EditTrailKinds.Album,
        "track" => EditTrailKinds.Track,
        _ => "Item",
    };

    private async Task LoadPageAsync(Node node)
    {
        var version = _version;
        var cts = _cts;
        node.Loading = true;
        Changed?.Invoke();
        MediaEditorOwnedChildSearchDto? page = null;
        try
        {
            page = await _api.GetMediaEditorOwnedChildrenAsync(
                node.Id, page: node.PagesLoaded + 1, pageSize: PageSize, directOnly: true, ct: cts?.Token ?? default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The files under {ParentId} could not be read for the editor's tree", node.Id);
        }

        if (version != _version)
        {
            return;
        }

        node.Loading = false;
        if (page is null)
        {
            node.Failed = true;
            Changed?.Invoke();
            return;
        }

        node.PagesLoaded++;
        node.Total = page.TotalCount;
        foreach (var file in page.Items)
        {
            var leaf = FileNode(file, node);
            if (_nodes.ContainsKey(leaf.Id))
            {
                continue;
            }

            _nodes[leaf.Id] = leaf;
            node.Leaves.Add(leaf.Id);
        }

        Changed?.Invoke();
    }

    private Node FileNode(MediaEditorOwnedChildDto file, Node parent)
    {
        var matched = string.Equals(file.MatchState, "matched", StringComparison.OrdinalIgnoreCase);
        var kind = Kind switch
        {
            EditorFamilyKind.Show => EditTrailKinds.Episode,
            EditorFamilyKind.Album => EditTrailKinds.Track,
            _ => "Item",
        };
        var title = !string.IsNullOrWhiteSpace(file.MatchedTitle) ? file.MatchedTitle! : file.Title;
        return new Node
        {
            Id = file.WorkId,
            ParentId = parent.Id,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? file.SourceFileName : title,
            Detail = NumberLabel(file),
            Matched = matched,
        };
    }

    private static string? NumberLabel(MediaEditorOwnedChildDto file)
    {
        if (string.IsNullOrWhiteSpace(file.MatchedNumber))
        {
            return null;
        }

        return file.DiscNumber is > 0 and var disc ? $"{disc}·{file.MatchedNumber}" : file.MatchedNumber;
    }

    private async Task OpenAncestorsAsync(Node node)
    {
        for (var parent = node.ParentId; parent is { } id && _nodes.TryGetValue(id, out var up); parent = up.ParentId)
        {
            if (!up.Expanded)
            {
                await OpenAsync(id);
            }
        }
    }

    private sealed class Node
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; set; }
        public string Kind { get; init; } = "Item";
        public string Title { get; init; } = string.Empty;
        public string? Detail { get; set; }
        public bool Matched { get; init; } = true;
        public string? ImageUrl { get; init; }
        public bool IsParent { get; init; }
        public bool Expanded { get; set; }
        public bool Loading { get; set; }
        public bool Failed { get; set; }
        public int PagesLoaded { get; set; }
        public int Total { get; set; }
        public List<Guid> Parents { get; } = [];
        public List<Guid> Leaves { get; } = [];

        public bool HasMore => IsParent && PagesLoaded > 0 && Leaves.Count < Total && !Failed;
    }
}
