using System.Globalization;
using MediaEngine.Api.Services.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayCardBuilder
{
    public DisplayCardDto FromWork(DisplayWorkRow row, string context, DisplayJourneyRow? progress)
    {
        var mediaKind = DisplayMediaRules.NormalizeDisplayKind(row.MediaType);
        var title = DisplayTitleFor(mediaKind, row.Title, row.Series, row.SeriesPosition);
        var action = PrimaryAction(row.AssetId, row.WorkId, row.CollectionId, mediaKind, progress?.ProgressPct, row.SeasonNumber, row.EpisodeNumber);
        var progressDto = progress is null || mediaKind == "Music" ? null : ToProgress(progress, action);
        return new DisplayCardDto(
            Id: row.WorkId,
            WorkId: row.WorkId,
            AssetId: row.AssetId,
            CollectionId: row.CollectionId,
            MediaType: mediaKind,
            GroupingType: "work",
            Title: title,
            Subtitle: SubtitleFor(mediaKind, CreatorFor(row), row.Series, row.SeriesPosition, row.ShowName, row.SeasonNumber, row.EpisodeNumber),
            Facts: BuildFacts(mediaKind, title, row.Year, row.Author, row.Artist, row.ContentRating, row.Runtime, row.Duration, row.PageCount, row.Rating, row.Narrator, row.Publisher),
            Artwork: ArtworkFor(row, row.AssetId),
            PreferredShape: PreferredShape(row.MediaType, row.BackgroundUrl, row.BannerUrl, row.SquareUrl),
            Presentation: PresentationFor(mediaKind),
            TileTextMode: string.Equals(context, "home", StringComparison.OrdinalIgnoreCase) ? "coverOnly" : "caption",
            PreviewPlacement: "smart",
            Progress: progressDto,
            Actions: WorkActions(action, row.WorkId, row.CollectionId, mediaKind, row.RootWorkId),
            Flags: FlagsFor(row.MediaType, isCollection: false),
            SortTimestamp: row.CreatedAt)
        {
            Subject = mediaKind == "TV" ? DisplaySubjectKind.TvEpisode : DisplaySubjectKind.Work,
            ContinuationState = mediaKind == "Music" ? DisplayContinuationState.Unstarted : StateFor(progress?.ProgressPct),
            EpisodeContext = mediaKind == "TV" ? EpisodeContext(row.RootWorkId, row.WorkId, row.AssetId, row.ShowName, row.Title, row.SeasonNumber, row.EpisodeNumber, progress) : null,
            Tagline = row.Tagline,
            Description = row.Description,
            Genres = DisplayMediaRules.SplitValues(row.Genre).ToList(),
            Badges = BuildBadges(mediaKind, row.Quality, StringHelpers.FirstNonBlank(row.Network, row.Source)),
            SortYear = ParseSortYear(row.Year),
            ListMetadata = new DisplayCardListMetadataDto(
                row.Author,
                row.Artist,
                row.Director,
                row.Narrator,
                row.Album,
                row.Series,
                row.Genre,
                StringHelpers.FirstNonBlank(row.Duration, row.Runtime),
                row.TrackNumber,
                row.Year,
                row.ShowName,
                row.SeasonNumber,
                row.EpisodeNumber)
            {
                RootWorkId = row.RootWorkId == Guid.Empty ? null : row.RootWorkId,
                ArtistPersonId = row.ArtistPersonId,
                IsUpdatingDetails = row.IsUpdatingDetails,
            },
        };
    }

    public DisplayCardDto FromJourney(DisplayJourneyRow row, string context)
    {
        var mediaKind = DisplayMediaRules.NormalizeDisplayKind(row.MediaType);
        var title = DisplayTitleFor(mediaKind, row.Title, row.Series, row.SeriesPosition);
        var action = PrimaryAction(row.AssetId, row.WorkId, row.CollectionId, mediaKind, row.ProgressPct, row.SeasonNumber, row.EpisodeNumber);

        return new DisplayCardDto(
            Id: row.WorkId,
            WorkId: row.WorkId,
            AssetId: row.AssetId,
            CollectionId: row.CollectionId,
            MediaType: mediaKind,
            GroupingType: "work",
            Title: title,
            Subtitle: mediaKind == "TV"
                ? ContinueEpisodeSubtitle(row.SeasonNumber, row.EpisodeNumber)
                    ?? SubtitleFor(mediaKind, StringHelpers.FirstNonBlank(row.Author, row.Artist, row.Narrator), row.Series, row.SeriesPosition, row.ShowName, row.SeasonNumber, row.EpisodeNumber)
                : SubtitleFor(mediaKind, StringHelpers.FirstNonBlank(row.Author, row.Artist, row.Narrator), row.Series, row.SeriesPosition, row.ShowName, row.SeasonNumber, row.EpisodeNumber),
            Facts: BuildFacts(mediaKind, title, row.Year, row.Author, row.Artist, row.ContentRating, row.Runtime, row.Duration, row.PageCount, row.Rating, row.Narrator),
            Artwork: ArtworkFor(row, row.AssetId),
            PreferredShape: PreferredShape(row.MediaType, row.BackgroundUrl, row.BannerUrl, row.SquareUrl),
            Presentation: PresentationFor(mediaKind),
            TileTextMode: string.Equals(context, "home", StringComparison.OrdinalIgnoreCase) ? "coverOnly" : "caption",
            PreviewPlacement: "smart",
            Progress: mediaKind == "Music" ? null : ToProgress(row, action),
            Actions: WorkActions(action, row.WorkId, row.CollectionId, mediaKind, row.RootWorkId),
            Flags: FlagsFor(row.MediaType, isCollection: false),
            SortTimestamp: row.LastAccessed)
        {
            Subject = mediaKind == "TV" ? DisplaySubjectKind.TvEpisode : DisplaySubjectKind.Work,
            ContinuationState = mediaKind == "Music" ? DisplayContinuationState.Unstarted : StateFor(row.ProgressPct),
            EpisodeContext = mediaKind == "TV" ? EpisodeContext(row.RootWorkId, row.WorkId, row.AssetId, row.ShowName, row.Title, row.SeasonNumber, row.EpisodeNumber, row) : null,
            Tagline = row.Tagline,
            Description = row.Description,
            Genres = DisplayMediaRules.SplitValues(row.Genre).ToList(),
            Badges = BuildBadges(mediaKind, row.Quality, StringHelpers.FirstNonBlank(row.Network, row.Source)),
            SortYear = ParseSortYear(row.Year),
            ListMetadata = new DisplayCardListMetadataDto(
                row.Author,
                row.Artist,
                null,
                row.Narrator,
                row.Album,
                row.Series,
                row.Genre,
                StringHelpers.FirstNonBlank(row.Duration, row.Runtime),
                row.TrackNumber,
                row.Year,
                row.ShowName,
                row.SeasonNumber,
                row.EpisodeNumber)
            {
                RootWorkId = row.RootWorkId == Guid.Empty ? null : row.RootWorkId,
            },
        };
    }

    public IReadOnlyList<DisplayCardDto> BuildCollectionCards(
        IReadOnlyList<DisplayWorkRow> works,
        string lane,
        int minimumSeriesItems = 2,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork = null) =>
        works
            .Where(work => work.CollectionId.HasValue)
            .GroupBy(work => work.CollectionId!.Value)
            .Select(group => ToCollectionCard(group.Key, group.ToList(), lane, minimumSeriesItems, progressByWork))
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

    public IReadOnlyList<DisplayCardDto> BuildMovieSeriesCards(
        IReadOnlyList<DisplayWorkRow> works,
        int minimumSeriesItems = 2,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork = null) =>
        works
            .Where(work => work.CollectionId.HasValue && DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Movie")
            .GroupBy(work => work.CollectionId!.Value)
            .Select(group => ToCollectionCard(group.Key, group.ToList(), "watch", minimumSeriesItems, progressByWork))
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

    public IReadOnlyList<DisplayCardDto> BuildTvShowCards(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork = null) =>
        works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV")
            .GroupBy(TvShowGroupId)
            .Select(group => ToTvShowCard(group.Key, group.ToList(), progressByWork))
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

    public static DisplayHeroDto ToHero(DisplayCardDto card, string eyebrow) =>
        new(card.Title, card.Subtitle, eyebrow, card.Artwork, card.Progress, card.Actions)
        {
            Subject = card.Subject,
            ContinuationState = card.ContinuationState,
            EpisodeContext = card.EpisodeContext,
            Facts = card.Facts,
            Id = card.Id,
            WorkId = card.WorkId,
            CollectionId = card.CollectionId,
            MediaType = card.MediaType,
            Presentation = card.Presentation,
            Tagline = card.Tagline,
            Description = card.Description,
            Genres = card.Genres,
            PreviewItems = card.PreviewItems,
            PreviewTotalCount = card.PreviewTotalCount,
        };

    public static DisplayCardDto FromHomeCollection(DisplayHomeCollectionRow row)
    {
        var action = new DisplayActionDto("openCollection", "Open", null, null, row.CollectionId, $"/details/collection/{row.CollectionId:D}?context=default");
        return new DisplayCardDto(
            Id: row.CollectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: row.CollectionId,
            MediaType: "Collection",
            GroupingType: "collection",
            Title: row.Title,
            Subtitle: row.Subtitle ?? CollectionCountLabel("Collection", row.ItemCount),
            Facts: HomeCollectionFacts(row),
            Artwork: ArtworkFor(row),
            PreferredShape: "landscape",
            Presentation: "default",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(false, false, true, false),
            SortTimestamp: row.CreatedAt)
        {
            Description = row.Subtitle,
            PreviewItems = row.PreviewItems,
            PreviewTotalCount = row.ItemCount,
            GroupSummary = BuildHomeCollectionSummary(row),
            SortYear = row.LatestYear ?? row.EarliestYear ?? 0,
        };
    }

    private DisplayCardDto? ToCollectionCard(
        Guid collectionId,
        IReadOnlyList<DisplayWorkRow> works,
        string lane,
        int minimumSeriesItems,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork)
    {
        if (works.Count == 0)
        {
            return null;
        }

        var representative = works
            .OrderByDescending(CollectionArtworkScore)
            .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.BackgroundUrl) || !string.IsNullOrWhiteSpace(work.BannerUrl))
            .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.CoverUrl) || !string.IsNullOrWhiteSpace(work.SquareUrl))
            .ThenByDescending(work => work.CreatedAt)
            .First();
        var mediaKind = DisplayMediaRules.NormalizeDisplayKind(representative.MediaType);
        if (!ShouldRenderCollectionCard(mediaKind, works, minimumSeriesItems))
        {
            return null;
        }

        var rawTitle = StringHelpers.FirstNonBlank(representative.CollectionTitle, representative.ShowName, representative.Series, representative.Album, representative.Artist, representative.Title) ?? "Collection";
        var title = mediaKind is "Movie" or "Book" or "Comic" or "Audiobook"
            ? SeriesDisplayFormatter.NormalizeContainerTitle(rawTitle, isStructuralSeries: true) ?? rawTitle
            : rawTitle;
        var representativeRootWorkId = representative.RootWorkId != Guid.Empty
            ? representative.RootWorkId
            : representative.WorkId;
        var action = new DisplayActionDto(CollectionActionKind(mediaKind), CollectionActionLabel(mediaKind), null, null, collectionId, CollectionUrlFor(collectionId, representativeRootWorkId, mediaKind, title));
        var artwork = CollectionArtworkFor(representative);
        var presentation = CollectionPresentation(lane, mediaKind);
        var previewItems = BuildSeriesPreviewItems(mediaKind, works, collectionId, progressByWork: progressByWork);
        var ownedCount = DistinctOwnedSeriesMemberCount(works);

        return new DisplayCardDto(
            Id: collectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: collectionId,
            MediaType: mediaKind,
            GroupingType: CollectionPresentation(lane, mediaKind),
            Title: title,
            Subtitle: CollectionSubtitle(mediaKind, works, ownedCount),
            Facts: CollectionFacts(mediaKind, works, representative.Genre, ownedCount, rating: representative.Rating),
            Artwork: artwork,
            PreferredShape: CollectionShape(lane, mediaKind, representative.MediaType, artwork),
            Presentation: presentation,
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: FlagsFor(representative.MediaType, isCollection: true),
            SortTimestamp: works.Max(work => work.CreatedAt))
        {
            Tagline = representative.Tagline,
            Description = representative.CollectionDescription,
            Genres = DisplayMediaRules.SplitValues(representative.Genre).ToList(),
            PreviewItems = previewItems,
            PreviewTotalCount = previewItems.Count > 0 ? ownedCount : null,
            GroupSummary = BuildGroupSummary(mediaKind, works, ownedCount, progressByWork, representative.CollectionType),
            SortYear = works.Select(work => ParseSortYear(work.Year)).DefaultIfEmpty(0).Max(),
        };
    }

    private DisplayCardDto? ToTvShowCard(
        Guid showRootWorkId,
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork)
    {
        if (works.Count == 0)
        {
            return null;
        }

        var representative = works
            .OrderByDescending(RootArtworkScore)
            .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.RootBackgroundUrl) || !string.IsNullOrWhiteSpace(work.RootBannerUrl))
            .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.BackgroundUrl) || !string.IsNullOrWhiteSpace(work.BannerUrl))
            .ThenByDescending(work => work.CreatedAt)
            .First();
        var artwork = RootArtworkFor(representative);
        var title = StringHelpers.FirstNonBlank(representative.ShowName, representative.Series, representative.Title, representative.CollectionTitle) ?? "TV Show";
        var action = new DisplayActionDto("openShow", "Open Show", showRootWorkId, null, null, $"/details/tvshow/{showRootWorkId:D}?context=watch");
        var previewItems = BuildSeriesPreviewItems("TV", works, tvShowRootId: showRootWorkId, progressByWork: progressByWork);
        var premiereYear = works.Select(work => ParseSortYear(work.Year)).Where(year => year > 0).DefaultIfEmpty(0).Min();
        var endYear = works.Select(work => ParseSortYear(work.SeriesEndYear)).Where(year => year >= premiereYear && year > 0).DefaultIfEmpty(0).Max();

        return new DisplayCardDto(
            Id: showRootWorkId,
            WorkId: null,
            AssetId: null,
            CollectionId: null,
            MediaType: "TV",
            GroupingType: "tvSeries",
            Title: title,
            Subtitle: CollectionSubtitle("TV", works),
            Facts: TvShowFacts(title, works, representative),
            Artwork: artwork,
            PreferredShape: PreferredShape("TV", artwork.BackgroundUrl, artwork.BannerUrl, artwork.SquareUrl),
            Presentation: "tvSeries",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: FlagsFor("TV", isCollection: true),
            SortTimestamp: works.Max(work => work.CreatedAt))
        {
            Subject = DisplaySubjectKind.TvShow,
            Tagline = representative.Tagline,
            Description = representative.RootDescription ?? representative.Description,
            Genres = DisplayMediaRules.SplitValues(representative.Genre).ToList(),
            Badges = BuildBadges("TV", representative.Quality, StringHelpers.FirstNonBlank(representative.Network, representative.Source)),
            PreviewItems = previewItems,
            PreviewTotalCount = works.Select(work => work.WorkId).Distinct().Count(),
            GroupSummary = new DisplayGroupSummaryDto
            {
                OwnedCount = works.Select(work => work.WorkId).Distinct().Count(),
                EarliestYear = premiereYear > 0 ? premiereYear : null,
                LatestYear = endYear > 0 ? endYear : premiereYear > 0 ? premiereYear : null,
                RelationshipLabel = "TV show",
            },
            SortYear = premiereYear,
        };
    }

    internal static DisplayContinuationState StateFor(double? percent) => percent switch
    {
        >= TvEpisodeContextResolver.CompletionPercent => DisplayContinuationState.Completed,
        > 0 => DisplayContinuationState.InProgress,
        _ => DisplayContinuationState.Unstarted,
    };

    private static DisplayEpisodeContextDto EpisodeContext(Guid rootId, Guid workId, Guid assetId, string? showTitle, string title, string? season, string? episode, DisplayJourneyRow? state) =>
        new(rootId == Guid.Empty ? workId : rootId, workId, assetId, showTitle ?? title, title,
            int.TryParse(season, out var sn) ? sn : null, int.TryParse(episode, out var en) ? en : null,
            StateFor(state?.ProgressPct), ValidSeconds(state?.PositionSeconds), ValidSeconds(state?.DurationSeconds));

    private static double? ValidSeconds(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static DisplayProgressDto ToProgress(DisplayJourneyRow row, DisplayActionDto resumeAction)
    {
        var position = ValidSeconds(row.PositionSeconds);
        var duration = ValidSeconds(row.DurationSeconds) is > 0 ? row.DurationSeconds : null;
        var remaining = position.HasValue && duration.HasValue ? Math.Max(0, duration.Value - position.Value) : (double?)null;
        var completed = row.ProgressPct >= TvEpisodeContextResolver.CompletionPercent;
        var label = completed ? DisplayMediaRules.NormalizeDisplayKind(row.MediaType) switch
        {
            "Movie" or "TV" => "Watched", "Book" or "Comic" => "Read", _ => "Finished",
        } : $"{Math.Clamp(row.ProgressPct, 0, 100):F0}%";
        if (!completed && remaining.HasValue) label += $" · {Math.Ceiling(remaining.Value / 60):F0} min remaining";
        return new(Math.Clamp(row.ProgressPct, 0, 100), label, row.LastAccessed, completed ? null : resumeAction)
        { PositionSeconds = position, DurationSeconds = duration, RemainingSeconds = remaining };
    }

    private static DisplayActionDto PrimaryAction(
        Guid? assetId,
        Guid workId,
        Guid? collectionId,
        string mediaKind,
        double? progressPct,
        string? seasonNumber,
        string? episodeNumber)
    {
        var isContinue = progressPct is > 0 and < 99.5;
        var isComplete = progressPct is >= 99.5;
        if (mediaKind is "Book" or "Comic")
        {
            return new DisplayActionDto("readWork", isContinue ? "Continue Reading" : "Read", workId, assetId, collectionId, WebUrlFor(workId, collectionId, mediaKind));
        }

        if (mediaKind == "TV")
        {
            var position = EpisodePosition(seasonNumber, episodeNumber);
            var label = $"{(isContinue ? "Resume" : isComplete ? "Play Again" : "Watch")}{(position is null ? string.Empty : $" {position}")}";
            return new DisplayActionDto("playAsset", label, workId, assetId, collectionId, $"/watch/player/{workId:D}");
        }

        if (mediaKind is "Movie" or "Music" or "Audiobook")
        {
            var label = isContinue
                ? ContinueLabel(mediaKind)
                : isComplete && mediaKind == "Movie"
                    ? "Play Again"
                    : "Play";
            return new DisplayActionDto("playAsset", label, workId, assetId, collectionId, WebUrlFor(workId, collectionId, mediaKind));
        }

        return new DisplayActionDto("openWork", "Open", workId, assetId, collectionId, WebUrlFor(workId, collectionId, mediaKind));
    }

    private static DisplayActionDto DetailsAction(Guid workId, Guid? collectionId, string mediaKind, Guid rootWorkId) =>
        new(
            "openWork",
            "Details",
            workId,
            null,
            collectionId,
            mediaKind == "TV" && rootWorkId != Guid.Empty && rootWorkId != workId
                ? TvEpisodeDetailRoute.Build(rootWorkId, workId)
                : $"/details/work/{workId:D}?context={WorkDetailContext(mediaKind)}");

    private static IReadOnlyList<DisplayActionDto> WorkActions(
        DisplayActionDto primaryAction,
        Guid workId,
        Guid? collectionId,
        string mediaKind,
        Guid rootWorkId) =>
        mediaKind == "Music"
            ? [primaryAction]
            : [primaryAction, DetailsAction(workId, collectionId, mediaKind, rootWorkId)];

    private static string WebUrlFor(Guid workId, Guid? collectionId, string mediaKind)
    {
        if (collectionId.HasValue)
        {
            return mediaKind switch
            {
                "Movie" or "TV" => $"/watch/player/{workId:D}",
                "Music" => SongPlaybackUrl(workId),
                "Audiobook" => $"/details/work/{workId:D}?context=listen",
                "Comic" => $"/details/work/{workId:D}?context=comics",
                "Book" => $"/details/work/{workId:D}?context=read",
                _ => $"/details/collection/{collectionId:D}?context=default",
            };
        }

        return mediaKind switch
        {
            "Movie" => $"/watch/player/{workId:D}",
            "TV" => $"/watch/player/{workId:D}",
            "Music" => SongPlaybackUrl(workId),
            "Audiobook" => $"/details/work/{workId:D}?context=listen",
            "Comic" => $"/details/work/{workId:D}?context=comics",
            "Book" => $"/details/work/{workId:D}?context=read",
            _ => $"/details/work/{workId:D}?context=default",
        };
    }

    private static string CollectionUrlFor(Guid collectionId, Guid representativeWorkId, string mediaKind, string title) =>
        mediaKind switch
        {
            "Movie" => $"/details/movieseries/{collectionId:D}?context=watch",
            "TV" => $"/details/tvshow/{representativeWorkId:D}?context=watch",
            "Music" => $"/details/musicalbum/{representativeWorkId:D}?context=listen",
            "Audiobook" => $"/details/collection/{collectionId:D}?context=listen",
            "Comic" => $"/details/comicseries/{collectionId}?context=comics",
            "Book" => $"/details/bookseries/{collectionId}?context=read",
            _ => $"/details/collection/{collectionId:D}?context=default",
        };

    private static string WorkDetailContext(string mediaKind) =>
        mediaKind switch
        {
            "Movie" or "TV" => "watch",
            "Music" or "Audiobook" => "listen",
            "Book" or "Comic" => "read",
            _ => "default",
        };

    private static DisplayCardFlagsDto FlagsFor(string mediaType, bool isCollection) =>
        new(DisplayMediaRules.IsWatchKind(mediaType) || DisplayMediaRules.IsListenKind(mediaType), DisplayMediaRules.IsReadKind(mediaType), isCollection, false);

    private static DisplayArtworkDto ArtworkFor(IDisplayArtworkRow row, Guid? assetId = null)
    {
        var hasPrimaryArtwork = !string.IsNullOrWhiteSpace(row.CoverUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverSmallUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverMediumUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverLargeUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareSmallUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareMediumUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareLargeUrl);
        var managedCoverFallback = !hasPrimaryArtwork && assetId.HasValue
            ? $"/stream/{assetId.Value:D}/cover"
            : null;

        return new DisplayArtworkDto(
            StringHelpers.FirstNonBlank(row.CoverUrl, managedCoverFallback),
            row.CoverSmallUrl,
            row.CoverMediumUrl,
            row.CoverLargeUrl,
            row.SquareUrl,
            row.SquareSmallUrl,
            row.SquareMediumUrl,
            row.SquareLargeUrl,
            row.BannerUrl,
            row.BannerSmallUrl,
            row.BannerMediumUrl,
            row.BannerLargeUrl,
            row.BackgroundUrl,
            row.BackgroundSmallUrl,
            row.BackgroundMediumUrl,
            row.BackgroundLargeUrl,
            row.LogoUrl,
            ParseInt(row.CoverWidthPx),
            ParseInt(row.CoverHeightPx),
            ParseInt(row.SquareWidthPx),
            ParseInt(row.SquareHeightPx),
            ParseInt(row.BannerWidthPx),
            ParseInt(row.BannerHeightPx),
            ParseInt(row.BackgroundWidthPx),
            ParseInt(row.BackgroundHeightPx),
            row.AccentColor);
    }

    private static DisplayArtworkDto CollectionArtworkFor(DisplayWorkRow representative)
    {
        var collectionCover = representative.CollectionCoverUrl;
        var collectionSquare = representative.CollectionSquareUrl;
        var collectionBanner = representative.CollectionBannerUrl;
        var collectionBackground = representative.CollectionBackgroundUrl;
        var collectionLogo = representative.CollectionLogoUrl;

        var row = new ArtworkProjection
        {
            CoverUrl = StringHelpers.FirstNonBlank(collectionCover, representative.RootCoverUrl, representative.CoverUrl),
            CoverSmallUrl = StringHelpers.FirstNonBlank(collectionCover, representative.RootCoverSmallUrl, representative.CoverSmallUrl),
            CoverMediumUrl = StringHelpers.FirstNonBlank(collectionCover, representative.RootCoverMediumUrl, representative.CoverMediumUrl),
            CoverLargeUrl = StringHelpers.FirstNonBlank(collectionCover, representative.RootCoverLargeUrl, representative.CoverLargeUrl),
            SquareUrl = StringHelpers.FirstNonBlank(collectionSquare, representative.RootSquareUrl, representative.SquareUrl),
            SquareSmallUrl = StringHelpers.FirstNonBlank(collectionSquare, representative.RootSquareSmallUrl, representative.SquareSmallUrl),
            SquareMediumUrl = StringHelpers.FirstNonBlank(collectionSquare, representative.RootSquareMediumUrl, representative.SquareMediumUrl),
            SquareLargeUrl = StringHelpers.FirstNonBlank(collectionSquare, representative.RootSquareLargeUrl, representative.SquareLargeUrl),
            BannerUrl = StringHelpers.FirstNonBlank(collectionBanner, representative.RootBannerUrl, collectionBackground, representative.RootBackgroundUrl, representative.BannerUrl),
            BannerSmallUrl = StringHelpers.FirstNonBlank(collectionBanner, representative.RootBannerSmallUrl, collectionBackground, representative.RootBackgroundSmallUrl, representative.BannerSmallUrl),
            BannerMediumUrl = StringHelpers.FirstNonBlank(collectionBanner, representative.RootBannerMediumUrl, collectionBackground, representative.RootBackgroundMediumUrl, representative.BannerMediumUrl),
            BannerLargeUrl = StringHelpers.FirstNonBlank(collectionBanner, representative.RootBannerLargeUrl, collectionBackground, representative.RootBackgroundLargeUrl, representative.BannerLargeUrl),
            BackgroundUrl = StringHelpers.FirstNonBlank(collectionBackground, representative.RootBackgroundUrl, collectionBanner, representative.RootBannerUrl, representative.BackgroundUrl),
            BackgroundSmallUrl = StringHelpers.FirstNonBlank(collectionBackground, representative.RootBackgroundSmallUrl, collectionBanner, representative.RootBannerSmallUrl, representative.BackgroundSmallUrl),
            BackgroundMediumUrl = StringHelpers.FirstNonBlank(collectionBackground, representative.RootBackgroundMediumUrl, collectionBanner, representative.RootBannerMediumUrl, representative.BackgroundMediumUrl),
            BackgroundLargeUrl = StringHelpers.FirstNonBlank(collectionBackground, representative.RootBackgroundLargeUrl, collectionBanner, representative.RootBannerLargeUrl, representative.BackgroundLargeUrl),
            LogoUrl = StringHelpers.FirstNonBlank(collectionLogo, representative.RootLogoUrl, representative.LogoUrl),
            CoverWidthPx = representative.RootCoverWidthPx ?? representative.CoverWidthPx,
            CoverHeightPx = representative.RootCoverHeightPx ?? representative.CoverHeightPx,
            SquareWidthPx = representative.RootSquareWidthPx ?? representative.SquareWidthPx,
            SquareHeightPx = representative.RootSquareHeightPx ?? representative.SquareHeightPx,
            BannerWidthPx = representative.RootBannerWidthPx ?? representative.BannerWidthPx,
            BannerHeightPx = representative.RootBannerHeightPx ?? representative.BannerHeightPx,
            BackgroundWidthPx = representative.RootBackgroundWidthPx ?? representative.BackgroundWidthPx,
            BackgroundHeightPx = representative.RootBackgroundHeightPx ?? representative.BackgroundHeightPx,
            AccentColor = StringHelpers.FirstNonBlank(representative.CollectionAccentColor, representative.RootAccentColor, representative.AccentColor),
        };

        return ArtworkFor(row, representative.AssetId);
    }

    private static DisplayArtworkDto RootArtworkFor(DisplayWorkRow representative)
    {
        var row = new ArtworkProjection
        {
            // A TV show identity must never borrow episode stills. When show-level
            // artwork is unavailable, render a settled placeholder instead.
            CoverUrl = representative.RootCoverUrl,
            CoverSmallUrl = representative.RootCoverSmallUrl,
            CoverMediumUrl = representative.RootCoverMediumUrl,
            CoverLargeUrl = representative.RootCoverLargeUrl,
            SquareUrl = representative.RootSquareUrl,
            SquareSmallUrl = representative.RootSquareSmallUrl,
            SquareMediumUrl = representative.RootSquareMediumUrl,
            SquareLargeUrl = representative.RootSquareLargeUrl,
            BannerUrl = StringHelpers.FirstNonBlank(representative.RootBannerUrl, representative.RootBackgroundUrl),
            BannerSmallUrl = StringHelpers.FirstNonBlank(representative.RootBannerSmallUrl, representative.RootBackgroundSmallUrl),
            BannerMediumUrl = StringHelpers.FirstNonBlank(representative.RootBannerMediumUrl, representative.RootBackgroundMediumUrl),
            BannerLargeUrl = StringHelpers.FirstNonBlank(representative.RootBannerLargeUrl, representative.RootBackgroundLargeUrl),
            BackgroundUrl = StringHelpers.FirstNonBlank(representative.RootBackgroundUrl, representative.RootBannerUrl),
            BackgroundSmallUrl = StringHelpers.FirstNonBlank(representative.RootBackgroundSmallUrl, representative.RootBannerSmallUrl),
            BackgroundMediumUrl = StringHelpers.FirstNonBlank(representative.RootBackgroundMediumUrl, representative.RootBannerMediumUrl),
            BackgroundLargeUrl = StringHelpers.FirstNonBlank(representative.RootBackgroundLargeUrl, representative.RootBannerLargeUrl),
            LogoUrl = representative.RootLogoUrl,
            CoverWidthPx = representative.RootCoverWidthPx,
            CoverHeightPx = representative.RootCoverHeightPx,
            SquareWidthPx = representative.RootSquareWidthPx,
            SquareHeightPx = representative.RootSquareHeightPx,
            BannerWidthPx = representative.RootBannerWidthPx,
            BannerHeightPx = representative.RootBannerHeightPx,
            BackgroundWidthPx = representative.RootBackgroundWidthPx,
            BackgroundHeightPx = representative.RootBackgroundHeightPx,
            AccentColor = StringHelpers.FirstNonBlank(representative.RootAccentColor, representative.AccentColor),
        };

        return ArtworkFor(row);
    }

    private static IReadOnlyList<string> BuildFacts(
        string mediaKind,
        string title,
        string? year,
        string? author,
        string? artist,
        string? contentRating,
        string? runtime,
        string? duration,
        string? pageCount,
        string? starRating,
        string? narrator = null,
        string? publisher = null)
        => DisplayFactBuilder.Build(
            mediaKind,
            title,
            year: year,
            author: author,
            artist: artist,
            narrator: narrator,
            publisher: publisher,
            contentRating: contentRating,
            runtime: runtime,
            duration: duration,
            pageCount: pageCount,
            starRating: starRating);

    private static string PreferredShape(string mediaType, string? backgroundUrl, string? bannerUrl, string? squareUrl)
    {
        var mediaKind = DisplayMediaRules.NormalizeDisplayKind(mediaType);
        if (mediaKind is "Music" or "Audiobook")
        {
            return "square";
        }

        if (mediaKind is "Movie" or "TV")
        {
            return !string.IsNullOrWhiteSpace(backgroundUrl) || !string.IsNullOrWhiteSpace(bannerUrl)
                ? "landscape"
                : "portrait";
        }

        if (mediaKind is not "Book" and not "Comic" && !string.IsNullOrWhiteSpace(squareUrl))
        {
            return "square";
        }

        return "portrait";
    }

    private static string PresentationFor(string mediaKind) => mediaKind switch
    {
        "TV" => "tvSeries",
        "Movie" => "movie",
        "Music" => "album",
        "Audiobook" => "audiobook",
        "Comic" => "comic",
        "Book" => "book",
        _ => "default",
    };

    private static string CollectionShape(string lane, string mediaKind, string mediaType, DisplayArtworkDto artwork)
    {
        if (lane == "listen" || mediaKind is "Music" or "Audiobook")
        {
            return "square";
        }

        return PreferredShape(mediaType, artwork.BackgroundUrl, artwork.BannerUrl, artwork.SquareUrl);
    }

    private static string CollectionPresentation(string lane, string mediaKind) => mediaKind switch
    {
        "TV" => "tvSeries",
        "Movie" => "movieSeries",
        "Book" => "bookSeries",
        "Comic" => "comicSeries",
        "Audiobook" => "audiobookSeries",
        "Music" when lane == "listen" => "album",
        "Music" => "album",
        _ => "default",
    };

    private static IReadOnlyList<string> CollectionFacts(
        string mediaKind,
        IReadOnlyList<DisplayWorkRow> works,
        string? genre,
        int? ownedCount = null,
        string? rating = null)
    {
        var facts = new List<string>();
        if (mediaKind == "TV" && HasRecentlyAddedEpisodes(works))
        {
            AddFact(facts, "New episodes added");
        }

        AddFact(facts, CollectionSubtitle(mediaKind, works, ownedCount));
        AddFact(facts, DisplayFactBuilder.Build(mediaKind, string.Empty, rating: rating).FirstOrDefault());
        facts.AddRange(DisplayMediaRules.SplitValues(genre).Where(value => !string.Equals(value, mediaKind, StringComparison.OrdinalIgnoreCase)).Take(2));
        return facts;
    }

    private static IReadOnlyList<string> TvShowFacts(
        string title,
        IReadOnlyList<DisplayWorkRow> works,
        DisplayWorkRow representative)
    {
        var facts = BuildFacts(
                "TV",
                title,
                TvShowYearRange(works, representative.Year),
                representative.Author,
                representative.Artist,
                representative.ContentRating,
                representative.Runtime,
                representative.Duration,
                representative.PageCount,
                representative.Rating)
            .ToList();
        var episodeCount = works.Select(work => work.WorkId).Distinct().Count();
        facts.Insert(Math.Min(2, facts.Count), CollectionCountLabel("TV", episodeCount));
        facts.AddRange(DisplayMediaRules.SplitValues(representative.Genre)
            .Where(value => !string.Equals(value, "TV", StringComparison.OrdinalIgnoreCase))
            .Take(2));
        return facts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> HomeCollectionFacts(DisplayHomeCollectionRow row)
    {
        var facts = new List<string>();
        AddFact(facts, CollectionCountLabel("Collection", row.ItemCount));
        AddFact(facts, row.CollectionType);

        if (row.WatchCount > 0)
        {
            AddFact(facts, $"{row.WatchCount} watch");
        }

        if (row.ReadCount > 0)
        {
            AddFact(facts, $"{row.ReadCount} read");
        }

        if (row.ListenCount > 0)
        {
            AddFact(facts, $"{row.ListenCount} listen");
        }

        return facts;
    }

    private static string? TvShowYearRange(IReadOnlyList<DisplayWorkRow> works, string? fallbackYear)
    {
        var start = works.Select(work => ParseSortYear(work.Year)).Where(year => year > 0).DefaultIfEmpty(0).Min();
        var end = works.Select(work => ParseSortYear(work.SeriesEndYear)).Where(year => year >= start && year > 0).DefaultIfEmpty(0).Max();
        if (start <= 0)
        {
            return fallbackYear;
        }

        return end > start ? $"{start}\u2013{end}" : start.ToString(CultureInfo.InvariantCulture);
    }

    private static DisplayGroupSummaryDto BuildHomeCollectionSummary(DisplayHomeCollectionRow row)
    {
        var mediaCounts = new List<DisplayGroupMediaCountDto>();
        AddMediaCount(mediaCounts, "Watch", row.WatchCount);
        AddMediaCount(mediaCounts, "Read", row.ReadCount);
        AddMediaCount(mediaCounts, "Listen", row.ListenCount);
        AddMediaCount(mediaCounts, "Other", row.OtherCount);

        return new DisplayGroupSummaryDto
        {
            OwnedCount = row.ItemCount,
            EarliestYear = row.EarliestYear,
            LatestYear = row.LatestYear,
            RelationshipLabel = RelationshipLabel(row.CollectionType),
            MediaCounts = mediaCounts,
        };
    }

    private static DisplayGroupSummaryDto BuildGroupSummary(
        string mediaKind,
        IReadOnlyList<DisplayWorkRow> works,
        int ownedCount,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork,
        string? collectionType)
    {
        var members = works
            .GroupBy(
                work => NormalizeSeriesMemberKey(StringHelpers.FirstNonBlank(work.IdentityQid, work.Title)),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToList();
        var completedCount = 0;
        var inProgressCount = 0;
        if (progressByWork is not null)
        {
            foreach (var member in members)
            {
                var progress = member
                    .Where(work => progressByWork.ContainsKey(work.WorkId))
                    .Select(work => progressByWork[work.WorkId].ProgressPct)
                    .ToList();
                if (progress.Any(percent => percent >= 99.5))
                {
                    completedCount++;
                }
                else if (progress.Any(percent => percent is > 0 and < 99.5))
                {
                    inProgressCount++;
                }
            }
        }

        var knownTotal = mediaKind == "Comic"
            ? null
            : works
                .Select(work => work.CollectionManifestTotalCount)
                .Where(total => total >= ownedCount && total > 0)
                .Cast<int?>()
                .Max();

        var mediaCounts = works
            .GroupBy(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DisplayGroupMediaCountDto(
                group.Key,
                group.Select(work => work.WorkId).Distinct().Count()))
            .Where(count => count.Count > 0)
            .OrderBy(count => MediaCountOrder(count.MediaType))
            .ThenBy(count => count.MediaType, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var years = works
            .Select(work => ParseSortYear(work.Year))
            .Where(year => year > 0)
            .Distinct()
            .Order()
            .ToList();

        return new DisplayGroupSummaryDto
        {
            OwnedCount = ownedCount,
            KnownTotalCount = knownTotal,
            CompletedCount = completedCount,
            InProgressCount = inProgressCount,
            EarliestYear = years.Count > 0 ? years[0] : null,
            LatestYear = years.Count > 0 ? years[^1] : null,
            SequenceRange = OwnedSequenceRange(mediaKind, works),
            RelationshipLabel = mediaKind is "Book" or "Comic" or "Movie" or "Audiobook"
                ? works.Any(work => !string.IsNullOrWhiteSpace(work.SeriesPosition)) ? "Ordered series" : "Series grouping"
                : RelationshipLabel(collectionType),
            MediaCounts = mediaCounts,
        };
    }

    private static string? OwnedSequenceRange(string mediaKind, IReadOnlyList<DisplayWorkRow> works)
    {
        var positions = works
            .Select(work => work.SeriesPosition?.Trim())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Select(position => new
            {
                Label = position!,
                Sort = ParseSeriesPosition(position),
            })
            .Where(position => position.Sort.HasValue)
            .GroupBy(position => position.Sort!.Value)
            .Select(group => group.First())
            .OrderBy(position => position.Sort)
            .ToList();
        if (positions.Count == 0)
        {
            return null;
        }

        var noun = mediaKind switch
        {
            "Comic" => positions.Count == 1 ? "Issue" : "Issues",
            "Audiobook" => positions.Count == 1 ? "Volume" : "Volumes",
            "Movie" => positions.Count == 1 ? "Entry" : "Entries",
            _ => positions.Count == 1 ? "Book" : "Books",
        };
        var range = positions.Count == 1
            ? positions[0].Label
            : $"{positions[0].Label}\u2013{positions[^1].Label}";
        return $"{noun} {range} owned";
    }

    private static string? RelationshipLabel(string? collectionType) => collectionType?.Trim() switch
    {
        "Universe" => "Shared universe",
        "Franchise" => "Franchise collection",
        "Series" => "Series grouping",
        "ContentGroup" => "Related works",
        "Playlist" or "PlaylistFolder" => "Curated list",
        "Smart" => "Smart collection",
        "Manual" => "Curated collection",
        _ => null,
    };

    private static void AddMediaCount(List<DisplayGroupMediaCountDto> counts, string mediaType, int count)
    {
        if (count > 0)
        {
            counts.Add(new DisplayGroupMediaCountDto(mediaType, count));
        }
    }

    private static int MediaCountOrder(string mediaType) => mediaType switch
    {
        "Movie" or "TV" or "Watch" => 0,
        "Book" or "Comic" or "Read" => 1,
        "Music" or "Audiobook" or "Listen" => 2,
        _ => 3,
    };

    private static string CollectionCountLabel(string mediaKind, int count)
    {
        var noun = mediaKind switch
        {
            "TV" => count == 1 ? "episode" : "episodes",
            "Comic" => count == 1 ? "issue" : "issues",
            "Music" => count == 1 ? "track" : "tracks",
            _ => count == 1 ? "title" : "titles",
        };

        return $"{count} {noun}";
    }

    private static IReadOnlyList<DisplayCardPreviewItemDto> BuildSeriesPreviewItems(
        string mediaKind,
        IReadOnlyList<DisplayWorkRow> works,
        Guid? collectionId = null,
        Guid? tvShowRootId = null,
        IReadOnlyDictionary<Guid, DisplayJourneyRow>? progressByWork = null)
    {
        if (mediaKind is not ("Book" or "Comic" or "Movie" or "TV" or "Audiobook" or "Music"))
        {
            return [];
        }

        var orderedItems = works
            .Select(work => new SeriesPreviewCandidate(
                work,
                SeriesPreviewImage(work),
                PreviewSortPosition(mediaKind, work),
                DisplayTitleFor(mediaKind, work.Title, work.Series, work.SeriesPosition),
                mediaKind switch
                {
                    "TV" => EpisodePosition(work.SeasonNumber, work.EpisodeNumber),
                    "Music" => work.TrackNumber,
                    _ => work.SeriesPosition,
                }))
            .GroupBy(item => item.Work.WorkId)
            .Select(group => group.First())
            .OrderBy(item => item.SortPosition is null)
            .ThenBy(item => item.SortPosition ?? double.MaxValue)
            .ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Work.CreatedAt)
            .ToList();

        if (orderedItems.Count <= 4)
        {
            return ToPreviewItems(orderedItems, mediaKind, collectionId, tvShowRootId);
        }

        // Structural artwork is a deterministic presentation of the first owned
        // members in canonical sequence order. Journey-aware callers can still
        // request progress-biased representatives explicitly below.
        if (progressByWork is null)
        {
            return ToPreviewItems(orderedItems.Take(4).ToList(), mediaKind, collectionId, tvShowRootId);
        }

        var representativeWorkIds = new List<Guid>();
        var hasProgressHistory = progressByWork is not null
                                 && orderedItems.Any(item => progressByWork.ContainsKey(item.Work.WorkId));
        if (hasProgressHistory)
        {
            var current = orderedItems
                .Where(item => progressByWork!.TryGetValue(item.Work.WorkId, out var progress)
                               && progress.ProgressPct is > 0 and < 99.5)
                .OrderByDescending(item => progressByWork![item.Work.WorkId].LastAccessed)
                .FirstOrDefault();
            if (current is not null)
            {
                representativeWorkIds.Add(current.Work.WorkId);
                var currentIndex = orderedItems.IndexOf(current);
                if (currentIndex + 1 < orderedItems.Count)
                {
                    representativeWorkIds.Add(orderedItems[currentIndex + 1].Work.WorkId);
                }
            }
            else
            {
                var next = orderedItems.FirstOrDefault(item => !progressByWork!.TryGetValue(item.Work.WorkId, out var progress)
                                                               || progress.ProgressPct < 99.5);
                if (next is not null)
                {
                    representativeWorkIds.Add(next.Work.WorkId);
                }
            }

            representativeWorkIds.Add(orderedItems[0].Work.WorkId);
            representativeWorkIds.AddRange(orderedItems
                .Where(item => progressByWork!.TryGetValue(item.Work.WorkId, out var progress)
                               && progress.ProgressPct >= 99.5)
                .OrderByDescending(item => progressByWork![item.Work.WorkId].LastAccessed)
                .Select(item => item.Work.WorkId));
        }
        else
        {
            representativeWorkIds.Add(orderedItems[0].Work.WorkId);
            representativeWorkIds.Add(orderedItems[1].Work.WorkId);
        }

        representativeWorkIds.AddRange(orderedItems
            .OrderByDescending(item => item.Work.CreatedAt)
            .Select(item => item.Work.WorkId));
        representativeWorkIds.Add(orderedItems[^1].Work.WorkId);
        representativeWorkIds.AddRange(orderedItems.Select(item => item.Work.WorkId));

        var representativeItems = representativeWorkIds
            .Distinct()
            .Take(4)
            .Select(workId => orderedItems.First(item => item.Work.WorkId == workId))
            .ToList();
        if (!hasProgressHistory)
        {
            representativeItems = representativeItems
                .OrderBy(orderedItems.IndexOf)
                .ToList();
        }

        return ToPreviewItems(representativeItems, mediaKind, collectionId, tvShowRootId);
    }

    private static IReadOnlyList<DisplayCardPreviewItemDto> ToPreviewItems(
        IReadOnlyList<SeriesPreviewCandidate> representativeItems,
        string mediaKind,
        Guid? collectionId,
        Guid? tvShowRootId)
    {
        return representativeItems
            .Select(item => new DisplayCardPreviewItemDto(
                item.Work.WorkId,
                item.Work.AssetId,
                item.DisplayTitle,
                item.ImageUrl ?? string.Empty,
                SeriesPreviewShape(item.Work, item.ImageUrl),
                item.DisplayPosition,
                mediaKind,
                PreviewWebUrlFor(item.Work.WorkId, mediaKind, collectionId, tvShowRootId),
                item.Work.Description,
                BuildFacts(
                    mediaKind,
                    item.DisplayTitle,
                    item.Work.Year,
                    item.Work.Author,
                    item.Work.Artist,
                    item.Work.ContentRating,
                    item.Work.Runtime,
                    item.Work.Duration,
                    item.Work.PageCount,
                    item.Work.Rating,
                    item.Work.Narrator,
                    item.Work.Publisher)))
            .ToList();
    }

    private sealed record SeriesPreviewCandidate(
        DisplayWorkRow Work,
        string? ImageUrl,
        double? SortPosition,
        string DisplayTitle,
        string? DisplayPosition);

    private static string? SeriesPreviewImage(DisplayWorkRow work) =>
        StringHelpers.FirstNonBlank(
            work.CoverSmallUrl,
            work.CoverMediumUrl,
            work.CoverLargeUrl,
            work.CoverUrl,
            work.SquareSmallUrl,
            work.SquareMediumUrl,
            work.SquareLargeUrl,
            work.SquareUrl,
            work.BackgroundSmallUrl,
            work.BackgroundMediumUrl,
            work.BackgroundLargeUrl,
            work.BackgroundUrl,
            work.BannerSmallUrl,
            work.BannerMediumUrl,
            work.BannerLargeUrl,
            work.BannerUrl);

    private static string SeriesPreviewShape(DisplayWorkRow work, string? imageUrl)
    {
        if (MatchesAny(imageUrl, work.SquareUrl, work.SquareSmallUrl, work.SquareMediumUrl, work.SquareLargeUrl))
        {
            return "square";
        }

        if (MatchesAny(
                imageUrl,
                work.BackgroundUrl,
                work.BackgroundSmallUrl,
                work.BackgroundMediumUrl,
                work.BackgroundLargeUrl,
                work.BannerUrl,
                work.BannerSmallUrl,
                work.BannerMediumUrl,
                work.BannerLargeUrl))
        {
            return "wide";
        }

        var width = ParseInt(work.CoverWidthPx);
        var height = ParseInt(work.CoverHeightPx);
        if (width is > 0 && height is > 0)
        {
            var ratio = width.Value / (double)height.Value;
            if (ratio >= 1.32)
            {
                return "wide";
            }

            if (ratio >= 0.86)
            {
                return "square";
            }
        }

        return "portrait";
    }

    private static double? PreviewSortPosition(string mediaKind, DisplayWorkRow work)
    {
        if (mediaKind == "Music")
        {
            return ParseSeriesPosition(work.TrackNumber);
        }

        if (mediaKind != "TV")
        {
            return ParseSeriesPosition(work.SeriesPosition);
        }

        var season = ParseSeriesPosition(work.SeasonNumber);
        var episode = ParseSeriesPosition(work.EpisodeNumber);
        if (season is null && episode is null)
        {
            return null;
        }

        return (season ?? 0) * 10000 + (episode ?? 0);
    }

    internal static string PreviewWebUrlFor(
        Guid workId,
        string mediaKind,
        Guid? collectionId = null,
        Guid? tvShowRootId = null)
    {
        var normalizedKind = DisplayMediaRules.NormalizeDisplayKind(mediaKind);
        return normalizedKind switch
        {
            "TV" or "Movie" => $"/details/work/{workId:D}?context=watch",
            "Music" => SongPlaybackUrl(workId),
            "Audiobook" => $"/details/work/{workId:D}?context=listen",
            "Book" or "Comic" => $"/details/work/{workId:D}?context=read",
            _ => $"/details/work/{workId:D}",
        };
    }

    private static string SongPlaybackUrl(Guid workId) =>
        $"/listen/music?browse=songs&track={workId:D}";

    private static double? ParseSeriesPosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var started = false;
        var seenDecimal = false;
        var chars = new List<char>();
        foreach (var character in value.Trim())
        {
            if (char.IsDigit(character))
            {
                started = true;
                chars.Add(character);
                continue;
            }

            if (character == '.' && started && !seenDecimal)
            {
                seenDecimal = true;
                chars.Add(character);
                continue;
            }

            if (started)
            {
                break;
            }
        }

        return chars.Count > 0
            && double.TryParse(new string(chars.ToArray()), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static string CollectionSubtitle(
        string mediaKind,
        IReadOnlyList<DisplayWorkRow> works,
        int? ownedCount = null)
    {
        if (mediaKind == "TV")
        {
            var seasonCount = works
                .Select(work => work.SeasonNumber)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (seasonCount > 0)
            {
                return seasonCount == 1 ? "1 season" : $"{seasonCount} seasons";
            }
        }

        if (mediaKind is "Book" or "Comic" or "Movie" or "Audiobook")
        {
            return OwnedSeriesCountLabel(mediaKind, ownedCount ?? DistinctOwnedSeriesMemberCount(works));
        }

        return CollectionCountLabel(mediaKind, works.Count);
    }

    private static bool HasRecentlyAddedEpisodes(IReadOnlyList<DisplayWorkRow> works)
    {
        if (works.Count == 0)
        {
            return false;
        }

        var threshold = DateTimeOffset.UtcNow.AddDays(-30);
        return works.Any(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV" && work.CreatedAt >= threshold);
    }

    private static bool ShouldRenderCollectionCard(string mediaKind, IReadOnlyList<DisplayWorkRow> works, int minimumSeriesItems)
    {
        if (mediaKind is "TV" or "Music")
        {
            return true;
        }

        return DistinctOwnedSeriesMemberCount(works) >= Math.Max(2, minimumSeriesItems);
    }

    private static int DistinctOwnedSeriesMemberCount(IReadOnlyList<DisplayWorkRow> works) =>
        works
            .Select(work => StringHelpers.FirstNonBlank(work.IdentityQid, work.Title))
            .Select(NormalizeSeriesMemberKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string OwnedSeriesCountLabel(string mediaKind, int ownedCount)
    {
        var noun = mediaKind switch
        {
            "Comic" => ownedCount == 1 ? "issue" : "issues",
            _ => ownedCount == 1 ? "title" : "titles",
        };

        return $"{ownedCount} owned {noun}";
    }

    private static string? ContinueEpisodeSubtitle(string? season, string? episode)
    {
        var normalizedSeason = NormalizeEpisodeOrdinal(season);
        var normalizedEpisode = NormalizeEpisodeOrdinal(episode);
        if (normalizedSeason is null && normalizedEpisode is null)
        {
            return null;
        }

        return normalizedSeason is null
            ? $"Continue · E{normalizedEpisode}"
            : normalizedEpisode is null
                ? $"Continue · S{normalizedSeason}"
                : $"Continue · S{normalizedSeason} E{normalizedEpisode}";
    }

    private static string? EpisodePosition(string? season, string? episode)
    {
        var normalizedSeason = NormalizeEpisodeOrdinal(season);
        var normalizedEpisode = NormalizeEpisodeOrdinal(episode);
        if (normalizedSeason is null && normalizedEpisode is null)
        {
            return null;
        }

        return normalizedSeason is null
            ? $"E{normalizedEpisode}"
            : normalizedEpisode is null
                ? $"S{normalizedSeason}"
                : $"S{normalizedSeason} E{normalizedEpisode}";
    }

    private static string? NormalizeEpisodeOrdinal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static int CollectionArtworkScore(DisplayWorkRow work)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(work.CollectionBackgroundUrl) || !string.IsNullOrWhiteSpace(work.CollectionBannerUrl))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(work.RootBackgroundUrl) || !string.IsNullOrWhiteSpace(work.RootBannerUrl))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(work.CollectionCoverUrl) || !string.IsNullOrWhiteSpace(work.CollectionSquareUrl))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(work.RootCoverUrl) || !string.IsNullOrWhiteSpace(work.RootSquareUrl))
        {
            score += 1;
        }

        return score;
    }

    private static int RootArtworkScore(DisplayWorkRow work)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(work.RootBackgroundUrl) || !string.IsNullOrWhiteSpace(work.RootBannerUrl))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(work.RootCoverUrl) || !string.IsNullOrWhiteSpace(work.RootSquareUrl))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(work.BackgroundUrl) || !string.IsNullOrWhiteSpace(work.BannerUrl))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(work.CoverUrl) || !string.IsNullOrWhiteSpace(work.SquareUrl))
        {
            score += 1;
        }

        return score;
    }

    private static Guid TvShowGroupId(DisplayWorkRow work)
    {
        if (work.RootWorkId != Guid.Empty)
        {
            return work.RootWorkId;
        }

        return work.CollectionId ?? work.WorkId;
    }

    private static string CollectionActionKind(string mediaKind) => mediaKind switch
    {
        "TV" => "openShow",
        "Movie" or "Book" or "Comic" or "Audiobook" => "openSeries",
        "Music" => "openAlbum",
        _ => "openCollection",
    };

    private static string CollectionActionLabel(string mediaKind) => mediaKind switch
    {
        "TV" => "Open Show",
        "Movie" or "Book" or "Comic" or "Audiobook" => "Open Series",
        "Music" => "Open Album",
        _ => "Open",
    };

    private static string ContinueLabel(string mediaKind) => mediaKind switch
    {
        "Movie" or "TV" => "Continue Watching",
        "Book" or "Comic" => "Continue Reading",
        "Music" or "Audiobook" => "Continue Listening",
        _ => "Continue",
    };

    private static IReadOnlyList<DisplayCardBadgeDto> BuildBadges(string mediaKind, string? quality, string? source)
    {
        if (mediaKind is not ("Movie" or "TV"))
        {
            return [];
        }

        var badges = new List<DisplayCardBadgeDto>();
        if (NormalizeWatchQualityLabel(quality) is { } qualityLabel)
        {
            badges.Add(new DisplayCardBadgeDto("quality", qualityLabel));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            badges.Add(new DisplayCardBadgeDto("source", source.Trim()));
        }

        return badges;
    }

    private static string? NormalizeWatchQualityLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Equals("2160p", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("UHD", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ultra HD", StringComparison.OrdinalIgnoreCase))
        {
            return "4K";
        }

        if (normalized.Contains("3840", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("2160", StringComparison.OrdinalIgnoreCase))
        {
            return "4K";
        }

        if (normalized.Contains("1080", StringComparison.OrdinalIgnoreCase))
        {
            return "HD";
        }

        return normalized;
    }

    private static string? CreatorFor(DisplayWorkRow row) =>
        StringHelpers.FirstNonBlank(row.Author, row.Artist, row.Director, row.Narrator);

    private static string? SubtitleFor(
        string mediaKind,
        string? creator,
        string? series,
        string? seriesPosition,
        string? showName,
        string? season,
        string? episode)
    {
        if (mediaKind == "TV")
        {
            return SeriesDisplayFormatter.FormatEpisodePosition(season, episode, showName);
        }

        if (mediaKind is "Book" or "Comic" or "Audiobook")
        {
            return creator;
        }

        if (mediaKind == "Movie")
        {
            return SeriesSubtitle(series, seriesPosition, "Movie");
        }

        return StringHelpers.FirstNonBlank(creator, series);
    }

    private static string? SeriesSubtitle(string? series, string? position, string memberLabel)
    {
        if (string.IsNullOrWhiteSpace(series))
        {
            return null;
        }

        return SeriesDisplayFormatter.FormatPosition(memberLabel, position, series);
    }

    private static string? FormatIssue(string? seriesPosition)
        => string.IsNullOrWhiteSpace(seriesPosition) ? null : $"Issue #{seriesPosition}";

    private static string DisplayTitleFor(string mediaKind, string title, string? series, string? seriesPosition)
    {
        if (mediaKind == "Comic" && IsGeneratedComicTitle(title, series, seriesPosition))
        {
            return FormatIssue(seriesPosition) ?? title;
        }

        return title;
    }

    private static bool IsGeneratedComicTitle(string? title, string? series, string? issueNumber)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(issueNumber))
        {
            return false;
        }

        var normalizedTitle = NormalizeOrdinalTitle(title);
        var normalizedSeries = NormalizeOrdinalTitle(series);
        var normalizedIssue = NormalizeOrdinalTitle(issueNumber);
        return normalizedTitle == $"{normalizedSeries}{normalizedIssue}"
            || normalizedTitle == $"{normalizedSeries}issue{normalizedIssue}"
            || normalizedTitle == $"{normalizedSeries}no{normalizedIssue}"
            || (normalizedTitle.StartsWith(normalizedSeries, StringComparison.OrdinalIgnoreCase)
                && normalizedTitle.EndsWith(normalizedIssue, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeOrdinalTitle(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? NormalizeSeriesMemberKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var chars = value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }


    private static int ParseSortYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var trimmed = value.Trim();
        var prefixLength = Math.Min(4, trimmed.Length);
        return int.TryParse(trimmed.AsSpan(0, prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1000 and <= 9999
                ? year
                : 0;
    }

    private static bool MatchesAny(string? value, params string?[] candidates) =>
        !string.IsNullOrWhiteSpace(value)
        && candidates.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

    private static void AddFact(List<string> facts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!facts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            facts.Add(trimmed);
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private sealed class ArtworkProjection : IDisplayArtworkRow
    {
        public string? CoverUrl { get; init; }
        public string? CoverSmallUrl { get; init; }
        public string? CoverMediumUrl { get; init; }
        public string? CoverLargeUrl { get; init; }
        public string? SquareUrl { get; init; }
        public string? SquareSmallUrl { get; init; }
        public string? SquareMediumUrl { get; init; }
        public string? SquareLargeUrl { get; init; }
        public string? BannerUrl { get; init; }
        public string? BannerSmallUrl { get; init; }
        public string? BannerMediumUrl { get; init; }
        public string? BannerLargeUrl { get; init; }
        public string? BackgroundUrl { get; init; }
        public string? BackgroundSmallUrl { get; init; }
        public string? BackgroundMediumUrl { get; init; }
        public string? BackgroundLargeUrl { get; init; }
        public string? LogoUrl { get; init; }
        public string? CoverWidthPx { get; init; }
        public string? CoverHeightPx { get; init; }
        public string? SquareWidthPx { get; init; }
        public string? SquareHeightPx { get; init; }
        public string? BannerWidthPx { get; init; }
        public string? BannerHeightPx { get; init; }
        public string? BackgroundWidthPx { get; init; }
        public string? BackgroundHeightPx { get; init; }
        public string? AccentColor { get; init; }
    }
}
