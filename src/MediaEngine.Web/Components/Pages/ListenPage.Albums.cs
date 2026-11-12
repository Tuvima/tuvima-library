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
    private DetailPageViewModel? BuildAlbumDetailModel(CollectionGroupDetailViewModel? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var id = detail.RootWorkId ?? detail.CollectionId;
        var artworkUrl = StringHelpers.FirstNonBlank(detail.CoverUrl, detail.HeroUrl, detail.BackgroundUrl);
        var artist = StringHelpers.FirstNonBlank(detail.Creator, detail.Writer, detail.Director);
        var tracks = detail.Works
            .OrderBy(work => ParseTrackNumber(StringHelpers.FirstNonBlank(work.TrackNumber, work.Ordinal?.ToString(CultureInfo.InvariantCulture))))
            .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
            .Select(work => new MediaGroupingItemViewModel
            {
                Id = work.WorkId.ToString("D"),
                EntityType = DetailEntityType.Work,
                Title = string.IsNullOrWhiteSpace(work.Title) ? "Untitled track" : work.Title,
                Subtitle = artist,
                ArtworkUrl = StringHelpers.FirstNonBlank(work.CoverUrl, artworkUrl),
                TrackNumber = StringHelpers.FirstNonBlank(work.TrackNumber, work.Ordinal?.ToString(CultureInfo.InvariantCulture)),
                Duration = work.Duration,
                DurationSeconds = work.DurationSeconds,
                Artist = artist,
                AssetId = work.AssetId?.ToString("D"),
                Metadata = string.IsNullOrWhiteSpace(work.Duration) ? [] : [new MetadataPill { Label = work.Duration!, Kind = "duration" }],
                Actions = work.WorkId == Guid.Empty ? [] : [new DetailAction { Key = "play-track", Label = "Play", Icon = "play_arrow" }],
                IsOwned = work.IsOwned,
                ProgressState = work.IsOwned ? LibraryProgressState.Unstarted : LibraryProgressState.Missing,
            })
            .ToList();

        IReadOnlyList<CreditGroupViewModel> contributors = string.IsNullOrWhiteSpace(artist)
            ? []
            : new List<CreditGroupViewModel>
            {
                new()
                {
                    Title = "Artist",
                    GroupType = CreditGroupType.MusicCredits,
                    Credits =
                    [
                        new EntityCreditViewModel
                        {
                            EntityType = RelatedEntityType.Person,
                            DisplayName = artist!,
                            FallbackInitials = Initials(artist!),
                            PrimaryRole = "Artist",
                            IsPrimary = true,
                        },
                    ],
                },
            };

        return new DetailPageViewModel
        {
            Id = id.ToString("D"),
            EntityType = DetailEntityType.MusicAlbum,
            PresentationContext = DetailPresentationContext.Listen,
            EditorTarget = id == Guid.Empty
                ? null
                : new DetailEditorTarget
                {
                    EntityId = id.ToString("D"),
                    EntityKind = detail.RootWorkId.HasValue ? "Work" : "Collection",
                    ContainerMode = detail.RootWorkId.HasValue ? "Canonical" : "Curated",
                    InitialTab = "tracks",
                },
            Title = detail.DisplayName,
            Subtitle = artist,
            Description = detail.Description,
            Tagline = detail.Tagline,
            Artwork = new ArtworkSet
            {
                CoverUrl = artworkUrl,
                PosterUrl = artworkUrl,
                BackdropUrl = StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, detail.BannerUrl),
                BannerUrl = detail.BannerUrl,
                LogoUrl = detail.LogoUrl,
                DominantColors = detail.DominantColors,
                PrimaryColor = detail.PrimaryColor,
                SecondaryColor = detail.SecondaryColor,
                AccentColor = detail.AccentColor,
                HeroArtwork = new HeroArtworkViewModel
                {
                    Url = StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, artworkUrl),
                    Mode = HeroArtworkMode.ArtworkFallback,
                    HasImage = !string.IsNullOrWhiteSpace(StringHelpers.FirstNonBlank(detail.BackgroundUrl, detail.HeroUrl, artworkUrl)),
                },
                PresentationMode = ArtworkPresentationMode.ColorGradientFromArtwork,
            },
            Metadata = BuildAlbumDetailMetadata(detail, tracks.Count),
            PrimaryActions =
            [
                new DetailAction { Key = "play-album", Label = "Listen", Icon = "headphones", IsPrimary = true },
            ],
            SecondaryActions =
            [
                new DetailAction
                {
                    Key = "my-list",
                    Label = L["TopBar_MyList"].Value,
                    Icon = _favoriteWorkIds.Contains(id) ? "check" : "add",
                    Tooltip = _favoriteWorkIds.Contains(id) ? L["MediaTile_RemoveFromMyList"].Value : L["MediaTile_AddToMyList"].Value,
                    DisplayStyle = "icon",
                    IsSelected = _favoriteWorkIds.Contains(id),
                },
                new DetailAction
                {
                    Key = "reaction-menu",
                    Label = "Rate",
                    Icon = "thumb_up",
                    Tooltip = "Rate this title",
                    DisplayStyle = "icon",
                    Children =
                    [
                        new DetailAction { Key = "reaction-dislike", Label = "Not for me", Icon = "thumb_down" },
                        new DetailAction { Key = "reaction-like", Label = "I like this", Icon = "thumb_up" },
                    ],
                },
                new DetailAction { Key = "shuffle", Label = "Shuffle", Icon = "shuffle", DisplayStyle = "icon" },
            ],
            OverflowActions = [],
            ContributorGroups = contributors,
            PreviewContributors = contributors.SelectMany(group => group.Credits).Take(3).ToList(),
            Tabs =
            [
                new DetailTab { Key = "tracks", Label = "Tracks" },
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "credits", Label = "Credits" },
                new DetailTab { Key = "details", Label = "Details" },
            ],
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "tracks",
                    Title = "Tracks",
                    Items = tracks,
                    OwnedCount = tracks.Count(track => track.IsOwned),
                    TotalCount = tracks.Count,
                    MissingCount = tracks.Count(track => !track.IsOwned),
                    CompletionPercent = tracks.Count == 0
                        ? 0
                        : Math.Round((double)tracks.Count(track => track.IsOwned) / tracks.Count * 100d, 1),
                },
            ],
            LibraryStatus = MediaEngine.Contracts.Details.LibraryStatus.Owned,
        };
    }

    private static IReadOnlyList<MetadataPill> BuildAlbumDetailMetadata(CollectionGroupDetailViewModel detail, int trackCount)
    {
        var values = new List<MetadataPill>();

        AddMetadata(values, StringHelpers.FirstNonBlank(detail.YearRange, detail.ReleaseDate), "year");
        AddMetadata(values, Pluralize(trackCount > 0 ? trackCount : detail.TotalItems, "track"), "track_count");
        AddMetadata(values, detail.TotalDuration, "duration");
        AddMetadata(values, detail.Genre, "genre");
        return values;
    }

    private static void AddMetadata(List<MetadataPill> values, string? label, string kind)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            values.Add(new MetadataPill { Label = label, Kind = kind });
        }
    }

    private static string Initials(string value)
        => string.Join(
            string.Empty,
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0])));

    private string BuildAlbumDetailRoute(string? albumName, string? artistName = null, Guid? trackId = null)
    {
        var album = _albumGroups.FirstOrDefault(group =>
            string.Equals(group.DisplayName, albumName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(artistName)
                || string.Equals(group.Creator, artistName, StringComparison.OrdinalIgnoreCase)));
        if (album is not null)
        {
            var trackQuery = trackId.HasValue ? $"&track={trackId.Value:D}" : string.Empty;
            return $"/details/musicalbum/{album.CollectionId:D}?context=listen{trackQuery}";
        }

        return trackId.HasValue
            ? $"/listen/music?browse=songs&track={trackId.Value:D}"
            : AlbumsRoute;
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyAlbumFilters()
    {
        IEnumerable<ContentGroupViewModel> filtered = _albumGroups;

        if (!string.IsNullOrWhiteSpace(_albumSearch))
        {
            filtered = filtered.Where(group =>
                group.DisplayName.Contains(_albumSearch, StringComparison.OrdinalIgnoreCase)
                || (group.Creator?.Contains(_albumSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.Equals(_albumArtistFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(group => string.Equals(group.Creator, _albumArtistFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(_albumGenreFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(group => AlbumMatchesGenre(group, _albumGenreFilter));
        }

        if (_albumFavoriteOnly)
        {
            filtered = filtered.Where(AlbumHasFavorites);
        }

        if (_albumArtOnly)
        {
            filtered = filtered.Where(group => !string.IsNullOrWhiteSpace(group.CoverUrl));
        }

        filtered = _albumSort switch
        {
            "title" => filtered.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            "artist" => filtered.OrderBy(group => group.Creator, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            "year" => filtered.OrderByDescending(group => ParseYear(group.Year)).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(group => group.CreatedAt).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return filtered.ToList();
    }

    private bool AlbumMatchesGenre(ContentGroupViewModel album, string genre)
        => AlbumWorksFor(album).Any(work => work.Genres.Any(item => string.Equals(item, genre, StringComparison.OrdinalIgnoreCase)));

    private bool AlbumHasFavorites(ContentGroupViewModel album)
        => AlbumWorksFor(album).Any(work => _favoriteWorkIds.Contains(work.Id));

    private IReadOnlyList<WorkViewModel> AlbumWorksFor(ContentGroupViewModel album)
        => _musicWorks
            .Where(work => AlbumMatchesWork(album, work))
            .ToList();

    private static bool AlbumMatchesWork(ContentGroupViewModel album, WorkViewModel work)
    {
        if (work.CollectionId == album.CollectionId)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(work.Album)
            || !string.Equals(work.Album, album.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(album.Creator))
        {
            return true;
        }

        var workCreator = StringHelpers.FirstNonBlank(work.Artist, work.Author);
        return string.Equals(workCreator, album.Creator, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ContentGroupViewModel> BuildMusicGroupSummaries(IEnumerable<WorkViewModel> works, string groupField)
    {
        var normalizedGroupField = groupField.Trim().ToLowerInvariant();
        return works
            .Select(work => new
            {
                Work = work,
                Key = normalizedGroupField == "artist"
                    ? StringHelpers.FirstNonBlank(work.Artist, work.Author)
                    : StringHelpers.FirstNonBlank(work.Album),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupWorks = group.Select(item => item.Work).ToList();
                var first = groupWorks.First();
                var creator = normalizedGroupField == "album"
                    ? StringHelpers.FirstNonBlank(first.Artist, first.Author)
                    : null;

                return new ContentGroupViewModel
                {
                    CollectionId = GenerateDeterministicGuid($"music:{normalizedGroupField}:{group.Key}"),
                    DisplayName = group.Key,
                    PrimaryMediaType = "Music",
                    WorkCount = groupWorks.Count,
                    CoverUrl = StringHelpers.FirstNonBlank(first.SquareUrl, first.CoverUrl),
                    BackgroundUrl = first.BackgroundUrl,
                    BannerUrl = first.BannerUrl,
                    Creator = creator,
                    ArtistPhotoUrl = normalizedGroupField == "artist" ? StringHelpers.FirstNonBlank(first.SquareUrl, first.CoverUrl) : null,
                    CreatedAt = groupWorks.Max(work => work.CreatedAt),
                };
            })
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
