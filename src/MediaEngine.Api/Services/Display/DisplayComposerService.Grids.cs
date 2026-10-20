using System.Globalization;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Display;

// The "View all" grid read: the full item list behind a shelf, with sort, filters and paging.
public sealed partial class DisplayComposerService
{
    private const int MusicStatsBatchSize = 500;
    private const int MaxGenreOptions = 40;
    private static readonly IReadOnlySet<string> NoFormats = new HashSet<string>();

    private static readonly string[] TypeOrder =
    [
        DisplayGridValues.TypeMovie, DisplayGridValues.TypeTv, DisplayGridValues.TypeVideo, DisplayGridValues.TypeBook, DisplayGridValues.TypeComic,
        DisplayGridValues.TypeAudiobook, DisplayGridValues.TypeMusic, DisplayGridValues.TypeCollection,
    ];

    private static readonly string[] FormatOrder =
    [
        DisplayGridValues.FormatEpub, DisplayGridValues.FormatPdf, DisplayGridValues.FormatComic,
        DisplayGridValues.Format4K, DisplayGridValues.FormatHd, DisplayGridValues.FormatSd,
    ];

    private static readonly string[] StatusOrder =
    [
        DisplayGridValues.StatusNotStarted, DisplayGridValues.StatusInProgress, DisplayGridValues.StatusFinished,
    ];

    /// <summary>
    /// One page of a shelf's full item list. Returns null when there is no such grid; throws <see cref="DisplayGridQueryException"/>
    /// when the sort or a filter value is not one the grids offer. The items come from the same authorized projection
    /// the lane pages read, so the profile's content limit, library and lane grants apply exactly as they do there.
    /// </summary>
    public async Task<DisplayGridPageDto?> BuildGridAsync(
        string lane,
        string shelfKey,
        DisplayGridQuery query,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        var selection = ParseGridSelection(query);
        var definition = DisplayGridCatalog.Find(lane, shelfKey);
        return definition is null ? null : await BuildGridAsync(definition, selection, query.Offset, query.Limit, profileId, ct);
    }

    private static GridSelection ParseGridSelection(DisplayGridQuery query) => new(
        DisplayGridCatalog.NormalizeSort(query.Sort),
        DisplayGridCatalog.NormalizeType(query.Type),
        string.IsNullOrWhiteSpace(query.Genre) ? null : query.Genre.Trim(),
        DisplayGridCatalog.NormalizeFormat(query.Format),
        DisplayGridCatalog.NormalizeStatus(query.Status));

    private async Task<DisplayGridPageDto?> BuildGridAsync(
        DisplayGridDefinition definition,
        GridSelection selection,
        int offset,
        int limit,
        Guid? profileId,
        CancellationToken ct)
    {
        var sort = selection.Sort ?? definition.DefaultSort;
        var source = await BuildGridSourceAsync(definition, sort, profileId, ct);
        if (source is null)
        {
            return null;
        }

        var entries = source.Entries;
        var formatsApply = entries.Any(entry => entry.WorkIds.Count > 0 && entry.Type is
            DisplayGridValues.TypeMovie or DisplayGridValues.TypeTv or DisplayGridValues.TypeBook or DisplayGridValues.TypeComic);
        if (_formats is not null && (formatsApply || selection.Format is not null))
        {
            var scope = await _readService.LoadLibraryScopeAsync(ct);
            var workIds = entries.SelectMany(entry => entry.WorkIds).ToHashSet();
            var formats = DisplayFormatReader.ForViewer(await _formats.LoadAsync(ct), workIds, scope);
            entries = entries.Select(entry => entry with { Formats = FormatsOf(entry, formats) }).ToList();
        }
        else
        {
            formatsApply = false;
        }

        var matching = entries.Where(entry => Matches(entry, selection, skip: null)).ToList();
        var ordered = SortGridEntries(matching, sort);
        var start = Math.Max(0, offset);
        var take = Math.Clamp(limit <= 0 ? DisplayGridValues.DefaultPageSize : limit, 1, DisplayGridValues.MaxPageSize);
        var page = ordered.Skip(start).Take(take).Select(entry => entry.Card).ToList();
        var next = start + page.Count;
        var hasMore = next < ordered.Count;

        return new DisplayGridPageDto(
            definition.Lane,
            definition.ShelfKey,
            source.Title,
            source.Subtitle,
            page,
            ordered.Count,
            start,
            take,
            hasMore,
            hasMore ? next.ToString(CultureInfo.InvariantCulture) : null,
            sort,
            definition.DefaultSort,
            definition.RememberKey,
            DisplayGridCatalog.SortOptions,
            BuildGridFilterGroups(definition, entries, selection, formatsApply));
    }

    /// <summary>
    /// TV and native "See all": the shelf's full list in its default order, as a <see cref="DisplayShelfPageDto"/>.
    /// Returns null when the shelf has no grid, so the caller keeps its own shelf rules.
    /// </summary>
    private async Task<DisplayShelfPageDto?> TryBuildShelfPageFromGridAsync(
        string shelfKey,
        string? lane,
        int start,
        int take,
        Guid? profileId,
        CancellationToken ct)
    {
        var definition = DisplayGridCatalog.Find(string.IsNullOrWhiteSpace(lane) ? DisplayGridCatalog.HomeLane : lane, shelfKey);
        if (definition is null)
        {
            return null;
        }

        var grid = await BuildGridAsync(definition, new GridSelection(null, null, null, null, null), start, take, profileId, ct);
        if (grid is null)
        {
            return null;
        }

        return new DisplayShelfPageDto(
            new DisplayShelfDto(definition.ShelfKey, grid.Title, grid.Subtitle, grid.Items, definition.Route),
            grid.NextCursor,
            grid.Offset,
            grid.Limit,
            grid.HasMore);
    }

    // -- sources ----------------------------------------------------------------------------------------------

    private async Task<GridSource?> BuildGridSourceAsync(DisplayGridDefinition definition, string sort, Guid? profileId, CancellationToken ct)
    {
        var works = await _readService.LoadWorksAsync(ct);
        var progressByWork = LatestProgressByWork(await _readService.LoadStatesAsync(profileId, null, ct));
        var context = new GridContext(works, progressByWork);
        var title = definition.Title;
        var subtitle = definition.Subtitle;
        List<GridCard> cards;

        switch (definition.Source)
        {
            case DisplayGridSource.HomeWatchNext:
                cards = [.. works
                    .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Movie")
                    .Select(work => new GridCard(_cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)), false)),
                    .. _cards.BuildTvShowCards(works, progressByWork).Select(card => new GridCard(card with { TileTextMode = "coverOnly" }, false))];
                break;
            case DisplayGridSource.HomeReadNext:
                cards = [.. works
                    .Where(work => DisplayMediaRules.IsReadKind(work.MediaType))
                    .Select(work => new GridCard(_cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)), false))];
                break;
            case DisplayGridSource.HomeListenNext:
                cards = [.. BuildMusicAlbumCards(MusicWorks(works)).Select(card => new GridCard(card, false)),
                    .. works
                        .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Audiobook")
                        .Select(work => new GridCard(_cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)), false))];
                break;
            case DisplayGridSource.HomeContinue:
                {
                    var journey = (await _readService.LoadJourneyAsync(profileId, null, ct)).ToList();
                    cards = [.. CollapseJourneyByIdentity(journey)
                    .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) != "Music")
                    .Where(item => item.ProgressPct is > 0 and < 99.5)
                    .Select(item => new GridCard(_cards.FromJourney(item, "home"), true)),
                    .. BuildMusicJourneyAlbumCards(works, journey).Select(card => new GridCard(card, true))];
                    break;
                }
            case DisplayGridSource.ReadContinue:
            case DisplayGridSource.WatchContinue:
                {
                    var lane = definition.Lane;
                    var journey = await _readService.LoadJourneyAsync(profileId, lane, ct);
                    cards = [.. CollapseJourneyByIdentity(journey).Select(item => new GridCard(_cards.FromJourney(item, lane), true))];
                    break;
                }
            case DisplayGridSource.ReadRecentlyAdded:
            case DisplayGridSource.ReadAuthor:
                {
                    var readWorks = CollapseReadVariantsByQid(works.Where(work => DisplayMediaRules.IsReadKind(work.MediaType))).ToList();
                    if (definition.Source == DisplayGridSource.ReadAuthor)
                    {
                        readWorks = readWorks
                            .Where(work => !string.IsNullOrWhiteSpace(work.Author)
                                           && DisplayMediaRules.StableKey(work.Author) == definition.Preset)
                            .ToList();
                        if (readWorks.Count == 0)
                        {
                            return null;
                        }

                        title = readWorks[0].Author!;
                        subtitle = $"{readWorks.Count} titles";
                    }

                    cards = [.. readWorks.Select(work => new GridCard(_cards.FromWork(work, "read", progressByWork.GetValueOrDefault(work.WorkId)), false))];
                    break;
                }
            case DisplayGridSource.WatchTvShows:
                cards = [.. _cards.BuildTvShowCards(works.Where(work => DisplayMediaRules.IsWatchKind(work.MediaType)).ToList(), progressByWork)
                    .Select(card => new GridCard(card, false))];
                break;
            case DisplayGridSource.WatchVideos:
                cards = [.. _cards.BuildVideoCards(works, progressByWork).Select(card => new GridCard(card, false))];
                break;
            case DisplayGridSource.WatchMovies:
                cards = [.. works
                    .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Movie")
                    .Select(work => new GridCard(_cards.FromWork(work, "watch", progressByWork.GetValueOrDefault(work.WorkId)), false))];
                break;
            case DisplayGridSource.WatchGenre:
                {
                    var watchWorks = works.Where(work => DisplayMediaRules.IsWatchKind(work.MediaType)).ToList();
                    var matching = watchWorks
                        .Where(work => DisplayMediaRules.SplitValues(work.Genre).Any(genre => DisplayMediaRules.StableKey(genre) == definition.Preset))
                        .ToList();
                    if (matching.Count == 0)
                    {
                        return null;
                    }

                    title = matching
                        .SelectMany(work => DisplayMediaRules.SplitValues(work.Genre))
                        .First(genre => DisplayMediaRules.StableKey(genre) == definition.Preset);
                    var shows = _cards.BuildTvShowCards(watchWorks, progressByWork).ToDictionary(card => card.Id);
                    cards = [.. matching
                    .Select(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV"
                        ? shows.GetValueOrDefault(TvGroupId(work))
                        : _cards.FromWork(work, "watch", progressByWork.GetValueOrDefault(work.WorkId)))
                    .Where(card => card is not null)
                    .Cast<DisplayCardDto>()
                    .DistinctBy(card => card.Id)
                    .Select(card => new GridCard(card, false))];
                    subtitle = $"{cards.Count} titles";
                    break;
                }
            case DisplayGridSource.ListenContinue:
                {
                    var journey = await _readService.LoadJourneyAsync(profileId, "listen", ct);
                    cards = [.. CollapseJourneyByIdentity(journey)
                    .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) == "Audiobook")
                    .Where(item => item.ProgressPct is > 0 and < 99.5)
                    .GroupBy(item => item.WorkId)
                    .Select(group => group.OrderByDescending(item => item.LastAccessed).First())
                    .Select(item => new GridCard(_cards.FromJourney(item, "listen"), true))];
                    break;
                }
            case DisplayGridSource.ListenRecentlyPlayed:
                {
                    var journey = (await _readService.LoadJourneyAsync(profileId, "listen", ct)).ToList();
                    cards = [.. BuildMusicJourneyAlbumCards(MusicWorks(works), journey).Select(card => new GridCard(card, true))];
                    break;
                }
            case DisplayGridSource.ListenAlbums:
                // Every album with its own card. The shelf's "N new tracks added" fact is left out: over the whole
                // library it would claim every track is new.
                cards = [.. BuildMusicAlbumCards(MusicWorks(works)).Select(card => new GridCard(card, false))];
                break;
            case DisplayGridSource.ListenAudiobooks:
                cards = [.. works
                    .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Audiobook")
                    .Select(work => new GridCard(_cards.FromWork(work, "listen", progressByWork.GetValueOrDefault(work.WorkId)), false))];
                break;
            case DisplayGridSource.MyList:
            case DisplayGridSource.Liked:
                cards = await BuildPersonalGridCardsAsync(definition.Source, works, progressByWork, profileId, ct);
                break;
            default:
                return null;
        }

        // Play counts only decide the order of "Last played"; every other order skips the read.
        var stats = sort == DisplayGridValues.SortPlayed
            ? await LoadGridMusicStatsAsync(cards, context, profileId, ct)
            : new Dictionary<Guid, MusicPlayStat>();
        var entries = cards.Select(card => context.Entry(card, stats)).ToList();
        return new GridSource(entries, title, subtitle);
    }

    private async Task<List<GridCard>> BuildPersonalGridCardsAsync(
        DisplayGridSource source,
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork,
        Guid? profileId,
        CancellationToken ct)
    {
        // Only the signed-in profile's own lists are ever read; without one there is nothing to show.
        if (profileId is not { } profile || _personal is null)
        {
            return [];
        }

        var saved = source == DisplayGridSource.MyList;
        var entries = saved
            ? await _personal.LoadSavedAsync(profile, ct)
            : await _personal.LoadLikedAsync(profile, ct);
        if (entries.Count == 0)
        {
            return [];
        }

        var mediaIds = entries
            .Where(entry => entry.Kind is not (ProfileEntityKind.Collection or ProfileEntityKind.Playlist))
            .Select(entry => entry.EntityId)
            .Distinct()
            .ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, DisplayCardDto>()
            : ResolveItemCards(mediaIds, works, progressByWork, await _readService.LoadFavoriteWorkIdsAsync(profileId, ct));
        var collections = entries.Any(entry => entry.Kind == ProfileEntityKind.Collection)
            ? (await _readService.LoadHomeCollectionsAsync(profileId, ct)).GroupBy(row => row.CollectionId).ToDictionary(group => group.Key, group => group.First())
            : new Dictionary<Guid, DisplayHomeCollectionRow>();
        // The profile's playlists are only listed for a caller who can see any of the catalogue at all.
        var playlists = saved && works.Count > 0 && entries.Any(entry => entry.Kind == ProfileEntityKind.Playlist)
            ? await _personal.LoadPlaylistsAsync(profile, works.Select(work => work.WorkId).ToHashSet(), ct)
            : new Dictionary<Guid, ManagedCollectionDto>();

        var cards = new List<GridCard>();
        var seen = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            DisplayCardDto? card = entry.Kind switch
            {
                ProfileEntityKind.Collection => collections.TryGetValue(entry.EntityId, out var row) ? DisplayCardBuilder.FromHomeCollection(row) : null,
                ProfileEntityKind.Playlist => playlists.TryGetValue(entry.EntityId, out var playlist) ? FromPlaylist(playlist) : null,
                _ => media.GetValueOrDefault(entry.EntityId),
            };
            if (card is null || !seen.Add(card.Id))
            {
                continue;
            }

            cards.Add(new GridCard(card, false, entry.At));
        }

        return cards;
    }

    private static DisplayCardDto FromPlaylist(ManagedCollectionDto playlist)
    {
        var url = playlist.CoverArtworkUrl;
        var route = $"/listen/music/playlists/{playlist.Id:D}";
        return new DisplayCardDto(
            Id: playlist.Id,
            WorkId: null,
            AssetId: null,
            CollectionId: playlist.Id,
            MediaType: "Music",
            GroupingType: "playlist",
            Title: playlist.Name,
            Subtitle: playlist.ItemCount == 1 ? "1 item" : $"{playlist.ItemCount} items",
            Facts: [],
            Artwork: new DisplayArtworkDto(
                url, null, null, null, url, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null),
            PreferredShape: "square",
            Presentation: "album",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [new DisplayActionDto("openPlaylist", "Open", null, null, playlist.Id, route)],
            Flags: new DisplayCardFlagsDto(false, false, true, false),
            SortTimestamp: playlist.ModifiedAt ?? playlist.CreatedAt)
        {
            Description = playlist.Description,
        };
    }

    private static List<DisplayWorkRow> MusicWorks(IReadOnlyList<DisplayWorkRow> works) =>
        works.Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music").ToList();

    private async Task<IReadOnlyDictionary<Guid, MusicPlayStat>> LoadGridMusicStatsAsync(
        IReadOnlyList<GridCard> cards,
        GridContext context,
        Guid? profileId,
        CancellationToken ct)
    {
        var stats = new Dictionary<Guid, MusicPlayStat>();
        if (_musicStats is null || profileId is not { } profile)
        {
            return stats;
        }

        var trackIds = cards
            .Where(item => item.Card.Subject == DisplaySubjectKind.Album && item.Card.WorkId.HasValue)
            .SelectMany(item => context.AlbumTracks(item.Card.WorkId!.Value))
            .Select(track => track.WorkId)
            .Distinct()
            .ToList();
        foreach (var batch in trackIds.Chunk(MusicStatsBatchSize))
        {
            foreach (var (workId, stat) in await _musicStats.GetStatsAsync(profile, batch, ct))
            {
                stats[workId] = stat;
            }
        }

        return stats;
    }

    // -- sorting and filtering --------------------------------------------------------------------------------

    private static List<GridEntry> SortGridEntries(List<GridEntry> entries, string sort)
    {
        // Stable under every sort: ties fall back to the title (the browse title rule), then the card id.
        var titleThenId = (IOrderedEnumerable<GridEntry> ordered) => ordered
            .ThenBy(entry => entry.SortTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Card.Id);
        return sort switch
        {
            DisplayGridValues.SortTitle => entries
                .OrderBy(entry => entry.SortTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Card.Id)
                .ToList(),
            DisplayGridValues.SortYear => titleThenId(entries
                    .OrderBy(entry => entry.Card.SortYear <= 0)
                    .ThenByDescending(entry => entry.Card.SortYear))
                .ToList(),
            DisplayGridValues.SortPlayed => titleThenId(entries
                    .OrderBy(entry => entry.PlayedAt is null)
                    .ThenByDescending(entry => entry.PlayedAt ?? DateTimeOffset.MinValue)
                    .ThenByDescending(entry => entry.AddedAt))
                .ToList(),
            _ => titleThenId(entries.OrderByDescending(entry => entry.AddedAt)).ToList(),
        };
    }

    private static bool Matches(GridEntry entry, GridSelection selection, string? skip) =>
        (skip == DisplayGridValues.FilterType || selection.Type is null || entry.Type == selection.Type)
        && (skip == DisplayGridValues.FilterGenre || selection.Genre is null
            || entry.Card.Genres.Contains(selection.Genre, StringComparer.OrdinalIgnoreCase))
        && (skip == DisplayGridValues.FilterFormat || selection.Format is null || entry.Formats.Contains(selection.Format))
        && (skip == DisplayGridValues.FilterStatus || selection.Status is null || StatusToken(entry.Status) == selection.Status);

    private static IReadOnlyList<DisplayGridFilterGroupDto> BuildGridFilterGroups(
        DisplayGridDefinition definition,
        IReadOnlyList<GridEntry> entries,
        GridSelection selection,
        bool formatsApply)
    {
        var groups = new List<DisplayGridFilterGroupDto>();
        var counts = (string skip) => entries.Where(entry => Matches(entry, selection, skip)).ToList();

        // Media type: only when the list really mixes types.
        if (entries.Select(entry => entry.Type).Distinct().Count() >= 2)
        {
            var candidates = counts(DisplayGridValues.FilterType);
            AddGroup(groups, DisplayGridValues.FilterType, "Media type", selection.Type,
                TypeOrder.Select(type => (type, TypeLabel(type), candidates.Count(entry => entry.Type == type))));
        }

        // Genre: not on a list that is already one genre.
        if (definition.Source != DisplayGridSource.WatchGenre)
        {
            var candidates = counts(DisplayGridValues.FilterGenre);
            var named = candidates
                .SelectMany(entry => entry.Card.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Name: group.Key, Count: group.Count()))
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var options = named.Take(MaxGenreOptions).ToList();
            if (selection.Genre is not null && !options.Any(item => string.Equals(item.Name, selection.Genre, StringComparison.OrdinalIgnoreCase)))
            {
                var chosen = named.Where(item => string.Equals(item.Name, selection.Genre, StringComparison.OrdinalIgnoreCase)).ToList();
                options.Add(chosen.Count > 0 ? chosen[0] : (selection.Genre, 0));
            }

            AddGroup(groups, DisplayGridValues.FilterGenre, "Genre", selection.Genre,
                options.Select(item => (item.Name, item.Name, item.Count)));
        }

        if (formatsApply)
        {
            var candidates = counts(DisplayGridValues.FilterFormat);
            AddGroup(groups, DisplayGridValues.FilterFormat, "Format", selection.Format,
                FormatOrder.Select(format => (format, FormatLabel(format), candidates.Count(entry => entry.Formats.Contains(format)))));
        }

        // Progress: not on lists ordered by what was played last, and not where music (which has no progress) is in view.
        var inView = entries.Where(entry => selection.Type is null || entry.Type == selection.Type);
        if (!definition.IsPlayedDefault && !inView.Any(entry => entry.Type == DisplayGridValues.TypeMusic))
        {
            var candidates = counts(DisplayGridValues.FilterStatus);
            AddGroup(groups, DisplayGridValues.FilterStatus, "Progress", selection.Status,
                StatusOrder.Select(status => (status, StatusLabel(status), candidates.Count(entry => StatusToken(entry.Status) == status))));
        }

        return groups;
    }

    // A group is only worth showing when there is a choice to make (two or more options) or a value is already chosen.
    private static void AddGroup(
        List<DisplayGridFilterGroupDto> groups,
        string key,
        string label,
        string? selected,
        IEnumerable<(string Value, string Label, int Count)> options)
    {
        var shown = options
            .Where(option => option.Count > 0 || string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase))
            .Select(option => new DisplayGridFilterOptionDto(
                option.Value, option.Label, option.Count, string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (shown.Count >= 2 || shown.Any(option => option.Selected))
        {
            groups.Add(new DisplayGridFilterGroupDto(key, label, shown));
        }
    }

    // `formats` already holds only files in libraries the viewer may see: a 4K copy in a library they have no grant for
    // must not be offered, counted or matched, while one in any granted library counts.
    private static IReadOnlySet<string> FormatsOf(GridEntry entry, IReadOnlyDictionary<Guid, IReadOnlySet<string>> formats)
    {
        if (entry.WorkIds.Count == 0
            || entry.Type is not (DisplayGridValues.TypeMovie or DisplayGridValues.TypeTv or DisplayGridValues.TypeBook or DisplayGridValues.TypeComic))
        {
            return NoFormats;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workId in entry.WorkIds)
        {
            if (formats.TryGetValue(workId, out var owned))
            {
                found.UnionWith(owned);
            }
        }

        return found;
    }

    private static string StatusToken(DisplayContinuationState state) => state switch
    {
        DisplayContinuationState.Completed => DisplayGridValues.StatusFinished,
        DisplayContinuationState.InProgress => DisplayGridValues.StatusInProgress,
        _ => DisplayGridValues.StatusNotStarted,
    };

    private static string TypeOf(DisplayCardDto card) => DisplayMediaRules.NormalizeDisplayKind(card.MediaType) switch
    {
        "Movie" => DisplayGridValues.TypeMovie,
        "TV" => DisplayGridValues.TypeTv,
        "Video" => DisplayGridValues.TypeVideo,
        "Book" => DisplayGridValues.TypeBook,
        "Comic" => DisplayGridValues.TypeComic,
        "Audiobook" => DisplayGridValues.TypeAudiobook,
        "Music" => DisplayGridValues.TypeMusic,
        _ => DisplayGridValues.TypeCollection,
    };

    private static string TypeLabel(string type) => type switch
    {
        DisplayGridValues.TypeMovie => "Movies",
        DisplayGridValues.TypeTv => "TV shows",
        DisplayGridValues.TypeVideo => "Videos",
        DisplayGridValues.TypeBook => "Books",
        DisplayGridValues.TypeComic => "Comics",
        DisplayGridValues.TypeAudiobook => "Audiobooks",
        DisplayGridValues.TypeMusic => "Music",
        _ => "Collections",
    };

    private static string FormatLabel(string format) => format switch
    {
        DisplayGridValues.FormatEpub => "EPUB",
        DisplayGridValues.FormatPdf => "PDF",
        DisplayGridValues.FormatComic => "Comic file",
        DisplayGridValues.Format4K => "4K",
        DisplayGridValues.FormatHd => "HD",
        _ => "SD",
    };

    private static string StatusLabel(string status) => status switch
    {
        DisplayGridValues.StatusInProgress => "In progress",
        DisplayGridValues.StatusFinished => "Finished",
        _ => "Not started",
    };

    // -- shapes -----------------------------------------------------------------------------------------------

    private sealed record GridSelection(string? Sort, string? Type, string? Genre, string? Format, string? Status);

    /// <param name="FromJourney">True for cards built from play history, whose time stamp is when they were last played.</param>
    /// <param name="At">For personal lists, when the profile saved or liked the item.</param>
    private sealed record GridCard(DisplayCardDto Card, bool FromJourney, DateTimeOffset? At = null);

    private sealed record GridEntry(
        DisplayCardDto Card,
        IReadOnlyList<Guid> WorkIds,
        DateTimeOffset AddedAt,
        DateTimeOffset? PlayedAt,
        DisplayContinuationState Status,
        string SortTitle,
        string Type,
        IReadOnlySet<string> Formats);

    private sealed record GridSource(IReadOnlyList<GridEntry> Entries, string Title, string? Subtitle);

    /// <summary>What a grid needs to know about each card beyond the card: when it was added, last played, and which Works it stands for.</summary>
    private sealed class GridContext(IReadOnlyList<DisplayWorkRow> works, IReadOnlyDictionary<Guid, DisplayJourneyRow> progress)
    {
        private Dictionary<Guid, DisplayWorkRow>? _byWork;
        private Dictionary<Guid, List<DisplayWorkRow>>? _episodesByShow;
        private Dictionary<Guid, IReadOnlyList<DisplayWorkRow>>? _albumOfTrack;

        public IReadOnlyList<DisplayWorkRow> AlbumTracks(Guid trackWorkId) =>
            (_albumOfTrack ??= BuildAlbumIndex()).GetValueOrDefault(trackWorkId) ?? [];

        public GridEntry Entry(GridCard item, IReadOnlyDictionary<Guid, MusicPlayStat> stats)
        {
            var card = item.Card;
            var added = item.At ?? card.SortTimestamp;
            DateTimeOffset? played = null;
            IReadOnlyList<Guid> workIds = [];
            var status = card.ContinuationState;
            var sortTitle = card.Title;

            if (card.Subject == DisplaySubjectKind.TvShow)
            {
                var episodes = (_episodesByShow ??= BuildEpisodeIndex()).GetValueOrDefault(card.Id) ?? [];
                workIds = episodes.Select(episode => episode.WorkId).ToList();
                if (item.At is null && episodes.Count > 0)
                {
                    added = episodes.Max(episode => episode.CreatedAt);
                }

                played = LatestPlayed(episodes.Select(episode => episode.WorkId));
                status = ShowStatus(episodes);
            }
            else if (card.Subject == DisplaySubjectKind.Album && card.WorkId is { } representative)
            {
                var tracks = AlbumTracks(representative);
                if (item.At is null && tracks.Count > 0)
                {
                    added = tracks.Max(track => track.CreatedAt);
                }

                played = tracks
                    .Select(track => stats.TryGetValue(track.WorkId, out var stat) ? stat.LastPlayedAt : (DateTimeOffset?)null)
                    .Append(LatestPlayed(tracks.Select(track => track.WorkId)))
                    .Max();
            }
            else if (card.WorkId is { } workId && (_byWork ??= BuildWorkIndex()).TryGetValue(workId, out var row))
            {
                workIds = [workId];
                if (item.At is null)
                {
                    added = row.CreatedAt;
                }

                played = LatestPlayed([workId]);
                sortTitle = StringHelpers.FirstNonBlank(row.SortTitle, row.Title) ?? card.Title;
            }

            if (item.FromJourney)
            {
                played = played is { } known && known > card.SortTimestamp ? known : card.SortTimestamp;
            }

            return new GridEntry(card, workIds, added, played, status, sortTitle, TypeOf(card), NoFormats);
        }

        private DateTimeOffset? LatestPlayed(IEnumerable<Guid> workIds)
        {
            DateTimeOffset? latest = null;
            foreach (var id in workIds)
            {
                if (progress.TryGetValue(id, out var state) && (latest is null || state.LastAccessed > latest))
                {
                    latest = state.LastAccessed;
                }
            }

            return latest;
        }

        // A show reads as finished when every owned episode is, in progress as soon as any episode has been started.
        private DisplayContinuationState ShowStatus(IReadOnlyList<DisplayWorkRow> episodes)
        {
            var states = episodes
                .Select(episode => DisplayCardBuilder.StateFor(progress.TryGetValue(episode.WorkId, out var state) ? state.ProgressPct : null))
                .ToList();
            return states.Count > 0 && states.All(state => state == DisplayContinuationState.Completed) ? DisplayContinuationState.Completed
                : states.Any(state => state != DisplayContinuationState.Unstarted) ? DisplayContinuationState.InProgress
                : DisplayContinuationState.Unstarted;
        }

        private Dictionary<Guid, DisplayWorkRow> BuildWorkIndex()
        {
            var index = new Dictionary<Guid, DisplayWorkRow>();
            foreach (var work in works)
            {
                index.TryAdd(work.WorkId, work);
            }

            return index;
        }

        private Dictionary<Guid, List<DisplayWorkRow>> BuildEpisodeIndex() => works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV")
            .GroupBy(TvGroupId)
            .ToDictionary(group => group.Key, group => group.ToList());

        private Dictionary<Guid, IReadOnlyList<DisplayWorkRow>> BuildAlbumIndex()
        {
            var index = new Dictionary<Guid, IReadOnlyList<DisplayWorkRow>>();
            foreach (var album in works
                         .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
                         .Where(work => !string.IsNullOrWhiteSpace(work.Album))
                         .GroupBy(AlbumGroupKey, StringComparer.OrdinalIgnoreCase))
            {
                var tracks = album.ToList();
                foreach (var track in tracks)
                {
                    index.TryAdd(track.WorkId, tracks);
                }
            }

            return index;
        }
    }
}
