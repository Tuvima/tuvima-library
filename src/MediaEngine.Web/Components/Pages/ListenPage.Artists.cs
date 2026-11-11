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
    private async Task EnsureArtistSelectionAsync(CancellationToken ct = default)
    {
        if (_artistGroups.Count == 0)
        {
            _selectedArtistName = null;
            _artistDetail = null;
            return;
        }

        var desiredArtist = ResolveDesiredArtistName();
        if (string.IsNullOrWhiteSpace(desiredArtist))
        {
            _selectedArtistName = null;
            _artistDetail = null;
            return;
        }

        await SelectArtistAsync(desiredArtist, persistSelection: false, ct);
    }

    private string? ResolveDesiredArtistName()
    {
        if (!string.IsNullOrWhiteSpace(ArtistKey))
        {
            return CanonicalArtistName(Uri.UnescapeDataString(ArtistKey));
        }

        if (!string.IsNullOrWhiteSpace(_selectedArtistName) && ArtistExists(_selectedArtistName))
        {
            return CanonicalArtistName(_selectedArtistName);
        }

        if (!string.IsNullOrWhiteSpace(_restoredArtistName) && ArtistExists(_restoredArtistName))
        {
            return CanonicalArtistName(_restoredArtistName);
        }

        return _artistGroups.FirstOrDefault()?.DisplayName;
    }

    private bool ArtistExists(string artistName)
        => _artistGroups.Any(artist => string.Equals(artist.DisplayName, artistName, StringComparison.OrdinalIgnoreCase));

    private string CanonicalArtistName(string artistName)
        => _artistGroups.FirstOrDefault(artist => string.Equals(artist.DisplayName, artistName, StringComparison.OrdinalIgnoreCase))?.DisplayName
           ?? artistName;

    private async Task SelectArtistAsync(string artistName, bool persistSelection = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        artistName = CanonicalArtistName(artistName);
        if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase) && _artistDetail is not null)
        {
            if (persistSelection)
            {
                await PersistArtistSelectionAsync(artistName);
            }

            return;
        }

        _selectedArtistName = artistName;
        _artistDetail = null;
        StateHasChanged();

        try
        {
            if (!_artistDetailCache.TryGetValue(artistName, out var detail))
            {
                detail = await ApiClient.GetArtistDetailByNameAsync(artistName, ct);
                _artistDetailCache[artistName] = detail;
            }

            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {
                _artistDetail = detail;
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice(L["ListenPage_ArtistLoadFailed", artistName, ex.Message].Value, AppUiTone.Error);
            }
        }
        finally
        {
            if (string.Equals(_selectedArtistName, artistName, StringComparison.OrdinalIgnoreCase))
            {

                if (persistSelection)
                {
                    await PersistArtistSelectionAsync(artistName);
                }

                StateHasChanged();
            }
        }
    }

    private async Task PersistArtistSelectionAsync(string artistName)
    {
        if (!_uiStateLoaded)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("listenUi.setSelectedArtist", artistName);
            _lastPersistedArtistName = artistName;
            _restoredArtistName = artistName;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not persist selected listen artist {ArtistName}.", artistName);
        }
    }

    private IReadOnlyList<ContentGroupViewModel> ApplyArtistFilters()
    {
        IEnumerable<ContentGroupViewModel> filtered = _artistGroups;

        if (!string.IsNullOrWhiteSpace(_artistSearch))
        {
            filtered = filtered.Where(group => group.DisplayName.Contains(_artistSearch, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _artistSort switch
        {
            "tracks" => filtered.OrderByDescending(group => group.WorkCount).ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return filtered.ToList();
    }

    private void OpenArtist(string artistName)
    {
        var identity = _artistGroups.FirstOrDefault(group =>
            string.Equals(group.DisplayName, artistName, StringComparison.OrdinalIgnoreCase));
        NavigateTo(MediaPersonGroupTileComposer.NavigationUrl(
            identity?.PersonId ?? identity?.ArtistPersonId,
            artistName));
    }

    private Task OnTrackArtistClicked(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            OpenArtist(artistName);
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<WorkViewModel> ResolveGroupWorks(IEnumerable<CollectionGroupWorkDto> works)
        => works
            .Select(groupWork => _workLookup.GetValueOrDefault(groupWork.WorkId))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();

    private IReadOnlyList<WorkViewModel> ResolveArtistTracks()
    {
        if (_artistDetail is null)
        {
            return [];
        }

        var directWorks = ResolveGroupWorks(_artistDetail.Works);
        if (directWorks.Count > 0)
        {
            return directWorks;
        }

        return _artistDetail.Seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => _workLookup.GetValueOrDefault(episode.WorkId))
            .Where(work => work is not null)
            .Cast<WorkViewModel>()
            .ToList();
    }
}
