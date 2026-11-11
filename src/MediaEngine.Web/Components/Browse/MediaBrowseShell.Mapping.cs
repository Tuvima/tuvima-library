using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Components.Browse;

public partial class MediaBrowseShell
{
    private LibraryItemViewModel ToLibraryItem(DisplayCardDto card)
    {
        var metadata = card.ListMetadata;
        var isMusic = string.Equals(card.MediaType, "Music", StringComparison.OrdinalIgnoreCase);
        var managedAssetCoverUrl = card.AssetId.HasValue
            ? ApiClient.ToAbsoluteEngineUrl($"/stream/{card.AssetId.Value:D}/cover")
            : null;
        var coverUrl = isMusic
            ? FirstValue(card.Artwork.SquareUrl, card.Artwork.SquareMediumUrl, card.Artwork.CoverUrl, card.Artwork.CoverMediumUrl, managedAssetCoverUrl)
            : FirstValue(card.Artwork.CoverUrl, card.Artwork.CoverMediumUrl, card.Artwork.SquareUrl, card.Artwork.SquareMediumUrl, managedAssetCoverUrl);
        var thumbnailUrl = isMusic
            ? FirstValue(card.Artwork.SquareSmallUrl, card.Artwork.SquareUrl, card.Artwork.CoverSmallUrl, coverUrl)
            : FirstValue(card.Artwork.CoverSmallUrl, card.Artwork.CoverUrl, card.Artwork.SquareSmallUrl, coverUrl);
        var durationSeconds = NormalizeListDurationSeconds(metadata?.Duration, isMusic);

        return new LibraryItemViewModel
        {
            EntityId = card.WorkId ?? card.Id,
            AssetId = card.AssetId,
            CollectionId = card.CollectionId,
            RootWorkId = metadata?.RootWorkId,
            ArtistPersonId = metadata?.ArtistPersonId,
            IsUpdatingDetails = metadata?.IsUpdatingDetails == true,
            Title = card.Title,
            Author = metadata?.Author,
            Director = metadata?.Director,
            Artist = isMusic
                ? FirstValue(metadata?.Artist, metadata?.Author, card.Subtitle)
                : metadata?.Artist,
            Series = metadata?.Series,
            Narrator = metadata?.Narrator,
            Genre = FirstValue(metadata?.Genre, card.Genres.Count == 0 ? null : string.Join(", ", card.Genres)),
            Album = metadata?.Album,
            TrackNumber = metadata?.TrackNumber,
            Season = metadata?.SeasonNumber,
            Episode = metadata?.EpisodeNumber,
            Duration = LibraryHelpers.FormatDuration(durationSeconds, metadata?.Duration),
            DurationSeconds = durationSeconds,
            ShowName = metadata?.ShowName,
            Year = metadata?.Year,
            MediaType = card.MediaType,
            CoverUrl = coverUrl,
            CoverThumbUrl = thumbnailUrl,
            BackgroundUrl = FirstValue(card.Artwork.BackgroundUrl, card.Artwork.BackgroundMediumUrl),
            BannerUrl = FirstValue(card.Artwork.BannerUrl, card.Artwork.BannerMediumUrl),
            CreatedAt = card.SortTimestamp,
            LastPlayedAt = metadata?.LastPlayedAt ?? card.Progress?.LastAccessed,
            PlayCount = metadata?.PlayCount,
            IsFavorite = card.Flags.IsFavorite,
            Status = L["MediaBrowseShell_Ready"].Value,
            IsReadyForLibrary = true,
            LibraryVisibility = "visible",
            ArtworkState = string.IsNullOrWhiteSpace(coverUrl) ? "missing" : "settled",
        };
    }

    private static WorkViewModel ToSongWork(LibraryItemViewModel item)
    {
        var values = new List<CanonicalValueViewModel>();

        AddCanonical(values, "title", item.Title);
        AddCanonical(values, "artist", item.Artist ?? item.Author);
        AddCanonical(values, "album", item.Album);
        AddCanonical(values, "genre", item.Genre);
        AddCanonical(values, "track_number", item.TrackNumber);
        AddCanonical(values, "release_year", item.Year);
        AddCanonical(values, "duration_seconds", item.DurationSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddCanonical(values, "play_count", item.PlayCount?.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new WorkViewModel
        {
            Id = item.EntityId,
            AssetId = item.AssetId,
            CollectionId = item.CollectionId,
            RootWorkId = item.RootWorkId,
            ArtistPersonId = item.ArtistPersonId,
            IsUpdatingDetails = item.IsUpdatingDetails,
            MediaType = "Music",
            CreatedAt = item.CreatedAt,
            ResolvedCoverUrl = item.CoverUrl,
            CanonicalValues = values,
        };
    }

    private static void AddCanonical(List<CanonicalValueViewModel> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(new CanonicalValueViewModel { Key = key, Value = value });
        }
    }

    private static long? NormalizeListDurationSeconds(string? value, bool isMusic)
    {
        if (isMusic
            && !string.IsNullOrWhiteSpace(value)
            && !value.Contains(':', StringComparison.Ordinal)
            && double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rawDuration)
            && rawDuration >= 10_000)
        {
            return Math.Max(0, (long)Math.Round(rawDuration / 1000d, MidpointRounding.AwayFromZero));
        }

        return LibraryHelpers.NormalizeDurationSeconds(value);
    }

    private static string? FirstValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private MediaTileViewModel ToGroupCard(ContentGroupViewModel group)
    {
        if (IsPersonGrouping)
        {
            return ToPersonGroupCard(group);
        }

        var presentation = (BehaviourTabId, _grouping) switch
        {
            ("tv", "shows") => "tvSeries",
            ("tv", "timeline") => "tvSeries",
            ("movies", "series") => "movieSeries",
            ("books", "series") => "bookSeries",
            ("comics", "series") => "comicSeries",
            ("audiobooks", "series") => "audiobookSeries",
            ("music", "albums" or "timeline") => "album",
            ("music", "artists") => "artist",
            _ => "default",
        };

        var wideArtwork = BrowseArtworkRules.ResolveWideArtwork(group.BackgroundUrl, group.BannerUrl);
        var navigationUrl = BuildGroupNavigationUrl(group);
        var displayMediaType = FormatMediaLabel(group.PrimaryMediaType);
        var logoUrl = IsTvNetworksGrouping
            ? StreamingServiceLogos.ResolveLogoPath(group.DisplayName) ?? group.LogoUrl
            : group.LogoUrl;
        var coverUrl = string.Equals(presentation, "artist", StringComparison.OrdinalIgnoreCase)
            ? group.ArtistPhotoUrl
            : group.CoverUrl;
        var coverSmallUrl = MediaTileArtworkUrl.Sized(coverUrl, "s");
        var coverMediumUrl = MediaTileArtworkUrl.Sized(coverUrl, "m");
        var coverLargeUrl = MediaTileArtworkUrl.Sized(coverUrl, "l");
        var artistSmallUrl = MediaTileArtworkUrl.Sized(group.ArtistPhotoUrl, "s");
        var artistMediumUrl = MediaTileArtworkUrl.Sized(group.ArtistPhotoUrl, "m");
        var artistLargeUrl = MediaTileArtworkUrl.Sized(group.ArtistPhotoUrl, "l");
        var bannerSmallUrl = MediaTileArtworkUrl.Sized(group.BannerUrl, "s");
        var bannerMediumUrl = MediaTileArtworkUrl.Sized(group.BannerUrl, "m");
        var bannerLargeUrl = MediaTileArtworkUrl.Sized(group.BannerUrl, "l");
        var backgroundSmallUrl = MediaTileArtworkUrl.Sized(wideArtwork, "s");
        var backgroundMediumUrl = MediaTileArtworkUrl.Sized(wideArtwork, "m");
        var backgroundLargeUrl = MediaTileArtworkUrl.Sized(wideArtwork, "l");
        var card = new DisplayCardDto(
            Id: GetGroupNavigationId(group),
            WorkId: null,
            AssetId: null,
            CollectionId: group.CollectionId,
            MediaType: displayMediaType,
            GroupingType: string.Equals(presentation, "default", StringComparison.OrdinalIgnoreCase) ? "collection" : presentation,
            Title: group.DisplayName,
            Subtitle: group.Creator ?? group.Network,
            Facts: BrowseArtworkRules.CompactFacts(GroupYearLabel(group), GroupCountLabel(group)),
            Artwork: new DisplayArtworkDto(coverUrl, coverSmallUrl, coverMediumUrl, coverLargeUrl, group.ArtistPhotoUrl, artistSmallUrl, artistMediumUrl, artistLargeUrl, group.BannerUrl, bannerSmallUrl, bannerMediumUrl, bannerLargeUrl, wideArtwork, backgroundSmallUrl, backgroundMediumUrl, backgroundLargeUrl, logoUrl, group.CoverWidthPx, group.CoverHeightPx, group.SquareWidthPx, group.SquareHeightPx, group.BannerWidthPx, group.BannerHeightPx, group.BackgroundWidthPx, group.BackgroundHeightPx, group.MediaTypeColor ?? AccentForMedia(group.PrimaryMediaType)),
            PreferredShape: BrowseArtworkRules.PreferredDisplayShape(group.PrimaryMediaType, wideArtwork, group.ArtistPhotoUrl),
            Presentation: presentation,
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [new DisplayActionDto("openCollection", "Explore", null, null, group.CollectionId, navigationUrl)],
            Flags: new DisplayCardFlagsDto(IsPlayableMedia(group.PrimaryMediaType), IsReadMedia(group.PrimaryMediaType), true, false),
            SortTimestamp: group.CreatedAt)
        {
            PreviewItems = group.PreviewItems
                .Select(item => new DisplayCardPreviewItemDto(
                    item.WorkId,
                    null,
                    item.Title,
                    item.ImageUrl,
                    item.Shape,
                    item.Position,
                    group.PrimaryMediaType,
                    BuildGroupPreviewNavigationUrl(group, item.WorkId),
                    item.Description,
                    item.Facts))
                .ToList(),
            PreviewTotalCount = group.WorkCount,
            GroupSummary = new DisplayGroupSummaryDto
            {
                OwnedCount = group.WorkCount,
                EarliestYear = group.EarliestYear,
                LatestYear = group.LatestYear,
                MediaCounts = [new DisplayGroupMediaCountDto(displayMediaType, group.WorkCount)],
            },
        };

        return WithCardOverrides(
            MediaTileComposerService.FromDisplayCard(card),
            description: BuildGroupDescription(group),
            heroUrl: null,
            previewImages: BuildGroupPreviewImages(group, wideArtwork, presentation),
            creator: group.Creator,
            collectionKey: group.DisplayName,
            showLogoAsBrand: IsTvNetworksGrouping,
            sortYear: group.LatestYear ?? group.EarliestYear ?? ParseYear(group.Year));
    }

    private static IReadOnlyList<string> BuildGroupPreviewImages(ContentGroupViewModel group, string? wideArtwork, string presentation)
    {
        IEnumerable<string?> candidates = string.Equals(presentation, "artist", StringComparison.OrdinalIgnoreCase)
            ? new[] { group.ArtistPhotoUrl, group.CoverUrl, group.BannerUrl, group.BackgroundUrl }
            : new[] { wideArtwork, group.BannerUrl, group.BackgroundUrl, group.CoverUrl };

        return candidates
            .Select(value => MediaTileArtworkUrl.Sized(value, "s") ?? value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string GroupCountLabel(ContentGroupViewModel group)
    {
        if (string.Equals(group.PrimaryMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && group.SeasonCount is int seasonCount
            && seasonCount > 0)
        {
            return seasonCount == 1 ? "1 season" : $"{seasonCount} seasons";
        }

        var noun = group.PrimaryMediaType switch
        {
            "Comic" => group.WorkCount == 1 ? "issue" : "issues",
            "Music" => group.WorkCount == 1 ? "track" : "tracks",
            _ => group.WorkCount == 1 ? "title" : "titles",
        };

        return $"{group.WorkCount} {noun}";
    }

    private static string? GroupYearLabel(ContentGroupViewModel group)
    {
        if (group.EarliestYear is int earliest && group.LatestYear is int latest)
        {
            return earliest == latest
                ? earliest.ToString(CultureInfo.InvariantCulture)
                : $"{earliest.ToString(CultureInfo.InvariantCulture)}–{latest.ToString(CultureInfo.InvariantCulture)}";
        }

        return group.EarliestYear?.ToString(CultureInfo.InvariantCulture)
            ?? group.LatestYear?.ToString(CultureInfo.InvariantCulture)
            ?? group.Year;
    }

    private List<LibraryItemViewModel> BuildContainerDisplayItems()
    {
        var isSystemGroup = GetSystemViewGroupField() is not null;
        return _groups.Select(group => new LibraryItemViewModel
        {
            EntityId = isSystemGroup ? GenerateDeterministicGuid(group.DisplayName) : group.CollectionId,
            Title = group.DisplayName,
            MediaType = group.PrimaryMediaType,
            CoverUrl = group.ArtistPhotoUrl ?? group.CoverUrl,
            ArtistPhotoUrl = group.ArtistPhotoUrl,
            BackgroundUrl = group.BackgroundUrl,
            BannerUrl = group.BannerUrl,
            Author = group.Creator,
            Artist = group.Creator,
            Network = group.Network,
            Year = group.Year,
            SeasonCount = group.SeasonCount?.ToString(),
            AlbumCount = group.AlbumCount?.ToString(),
            TrackCount = group.WorkCount.ToString(),
        }).ToList();
    }

    private Guid GetGroupNavigationId(ContentGroupViewModel group) =>
        IsPersonGrouping && (group.PersonId ?? group.ArtistPersonId) is Guid personId
            ? personId
            : UsesTvShowTimeline
                ? GetTvShowGroupRouteId(group)
            : GetSystemViewGroupField() is not null
            ? GenerateDeterministicGuid(group.DisplayName)
            : IsTvShowsGrouping
                ? GetTvShowGroupRouteId(group)
            : group.CollectionId;

    private static Guid GetTvShowGroupRouteId(ContentGroupViewModel group) =>
        group.RootWorkId ?? group.CollectionId;

    private string? GetSystemViewGroupField() =>
        UsesAlbumTimeline
            ? "album"
            : UsesTvShowTimeline
                ? "show_name"
            : BrowseQueryBuilder.GetSystemViewGroupField(BehaviourTabId, _grouping);

    private bool IsPersonGrouping => _grouping is "authors" or "creators" or "directors" or "artists" or "narrators";

    private MediaTileViewModel ToPersonGroupCard(ContentGroupViewModel group)
    {
        var navigationUrl = MediaPersonGroupTileComposer.NavigationUrl(
            group.PersonId ?? group.ArtistPersonId,
            group.DisplayName,
            PersonDetailContext);
        var artworkItems = group.PreviewItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Select(item => new ArtworkStackItem
            {
                Id = item.WorkId.ToString("D"),
                WorkId = item.WorkId,
                Title = item.Title,
                ImageUrl = item.ImageUrl!,
                MediaType = group.PrimaryMediaType,
                NavigationUrl = BuildGroupPreviewNavigationUrl(group, item.WorkId),
                Shape = item.Shape?.Trim().ToLowerInvariant() switch
                {
                    "square" => ArtworkShape.Square,
                    "wide" or "landscape" => ArtworkShape.Wide,
                    _ => ArtworkShape.Portrait,
                },
                Position = item.Position,
                Description = item.Description,
                Facts = item.Facts ?? [],
            })
            .Take(4)
            .ToList();

        if (artworkItems.Count == 0 && !string.IsNullOrWhiteSpace(group.CoverUrl))
        {
            artworkItems.Add(new ArtworkStackItem
            {
                Id = group.CollectionId.ToString("D"),
                Title = group.DisplayName,
                ImageUrl = group.CoverUrl,
                MediaType = group.PrimaryMediaType,
                Shape = ArtworkShape.Portrait,
            });
        }

        var roles = group.PersonRoles.Count > 0
            ? group.PersonRoles
            : new List<string> { PersonGroupingRoleLabel };

        return MediaPersonGroupTileComposer.Compose(
            GetGroupNavigationId(group),
            group.DisplayName,
            group.PersonId ?? group.ArtistPersonId,
            group.PersonPhotoUrl ?? group.ArtistPhotoUrl,
            roles,
            group.PrimaryMediaType,
            group.WorkCount,
            group.EarliestYear,
            group.LatestYear,
            group.CreatedAt,
            artworkItems,
            navigationUrl,
            group.MediaTypeColor ?? AccentForMedia(group.PrimaryMediaType),
            group.AlbumCount,
            group.DistinctTitleCount ?? group.WorkCount);
    }

    private string PersonGroupingRoleLabel => _grouping switch
    {
        "authors" => "Author",
        "creators" => "Creator",
        "directors" => "Director",
        "artists" => "Artist",
        "narrators" => "Narrator",
        _ => "Creator",
    };

    private static Guid GenerateDeterministicGuid(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private static MediaTileViewModel WithCardOverrides(
        MediaTileViewModel card,
        string? description,
        string? heroUrl,
        IReadOnlyList<string>? previewImages,
        string? creator,
        string? collectionKey,
        bool showLogoAsBrand,
        int sortYear) => new()
        {
            Id = card.Id,
            WorkId = card.WorkId,
            CollectionId = card.CollectionId,
            Title = card.Title,
            Subtitle = card.Subtitle,
            Description = description,
            CoverUrl = card.CoverUrl,
            BackgroundUrl = card.BackgroundUrl,
            BannerUrl = card.BannerUrl,
            HeroUrl = heroUrl,
            LogoUrl = card.LogoUrl,
            ShowLogoAsBrand = showLogoAsBrand,
            PreviewImages = previewImages ?? card.PreviewImages,
            ArtworkStackItems = card.ArtworkStackItems,
            PreviewTotalCount = card.PreviewTotalCount,
            StatusText = card.StatusText,
            MetaText = card.MetaText,
            MediaCounts = card.MediaCounts,
            GroupSummary = card.GroupSummary,
            ContextLines = card.ContextLines,
            HoverFacts = card.HoverFacts,
            Tldr = card.Tldr,
            VibeTags = card.VibeTags,
            MediaKind = card.MediaKind,
            AccentColor = card.AccentColor,
            SecondaryAccentColor = card.SecondaryAccentColor,
            ArtworkPalette = card.ArtworkPalette,
            Shape = card.Shape,
            Presentation = card.Presentation,
            SurfaceKind = card.SurfaceKind,
            TileTextMode = card.TileTextMode,
            PreviewPlacement = card.PreviewPlacement,
            TileImageUrl = card.TileImageUrl,
            TileImageSrcSet = card.TileImageSrcSet,
            TileImageSizes = card.TileImageSizes,
            HoverImageUrl = card.HoverImageUrl,
            HoverImageSrcSet = card.HoverImageSrcSet,
            HoverImageSizes = card.HoverImageSizes,
            HeroBackgroundImageUrl = card.HeroBackgroundImageUrl,
            PreviewImageUrl = card.PreviewImageUrl,
            TileImageFitMode = card.TileImageFitMode,
            HoverImageFitMode = card.HoverImageFitMode,
            RepresentativeEntityId = card.RepresentativeEntityId,
            NavigationUrl = card.NavigationUrl,
            PrimaryNavigationUrl = card.PrimaryNavigationUrl,
            DetailsNavigationUrl = card.DetailsNavigationUrl,
            PrimaryActionLabel = card.PrimaryActionLabel,
            ProgressPct = card.ProgressPct,
            ProgressLabel = card.ProgressLabel,
            Creator = creator,
            CollectionKey = collectionKey,
            Genres = card.Genres,
            SortYear = sortYear,
            SortTimestamp = card.SortTimestamp,
            IsCollection = card.IsCollection,
            UseGroupTile = card.UseGroupTile,
        };

    private static bool IsReadMedia(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        return value.Contains("book", StringComparison.OrdinalIgnoreCase)
               || value.Contains("comic", StringComparison.OrdinalIgnoreCase)
               || value.Contains("epub", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cbz", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cbr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsListenMedia(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        return value.Contains("music", StringComparison.OrdinalIgnoreCase)
               || value.Contains("audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWatchMedia(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        return value.Contains("movie", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tv", StringComparison.OrdinalIgnoreCase)
               || value.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayableMedia(string? mediaType) =>
        IsWatchMedia(mediaType) || IsListenMedia(mediaType);

    private static string BuildGroupDescription(ContentGroupViewModel group)
    {
        var suppliedDescription = new[] { group.Description, group.Tagline }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(suppliedDescription))
        {
            return suppliedDescription;
        }

        var parts = new[]
        {
            group.Creator,
            group.Network,
            group.SeasonCount is > 0 ? $"{group.SeasonCount.Value} seasons" : null,
            group.AlbumCount is > 0 ? $"{group.AlbumCount.Value} albums" : null,
            group.WorkCount > 0 ? $"{group.WorkCount} items" : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        return string.Join(" · ", parts);
    }

    private static string AccentForMedia(string? mediaType) =>
        AppMediaPresentation.AccentFor(mediaType);

    private static string FormatMediaLabel(string? mediaType) => mediaType switch
    {
        "TV" => "TV Series",
        "Movies" => "Movies",
        "Books" => "Books",
        "Comics" => "Comics",
        "Music" => "Music",
        "Audiobooks" => "Audiobooks",
        _ => mediaType ?? "Library",
    };

    private static int ParseYear(string? year)
    {
        if (string.IsNullOrWhiteSpace(year))
        {
            return 0;
        }

        return int.TryParse(year.AsSpan(0, Math.Min(4, year.Length)), out var parsed) ? parsed : 0;
    }
}
