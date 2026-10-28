using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The kinds of item a trail step can be. <see cref="Person"/> is an artist: it is opened as a person, not as a library title.</summary>
public static class EditTrailKinds
{
    public const string Show = "Show";
    public const string Season = "Season";
    public const string Episode = "Episode";
    public const string Album = "Album";
    public const string Track = "Track";
    public const string Person = "Person";

    /// <summary>The editor scope that edits an item of this kind (null for a kind with its own editor, such as a person).</summary>
    public static string? ScopeOf(string kind) => kind switch
    {
        Show => "series",
        Season => "season",
        Episode => "episode",
        Album => "album",
        Track => "track",
        _ => null,
    };
}

/// <summary>
/// Who is who on the page being edited (universal editor spec D7): the trail to the page's own item (Show &gt; Season &gt; Episode, or
/// Artist &gt; Album &gt; Track) and the children each step can switch to. It is read from the detail page the Dashboard already has (the
/// seasons and episodes of a show, the tracks of an album, the parent ids), so switching asks the Engine for nothing more than the fields of
/// the item switched to. Pure: nothing is changed or fetched here.
/// </summary>
public sealed class EditTrailCatalog
{
    /// <summary>How a season's own page is asked for when the bar needs its episodes (the same windowed answer the page uses).</summary>
    public static SequenceWindowQuery SeasonWindow => SequenceWindowQuery.Windowed;

    public static EditTrailCatalog Empty { get; } = new([], new Dictionary<Guid, IReadOnlyList<EditTrailItem>>(), new Dictionary<Guid, IReadOnlyList<EditTrailItem>>());

    private readonly Dictionary<Guid, IReadOnlyList<EditTrailItem>> _trails;
    private readonly Dictionary<Guid, IReadOnlyList<EditTrailItem>> _children;

    private EditTrailCatalog(
        IReadOnlyList<EditTrailItem> pageTrail,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> trails,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> children)
    {
        PageTrail = pageTrail;
        _trails = trails;
        _children = children;
    }

    /// <summary>Where the page's own item sits, outermost first, ending with the page's item. Empty when the page has no usable id.</summary>
    public IReadOnlyList<EditTrailItem> PageTrail { get; private set; }

    /// <summary>
    /// Fills in the parents the page does not name, from the Engine's own chain for the item (show and season of an episode, for example).
    /// The chain must end with the page's item; parents the page already names are kept as they are.
    /// </summary>
    public void AdoptAncestors(IReadOnlyList<EditTrailItem> chain)
    {
        if (chain.Count < 2 || PageTrail.Count == 0 || PageTrail.Count >= chain.Count || chain[^1].EntityId != PageTrail[^1].EntityId)
        {
            return;
        }

        var trail = new List<EditTrailItem>();
        for (var index = 0; index < chain.Count; index++)
        {
            trail.Add(index == chain.Count - 1 ? PageTrail[^1] : chain[index]);
            _trails[trail[index].EntityId] = [.. trail];
        }

        PageTrail = trail;
        for (var index = 0; index < trail.Count - 1; index++)
        {
            AddChildren(trail[index].EntityId, [trail[index + 1]]);
        }
    }

    /// <summary>The items a step can switch to (its seasons, episodes or tracks), in page order. Empty when it has none known.</summary>
    public IReadOnlyList<EditTrailItem> ChildrenOf(Guid entityId) =>
        _children.TryGetValue(entityId, out var children) ? children : [];

    /// <summary>The trail that ends at an item the page knows, or null.</summary>
    public IReadOnlyList<EditTrailItem>? TrailTo(Guid entityId) => _trails.GetValueOrDefault(entityId);

    /// <summary>The name of an item the page knows, or null.</summary>
    public string? NameOf(Guid entityId) => _trails.TryGetValue(entityId, out var trail) ? trail[^1].Title : null;

    /// <summary>True when the step has anything to switch to (children, or a parent trail above it).</summary>
    public bool CanSwitchFrom(Guid entityId) => ChildrenOf(entityId).Count > 0;

    /// <summary>True when some item on the page is not the one being edited, so the trail has something to offer.</summary>
    public bool HasChoices => _children.Values.Any(children => children.Count > 0);

    /// <summary>Adds children found later for an item the catalog knows (a season whose episodes were not on the page yet). Existing children stay.</summary>
    public void AddChildren(Guid parentId, IEnumerable<EditTrailItem> children)
    {
        if (!_trails.TryGetValue(parentId, out var parentTrail))
        {
            return;
        }

        var merged = new List<EditTrailItem>(ChildrenOf(parentId));
        foreach (var child in children)
        {
            if (child.EntityId == Guid.Empty || merged.Any(known => known.EntityId == child.EntityId))
            {
                continue;
            }

            merged.Add(child);
            _trails.TryAdd(child.EntityId, [.. parentTrail, child]);
        }

        _children[parentId] = merged;
    }

    /// <summary>Reads the catalog from the page. An item with nothing else around it gives a catalog of just itself.</summary>
    public static EditTrailCatalog From(DetailPageViewModel? model)
    {
        if (model is null || !Guid.TryParse(model.Id, out var modelId))
        {
            return Empty;
        }

        // The page's own item is named by the id its fields are edited and staged under: a show page is addressed by its collection but
        // edited through its root work, so the editor's id wins when the page names one.
        var pageId = model.EditorTarget is { EntityKind: "Work" } target && Guid.TryParse(target.EntityId, out var editorId) && editorId != Guid.Empty
            ? editorId
            : modelId;

        var trails = new Dictionary<Guid, IReadOnlyList<EditTrailItem>>();
        var children = new Dictionary<Guid, IReadOnlyList<EditTrailItem>>();
        IReadOnlyList<EditTrailItem> pageTrail;
        switch (model.EntityType)
        {
            case DetailEntityType.TvShow:
                pageTrail = [Page(model, pageId, EditTrailKinds.Show)];
                Register(trails, children, pageTrail, SeasonsOf(model));
                AddEpisodes(model, pageId, trails, children);
                break;
            case DetailEntityType.TvSeason:
            case DetailEntityType.TvEpisode:
                pageTrail = TvTrail(model, pageId, trails, children);
                break;
            case DetailEntityType.MusicAlbum:
                pageTrail = AlbumTrail(model, pageId, trails, children);
                break;
            default:
                pageTrail = [Page(model, pageId, model.EntityType.ToString())];
                break;
        }

        foreach (var step in pageTrail.Select((item, index) => (item, index)))
        {
            trails.TryAdd(step.item.EntityId, [.. pageTrail.Take(step.index + 1)]);
        }

        return new EditTrailCatalog(pageTrail, trails, children);
    }

    // Show > Season > Episode on a season or episode page. The show is the placement's container; the season is the group the page's
    // own episode sits in (an episode page) or the page itself (a season page).
    private static IReadOnlyList<EditTrailItem> TvTrail(
        DetailPageViewModel model,
        Guid pageId,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> trails,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> children)
    {
        var placement = model.SequencePlacement;
        var steps = new List<EditTrailItem>();
        if (placement is not null && Guid.TryParse(placement.ContainerId, out var showId) && showId != pageId)
        {
            var show = new EditTrailItem(showId, Fallback(placement.ContainerTitle, "This show"), EditTrailKinds.Show);
            steps.Add(show);
            Register(trails, children, [show], SeasonsOf(model));
        }

        if (model.EntityType == DetailEntityType.TvEpisode)
        {
            var current = placement?.OrderedItems.FirstOrDefault(item => item.IsCurrent && item.Id == model.Id)
                          ?? placement?.Groups.SelectMany(group => group.Items).FirstOrDefault(item => item.IsCurrent);
            var group = placement?.Groups.FirstOrDefault(candidate =>
                    current is not null && string.Equals(candidate.Key, current.GroupKey, StringComparison.Ordinal))
                ?? placement?.Groups.FirstOrDefault(candidate => candidate.Items.Any(item => item.Id == model.Id));
            if (group is not null && Guid.TryParse(group.EntityId, out var seasonId) && seasonId != pageId)
            {
                var season = new EditTrailItem(seasonId, Fallback(group.Title, "This season"), EditTrailKinds.Season, group.ArtworkSmallUrl ?? group.ArtworkUrl);
                var seasonTrail = new List<EditTrailItem>(steps) { season };
                Register(trails, children, seasonTrail, EpisodesOf(group));
                steps.Add(season);
            }
        }

        var self = Page(model, pageId, model.EntityType == DetailEntityType.TvSeason ? EditTrailKinds.Season : EditTrailKinds.Episode);
        if (model.EntityType == DetailEntityType.TvSeason && placement is not null)
        {
            var own = placement.Groups.FirstOrDefault(group => Guid.TryParse(group.EntityId, out var id) && id == pageId)
                      ?? (placement.Groups.Count == 1 ? placement.Groups[0] : null);
            if (own is not null)
            {
                Register(trails, children, [.. steps, self], EpisodesOf(own));
            }
        }

        steps.Add(self);
        return steps;
    }

    // Artist > Album > Track: the artist the album page already names, the album, and its owned tracks.
    private static IReadOnlyList<EditTrailItem> AlbumTrail(
        DetailPageViewModel model,
        Guid pageId,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> trails,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> children)
    {
        var steps = new List<EditTrailItem>();
        var companion = model.MusicAlbumCompanion;
        if (companion is not null && Guid.TryParse(companion.PrimaryArtistId, out var artistId) && artistId != pageId)
        {
            steps.Add(new EditTrailItem(artistId, Fallback(companion.PrimaryArtistName, "This artist"), EditTrailKinds.Person));
        }

        var album = Page(model, pageId, EditTrailKinds.Album);
        var albumTrail = new List<EditTrailItem>(steps) { album };
        var seen = new HashSet<Guid>();
        var tracks = new List<EditTrailItem>();
        foreach (var group in model.MediaGroups.Where(group =>
                     model.PrimaryModule.GroupKeys.Count == 0
                     || model.PrimaryModule.GroupKeys.Contains(group.Key, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var item in group.Items.Where(item => item.IsOwned))
            {
                if (Guid.TryParse(item.Id, out var trackId) && trackId != pageId && seen.Add(trackId))
                {
                    tracks.Add(new EditTrailItem(trackId, Fallback(item.Title, "This track"), EditTrailKinds.Track, item.ArtworkUrl));
                }
            }
        }

        Register(trails, children, albumTrail, tracks);
        if (steps.Count > 0)
        {
            // The artist's step offers the album it was opened from, so the person can go back up.
            Register(trails, children, steps, [album]);
        }

        steps.Add(album);
        return steps;
    }

    private static List<EditTrailItem> SeasonsOf(DetailPageViewModel model) =>
        (model.SequencePlacement?.Groups ?? [])
            .Where(group => Guid.TryParse(group.EntityId, out _))
            .Select(group => new EditTrailItem(Guid.Parse(group.EntityId!), Fallback(group.Title, "Season"), EditTrailKinds.Season, group.ArtworkSmallUrl ?? group.ArtworkUrl))
            .ToList();

    /// <summary>The owned episodes a season group lists, in page order, each named for the dropdown.</summary>
    public static List<EditTrailItem> EpisodesOf(SequenceGroupViewModel group) =>
        group.Items
            .Where(item => item.IsOwned && Guid.TryParse(item.Id, out _))
            .Select(item => new EditTrailItem(Guid.Parse(item.Id), Fallback(item.Title, "Episode"), EditTrailKinds.Episode, item.EpisodeStillUrl ?? item.ArtworkUrl))
            .DistinctBy(item => item.EntityId)
            .ToList();

    // The episodes of each season a show page already holds (the selected season's window); the other seasons add theirs when opened.
    private static void AddEpisodes(DetailPageViewModel model, Guid pageId, Dictionary<Guid, IReadOnlyList<EditTrailItem>> trails, Dictionary<Guid, IReadOnlyList<EditTrailItem>> children)
    {
        var show = Page(model, pageId, EditTrailKinds.Show);
        foreach (var group in model.SequencePlacement?.Groups ?? [])
        {
            if (!Guid.TryParse(group.EntityId, out var seasonId) || group.Items.Count == 0)
            {
                continue;
            }

            var season = new EditTrailItem(seasonId, Fallback(group.Title, "Season"), EditTrailKinds.Season, group.ArtworkSmallUrl ?? group.ArtworkUrl);
            Register(trails, children, [show, season], EpisodesOf(group));
        }
    }

    // Registers the children of the last step of <parentTrail>, each with the trail that leads to it.
    private static void Register(
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> trails,
        Dictionary<Guid, IReadOnlyList<EditTrailItem>> children,
        IReadOnlyList<EditTrailItem> parentTrail,
        IReadOnlyList<EditTrailItem> list)
    {
        var parent = parentTrail[^1];
        trails.TryAdd(parent.EntityId, parentTrail);
        if (list.Count == 0)
        {
            return;
        }

        var known = children.TryGetValue(parent.EntityId, out var existing) ? existing.ToList() : [];
        foreach (var child in list)
        {
            if (known.Any(item => item.EntityId == child.EntityId))
            {
                continue;
            }

            known.Add(child);
            trails.TryAdd(child.EntityId, [.. parentTrail, child]);
        }

        children[parent.EntityId] = known;
    }

    private static EditTrailItem Page(DetailPageViewModel model, Guid pageId, string kind) =>
        new(pageId, Fallback(model.Title, "This item"), kind, model.Artwork.CoverUrl ?? model.Artwork.PosterUrl);

    private static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
