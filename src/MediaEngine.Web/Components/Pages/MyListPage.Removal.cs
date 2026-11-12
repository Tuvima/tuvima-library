using System.Globalization;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// My List: taking titles off the list. Three ways lead here (a cover's own "..." menu, Select, and Remove finished); each ends the same
/// way: the grid is read again and a line under the count offers Undo for about ten seconds (spec my-list D5, D10).
/// </summary>
public partial class MyListPage
{
    /// <summary>One saved title as My List stores it: the kind and id the cover's own My List action uses.</summary>
    private readonly record struct SavedKey(ProfileEntityKind Kind, Guid Id);

    // Where keyboard focus goes after a render (see FocusAsync): into the Select bar when Select opens, back to the button that opened it when it closes.
    private const string SelectBarFocus = "[data-my-list-selbar]";
    private const string SelectButtonFocus = "[data-my-list-select]";
    private const string PhoneMenuFocus = ".my-list-phonebar .my-list-page-menu .app-overflow-menu__trigger";

    private readonly HashSet<SavedKey> _selected = [];
    private bool _selecting;
    private UndoOffer? _undoOffer;
    private bool _changeFailed;
    private string? _focusRequest;

    // The saved list as of the last time this page looked, so a removal made elsewhere (a cover's menu) shows what left.
    private IReadOnlyList<ProfileSavedItemDto> _knownSaved = [];

    // False until the list has been read once for this profile (the starting read, or anything this page did that changed it).
    private bool _baselineKnown;

    // The starting read of the saved list; a removal made while it is on its way waits for it, so the title that left is still known.
    private Task? _startingSaved;

    // Above zero while this page is itself removing or restoring: the service's Changed event for that is already handled here.
    private int _ownChanges;

    private static SavedKey? KeyOf(MediaTileViewModel tile) =>
        MediaTileActionTarget.For(tile) is { CanSave: true } target ? new SavedKey(target.SavedKind, target.Id) : null;

    private static bool Same(ProfileSavedItemDto left, ProfileSavedItemDto right) =>
        left.EntityKind == right.EntityKind && left.EntityId == right.EntityId;

    // -- Select ---------------------------------------------------------------------------------------------------------------

    // No Select on a TV: the remote has no way to tick covers.
    private bool CanSelect => !Device.IsTv;

    private void EnterSelect()
    {
        if (!CanSelect)
        {
            return;
        }

        _selecting = true;
        _selected.Clear();
        _focusRequest = SelectBarFocus;
    }

    private void LeaveSelect()
    {
        _selecting = false;
        _selected.Clear();
        _focusRequest = Device.IsPhone ? PhoneMenuFocus : SelectButtonFocus;
    }

    // The page's own key handler: Escape leaves Select from anywhere inside the page.
    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (_selecting && e.Key == "Escape")
        {
            LeaveSelect();
        }
    }

    private bool IsTileSelected(MediaTileViewModel tile) => KeyOf(tile) is { } key && _selected.Contains(key);

    private Task ToggleTileAsync(MediaTileViewModel tile)
    {
        if (KeyOf(tile) is { } key && !_selected.Remove(key))
        {
            _selected.Add(key);
        }

        return Task.CompletedTask;
    }

    // A cover that has left the list (removed, or after a reload) can no longer be ticked.
    private void PruneSelection() => _selected.RemoveWhere(key => !_tiles.Any(tile => KeyOf(tile) == key));

    private async Task RemoveSelectedAsync()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var removed = await RemoveAsync([.. _selected.Select(key => new ProfileSavedItemKeyDto(key.Kind, key.Id))]);
        if (removed)
        {
            LeaveSelect();
        }
    }

    // -- Remove finished ----------------------------------------------------------------------------------------------------------

    private async Task RemoveFinishedAsync()
    {
        _changeFailed = false;
        var finished = await Saved.TryGetFinishedAsync();
        if (finished is null)
        {
            // The Engine could not say which titles are finished, so nothing is removed and the in-place alert says so.
            _changeFailed = true;
            return;
        }

        _finishedCount = finished.Count;
        if (finished.Count == 0)
        {
            return;
        }

        await RemoveAsync([.. finished.Select(item => new ProfileSavedItemKeyDto(item.EntityKind, item.EntityId))]);
    }

    // -- removing and Undo -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the titles off, reads the grid again and offers Undo for what really left. False (with the in-place alert) when the Engine could
    /// not do all of it; when only a later batch failed, the titles that did leave still show Undo beside the alert.
    /// </summary>
    private async Task<bool> RemoveAsync(IReadOnlyList<ProfileSavedItemKeyDto> keys)
    {
        _changeFailed = false;
        _ownChanges++;
        try
        {
            var result = await Saved.RemoveManyAsync(keys);
            if (result.Done.Count == 0)
            {
                _changeFailed = true;
                return false;
            }

            _undoOffer = OfferFor(result.Done);
            await RefreshKnownSavedAsync(removed: result.Done, restored: []);
            await LoadFirstPageAsync(silent: true);
            if (result.Failed)
            {
                _changeFailed = true;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not remove {Count} titles from My List.", keys.Count);
            _changeFailed = true;
            return false;
        }
        finally
        {
            _ownChanges--;
        }
    }

    private UndoOffer OfferFor(IReadOnlyList<ProfileSavedItemDto> removed) => new(
        removed.Count == 1
            ? L["MyList_RemovedOne"].Value
            : L["MyList_RemovedMany", removed.Count.ToString("N0", CultureInfo.CurrentCulture)].Value,
        () => RestoreAsync(removed));

    // Undo puts the titles back with their original saved dates, so the order does not change. A failure is thrown so the Undo line itself says so.
    private async Task RestoreAsync(IReadOnlyList<ProfileSavedItemDto> removed)
    {
        _ownChanges++;
        try
        {
            var result = await Saved.RestoreManyAsync(removed);
            if (result.Done.Count > 0)
            {
                await RefreshKnownSavedAsync(removed: [], restored: result.Done);
                await LoadFirstPageAsync(silent: true);
            }

            if (result.Failed || result.Done.Count == 0)
            {
                throw new InvalidOperationException(L["MyList_CouldNotChange"].Value);
            }
        }
        finally
        {
            _ownChanges--;
        }
    }

    private void ClearUndo() => _undoOffer = null;

    // -- a removal made somewhere else (a cover's own "..." menu) ---------------------------------------------------------------------

    private void OnSavedChanged() => _ = InvokeAsync(HandleSavedChangedAsync);

    // A cover's own menu could not save or remove: the same in-place alert as for Select.
    private void OnSavedChangeFailed() => _ = InvokeAsync(() =>
    {
        _changeFailed = true;
        StateHasChanged();
    });

    private async Task HandleSavedChangedAsync()
    {
        if (_ownChanges > 0)
        {
            return;
        }

        if (_startingSaved is { IsCompleted: false } starting)
        {
            await starting;
        }

        // A read that failed says nothing about what is saved: the comparison is skipped and the earlier list stays the baseline.
        if (await Saved.TryGetListAsync() is not { } now)
        {
            return;
        }

        var before = _knownSaved;
        SetKnownSaved(now);
        var left = before.Where(old => !now.Any(item => Same(item, old))).ToList();
        if (left.Count > 0)
        {
            _undoOffer = OfferFor(left);
        }

        if (left.Count > 0 || before.Count != now.Count)
        {
            await LoadFirstPageAsync(silent: true);
        }

        StateHasChanged();
    }

    private void SetKnownSaved(IReadOnlyList<ProfileSavedItemDto> list)
    {
        _knownSaved = list;
        _baselineKnown = true;
    }

    // After this page changed the list: read it again, or when that read fails, apply what was done to the list it already knew.
    private async Task RefreshKnownSavedAsync(IReadOnlyList<ProfileSavedItemDto> removed, IReadOnlyList<ProfileSavedItemDto> restored)
    {
        if (await Saved.TryGetListAsync() is { } list)
        {
            SetKnownSaved(list);
            return;
        }

        SetKnownSaved([
            .. _knownSaved.Where(old => !removed.Any(gone => Same(gone, old))),
            .. restored.Where(back => !_knownSaved.Any(known => Same(known, back))),
        ]);
    }

    // The starting read (see LoadChromeAsync). It never replaces a list this page already learned from its own changes.
    private async Task ReadStartingSavedAsync(int version)
    {
        var list = await Saved.TryGetListAsync();
        if (list is not null && version == _chromeVersion && !_baselineKnown)
        {
            SetKnownSaved(list);
        }
    }

    // -- focus ------------------------------------------------------------------------------------------------------------------------

    private async Task FocusAsync(string selector)
    {
        try
        {
            _focusModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/my-list-focus.js");
            await _focusModule.InvokeVoidAsync("focusFirst", selector);
        }
        catch (JSDisconnectedException)
        {
            // The browser tab closed while focus was moving; there is nothing left to focus.
        }
    }
}
