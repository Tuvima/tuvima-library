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
    private Task NavigateAndReloadAsync(bool reloadData = true, bool replace = false)
    {
        _navigatingInternally = true;
        Nav.NavigateTo(BuildCurrentUrl(), replace: replace);
        return reloadData ? LoadBrowseContentAsync() : Task.CompletedTask;
    }

    private string BuildCurrentUrl()
    {
        var defaultTab = Preset.Tabs[0].Id;
        var path = string.Equals(_activeTabId, defaultTab, StringComparison.OrdinalIgnoreCase)
                   && !Preset.UseExplicitDefaultTabRoute
            ? Preset.RouteBase
            : $"{Preset.RouteBase}/{_activeTabId}";

        var queryParts = new List<string>();
        if (_timelineGrouping == TimelineGrouping.Decade)
        {
            queryParts.Add("period=decade");
        }

        if (!string.Equals(_grouping, CurrentTab.DefaultGrouping, StringComparison.OrdinalIgnoreCase))
        {
            queryParts.Add($"browse={Uri.EscapeDataString(_grouping)}");
        }

        if (_layout != ResolveLayout(null, CurrentTab, _grouping))
        {
            queryParts.Add($"view={_layout.ToString().ToLowerInvariant()}");
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            queryParts.Add($"q={Uri.EscapeDataString(_searchText)}");
        }

        if (_selectedGenres.Count > 0)
        {
            queryParts.AddRange(_selectedGenres.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"genre={Uri.EscapeDataString(value)}"));
        }

        if (_selectedCreators.Count > 0)
        {
            queryParts.AddRange(_selectedCreators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"person={Uri.EscapeDataString(value)}"));
        }

        if (!string.IsNullOrWhiteSpace(_status))
        {
            queryParts.Add($"status={Uri.EscapeDataString(_status)}");
        }

        if (_libraryChip is Guid chipLibrary)
        {
            queryParts.Add($"library={chipLibrary:D}");
        }

        if (_unmatched)
        {
            queryParts.Add("unmatched=true");
        }

        if (_selectedYears.Count > 0)
        {
            queryParts.AddRange(_selectedYears.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"year={Uri.EscapeDataString(value)}"));
        }

        if (_selectedVibes.Count > 0)
        {
            queryParts.AddRange(_selectedVibes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"vibe={Uri.EscapeDataString(value)}"));
        }

        if (_selectedThemes.Count > 0)
        {
            queryParts.AddRange(_selectedThemes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"theme={Uri.EscapeDataString(value)}"));
        }

        if (!string.Equals(_sortBy, ResolveSort(null), StringComparison.OrdinalIgnoreCase))
        {
            queryParts.Add($"sort={Uri.EscapeDataString(_sortBy)}");
        }

        return queryParts.Count == 0 ? path : $"{path}?{string.Join("&", queryParts)}";
    }

    private string BuildGroupNavigationUrl(ContentGroupViewModel group)
    {
        if (IsPersonGrouping)
        {
            return MediaPersonGroupTileComposer.NavigationUrl(
                group.PersonId ?? group.ArtistPersonId,
                group.DisplayName,
                PersonDetailContext);
        }

        return (BehaviourTabId, _grouping) switch
        {
            ("tv", "shows") => $"/details/tvshow/{GetTvShowGroupRouteId(group):D}?context=watch",
            ("tv", "timeline") => $"/details/tvshow/{GetTvShowGroupRouteId(group):D}?context=watch",
            ("movies", "series") => $"/details/movieseries/{group.CollectionId:D}?context=watch",
            ("movies", "collections") => $"/details/collection/{group.CollectionId:D}?context=watch",
            ("books", "series") => $"/details/bookseries/{group.CollectionId:D}?context=read",
            ("audiobooks", "series") => $"/details/collection/{group.CollectionId:D}?context=listen",
            ("comics", "series") => $"/details/comicseries/{group.CollectionId:D}?context=comics",
            ("music", "albums" or "timeline") => BuildMusicAlbumNavigationUrl(group),
            (_, "publishers" or "networks") =>
                $"/search?q={Uri.EscapeDataString(group.DisplayName)}",
            _ => BuildCurrentUrl(),
        };
    }

    private static string BuildMusicAlbumNavigationUrl(ContentGroupViewModel group)
        => $"/details/musicalbum/{(group.RootWorkId ?? group.CollectionId):D}?context=listen";

    private string? PersonDetailContext => BehaviourTabId switch
    {
        "music" or "audiobooks" => "listen",
        "movies" or "tv" or "videos" => "watch",
        "books" or "comics" => "read",
        _ => null,
    };

    private string BuildGroupPreviewNavigationUrl(ContentGroupViewModel group, Guid workId) =>
        (BehaviourTabId, _grouping) switch
        {
            ("movies", "series") => $"/details/work/{workId:D}?context=watch",
            ("books", "series") => $"/details/work/{workId:D}?context=read",
            ("audiobooks", "series") => $"/details/work/{workId:D}?context=listen",
            ("comics", "series") => $"/details/work/{workId:D}?context=comics",
            ("music", "albums") => $"/details/musicalbum/{(group.RootWorkId ?? group.CollectionId):D}?context=listen&track={workId:D}",
            _ => $"/details/work/{workId:D}",
        };
}
