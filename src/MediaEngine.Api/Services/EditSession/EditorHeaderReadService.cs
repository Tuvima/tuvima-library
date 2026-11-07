using System.Globalization;
using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The editor's header (universal editor v5, 3a): the way around a family (home, Season or Section, item), the cover's stored colours, the
/// supporting files that belong to each level, the file facts Quick facts shows now the Files tab is gone, and the numbers and per-kind
/// rules from <c>config/editor.json</c>. A read only: nothing is written. The Dashboard decides nothing from the kind of item; every
/// choice (is there a home button, a second dropdown, an item dropdown) is made here.
/// </summary>
public sealed class EditorHeaderReadService(
    IDatabaseConnection db,
    IMediaEditorNavigationReadService navigation,
    IItemExtraRepository extras,
    IItemExtrasScanner scanner,
    IEditorConfigurationSource editorConfig,
    ILibraryFolderResolver folders,
    ItemQuickFactsService quickFacts,
    MediaEditorWorkVersionReadRepository versions,
    IExtrasConfigurationSource? extrasRules = null,
    ILogger<EditorHeaderReadService>? logger = null) : IEditorFamilySource
{
    // The same threshold the collection rules and the Watch rows use for "watched".
    private const double WatchedProgressPercent = 99.5;

    // How many of a container's files are looked at to find the folder its members share.
    private const int MembersSampled = 3;

    private readonly ILogger _logger = logger ?? NullLogger<EditorHeaderReadService>.Instance;

    /// <summary>
    /// The header of the item with this work id; null when no such item is in the Library, or when the header could not be built (the
    /// failure is logged with the item id, and the editor still opens without a header rather than failing the whole read).
    /// <paramref name="supportingFileId"/> is the supporting file the editor is open on, so its row in the dropdown is marked current.
    /// </summary>
    public async Task<EditorHeaderDto?> GetHeaderAsync(Guid workId, Guid? profileId, CancellationToken ct = default, Guid? supportingFileId = null)
    {
        try
        {
            return await BuildHeaderAsync(workId, profileId, supportingFileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The editor header of item {ItemId} could not be built; the editor opens without it", workId);
            return null;
        }
    }

    private async Task<EditorHeaderDto?> BuildHeaderAsync(Guid workId, Guid? profileId, Guid? supportingFileId, CancellationToken ct)
    {
        var family = await ReadFamilyAsync(workId, ct).ConfigureAwait(false);
        if (family is null)
        {
            return null;
        }

        var config = editorConfig.Current;
        var current = family.Current;
        var rootIsCurrent = family.Root.EntityId == current.EntityId;
        var kind = ResolveKind(family.MediaType, current);
        var support = await ReadSupportAsync(family, ct).ConfigureAwait(false);

        var secondLevel = BuildSecondLevel(family, support);
        var levelChoice = ChooseLevel(family, current, secondLevel);
        var level = await BuildLevelAsync(family, levelChoice, support, query: null, offset: 0, profileId, supportingFileId, ct).ConfigureAwait(false);

        var cover = await ReadCoverAsync(family.Root.EntityId, ct).ConfigureAwait(false);

        // A movie, a book or a comic under a Series keeps the home button, with the Series as home: the same rule as a book in a Series
        // (spec 3a, "Book or comic in a Series"; L7, "a film series"). Only an item with no family at all has none.
        var showHome = family.HasFamily;
        var root = new EditorHeaderRootDto
        {
            Id = family.Root.EntityId,
            Title = family.Root.Title,
            MediaType = family.MediaType,
            CoverUrl = cover.CoverUrl,
        };

        return new EditorHeaderDto
        {
            Kind = kind,
            Root = root,
            CurrentId = current.EntityId,
            Label = !rootIsCurrent && family.HasFamily ? family.Root.Title : KindWord(kind),
            LabelIsKind = rootIsCurrent || !family.HasFamily,
            ShowHome = showHome,
            HomeId = showHome ? family.Root.EntityId : null,
            HomeIsCurrent = showHome && rootIsCurrent,
            SecondLevel = secondLevel,
            ShowItemDropdown = family.HasFamily || support.TotalCount > 0,
            Level = level,
            FolderButton = BuildFolderButton(support, levelChoice),
            Colors = cover.Colors,
            FileFacts = family.Current.IsLeaf || !family.HasFamily ? await ReadFileFactsAsync(current.EntityId, support, ct).ConfigureAwait(false) : null,
            Config = BuildConfig(config, kind),
        };
    }

    /// <summary>
    /// One level of the item dropdown: the items of a season, a section or the root (searched and paged), and the supporting files that
    /// belong to it. <paramref name="levelId"/> null means the root. Null when the item or the level is not in the family.
    /// </summary>
    public async Task<EditorHeaderLevelDto?> GetLevelAsync(
        Guid workId, Guid? levelId, string? query, int offset, Guid? profileId, CancellationToken ct = default, Guid? supportingFileId = null)
    {
        var family = await ReadFamilyAsync(workId, ct).ConfigureAwait(false);
        if (family is null)
        {
            return null;
        }

        var wanted = levelId ?? family.Root.EntityId;
        if (family.Nodes.All(node => node.EntityId != wanted))
        {
            return null;
        }

        var support = await ReadSupportAsync(family, ct).ConfigureAwait(false);
        var secondLevel = BuildSecondLevel(family, support);
        var choice = secondLevel?.Entries.Any(entry => entry.Id == wanted) == true
            ? new LevelChoice(wanted, LevelKinds.Mid)
            : new LevelChoice(wanted, family.HasSecondLevel ? LevelKinds.Root : LevelKinds.All);
        return await BuildLevelAsync(family, choice, support, query, Math.Max(0, offset), profileId, supportingFileId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The title's own folder (what the folder button opens) for the item with this work id; null when its files are in no title folder.
    /// For a book or film in a Series this is the item's own folder, not a sibling's. Reads the family's structure only (no values).
    /// </summary>
    public async Task<string?> GetItemFolderAsync(Guid workId, CancellationToken ct = default)
    {
        var navigator = await navigation.GetNavigatorAsync(workId, ct, structureOnly: true).ConfigureAwait(false);
        if (navigator is null)
        {
            return null;
        }

        var rootId = navigator.Enabled
            ? navigator.Nodes.FirstOrDefault(node => node.IsRoot)?.EntityId ?? navigator.SelectedEntityId
            : navigator.SelectedEntityId;
        return await ResolveItemFolderAsync(ReadTreePaths(rootId), navigator.SelectedEntityId, ct).ConfigureAwait(false);
    }

    /// <summary>True when this supporting file sits in the item's own title folder (a book's neighbour in its Series does not).</summary>
    public async Task<bool> OwnsSupportingFileAsync(Guid workId, Guid supportingFileId, CancellationToken ct = default)
    {
        var extra = await extras.GetAsync(supportingFileId, ct).ConfigureAwait(false);
        var itemFolder = extra is null ? null : await GetItemFolderAsync(workId, ct).ConfigureAwait(false);
        return extra is not null && itemFolder is not null
            && string.Equals(Path.GetFullPath(extra.ItemFolder), Path.GetFullPath(itemFolder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rules of one kind of item, for the editors that do not open on a work (a person, a Universe, a supporting file, a View photo
    /// or gallery, several items at once), so the Dashboard never writes their tabs, facts or search fields itself. Null for a kind
    /// <c>config/editor.json</c> does not know.
    /// </summary>
    public EditorHeaderConfigDto? GetKindConfig(string kind) =>
        EditorKinds.All.Contains(kind, StringComparer.Ordinal) ? BuildConfig(editorConfig.Current, kind) : null;

    /// <summary>
    /// The owned members of the family around an item, grouped the way the editor's dropdowns group them (a show's seasons, an album's
    /// tracks), each with its season, episode or track number. For the Bookmarks overview of a show, season or album; null when the item
    /// is not in the Library. When the item is a season, only that season is listed.
    /// </summary>
    public async Task<EditorFamilyMembers?> GetFamilyMembersAsync(Guid workId, CancellationToken ct = default)
    {
        var family = await ReadFamilyAsync(workId, ct).ConfigureAwait(false);
        if (family is null)
        {
            return null;
        }

        var leaves = family.Leaves.Where(node => node.IsOwned).ToList();
        var groups = new List<EditorFamilyGroup>();
        if (family.HasSecondLevel)
        {
            var mids = family.Mids.Where(mid => family.Current.IsRoot || mid.EntityId == family.Current.EntityId || family.Current.ParentId == mid.EntityId);
            foreach (var mid in mids)
            {
                var members = leaves.Where(node => node.ParentNodeId == mid.EntityId).Select(ToMember).OrderBy(member => member.Number ?? int.MaxValue).ThenBy(member => member.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                groups.Add(new EditorFamilyGroup(mid.EntityId, mid.Title, FirstNumber(mid.OrdinalLabel), members));
            }
        }
        else
        {
            var members = leaves.Select(ToMember).OrderBy(member => member.Number ?? int.MaxValue).ThenBy(member => member.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            groups.Add(new EditorFamilyGroup(null, string.Empty, null, members));
        }

        return new EditorFamilyMembers(family.Root.EntityId, family.Root.Title, family.MediaType, groups);

        static EditorFamilyMember ToMember(MediaEditorNavigatorNodeEnvelope node) => new(node.EntityId, node.Title, FirstNumber(node.OrdinalLabel));
    }

    private static int? FirstNumber(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var start = label.IndexOfAny("0123456789".ToCharArray());
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < label.Length && char.IsAsciiDigit(label[end]))
        {
            end++;
        }

        return int.TryParse(label.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    // -- Family ---------------------------------------------------------------------------------------------------------------------

    private async Task<Family?> ReadFamilyAsync(Guid workId, CancellationToken ct)
    {
        var navigator = await navigation.GetNavigatorAsync(workId, ct).ConfigureAwait(false);
        if (navigator is null)
        {
            return null;
        }

        // A standalone book, comic, audiobook or video has no family to walk, so the navigator is switched off for it. It still gets a
        // header: a single item with no home button.
        var nodes = navigator.Enabled && navigator.Nodes.Count > 0
            ? navigator.Nodes
            : [ReadSingleNode(navigator.SelectedEntityId, navigator.MediaType)];
        var root = nodes.FirstOrDefault(node => node.IsRoot) ?? nodes[0];
        var current = nodes.FirstOrDefault(node => node.EntityId == workId);
        if (current is null)
        {
            // A launch id that is an edition or a file: the navigator answers for its work.
            current = nodes.FirstOrDefault(node => node.EntityId == navigator.SelectedEntityId) ?? root;
        }

        var mediaType = navigator.MediaType;
        return new Family(mediaType, nodes, root, current, hasFamily: nodes.Count > 1);
    }

    private MediaEditorNavigatorNodeEnvelope ReadSingleNode(Guid workId, string mediaType)
    {
        using var connection = db.CreateConnection();
        var title = connection.QueryFirstOrDefault<string>("""
            SELECT value FROM canonical_values WHERE entity_id = @workId AND key = 'title' ORDER BY last_scored_at DESC LIMIT 1;
            """, new { workId }) ?? string.Empty;
        var kind = mediaType switch
        {
            "Books" => "book",
            "Audiobooks" => "audiobook",
            "Comics" => "issue",
            _ => "item",
        };
        return new MediaEditorNavigatorNodeEnvelope(
            workId, null, workId, string.Empty, kind, string.Empty, title, null, null, 0, true, true, true, null, null, [], true, true, false, 0);
    }

    // The navigator is enabled for a one-row TV, Music or Movies tree, so a standalone item comes back as a single node.
    private sealed class Family
    {
        public Family(string mediaType, IReadOnlyList<MediaEditorNavigatorNodeEnvelope> nodes, MediaEditorNavigatorNodeEnvelope root,
            MediaEditorNavigatorNodeEnvelope current, bool hasFamily)
        {
            MediaType = mediaType;
            Nodes = nodes;
            Root = new FamilyNode(root);
            Current = new FamilyNode(current);
            HasFamily = hasFamily;
            Mids = IsTwoLevel(mediaType)
                ? [.. nodes.Where(node => node.ParentNodeId == root.EntityId && !node.IsLeaf)]
                : [];
        }

        public string MediaType { get; }

        public IReadOnlyList<MediaEditorNavigatorNodeEnvelope> Nodes { get; }

        public FamilyNode Root { get; }

        public FamilyNode Current { get; }

        public bool HasFamily { get; }

        /// <summary>The seasons or sections: the parents directly under the root, for the kinds that have a second dropdown.</summary>
        public IReadOnlyList<MediaEditorNavigatorNodeEnvelope> Mids { get; }

        public bool HasSecondLevel => Mids.Count > 0;

        public string SecondLevelLabel => string.Equals(MediaType, VideoFolderWording.FolderMediaType, StringComparison.Ordinal) ? "Section" : "Season";

        public IEnumerable<MediaEditorNavigatorNodeEnvelope> Leaves => Nodes.Where(node => node.IsLeaf);

        private static bool IsTwoLevel(string mediaType) =>
            string.Equals(mediaType, "TV", StringComparison.Ordinal) || string.Equals(mediaType, VideoFolderWording.FolderMediaType, StringComparison.Ordinal);
    }

    private sealed class FamilyNode(MediaEditorNavigatorNodeEnvelope node)
    {
        public Guid EntityId { get; } = node.EntityId;

        public string Title { get; } = node.Title;

        public bool IsLeaf { get; } = node.IsLeaf;

        public bool IsRoot { get; } = node.IsRoot;

        public int Depth { get; } = node.Depth;

        public Guid? ParentId { get; } = node.ParentNodeId;

        public string NodeKind { get; } = node.NodeKind;
    }

    private static string ResolveKind(string mediaType, FamilyNode current) => mediaType switch
    {
        "TV" when current.IsRoot => EditorKinds.TvShow,
        "TV" when !current.IsLeaf => EditorKinds.TvSeason,
        "TV" => EditorKinds.TvEpisode,
        "Movies" when !current.IsLeaf => EditorKinds.Series,
        "Movies" => EditorKinds.Movie,
        "Music" when !current.IsLeaf => EditorKinds.Album,
        "Music" => EditorKinds.Song,
        "Books" when !current.IsLeaf => EditorKinds.Series,
        "Books" => EditorKinds.Book,
        "Audiobooks" when !current.IsLeaf => EditorKinds.Series,
        "Audiobooks" => EditorKinds.Audiobook,
        "Comics" when !current.IsLeaf => EditorKinds.Series,
        "Comics" => EditorKinds.Comic,
        VideoFolderWording.FolderMediaType when current.IsRoot && !current.IsLeaf => EditorKinds.Course,
        VideoFolderWording.FolderMediaType when !current.IsLeaf => EditorKinds.CourseSection,
        VideoFolderWording.FolderMediaType => EditorKinds.Video,
        _ => EditorKinds.Item,
    };

    // The kind word on the label line when there is no parent to name ("Movie", "Book"). These are the product's own words.
    private static string KindWord(string kind) => kind switch
    {
        EditorKinds.TvShow => "Show",
        EditorKinds.TvSeason => "Season",
        EditorKinds.TvEpisode => "Episode",
        EditorKinds.Movie => "Movie",
        EditorKinds.Album => "Album",
        EditorKinds.Song => "Song",
        EditorKinds.Book => "Book",
        EditorKinds.Audiobook => "Audiobook",
        EditorKinds.Comic => "Comic",
        EditorKinds.Series => "Series",
        EditorKinds.Course => VideoFolderWording.KindLabel,
        EditorKinds.CourseSection => VideoFolderWording.SectionLabel,
        EditorKinds.Video => VideoFolderWording.ItemLabel,
        _ => string.Empty,
    };

    private static EditorHeaderConfigDto BuildConfig(EditorConfiguration config, string kind)
    {
        var rules = config.KindFor(kind);
        return new EditorHeaderConfigDto
        {
            Kind = kind,
            InlineSupportingFiles = config.InlineSupportingFiles,
            CardsPerGroup = config.CardsPerGroup,
            RowsPerPage = config.RowsPerPage,
            ItemListPageSize = config.ItemListPageSize,
            Tabs = [.. rules.Tabs],
            FactsLine = [.. rules.FactsLine],
            QuickFacts = [.. rules.QuickFacts],
            MatchFields = [.. rules.MatchFields],
        };
    }

    // -- Levels ---------------------------------------------------------------------------------------------------------------------

    private enum LevelKinds
    {
        /// <summary>A family with no second dropdown: the root's list and every supporting file of the title.</summary>
        All,

        /// <summary>The root of a family that has a second dropdown, with no season or section chosen.</summary>
        Root,

        /// <summary>A season or a section.</summary>
        Mid,
    }

    private sealed record LevelChoice(Guid? LevelId, LevelKinds Kind);

    private static LevelChoice ChooseLevel(Family family, FamilyNode current, EditorHeaderSecondLevelDto? secondLevel)
    {
        if (!family.HasSecondLevel)
        {
            return new LevelChoice(family.Root.EntityId, LevelKinds.All);
        }

        if (secondLevel?.CurrentId is { } mid)
        {
            return new LevelChoice(mid, LevelKinds.Mid);
        }

        // A show or course opened on its root, with no season or section chosen: nothing to list yet. A video straight under a course
        // lists the course's own loose videos.
        return current.IsRoot
            ? new LevelChoice(null, LevelKinds.Root)
            : new LevelChoice(family.Root.EntityId, LevelKinds.Root);
    }

    private EditorHeaderSecondLevelDto? BuildSecondLevel(Family family, Support support)
    {
        if (!family.HasSecondLevel)
        {
            return null;
        }

        Guid? currentId = null;
        if (!family.Current.IsRoot)
        {
            currentId = family.Mids.Any(mid => mid.EntityId == family.Current.EntityId)
                ? family.Current.EntityId
                : family.Mids.Any(mid => mid.EntityId == family.Current.ParentId) ? family.Current.ParentId : null;
        }

        return new EditorHeaderSecondLevelDto
        {
            Label = family.SecondLevelLabel,
            CurrentId = currentId,
            Entries =
            [
                .. family.Mids.Select(mid => new EditorHeaderLevelEntryDto
                {
                    Id = mid.EntityId,
                    Title = mid.Title,
                    Number = support.MidNumbers.GetValueOrDefault(mid.EntityId),
                    ItemCount = mid.QuarantineCount,
                    SupportingFileCount = support.CountFor(mid.EntityId),
                }),
            ],
        };
    }

    private async Task<EditorHeaderLevelDto> BuildLevelAsync(
        Family family, LevelChoice choice, Support support, string? query, int offset, Guid? profileId, Guid? supportingFileId, CancellationToken ct)
    {
        var config = editorConfig.Current;
        var pageSize = config.ItemListPageSize;
        var levelNode = choice.LevelId is { } id ? family.Nodes.FirstOrDefault(node => node.EntityId == id) : null;

        // The level's items: a standalone item lists itself; a family lists the leaves directly under the level.
        IReadOnlyList<MediaEditorNavigatorNodeEnvelope> members = !family.HasFamily
            ? [.. family.Nodes.Where(node => node.EntityId == family.Current.EntityId)]
            : choice.LevelId is { } levelId
                ? [.. family.Leaves.Where(node => node.ParentNodeId == levelId)]
                : [];

        // A lesson with no stored number is numbered by its place in the whole ordered list, so a search never renumbers the lessons.
        var places = new Dictionary<Guid, int>(members.Count);
        for (var place = 0; place < members.Count; place++)
        {
            places.TryAdd(members[place].EntityId, place + 1);
        }

        var matching = string.IsNullOrWhiteSpace(query)
            ? members
            : [.. members.Where(node => node.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
                || (node.OrdinalLabel ?? string.Empty).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))];

        var page = matching.Skip(offset).Take(pageSize).ToList();
        var values = await ReadItemValuesAsync(
            [.. page.Select(node => node.EntityId), .. (levelNode is null ? [] : new[] { levelNode.EntityId })], profileId, ct).ConfigureAwait(false);
        int? seasonNumber = null;
        if (levelNode is not null)
        {
            seasonNumber = support.MidNumbers.TryGetValue(levelNode.EntityId, out var known) ? known : values.GetValueOrDefault(levelNode.EntityId)?.SeasonNumber;
        }

        var items = new List<EditorNavItemDto>(page.Count);
        for (var index = 0; index < page.Count; index++)
        {
            items.Add(BuildItem(family, page[index], values.GetValueOrDefault(page[index].EntityId), seasonNumber, places.GetValueOrDefault(page[index].EntityId)));
        }

        return new EditorHeaderLevelDto
        {
            LevelId = choice.LevelId,
            LevelTitle = levelNode?.Title,
            Total = matching.Count,
            Offset = offset,
            PageSize = pageSize,
            HasMore = offset + page.Count < matching.Count,
            Items = items,
            SupportingFiles = support.SummaryFor(choice, config.InlineSupportingFiles, supportingFileId),
        };
    }

    private EditorNavItemDto BuildItem(Family family, MediaEditorNavigatorNodeEnvelope node, ItemValues? values, int? seasonNumber, int position)
    {
        var isVideo = string.Equals(family.MediaType, VideoFolderWording.FolderMediaType, StringComparison.Ordinal);
        var isTv = string.Equals(family.MediaType, "TV", StringComparison.Ordinal);
        var number = isTv ? values?.EpisodeNumber ?? values?.Ordinal
            : string.Equals(family.MediaType, "Music", StringComparison.Ordinal) ? values?.TrackNumber ?? values?.Ordinal
            : values?.Ordinal ?? (isVideo && position > 0 ? position : null);

        // A course shows a plain number and a length, never an S/E code; only a show's episodes carry one.
        string? code = null;
        if (isTv && number is { } episode && (seasonNumber ?? values?.SeasonNumber) is { } season)
        {
            code = $"S{season.ToString("00", CultureInfo.InvariantCulture)}E{episode.ToString("00", CultureInfo.InvariantCulture)}";
        }
        else if (!isTv && number is { } plain && family.HasFamily)
        {
            code = plain.ToString("00", CultureInfo.InvariantCulture);
        }

        return new EditorNavItemDto
        {
            Id = node.EntityId,
            Kind = ItemKind(node, isVideo),
            Code = code,
            Number = number,
            Title = node.Title,
            StillUrl = node.ArtworkUrl,
            State = isVideo
                ? values?.Watched == true ? EditorNavItemStates.Watched : values?.Watched == false ? EditorNavItemStates.Unwatched : EditorNavItemStates.None
                : string.Equals(node.MatchState, "unmatched", StringComparison.OrdinalIgnoreCase) ? EditorNavItemStates.NotMatched : EditorNavItemStates.Matched,
            DurationSeconds = values?.DurationSeconds,
            IsCurrent = node.EntityId == family.Current.EntityId,
        };
    }

    private static string ItemKind(MediaEditorNavigatorNodeEnvelope node, bool isVideo) => isVideo ? "video" : node.NodeKind switch
    {
        "episode" => "episode",
        "track" => "track",
        "issue" => "comic",
        "book" => "book",
        "audiobook" => "audiobook",
        "movie" => "movie",
        _ => "item",
    };

    // -- Item values ----------------------------------------------------------------------------------------------------------------

    private sealed class ItemValues
    {
        public int? Ordinal { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public int? TrackNumber { get; set; }

        public double? DurationSeconds { get; set; }

        public bool? Watched { get; set; }
    }

    private async Task<Dictionary<Guid, ItemValues>> ReadItemValuesAsync(IReadOnlyList<Guid> workIds, Guid? profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        var result = new Dictionary<Guid, ItemValues>();
        var ids = workIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        // Four small reads by id (the work's own values, its representative file, that file's length and values), joined here, so a 50-row
        // page never multiplies the work's values by the file's values inside the data store.
        var blobs = ids.Select(GuidSql.ToBlob).ToArray();
        using var connection = db.CreateConnection();
        var ordinals = connection.Query<(Guid WorkId, long? Ordinal)>(
            "SELECT id AS WorkId, CAST(ordinal AS INTEGER) AS Ordinal FROM works WHERE id IN @blobs;", new { blobs })
            .ToDictionary(row => row.WorkId, row => row.Ordinal);
        if (ordinals.Count == 0)
        {
            return result;
        }

        var workValues = ReadKeyValues(connection, blobs, WorkValueKeys);
        var representatives = connection.Query<(Guid WorkId, Guid AssetId)>("""
            SELECT e.work_id AS WorkId, MIN(ma.id) AS AssetId
            FROM editions e
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id IN @blobs
            GROUP BY e.work_id;
            """, new { blobs }).ToDictionary(row => row.WorkId, row => row.AssetId);

        var assetBlobs = representatives.Values.Distinct().Select(GuidSql.ToBlob).ToArray();
        var assetValues = assetBlobs.Length == 0
            ? new Dictionary<(Guid EntityId, string Key), string?>()
            : ReadKeyValues(connection, assetBlobs, AssetValueKeys);
        var durations = assetBlobs.Length == 0
            ? new Dictionary<Guid, double>()
            : connection.Query<(Guid AssetId, double Duration)>("""
                SELECT pic.asset_id AS AssetId, pic.duration_secs AS Duration
                FROM media_assets ma
                INNER JOIN playback_inspection_cache pic ON pic.asset_id = ma.id AND pic.source_hash = ma.content_hash
                WHERE ma.id IN @assetBlobs AND pic.duration_secs IS NOT NULL;
                """, new { assetBlobs }).ToDictionary(row => row.AssetId, row => row.Duration);

        var watched = new Dictionary<Guid, bool>();
        if (profileId is { } profile && assetBlobs.Length > 0)
        {
            foreach (var state in connection.Query<(Guid AssetId, double Progress)>("""
                SELECT asset_id AS AssetId, progress_pct AS Progress
                FROM user_states
                WHERE user_id = @profile AND asset_id IN @assetBlobs;
                """, new { profile, assetBlobs }))
            {
                watched[state.AssetId] = state.Progress >= WatchedProgressPercent;
            }
        }

        foreach (var (workId, ordinal) in ordinals)
        {
            Guid? assetId = representatives.TryGetValue(workId, out var representative) ? representative : null;
            string? OfWork(string key) => workValues.GetValueOrDefault((workId, key));
            string? OfAsset(string key) => assetId is { } asset ? assetValues.GetValueOrDefault((asset, key)) : null;
            double? duration = assetId is { } timed && durations.TryGetValue(timed, out var seconds) && seconds > 0
                ? seconds
                : double.TryParse(OfWork("runtime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
                    ? minutes * 60
                    : null;
            result[workId] = new ItemValues
            {
                Ordinal = ordinal is > 0 and <= int.MaxValue ? (int)ordinal : null,
                SeasonNumber = ParseNumber(OfAsset("season_number")) ?? ParseNumber(OfWork("season_number"))
                    ?? (ordinal is >= 0 and <= int.MaxValue ? (int?)ordinal : null),
                EpisodeNumber = ParseNumber(OfAsset("episode_number")),
                TrackNumber = ParseNumber(OfAsset("track_number")),
                DurationSeconds = duration,
                Watched = assetId is { } owner && profileId.HasValue ? watched.GetValueOrDefault(owner) : null,
            };
        }

        return result;
    }

    private static readonly string[] WorkValueKeys = ["season_number", "runtime"];

    private static readonly string[] AssetValueKeys = ["season_number", "episode_number", "track_number"];

    // One value per (entity, key): the same largest value the earlier joined read picked.
    private static Dictionary<(Guid EntityId, string Key), string?> ReadKeyValues(
        System.Data.IDbConnection connection, byte[][] entityBlobs, string[] keys) =>
        connection.Query<(Guid EntityId, string Key, string? Value)>("""
            SELECT entity_id AS EntityId, key AS Key, CAST(MAX(value) AS TEXT) AS Value
            FROM canonical_values
            WHERE entity_id IN @entityBlobs AND key IN @keys
            GROUP BY entity_id, key;
            """, new { entityBlobs, keys }).ToDictionary(row => (row.EntityId, row.Key), row => row.Value);

    private static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    // -- Supporting files -----------------------------------------------------------------------------------------------------------

    private sealed record SupportEntry(
        Guid Id, string Kind, string Title, string TypeBadge, string Role, string RoleLabel, string? Group,
        int? PageCount, double? DurationSeconds, int ItemCount, bool IsMissing, ExtraScope Scope, EditorHeaderAlongsideDto? Alongside);

    private sealed class Support
    {
        public string? ItemFolder { get; init; }

        public IReadOnlyList<SupportEntry> Entries { get; init; } = [];

        public int TotalCount => Entries.Count;

        /// <summary>Per season or section: the number (season number, or its place), for the dropdown.</summary>
        public Dictionary<Guid, int> MidNumbers { get; init; } = [];

        /// <summary>Per season or section: the scope its supporting files are counted under.</summary>
        public Dictionary<Guid, MidScope> MidScopes { get; init; } = [];

        /// <summary>Per season or section: the folder below the title folder its files sit in, when it has one.</summary>
        public Dictionary<Guid, string> MidFolders { get; init; } = [];

        public required Func<LevelChoice, int, Guid?, EditorHeaderSupportingFilesDto> Summary { get; init; }

        public int CountFor(Guid midId) => MidScopes.TryGetValue(midId, out var scope) ? Entries.Count(entry => scope.Contains(entry.Scope)) : 0;

        public EditorHeaderSupportingFilesDto SummaryFor(LevelChoice choice, int inline, Guid? currentFileId) => Summary(choice, inline, currentFileId);
    }

    /// <summary>The scope a season or section's supporting files are counted under.</summary>
    private sealed record MidScope(int? Season, string? FolderPath)
    {
        public string Key => Season is { } season ? ExtraScope.ForSeason(season).Key : ExtraScope.ForFolder(FolderPath ?? string.Empty).Key;

        public bool Contains(ExtraScope scope)
        {
            if (Season is { } season)
            {
                return scope.Season == season && scope.Level is ExtraScopeLevel.Season or ExtraScopeLevel.Episode;
            }

            if (string.IsNullOrEmpty(FolderPath))
            {
                return false;
            }

            return scope.Level is ExtraScopeLevel.Folder or ExtraScopeLevel.File
                && scope.Path is { } path
                && (scope.Level == ExtraScopeLevel.Folder && path.Equals(FolderPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(FolderPath + "/", StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<Support> ReadSupportAsync(Family family, CancellationToken ct)
    {
        var tree = ReadTreePaths(family.Root.EntityId);
        var itemFolder = await ResolveItemFolderAsync(tree, family.Current.EntityId, ct).ConfigureAwait(false);

        var entries = new List<SupportEntry>();
        if (itemFolder is not null)
        {
            var stored = await extras.ListByItemFoldersAsync([itemFolder], ct).ConfigureAwait(false);
            entries = BuildEntries(stored, family.MediaType);
        }

        // Each season or section: its number and, for a section, the folder its videos sit in below the title folder.
        var midNumbers = new Dictionary<Guid, int>();
        var midScopes = new Dictionary<Guid, MidScope>();
        var midFolders = new Dictionary<Guid, string>();
        if (family.HasSecondLevel)
        {
            var values = await ReadItemValuesAsync([.. family.Mids.Select(mid => mid.EntityId)], null, ct).ConfigureAwait(false);
            var isTv = string.Equals(family.MediaType, "TV", StringComparison.Ordinal);
            foreach (var mid in family.Mids)
            {
                var number = values.GetValueOrDefault(mid.EntityId)?.SeasonNumber;
                if (isTv)
                {
                    if (number is { } season)
                    {
                        midNumbers[mid.EntityId] = season;
                        midScopes[mid.EntityId] = new MidScope(season, null);
                    }
                }
                else if (values.GetValueOrDefault(mid.EntityId)?.Ordinal is { } place)
                {
                    midNumbers[mid.EntityId] = place;
                }

                var folder = itemFolder is null ? null : FolderBelow(itemFolder, tree.FirstOrDefault(path => path.ParentWorkId == mid.EntityId)?.FilePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    midFolders[mid.EntityId] = folder;
                    if (!isTv)
                    {
                        midScopes[mid.EntityId] = new MidScope(null, folder);
                    }
                }
            }
        }

        var inlineEntries = entries;
        var scopes = midScopes;
        var hasSecondLevel = family.HasSecondLevel;

        EditorHeaderSupportingFilesDto Summary(LevelChoice choice, int inline, Guid? currentFileId)
        {
            IEnumerable<SupportEntry> inLevel;
            string? scopeKey = null;
            switch (choice.Kind)
            {
                case LevelKinds.Mid when choice.LevelId is { } mid && scopes.TryGetValue(mid, out var scope):
                    inLevel = inlineEntries.Where(entry => scope.Contains(entry.Scope));
                    scopeKey = scope.Key;
                    break;
                case LevelKinds.Mid:
                    inLevel = [];
                    break;
                case LevelKinds.Root when hasSecondLevel:
                    inLevel = inlineEntries.Where(entry => entry.Scope.Level == ExtraScopeLevel.Root);
                    scopeKey = ExtraScope.Root.Key;
                    break;
                default:
                    inLevel = inlineEntries;
                    break;
            }

            var list = inLevel.ToList();
            return new EditorHeaderSupportingFilesDto
            {
                ScopeKey = scopeKey,
                Count = list.Count,
                TotalInRoot = inlineEntries.Count,
                ShowAll = list.Count > inline,
                Files = [.. list.Take(inline).Select(entry => ToHeaderFile(entry, currentFileId))],
            };
        }

        return new Support
        {
            ItemFolder = itemFolder,
            Entries = entries,
            MidNumbers = midNumbers,
            MidScopes = midScopes,
            MidFolders = midFolders,
            Summary = Summary,
        };
    }

    private List<SupportEntry> BuildEntries(IReadOnlyList<ItemExtra> stored, string mediaType)
    {
        var rules = extrasRules?.Current;
        var entries = new List<SupportEntry>();
        var pictureFolders = new Dictionary<(string Folder, string Group), SupportEntry>();
        foreach (var extra in stored)
        {
            if (extra.Kind == ItemExtraKinds.Picture)
            {
                // A picture folder is one entry however many pictures it holds (D17).
                var key = (extra.ItemFolder.ToLowerInvariant(), (extra.GroupKey ?? string.Empty).ToLowerInvariant());
                if (pictureFolders.TryGetValue(key, out var folder))
                {
                    pictureFolders[key] = folder with { ItemCount = folder.ItemCount + 1 };
                    continue;
                }

                var first = ToEntry(extra, rules, string.IsNullOrWhiteSpace(extra.GroupKey) ? extra.DisplayTitle : extra.GroupKey, mediaType);
                pictureFolders[key] = first;
                entries.Add(first);
                continue;
            }

            entries.Add(ToEntry(extra, rules, extra.DisplayTitle, mediaType));
        }

        // The folder entries above were added as the first picture; swap in the counted ones.
        for (var index = 0; index < entries.Count; index++)
        {
            var match = pictureFolders.Values.FirstOrDefault(folder => folder.Id == entries[index].Id);
            if (match is not null)
            {
                entries[index] = match;
            }
        }

        return entries;
    }

    private static SupportEntry ToEntry(ItemExtra extra, ExtrasConfiguration? rules, string title, string mediaType) => new(
        extra.Id,
        extra.Kind switch
        {
            ItemExtraKinds.Picture => "pictures",
            ItemExtraKinds.Clip => "clip",
            _ => "document",
        },
        title,
        Path.GetExtension(extra.FilePath).TrimStart('.').ToUpperInvariant(),
        extra.EffectiveRole,
        rules?.LabelFor(extra.Kind, extra.EffectiveRole) ?? ExtraRoles.Label(extra.EffectiveRole),
        extra.GroupName,
        extra.PageCount,
        extra.DurationSeconds,
        1,
        extra.IsMissingFromDisk,
        extra.EffectiveScope,
        AlongsideOf(extra.EffectiveAlongside, mediaType));

    // "with lesson 02" in a course, "with episode 3" on a show, "with track 4" on an album: the words under the file's group.
    private static EditorHeaderAlongsideDto? AlongsideOf(ExtraScope? alongside, string mediaType)
    {
        if (alongside is not { } scope || scope.Level is ExtraScopeLevel.None or ExtraScopeLevel.Root)
        {
            return null;
        }

        var label = scope.Label();
        if (label.Length == 0)
        {
            return null;
        }

        if (scope.Level == ExtraScopeLevel.File && string.Equals(mediaType, VideoFolderWording.FolderMediaType, StringComparison.Ordinal))
        {
            var number = new string(label.TakeWhile(char.IsDigit).ToArray());
            label = number.Length > 0 ? $"lesson {number}" : label;
        }
        else if (scope.Level == ExtraScopeLevel.Episode && scope.Episode is { } episode)
        {
            label = $"episode {episode.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            label = char.ToLowerInvariant(label[0]) + label[1..];
        }

        return new EditorHeaderAlongsideDto { Key = scope.Key, Label = $"with {label}" };
    }

    private static EditorHeaderSupportingFileDto ToHeaderFile(SupportEntry entry, Guid? currentFileId) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Title = entry.Title,
        TypeBadge = entry.TypeBadge,
        Role = entry.Role,
        RoleLabel = entry.RoleLabel,
        Group = entry.Group,
        PageCount = entry.PageCount,
        DurationSeconds = entry.DurationSeconds,
        ItemCount = entry.ItemCount,
        IsMissing = entry.IsMissing,
        ShownAlongside = entry.Alongside,
        IsCurrent = currentFileId == entry.Id,
    };

    private sealed record TreePath(Guid WorkId, Guid? ParentWorkId, string FilePath);

    // One file path for each work under the root, so the title's folder and each section's folder can be found.
    private List<TreePath> ReadTreePaths(Guid rootId)
    {
        using var connection = db.CreateConnection();
        return connection.Query<(Guid WorkId, Guid? ParentWorkId, string FilePath)>("""
            WITH RECURSIVE work_tree(id, parent_work_id) AS (
                SELECT w.id, w.parent_work_id FROM works w WHERE w.id = @rootId
                UNION ALL
                SELECT child.id, child.parent_work_id FROM works child INNER JOIN work_tree ON child.parent_work_id = work_tree.id
            )
            SELECT work_tree.id AS WorkId, work_tree.parent_work_id AS ParentWorkId, MIN(ma.file_path_root) AS FilePath
            FROM work_tree
            INNER JOIN editions e ON e.work_id = work_tree.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE ma.file_path_root IS NOT NULL AND ma.file_path_root <> ''
            GROUP BY work_tree.id, work_tree.parent_work_id
            ORDER BY work_tree.id;
            """, new { rootId })
            .Select(row => new TreePath(row.WorkId, row.ParentWorkId, row.FilePath))
            .ToList();
    }

    // The title folder of the item the editor is on. Its own file decides when it has one, so a book or film in a Series gets its own
    // folder, not a sibling's. An item with no file of its own (a show, a season, an album, a course, a Series) takes the folder its
    // members share; members spread over several folders (a Series of books) have no single title folder.
    private async Task<string?> ResolveItemFolderAsync(IReadOnlyList<TreePath> tree, Guid currentId, CancellationToken ct)
    {
        var own = tree.FirstOrDefault(path => path.WorkId == currentId);
        if (own is not null)
        {
            var title = await scanner.ResolveTitleFolderAsync(own.FilePath, ct).ConfigureAwait(false);
            if (title is not null)
            {
                return title.ItemFolder;
            }
        }

        string? shared = null;
        foreach (var path in tree.Take(MembersSampled))
        {
            var title = await scanner.ResolveTitleFolderAsync(path.FilePath, ct).ConfigureAwait(false);
            if (title is null)
            {
                continue;
            }

            if (shared is null)
            {
                shared = title.ItemFolder;
            }
            else if (!string.Equals(shared, title.ItemFolder, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return shared;
    }

    // The folder below the title folder that holds this file ("Season 02"); null when the file sits in the title folder itself.
    private static string? FolderBelow(string itemFolder, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemFolder));
        if (string.IsNullOrEmpty(directory) || !directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || directory.Length <= root.Length)
        {
            return null;
        }

        return directory[root.Length..].Replace('\\', '/').Trim('/');
    }

    private static EditorHeaderFolderButtonDto? BuildFolderButton(Support support, LevelChoice choice)
    {
        if (support.ItemFolder is null)
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(support.ItemFolder);
        var name = Path.GetFileName(root);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var levelFolder = choice.Kind == LevelKinds.Mid && choice.LevelId is { } mid && support.MidFolders.TryGetValue(mid, out var folder)
            ? folder.Split('/').Last()
            : null;
        var parent = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(root) ?? string.Empty));
        return new EditorHeaderFolderButtonDto
        {
            FolderName = name,
            PathLabel = levelFolder is not null
                ? $"{name} / {levelFolder}"
                : string.IsNullOrEmpty(parent) ? name : $"{parent} / {name}",
        };
    }

    // -- Cover and colours ----------------------------------------------------------------------------------------------------------

    private sealed record CoverRead(string? CoverUrl, EditorCoverColorsDto? Colors);

    private sealed class CoverRow
    {
        public Guid AssetId { get; init; }

        public string? PrimaryHex { get; init; }

        public string? SecondaryHex { get; init; }

        public string? AccentHex { get; init; }
    }

    // The stored colours of the root's cover: the primary picture's artwork record, else the cover row the pages stream. No new extraction.
    private async Task<CoverRead> ReadCoverAsync(Guid rootId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        using var connection = db.CreateConnection();
        var cover = connection.QueryFirstOrDefault<CoverRow>("""
            SELECT ea.id AS AssetId, ea.primary_hex AS PrimaryHex, ea.secondary_hex AS SecondaryHex, ea.accent_hex AS AccentHex
            FROM entity_assets ea
            WHERE ea.entity_id = @rootId AND ea.asset_type = 'CoverArt'
              AND COALESCE(NULLIF(ea.local_image_path_m, ''), NULLIF(ea.local_image_path, '')) IS NOT NULL
            ORDER BY ea.is_user_override DESC, ea.is_preferred DESC, COALESCE(ea.updated_at, ea.created_at) DESC
            LIMIT 1;
            """, new { rootId });
        var linked = connection.QueryFirstOrDefault<CoverRow>("""
            SELECT link.artwork_asset_id AS AssetId, aa.primary_hex AS PrimaryHex, aa.secondary_hex AS SecondaryHex, aa.accent_hex AS AccentHex
            FROM entity_artwork_links link
            INNER JOIN artwork_assets aa ON aa.id = link.artwork_asset_id
            WHERE link.entity_id = @rootId AND link.entity_type = 'Work' AND link.role = 'Primary'
            ORDER BY link.is_user_override DESC, link.is_preferred DESC, link.sort_order, link.id
            LIMIT 1;
            """, new { rootId });

        var source = !string.IsNullOrWhiteSpace(linked?.PrimaryHex) ? linked : cover;
        var colors = source is null || (string.IsNullOrWhiteSpace(source.PrimaryHex) && string.IsNullOrWhiteSpace(source.SecondaryHex) && string.IsNullOrWhiteSpace(source.AccentHex))
            ? null
            : new EditorCoverColorsDto
            {
                PrimaryHex = NullIfBlank(source.PrimaryHex),
                SecondaryHex = NullIfBlank(source.SecondaryHex),
                AccentHex = NullIfBlank(source.AccentHex),
            };
        var coverId = linked?.AssetId ?? cover?.AssetId;
        return new CoverRead(coverId is { } id ? $"/stream/artwork/{id:D}" : null, colors);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // -- File facts -----------------------------------------------------------------------------------------------------------------

    // What the old Files tab read gave, for Quick facts: quality, container, size, folder, the read-only mark, and "File not found".
    private async Task<EditorFileFactsDto?> ReadFileFactsAsync(Guid workId, Support support, CancellationToken ct)
    {
        var totals = await quickFacts.GetAsync(workId, ct).ConfigureAwait(false);
        var selector = await versions.GetAsync(workId, ct).ConfigureAwait(false);
        var asset = selector?.Editions.SelectMany(edition => edition.Assets).FirstOrDefault();
        if (totals is null && asset is null)
        {
            return null;
        }

        var folder = asset?.Folder;
        var path = folder is not null && asset is not null ? Path.Combine(folder, asset.FileName) : null;
        var resolved = path is null ? null : folders.ResolveSourceForPath(path);
        return new EditorFileFactsDto
        {
            FileCount = totals?.FileCount ?? (asset is null ? 0 : 1),
            Quality = totals?.BestQuality,
            Container = asset?.Container,
            SizeBytes = asset?.FileSizeBytes ?? totals?.SizeBytes,
            FileName = asset?.FileName,
            Folder = folder,
            // Tuvima leaves a file alone when its library folder is Read-only, or when no library folder holds it.
            ReadOnly = path is not null ? resolved is null || !resolved.Source.IsWritable : totals?.ReadOnlyLibrary == true,
            IsMissing = path is not null && !File.Exists(path),
        };
    }
}
