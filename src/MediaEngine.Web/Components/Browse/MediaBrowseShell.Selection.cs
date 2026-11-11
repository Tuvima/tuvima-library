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
    // The ticked rows in the order the table shows them. Only single titles can be edited together.
    private IReadOnlyList<BulkEditRow> BulkRows => _selectedItems.Count == 0
        ? []
        : [.. ActiveTableItems
            .Where(item => _selectedItems.Contains(item.EntityId))
            .Select(item => new BulkEditRow(item.EntityId, item.Title, BulkKindFor(item.MediaType), null, item.MediaType))
            .Where(row => BulkEditRules.IsSelectableKind(row.Kind))];

    private static DetailEntityType BulkKindFor(string? mediaType) => mediaType switch
    {
        "Movies" => DetailEntityType.Movie,
        "Books" => DetailEntityType.Book,
        "TV" => DetailEntityType.TvEpisode,
        "Comics" => DetailEntityType.ComicIssue,
        "Audiobooks" => DetailEntityType.Audiobook,
        "Music" => DetailEntityType.Work,
        _ => DetailEntityType.Collection,
    };

    private Task ClearBulkSelectionAsync()
    {
        _selectedItems.Clear();
        return Task.CompletedTask;
    }

    private Task OnBulkDeletedAsync(IReadOnlyList<Guid> ids)
    {
        _selectedItems.ExceptWith(ids);
        return ReloadAsync();
    }

    private Task OnSelectionChanged(HashSet<Guid> selected)
    {
        _selectedItems.Clear();
        foreach (var item in selected)
        {
            _selectedItems.Add(item);
        }
        return Task.CompletedTask;
    }

    private void HandleTableRowClicked(Guid entityId)
    {
        if (IsContainerGrouping)
        {
            var displayItem = ActiveTableItems.FirstOrDefault(item => item.EntityId == entityId);
            if (displayItem is null)
            {
                return;
            }

            var matchingGroup = _groups.FirstOrDefault(group =>
                string.Equals(group.DisplayName, displayItem.Title, StringComparison.OrdinalIgnoreCase));

            if (matchingGroup is not null)
            {
                _navigatingInternally = true;
                Nav.NavigateTo(BuildGroupNavigationUrl(matchingGroup));
            }

            return;
        }

        var selectedItem = ActiveTableItems.FirstOrDefault(item => item.EntityId == entityId);
        Nav.NavigateTo(selectedItem is not null
            ? MediaNavigation.ForLibraryItem(selectedItem)
            : $"/details/work/{entityId:D}");
    }

    private Task OpenItem(Guid entityId)
    {
        var displayItem = _items.FirstOrDefault(item => item.EntityId == entityId);
        Nav.NavigateTo(displayItem is not null
            ? MediaNavigation.ForLibraryItem(displayItem)
            : $"/details/work/{entityId:D}");
        return Task.CompletedTask;
    }

    // Edit opens the pop-over editor over this list (universal editor v3 D6): nothing navigates away, the list keeps its filters, sort and
    // scroll, and it reloads its rows when the pop-over closes with a change (the Engine's own answer says whether this item may be edited).
    private async Task OpenItemEditorAsync(Guid entityId)
    {
        var item = ActiveTableItems.FirstOrDefault(candidate => candidate.EntityId == entityId)
                   ?? _items.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (entityId == Guid.Empty)
        {
            return;
        }

        ClearNotice();
        _awaitingSingleEdit = true;
        // A delete that closes the editor has its line (files left on disk, say) shown here, in the notice above the list.
        if (!await EditLauncher.OpenAsync(
                ListEditTarget.ForLibraryRow(entityId, item?.Title, item?.MediaType),
                deletedNotice: notice => InvokeAsync(() =>
                {
                    ShowNotice(notice, AppUiTone.Warning);
                    StateHasChanged();
                })))
        {
            _awaitingSingleEdit = false;
            if (EditLauncher.LastNotice is { } why)
            {
                ShowNotice(why, AppUiTone.Warning);
            }
        }
    }

    // True from the moment a row's Edit opened the pop-over until it closes, so a bulk edit (which reloads through its own save) is not reloaded twice.
    private bool _awaitingSingleEdit;

    private void OnEditorClosed(EditorClosedEventArgs closed)
    {
        if (!_awaitingSingleEdit)
        {
            return;
        }

        _awaitingSingleEdit = false;
        if (closed.Changed)
        {
            _ = InvokeAsync(ReloadAfterBulkSaveAsync);
        }
    }

    private static string NormalizeEditorMediaType(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        if (value.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return "Movies";
        }

        if (value.Contains("tv", StringComparison.OrdinalIgnoreCase))
        {
            return "TV";
        }

        if (value.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return "Music";
        }

        if (value.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return "Audiobooks";
        }

        if (value.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return "Comics";
        }

        return "Books";
    }

    private static bool MediaTypeMatches(string? candidate, string? requested) =>
        string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase)
        || string.Equals(NormalizeEditorMediaType(candidate), NormalizeEditorMediaType(requested), StringComparison.OrdinalIgnoreCase);
}
