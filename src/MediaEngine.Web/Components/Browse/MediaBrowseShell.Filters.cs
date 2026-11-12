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
    private async Task TimelineGroupingChanged(TimelineGrouping value)
    {
        _timelineGrouping = value;
        Nav.NavigateTo(Nav.GetUriWithQueryParameter("period", value == TimelineGrouping.Decade ? "decade" : null));
        await Task.CompletedTask;
    }
    private async Task JumpTimelineYearAsync(int year)
    {
        var period = _timelineIndex?.FirstOrDefault(p => p.Year == year);
        if (period is null)
        {
            return;
        }

        _loading = true;
        ClearNotice();
        try
        {
            if (!await LoadDisplayCardsAsync(false, period.Offset))
            {
                ShowNotice(L["MediaBrowseShell_CouldNotLoadThatYear"].Value, AppUiTone.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not jump to timeline year {Year}", year);
            ShowNotice(L["MediaBrowseShell_CouldNotLoadThatYear"].Value, AppUiTone.Error);
        }
        finally { _loading = false; }
    }

    private async Task OnFolderChanged(string? folderId)
    {
        _folder = string.IsNullOrEmpty(folderId) ? null : folderId;
        ClearActiveGroupState();
        await NavigateAndReloadAsync(reloadData: true, replace: true);
    }

    private async Task OnBrowseTabChanged(int index)
    {
        if (index < 0 || index >= Preset.Tabs.Count)
        {
            return;
        }

        await SwitchTabAsync(Preset.Tabs[index].Id);
    }

    private async Task SwitchTabAsync(string tabId)
    {
        if (string.Equals(_activeTabId, tabId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeTabId = tabId;
        var tab = CurrentTab;
        _grouping = tab.DefaultGrouping;
        _layout = ResolveLayout(null, tab, _grouping);
        _sortBy = ResolveSort(null);
        _searchText = string.Empty;
        _selectedGenres.Clear();
        _selectedCreators.Clear();
        _status = string.Empty;
        _unmatched = false;
        _libraryChip = null;
        _folder = null;
        _selectedYears.Clear();
        _selectedVibes.Clear();
        _selectedThemes.Clear();
        DropAlbumSelectionWhenLeft();
        ClearActiveGroupState();
        await NavigateAndReloadAsync();
    }

    private async Task SwitchGroupingAsync(string grouping)
    {
        if (string.Equals(_grouping, grouping, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _grouping = grouping;
        _layout = ResolveLayout(null, CurrentTab, _grouping);
        DropAlbumSelectionWhenLeft();
        _sortBy = ResolveSort(null);
        ClearActiveGroupState();
        await NavigateAndReloadAsync();
    }

    private async Task SwitchLayoutAsync(LibraryLayoutMode layout)
    {
        if (_layout == layout || !SupportsLayoutToggle)
        {
            return;
        }

        _layout = layout;
        _selectedItems.Clear();
        _albumTicksHeld = false;
        DropAlbumSelectionWhenLeft();
        await NavigateAndReloadAsync(reloadData: true);
    }

    private async Task OnSearchChanged(string _)
    {
        ClearActiveGroupState();
        await NavigateAndReloadAsync(reloadData: true, replace: true);
    }

    private async Task OnSortChanged(string value)
    {
        _openFacetMenuKey = null;
        _sortBy = value;
        _phoneSongSort = null;
        await NavigateAndReloadAsync();
    }

    private Task OnOpenFacetMenuChanged(string? menuKey)
    {
        _openFacetMenuKey = menuKey;
        return Task.CompletedTask;
    }

    private Task CloseFacetMenuOnBackgroundAsync(MouseEventArgs _)
    {
        if (_openFacetMenuKey is not null)
        {
            _openFacetMenuKey = null;
        }

        return Task.CompletedTask;
    }

    private async Task OnGenresChanged(IReadOnlyList<string> values)
    {
        _selectedGenres.Clear();
        _selectedGenres.UnionWith(values);
        await NavigateAndReloadAsync();
    }

    private async Task OnCreatorsChanged(IReadOnlyList<string> values)
    {
        _selectedCreators.Clear();
        _selectedCreators.UnionWith(values);
        await NavigateAndReloadAsync();
    }

    private async Task OnYearsChanged(IReadOnlyList<string> values)
    {
        _selectedYears.Clear();
        _selectedYears.UnionWith(values);
        await NavigateAndReloadAsync();
    }

    private async Task OnVibesChanged(IReadOnlyList<string> values)
    {
        _selectedVibes.Clear();
        _selectedVibes.UnionWith(values);
        await NavigateAndReloadAsync();
    }

    private async Task OnThemesChanged(IReadOnlyList<string> values)
    {
        _selectedThemes.Clear();
        _selectedThemes.UnionWith(values);
        await NavigateAndReloadAsync();
    }

    private async Task OnInProgressChanged(bool selected)
    {
        _status = selected ? "in-progress" : string.Empty;
        await NavigateAndReloadAsync();
    }

    private async Task OnSecondaryQuickFilterChanged(bool selected)
    {
        _status = selected ? SecondaryQuickFilterValue : string.Empty;
        await NavigateAndReloadAsync();
    }

    private async Task OnFinishedChanged(bool selected)
    {
        _status = selected ? "finished" : string.Empty;
        await NavigateAndReloadAsync();
    }

    // Like the search box, the Unmatched filter replaces the address entry instead of adding one, so Back leaves the lane.
    private async Task OnUnmatchedChanged(bool selected)
    {
        _unmatched = selected;
        ClearActiveGroupState();
        await NavigateAndReloadAsync(reloadData: true, replace: true);
    }

    // Choosing a library chip replaces the address entry, like the other filters on the lane; Unmatched belongs to one library, so it resets.
    private async Task OnLibraryChipChanged(Guid? libraryId)
    {
        _libraryChip = libraryId;
        _folder = null;
        _unmatched = false;
        ClearActiveGroupState();
        await NavigateAndReloadAsync(reloadData: true, replace: true);
    }

    private async Task RemoveFilterAsync(string key)
    {
        if (key.StartsWith("genre:", StringComparison.Ordinal))
        {
            _selectedGenres.Remove(key[6..]);
        }
        else if (key.StartsWith("person:", StringComparison.Ordinal))
        {
            _selectedCreators.Remove(key[7..]);
        }
        else if (key.StartsWith("year:", StringComparison.Ordinal))
        {
            _selectedYears.Remove(key[5..]);
        }
        else if (key.StartsWith("vibe:", StringComparison.Ordinal))
        {
            _selectedVibes.Remove(key[5..]);
        }
        else if (key.StartsWith("theme:", StringComparison.Ordinal))
        {
            _selectedThemes.Remove(key[6..]);
        }
        else if (key == "status")
        {
            _status = string.Empty;
        }
        else if (key == "unmatched")
        {
            _unmatched = false;
        }

        // Like the toggle, taking the Unmatched filter off replaces the address entry instead of adding one.
        await NavigateAndReloadAsync(replace: key == "unmatched");
    }

    private async Task ClearFiltersAsync()
    {
        var hadUnmatched = _unmatched;
        _selectedGenres.Clear();
        _selectedCreators.Clear();
        _status = string.Empty;
        _unmatched = false;
        _selectedYears.Clear();
        _selectedVibes.Clear();
        _selectedThemes.Clear();

        await NavigateAndReloadAsync(replace: hadUnmatched);
    }

    private void ClearActiveGroupState()
    {
    }

    private IEnumerable<(string Value, string Label)> GetSortOptions() =>
        BrowseQueryBuilder.GetSortOptions(BehaviourTabId, _grouping);
}
