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
    private IReadOnlyList<WorkViewModel> SortedSongs => SortSongs(_musicWorks);
    private IReadOnlyList<WorkViewModel> FilteredSongs => ApplySongFilters(SortedSongs);
    private IReadOnlyList<WorkViewModel> RecentlyAddedTracks => _musicWorks.OrderByDescending(work => work.CreatedAt).ToList();
    private IReadOnlyList<WorkViewModel> AlbumTracks => _albumDetail is null
        ? []
        : ResolveGroupWorks(_albumDetail.Works)
            .OrderBy(work => ParseTrackNumber(work.TrackNumber))
            .ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    private IReadOnlyList<CollectionGroupSeasonDto> ArtistAlbums => _artistDetail?.Seasons ?? [];
    private IReadOnlyList<WorkViewModel> ArtistTracks => ResolveArtistTracks();
    private IReadOnlyList<WorkViewModel> ActivePlaylistTracks => ResolveActivePlaylistTracks();
    private IReadOnlyList<ListenPlaylistTrackRow> ActivePlaylistTrackRows => ResolveActivePlaylistTrackRows();
    private IReadOnlyList<PlaylistColumnDefinition> PlaylistColumns => BuildPlaylistColumns();
    private string PlaylistGridTemplate => BuildPlaylistGridTemplate();
    private string SongsGridTemplate => BuildSongsGridTemplate();
    private IReadOnlyList<ContentGroupViewModel> FilteredAlbumGroups => ApplyAlbumFilters();
    private IReadOnlyList<ContentGroupViewModel> FilteredArtistGroups => ApplyArtistFilters();
    private IReadOnlyList<MediaTileViewModel> ArtistPersonTiles => FilteredArtistGroups
        .Select(group => ToContentPersonTile(group, "Artist"))
        .ToList();
    private string ListenPageClass => IsAudioDetailSurface
        ? "listen-page listen-page--detail"
        : IsAudiobooksMode
            ? "listen-page listen-page--audiobooks"
            : "listen-page";
    private IReadOnlyList<AudiobookListenItem> AudiobookContinueCards => BuildAudiobookContinueCards();
    private IReadOnlyList<AudiobookDisplayItem> AudiobookDisplayItems => BuildAudiobookDisplayItems();
    private IReadOnlyList<AudiobookDisplayItem> FilteredAudiobookItems => ApplyAudiobookFilters();
    private IReadOnlyList<AudiobookDisplayItem> AudiobookBrowseItems => BuildAudiobookBrowseItems();
    private IReadOnlyList<MediaTileViewModel> AudiobookBrowseTiles => AudiobookBrowseItems.Select(ToAudiobookTile).ToList();
    private IReadOnlyList<ContentGroupViewModel> AudiobookSeriesGroups => ApplyAudiobookSeriesFilters();
    private IReadOnlyList<ContentGroupViewModel> FeaturedAudiobookSeries => _audiobookSeriesGroups
        .Where(group => group.WorkCount >= 2)
        .OrderByDescending(group => group.WorkCount)
        .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToList();
    private IReadOnlyList<AudiobookAuthorGroup> AudiobookAuthorGroups => BuildAudiobookAuthorGroups(FilteredAudiobookItems);
    private IReadOnlyList<AudiobookAuthorGroup> AudiobookNarratorGroups => BuildAudiobookNarratorGroups(FilteredAudiobookItems);
    private IReadOnlyList<MediaTileViewModel> AudiobookAuthorPersonTiles => AudiobookAuthorGroups
        .Select(group => ToAudiobookPersonTile(group, "Author", _audiobookAuthorPersonGroups))
        .ToList();
    private IReadOnlyList<MediaTileViewModel> AudiobookNarratorPersonTiles => AudiobookNarratorGroups
        .Select(group => ToAudiobookPersonTile(group, "Narrator", _audiobookNarratorPersonGroups))
        .ToList();
    private IReadOnlyList<MediaTileViewModel> MusicTimelineTiles => AlbumTiles;
    private string AudiobookBrowseTitle => _audiobookView switch
    {
        "series" => "Series",
        "authors" => "Authors",
        "narrators" => "Narrators",
        "timeline" => "Timeline",
        _ when !string.IsNullOrWhiteSpace(_audiobookSeriesFocus) => _audiobookSeriesFocus,
        _ => "Browse All",
    };
    private string AudiobookBrowseSummary => _audiobookView switch
    {
        "series" => AudiobookSeriesGroups.Count == 1 ? "1 series" : $"{AudiobookSeriesGroups.Count} series",
        "authors" => Pluralize(AudiobookAuthorGroups.Count, "author"),
        "narrators" => Pluralize(AudiobookNarratorGroups.Count, "narrator"),
        "timeline" => Pluralize(FilteredAudiobookItems.Count, "audiobook"),
        _ => Pluralize(FilteredAudiobookItems.Count, "audiobook"),
    };
    private string AudiobookBooksLayoutClass => _audiobookLayout == LibraryLayoutMode.List
        ? "audiobooks-books audiobooks-books--list"
        : "audiobooks-books";
    private IReadOnlyList<string> PlaylistCoverUrls => ActivePlaylistTracks.Select(track => track.CoverUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct().Cast<string>().Take(4).ToList();
    private IReadOnlyList<ManagedCollectionViewModel> PlaylistCollections => _managedCollections
        .Where(MediaTileComposerService.IsUserVisiblePlaylist)
        .OrderBy(GetPlaylistOrderIndex)
        .ThenBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<ManagedCollectionViewModel> ManualPlaylistCollections => PlaylistCollections
        .Where(CanAcceptPlaylistItems)
        .ToList();
    private bool HasPlaylistCollections => PlaylistCollections.Count > 0;
    private IReadOnlyList<MediaTileViewModel> HomePlaylistTiles => PlaylistCollections
        .Take(6)
        .Select(MediaTileComposerService.FromPlaylist)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> AlbumTiles => FilteredAlbumGroups
        .Select(ToAlbumTile)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> ArtistAlbumTiles => ArtistAlbums
        .Select(ToArtistAlbumTile)
        .ToList();
    private IReadOnlyList<MediaTileViewModel> PlaylistTiles => PlaylistCollections
        .Select(MediaTileComposerService.FromPlaylist)
        .ToList();
    private IReadOnlyList<string> SongGenres => _musicWorks
        .SelectMany(work => work.Genres)
        .Where(genre => !string.IsNullOrWhiteSpace(genre))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> SongArtists => _musicWorks
        .Select(work => StringHelpers.FirstNonBlankOr("-", work.Artist, work.Author, null))
        .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "-", StringComparison.Ordinal))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> SongAlbums => _musicWorks
        .Select(work => work.Album)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private IReadOnlyList<string> AlbumArtists => _albumGroups
        .Select(album => album.Creator)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    private bool HasTrackSelection => _selectedTrackIds.Count > 0;
    private int SelectedTrackCount => _selectedTrackIds.Count;
    private string TrackSelectionStatusText => HasTrackSelection
        ? $"{SelectedTrackCount} selected"
        : "Single-click to select, ctrl/shift-click to add more, and use the play icon to start playback.";

    private IReadOnlyList<MediaHubShelfViewModel> ListenHubShelves
    {
        get
        {
            var shelves = _listenHubPage.Shelves
                .Where(shelf => shelf.Kind != MediaTileShelfKind.Collections)
                .Select(shelf => MediaHubShelfViewModel.FromShelf(shelf))
                .ToList();

            if (HomePlaylistTiles.Count > 0)
            {
                shelves.Add(new MediaHubShelfViewModel
                {
                    Key = "listen-playlists",
                    Title = "Playlists",
                    Subtitle = "Saved playlists and mixes from your library",
                    Items = HomePlaylistTiles,
                    SeeAllRoute = PlaylistsRoute,
                });
            }

            return shelves;
        }
    }
}
