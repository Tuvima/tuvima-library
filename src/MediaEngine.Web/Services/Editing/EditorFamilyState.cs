using MediaEngine.Contracts.Metadata;
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

/// <summary>What a tree row stands for.</summary>
public enum FamilyRowType
{
    /// <summary>An item the person can switch to.</summary>
    Item,

    /// <summary>A label above a group of items ("Files with no episode"); not selectable.</summary>
    Heading,

    /// <summary>A plain sentence under a parent ("No files yet"); not selectable.</summary>
    Note,

    /// <summary>The end of a long list: the next page is read when it scrolls into view, or when the person presses it.</summary>
    More,

    /// <summary>A grey placeholder while a parent's files are being read.</summary>
    Skeleton,
}

/// <summary>One visible row of the family tree.</summary>
/// <param name="EntityId">The item the row switches to (the parent for a <see cref="FamilyRowType.More"/> row).</param>
/// <param name="Detail">The item's number in its parent ("3", "Disc 1 · 4"), shown ahead of the title.</param>
/// <param name="Reason">Why the item is not matched, in plain words; null when it is matched.</param>
/// <param name="Variant">"Also as audiobook" and the like, when one book or issue is owned in more than one form.</param>
public sealed record FamilyRow(
    FamilyRowType Type,
    Guid EntityId,
    string Title,
    string Kind,
    int Depth,
    bool Matched,
    string? Reason,
    string? Detail,
    string? ImageUrl,
    bool IsParent,
    bool Expanded,
    string? Variant);

/// <summary>
/// The family tree in the editor's left column (universal editor v3 WP2, D3): this show, album or series and only that, as show &gt; seasons
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

    /// <summary>True while the shape of the family is being read (the column shows row skeletons; the right side is usable at once).</summary>
    public bool Loading { get; private set; }

    /// <summary>The column heading: "This show", "This album" or "This series".</summary>
    public string Heading => Kind switch
    {
        EditorFamilyKind.Show => "This show",
        EditorFamilyKind.Album => "This album",
        EditorFamilyKind.Series => "This series",
        _ => string.Empty,
    };

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
        await EnsureAsync(entityId, open);
    }

    /// <summary>The rows to draw, top to bottom.</summary>
    public IReadOnlyList<FamilyRow> Rows()
    {
        var rows = new List<FamilyRow>();
        if (Kind == EditorFamilyKind.None || !_nodes.TryGetValue(_rootId, out var root))
        {
            if (Loading)
            {
                for (var i = 0; i < 6; i++)
                {
                    rows.Add(Skeleton(i == 0 ? 0 : 1));
                }
            }

            return rows;
        }

        AddRows(rows, root, 0);
        return rows;
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

    /// <summary>The name of an item in the tree, or null.</summary>
    public string? NameOf(Guid entityId) => _nodes.TryGetValue(entityId, out var node) ? node.Title : null;

    /// <summary>Opens or closes a parent. Opening reads its first page of files.</summary>
    public async Task ToggleAsync(Guid parentId)
    {
        if (!_nodes.TryGetValue(parentId, out var node) || !node.IsParent || parentId == _rootId)
        {
            return;
        }

        if (node.Expanded)
        {
            node.Expanded = false;
            Changed?.Invoke();
            return;
        }

        await OpenAsync(parentId);
        Changed?.Invoke();
    }

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

    /// <summary>Reads the next page of files under a parent (the end of its list scrolled into view, or the person pressed "Show more").</summary>
    public async Task LoadMoreAsync(Guid parentId)
    {
        if (_nodes.TryGetValue(parentId, out var node) && node.HasMore && !node.Loading)
        {
            await LoadPageAsync(node);
        }
    }

    /// <summary>Tries a parent's files again after they could not be read.</summary>
    public async Task RetryAsync(Guid parentId)
    {
        if (_nodes.TryGetValue(parentId, out var node) && node.Failed)
        {
            node.Failed = false;
            await LoadPageAsync(node);
        }
    }

    // The navigator's parents become tree nodes; the files under them arrive page by page.
    private void Build(MediaEditorNavigatorDto navigator)
    {
        _nodes.Clear();
        _rootId = Guid.Empty;
        Kind = EditorFamilyKind.None;
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
                Reason = node.MatchReason,
                ImageUrl = node.ArtworkUrl,
                IsParent = true,
                Expanded = node.IsRoot,
                Direct = navigator.MediaType == "TV" && node.IsRoot ? DirectLeaves.NoEpisode : DirectLeaves.Items,
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
            if (_nodes.TryGetValue(leaf.Id, out var existing) && !existing.IsParent)
            {
                existing.AddVariant(leaf.VariantOf(file));
                continue;
            }

            if (_nodes.ContainsKey(leaf.Id))
            {
                continue;
            }

            leaf.AddVariant(leaf.VariantOf(file));
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
            Reason = matched ? null : file.MatchReason ?? "Not matched yet.",
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

    private void AddRows(List<FamilyRow> rows, Node node, int depth)
    {
        rows.Add(new FamilyRow(FamilyRowType.Item, node.Id, node.Title, node.Kind, depth, node.Matched, node.Reason, node.Detail,
            node.ImageUrl, node.IsParent, node.Expanded, node.VariantText));
        if (!node.IsParent || !node.Expanded)
        {
            return;
        }

        // Seasons (or the album's tracks) first; then the files that sit straight under the parent.
        foreach (var parentId in node.Parents)
        {
            AddRows(rows, _nodes[parentId], depth + 1);
        }

        var headed = node.Direct == DirectLeaves.NoEpisode && node.Leaves.Count > 0;
        if (headed)
        {
            rows.Add(new FamilyRow(FamilyRowType.Heading, node.Id, "Files with no episode", string.Empty, depth + 1, true, null, null, null, false, false, null));
        }

        var leafDepth = headed ? depth + 2 : depth + 1;
        foreach (var leafId in node.Leaves)
        {
            var leaf = _nodes[leafId];
            rows.Add(new FamilyRow(FamilyRowType.Item, leaf.Id, leaf.Title, leaf.Kind, leafDepth, leaf.Matched, leaf.Reason, leaf.Detail,
                leaf.ImageUrl, false, false, leaf.VariantText));
        }

        if (node.Loading)
        {
            for (var i = 0; i < (node.PagesLoaded == 0 ? 3 : 1); i++)
            {
                rows.Add(Skeleton(depth + 1));
            }
        }
        else if (node.Failed)
        {
            rows.Add(new FamilyRow(FamilyRowType.Note, node.Id, "These files could not be read.", "Retry", depth + 1, true, null, null, null, false, false, null));
        }
        else if (node.HasMore)
        {
            rows.Add(new FamilyRow(FamilyRowType.More, node.Id, $"Show more ({node.Total - node.Leaves.Count} left)", string.Empty, depth + 1, true, null, null, null, false, false, null));
        }
        else if (node.PagesLoaded > 0 && node.Leaves.Count == 0 && node.Parents.Count == 0 && node.Direct != DirectLeaves.NoEpisode)
        {
            rows.Add(new FamilyRow(FamilyRowType.Note, node.Id, "No files in your library.", string.Empty, depth + 1, true, null, null, null, false, false, null));
        }
    }

    private static FamilyRow Skeleton(int depth) =>
        new(FamilyRowType.Skeleton, Guid.Empty, string.Empty, string.Empty, depth, true, null, null, null, false, false, null);

    private enum DirectLeaves
    {
        Items,
        NoEpisode,
    }

    private sealed class Node
    {
        private readonly List<string> _variants = [];

        public Guid Id { get; init; }
        public Guid? ParentId { get; set; }
        public string Kind { get; init; } = "Item";
        public string Title { get; init; } = string.Empty;
        public string? Detail { get; init; }
        public bool Matched { get; init; } = true;
        public string? Reason { get; init; }
        public string? ImageUrl { get; init; }
        public bool IsParent { get; init; }
        public bool Expanded { get; set; }
        public bool Loading { get; set; }
        public bool Failed { get; set; }
        public int PagesLoaded { get; set; }
        public int Total { get; set; }
        public DirectLeaves Direct { get; init; }
        public List<Guid> Parents { get; } = [];
        public List<Guid> Leaves { get; } = [];

        public bool HasMore => IsParent && PagesLoaded > 0 && Leaves.Count < Total && !Failed;

        public string? VariantText => _variants.Count > 1 ? "Also as " + string.Join(", ", _variants.Skip(1)) : null;

        public string VariantOf(MediaEditorOwnedChildDto file)
        {
            var extension = Path.GetExtension(file.SourceFilePath ?? string.Empty).TrimStart('.').ToLowerInvariant();
            if (extension is "m4b" or "mp3" or "m4a" or "aac" or "ogg" or "opus" or "flac")
            {
                return "audiobook";
            }

            return extension switch
            {
                "epub" or "mobi" or "azw3" => "ebook",
                "pdf" => "PDF",
                "cbz" or "cbr" => "comic file",
                _ => string.IsNullOrWhiteSpace(file.EditionLabel) ? "another file" : file.EditionLabel.ToLowerInvariant(),
            };
        }

        public void AddVariant(string variant)
        {
            if (!_variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
            {
                _variants.Add(variant);
            }
        }
    }
}
