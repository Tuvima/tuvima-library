using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    private static string Pluralize(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

    private MediaTileViewModel ToAudiobookTile(AudiobookDisplayItem item)
    {
        if (item.Card is not null)
        {
            return MediaTileComposerService.FromDisplayCard(item.Card);
        }

        var work = item.Work;
        var cover = work?.CoverUrl ?? work?.CoverUrlMedium ?? work?.CoverUrlSmall;
        var small = MediaTileArtworkUrl.Sized(cover, "s");
        var medium = MediaTileArtworkUrl.Sized(cover, "m");
        var backgroundMedium = MediaTileArtworkUrl.Sized(work?.BackgroundUrl, "m");
        var backgroundLarge = MediaTileArtworkUrl.Sized(work?.BackgroundUrl, "l");
        var route = AudiobookRoute(item);
        var facts = new[]
            {
                AudiobookCreatorLine(item),
                work?.Year,
                AudiobookDurationLabel(item),
                work is null ? null : GetCanonicalValue(work, "content_rating", "certification"),
                work is null ? null : GetCanonicalValue(work, "rating"),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        return new MediaTileViewModel
        {
            Id = work?.Id ?? item.Card?.Id ?? Guid.NewGuid(),
            WorkId = work?.Id,
            Title = AudiobookTitle(item),
            Subtitle = AudiobookCreatorLine(item),
            CoverUrl = cover,
            BackgroundUrl = work?.BackgroundUrl,
            LogoUrl = work?.LogoUrl,
            HoverFacts = facts,
            MediaKind = "Audiobook",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = string.IsNullOrWhiteSpace(backgroundMedium)
                ? MediaTileShape.Portrait
                : MediaTileShape.Landscape,
            Presentation = MediaTilePresentation.Default,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = small,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            HoverImageUrl = backgroundMedium ?? medium,
            HoverImageSrcSet = MediaTileArtworkUrl.SrcSet(backgroundMedium ?? medium, backgroundLarge),
            TileImageFitMode = MediaTileImageFitMode.Contain,
            HoverImageFitMode = string.IsNullOrWhiteSpace(backgroundMedium)
                ? MediaTileImageFitMode.Contain
                : MediaTileImageFitMode.Fill,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            PrimaryActionLabel = AudiobookProgressPercent(item) > 0 ? "Resume" : "Play",
            ProgressPct = AudiobookProgressPercent(item),
            ProgressLabel = AudiobookProgressLabel(item),
            AccentColor = "var(--tl-media-audio)",
            SortTimestamp = AudiobookSortTimestamp(item),
            SortYear = int.TryParse(work?.Year, out var audiobookYear) ? audiobookYear : 0,
        };
    }

    private MediaTileViewModel ToContentPersonTile(ContentGroupViewModel group, string defaultRole)
    {
        var personId = group.PersonId ?? group.ArtistPersonId;
        var roles = group.PersonRoles.Count > 0 ? group.PersonRoles : [defaultRole];
        var artworkItems = BuildContentGroupArtwork(group);
        return MediaPersonGroupTileComposer.Compose(
            personId ?? GenerateDeterministicGuid($"{defaultRole}:{group.DisplayName}"),
            group.DisplayName,
            personId,
            group.PersonPhotoUrl ?? group.ArtistPhotoUrl,
            roles,
            group.PrimaryMediaType,
            group.WorkCount,
            group.EarliestYear,
            group.LatestYear,
            group.CreatedAt,
            artworkItems,
            MediaPersonGroupTileComposer.NavigationUrl(personId, group.DisplayName),
            group.MediaTypeColor,
            group.AlbumCount,
            group.DistinctTitleCount ?? group.WorkCount);
    }

    private MediaTileViewModel ToAudiobookPersonTile(
        AudiobookAuthorGroup personGroup,
        string role,
        IReadOnlyList<ContentGroupViewModel> identityGroups)
    {
        var identity = identityGroups.FirstOrDefault(group =>
            PersonNamesMatch(group.DisplayName, personGroup.Name));
        var personId = identity?.PersonId ?? identity?.ArtistPersonId;
        var roles = identity?.PersonRoles.Count > 0 ? identity.PersonRoles : [role];
        var artworkItems = personGroup.Items
            .Select(item =>
            {
                var workId = AudiobookWorkId(item);
                return new ArtworkStackItem
                {
                    Id = workId?.ToString("D") ?? GenerateDeterministicGuid($"{personGroup.Name}:{AudiobookTitle(item)}").ToString("D"),
                    WorkId = workId,
                    Title = AudiobookTitle(item),
                    ImageUrl = AudiobookCoverUrl(item) ?? string.Empty,
                    MediaType = "Audiobooks",
                    NavigationUrl = AudiobookRoute(item),
                    Shape = ArtworkShape.Portrait,
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Take(4)
            .ToList();
        var years = personGroup.Items
            .Select(AudiobookPublicationYear)
            .Where(year => year.HasValue)
            .Select(year => year!.Value)
            .ToList();
        var latestTimestamp = personGroup.Items.Count == 0
            ? DateTimeOffset.MinValue
            : personGroup.Items.Max(AudiobookSortTimestamp);

        return MediaPersonGroupTileComposer.Compose(
            personId ?? GenerateDeterministicGuid($"{role}:{personGroup.Name}"),
            personGroup.Name,
            personId,
            identity?.PersonPhotoUrl ?? identity?.ArtistPhotoUrl,
            roles,
            "Audiobooks",
            personGroup.Items.Count,
            years.Count == 0 ? null : years.Min(),
            years.Count == 0 ? null : years.Max(),
            latestTimestamp,
            artworkItems,
            MediaPersonGroupTileComposer.NavigationUrl(personId, personGroup.Name),
            "var(--tl-media-audio)");
    }

    private static IReadOnlyList<ArtworkStackItem> BuildContentGroupArtwork(ContentGroupViewModel group)
    {
        var artworkItems = group.PreviewItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl))
            .Select(item => new ArtworkStackItem
            {
                Id = item.WorkId.ToString("D"),
                WorkId = item.WorkId,
                Title = item.Title,
                ImageUrl = item.ImageUrl,
                MediaType = group.PrimaryMediaType,
                Shape = item.Shape.Trim().ToLowerInvariant() switch
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

        return artworkItems;
    }

    private static int? AudiobookPublicationYear(AudiobookDisplayItem item)
    {
        if (int.TryParse(item.Work?.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workYear))
        {
            return workYear;
        }

        foreach (var fact in item.Card?.Facts ?? [])
        {
            if (fact.Length == 4
                && int.TryParse(fact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var factYear))
            {
                return factYear;
            }
        }

        return null;
    }

    private static bool PersonNamesMatch(string left, string right) =>
        string.Equals(
            NormalizePersonName(left),
            NormalizePersonName(right),
            StringComparison.Ordinal);

    private static string NormalizePersonName(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private MediaTileViewModel ToAlbumTile(ContentGroupViewModel album) =>
        SquareTile(
            id: album.CollectionId,
            title: album.DisplayName,
            subtitle: StringHelpers.FirstNonBlankOr("-", album.Creator, album.Year, Pluralize(album.WorkCount, "track")),
            imageUrl: album.CoverUrl ?? album.ArtistPhotoUrl,
            backgroundUrl: album.BackgroundUrl ?? album.HeroUrl,
            bannerUrl: album.BannerUrl,
            logoUrl: album.LogoUrl,
            description: album.Description,
            facts: string.IsNullOrWhiteSpace(album.Year) ? [] : [album.Year],
            route: $"/details/musicalbum/{album.CollectionId:D}?context=listen",
            presentation: MediaTilePresentation.Album,
            primaryActionLabel: "Open",
            sortYear: int.TryParse(album.Year, out var albumYear) ? albumYear : 0,
            creator: album.Creator);

    private MediaTileViewModel ToArtistAlbumTile(CollectionGroupSeasonDto album)
    {
        var title = album.SeasonLabel ?? $"Album {album.SeasonNumber}";
        var route = album.AlbumCollectionId.HasValue
            ? $"/details/musicalbum/{album.AlbumCollectionId.Value}?context=listen"
            : BuildAlbumDetailRoute(album.SeasonLabel, _artistDetail?.DisplayName);

        return SquareTile(
            id: album.AlbumCollectionId ?? GenerateDeterministicGuid($"{_artistDetail?.DisplayName}:{title}"),
            title: title,
            subtitle: StringHelpers.FirstNonBlankOr("-", album.Year, Pluralize(album.Episodes.Count, "track")),
            imageUrl: album.CoverUrl,
            route: route,
            presentation: MediaTilePresentation.Album,
            primaryActionLabel: "Open");
    }

    private static MediaTileViewModel SquareTile(
        Guid id,
        string title,
        string? subtitle,
        string? imageUrl,
        string route,
        MediaTilePresentation presentation,
        string primaryActionLabel,
        string? backgroundUrl = null,
        string? bannerUrl = null,
        string? logoUrl = null,
        string? description = null,
        IReadOnlyList<string>? facts = null,
        int sortYear = 0,
        string? creator = null)
    {
        var small = MediaTileArtworkUrl.Sized(imageUrl, "s");
        var medium = MediaTileArtworkUrl.Sized(imageUrl, "m");
        var backgroundMedium = MediaTileArtworkUrl.Sized(backgroundUrl, "m");
        var backgroundLarge = MediaTileArtworkUrl.Sized(backgroundUrl, "l");
        var bannerMedium = MediaTileArtworkUrl.Sized(bannerUrl, "m");
        var bannerLarge = MediaTileArtworkUrl.Sized(bannerUrl, "l");
        var hoverImage = StringHelpers.FirstNonBlank(backgroundMedium, bannerMedium, medium);
        return new MediaTileViewModel
        {
            Id = id,
            Title = title,
            Subtitle = subtitle,
            Creator = creator,
            Description = description,
            CoverUrl = imageUrl,
            BackgroundUrl = backgroundUrl,
            BannerUrl = bannerUrl,
            LogoUrl = logoUrl,
            HoverFacts = facts ?? [],
            MediaKind = "Music",
            Shape = MediaTileShape.Square,
            HoverArtworkShape = !string.IsNullOrWhiteSpace(backgroundMedium) || !string.IsNullOrWhiteSpace(bannerMedium)
                ? MediaTileShape.Landscape
                : MediaTileShape.Square,
            Presentation = presentation,
            SurfaceKind = MediaTileSurfaceKind.CoverSquare,
            TileTextMode = MediaTileTextMode.CoverOnly,
            TileImageUrl = small,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            HoverImageUrl = hoverImage,
            HoverImageSrcSet = MediaTileArtworkUrl.SrcSet(hoverImage, StringHelpers.FirstNonBlank(backgroundLarge, bannerLarge, medium)),
            TileImageFitMode = MediaTileImageFitMode.Contain,
            HoverImageFitMode = !string.IsNullOrWhiteSpace(backgroundMedium) || !string.IsNullOrWhiteSpace(bannerMedium)
                ? MediaTileImageFitMode.Fill
                : MediaTileImageFitMode.Contain,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            PrimaryNavigationUrl = route,
            PrimaryActionLabel = primaryActionLabel,
            AccentColor = "var(--tl-media-audio)",
            SortTimestamp = DateTimeOffset.UtcNow,
            SortYear = sortYear,
        };
    }

    private static Guid GenerateDeterministicGuid(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }
}
