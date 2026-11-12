using System.Globalization;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.AspNetCore.Components.Web;

namespace MediaEngine.Web.Components.Pages;

/// <summary>
/// My List: taking titles off the list. Three ways lead here (a cover's own "..." menu, Select, and Remove finished); each ends the same
/// way: the grid is read again and a line under the count offers Undo for about ten seconds (spec my-list D5, D10).
/// </summary>
public partial class MyListPage
{
    /// <summary>One saved title as My List stores it: the kind and id the cover's own My List action uses.</summary>
    private readonly record struct SavedKey(ProfileEntityKind Kind, Guid Id);

    private readonly HashSet<SavedKey> _selected = [];
    private bool _selecting;
    private UndoOffer? _undoOffer;
    private bool _changeFailed;

    // The saved list as of the last time this page looked, so a removal made elsewhere (a cover's menu) shows what left.
    private IReadOnlyList<ProfileSavedItemDto> _knownSaved = [];

    // Above zero while this page is itself removing or restoring: the service's Changed event for that is already handled here.
    private int _ownChanges;

    private static SavedKey? KeyOf(MediaTileViewModel tile) =>
        MediaTileActionTarget.For(tile) is { CanSave: true } target ? new SavedKey(target.SavedKind, target.Id) : null;

    private static bool Same(ProfileSavedItemDto left, ProfileSavedItemDto right) =>
        left.EntityKind == right.EntityKind && left.EntityId == right.EntityId;

    // -- Select ---------------------------------------------------------------------------------------------------------------

    private void EnterSelect()
    {
        _selecting = true;
        _selected.Clear();
    }

    private void LeaveSelect()
    {
        _selecting = false;
        _selected.Clear();
    }

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
        IReadOnlyList<ProfileSavedItemDto> finished;
        try
        {
            finished = await ApiClient.GetFinishedSavedItemsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read which titles on My List are finished.");
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

    /// <summary>Takes the titles off, reads the grid again and offers Undo. False (with the in-place alert) when the Engine could not do it.</summary>
    private async Task<bool> RemoveAsync(IReadOnlyList<ProfileSavedItemKeyDto> keys)
    {
        _changeFailed = false;
        _ownChanges++;
        try
        {
            var removed = await Saved.RemoveManyAsync(keys);
            if (removed is null)
            {
                _changeFailed = true;
                return false;
            }

            _undoOffer = OfferFor(removed);
            _knownSaved = await ReadSavedAsync();
            await LoadFirstPageAsync(silent: true);
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
            if (await Saved.RestoreManyAsync(removed) is null)
            {
                throw new InvalidOperationException(L["MyList_CouldNotChange"].Value);
            }

            _knownSaved = await ReadSavedAsync();
            await LoadFirstPageAsync(silent: true);
        }
        finally
        {
            _ownChanges--;
        }
    }

    private void ClearUndo() => _undoOffer = null;

    // -- a removal made somewhere else (a cover's own "..." menu) ---------------------------------------------------------------------

    private void OnSavedChanged() => _ = InvokeAsync(HandleSavedChangedAsync);

    private async Task HandleSavedChangedAsync()
    {
        if (_ownChanges > 0)
        {
            return;
        }

        var before = _knownSaved;
        var now = await ReadSavedAsync();
        _knownSaved = now;
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

    private async Task<IReadOnlyList<ProfileSavedItemDto>> ReadSavedAsync()
    {
        try
        {
            return await Saved.GetListAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not read the saved list for My List.");
            return _knownSaved;
        }
    }
}
