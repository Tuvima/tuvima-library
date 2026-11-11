using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

public partial class DetailPage
{
    // Opening the reader and playing an album or a shuffled pick from the page, and turning tracks into queue items.

    private async Task OpenReaderAsync(DetailAction action, bool restart = false)
    {
        if (!string.IsNullOrWhiteSpace(action.Route) && action.Route.StartsWith("/read/", StringComparison.OrdinalIgnoreCase))
        {
            Nav.NavigateTo(restart ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(action.Route, "restart", "true") : action.Route);
            return;
        }

        if (Model is null || !Guid.TryParse(Model.Id, out var workId))
        {
            await OnActionSelected.InvokeAsync(action);
            return;
        }

        var assetId = await ApiClient.ResolveWorkToAssetAsync(workId);
        if (assetId.HasValue)
        {
            Nav.NavigateTo($"/read/{assetId.Value:D}{(restart ? "?restart=true" : "")}");
            return;
        }

        await OnActionSelected.InvokeAsync(action);
    }

    private async Task PlayAlbumAsync(bool shuffle)
    {
        if (Model is null)
        {
            return;
        }

        var trackGroups = Model.PrimaryModule.GroupKeys.Count == 0
            ? Model.MediaGroups
            : Model.MediaGroups.Where(group => Model.PrimaryModule.GroupKeys.Contains(group.Key, StringComparer.OrdinalIgnoreCase));
        var tracks = trackGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsOwned && Guid.TryParse(item.Id, out _))
            .OrderBy(TrackSortKey)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToQueueItem)
            .Where(item => item.WorkId != Guid.Empty)
            .ToList();

        if (tracks.Count == 0)
        {
            ShowNotice(L["DetailPage_ThisAlbumHasNoPlayable"].Value, AppUiTone.Info);
            return;
        }

        await Playback.ReplaceQueueItemsAsync(tracks, 0, Model.Title, shuffle);
    }

    private async Task ShuffleStructuralContainerAsync()
    {
        if (Model is null)
        {
            return;
        }

        var candidates = Model.MediaGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsOwned && Guid.TryParse(item.Id, out _))
            .DistinctBy(item => $"{item.EntityType}:{item.Id}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            ShowNotice(L["DetailPage_ThisCollectionHasNoOwned"].Value, AppUiTone.Info);
            return;
        }

        var item = candidates[Random.Shared.Next(candidates.Count)];
        var workId = Guid.Parse(item.Id);
        if (item.EntityType is DetailEntityType.Movie or DetailEntityType.TvEpisode)
        {
            Nav.NavigateTo($"/watch/player/{workId:D}");
            return;
        }

        if (item.EntityType is DetailEntityType.Book or DetailEntityType.ComicIssue)
        {
            var assetId = Guid.TryParse(item.AssetId, out var parsedAssetId)
                ? parsedAssetId
                : await ApiClient.ResolveWorkToAssetAsync(workId);
            if (assetId.HasValue)
            {
                Nav.NavigateTo($"/read/{assetId.Value:D}");
                return;
            }
        }

        if (item.EntityType == DetailEntityType.Work)
        {
            await Playback.ReplaceQueueItemsAsync([ToQueueItem(item)], 0, Model.Title, shuffle: false);
            return;
        }

        var route = item.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.Route))?.Route;
        if (!string.IsNullOrWhiteSpace(route))
        {
            Nav.NavigateTo(route);
            return;
        }

        ShowNotice(L["DetailPage_TheSelectedItemCannotBe"].Value);
    }

    private ListenQueueItem ToQueueItem(MediaGroupingItemViewModel item)
        => new()
        {
            WorkId = Guid.TryParse(item.Id, out var workId) ? workId : Guid.Empty,
            AssetId = Guid.TryParse(item.AssetId, out var assetId) ? assetId : null,
            MediaType = "Music",
            Title = item.Title,
            Subtitle = item.Artist,
            Album = Model?.Title,
            AlbumWorkId = Guid.TryParse(Model?.Id, out var albumId) ? albumId : null,
            ArtistPersonId = Guid.TryParse(Model?.MusicAlbumCompanion?.PrimaryArtistId, out var artistId) ? artistId : null,
            CoverUrl = item.ArtworkUrl ?? Model?.Artwork.CoverUrl ?? Model?.Artwork.PosterUrl,
            Duration = item.Duration,
            InitialPositionSeconds = item.StartSeconds,
        };
}
