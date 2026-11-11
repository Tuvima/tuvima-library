namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private async Task OpenItemPickerAsync()
    {
        _showItemPicker = true;
        await EnsureLibraryItemsLoadedAsync();
    }

    private void CloseItemPicker()
    {
        _showItemPicker = false;
        _itemSearchText = string.Empty;
    }

    private async Task EnsureLibraryItemsLoadedAsync()
    {
        if (_lookupItems.Count > 0 || _loadingLibraryItems)
            return;

        _loadingLibraryItems = true;
        StateHasChanged();

        try
        {
            await RefreshLookupItemsAsync();
        }
        catch (Exception ex)
        {
            ShowNotice(L["CollectionEditorShell_FailedToLoadLibraryItems", ex.Message].Value, AppUiTone.Error);
        }
        finally
        {
            _loadingLibraryItems = false;
            StateHasChanged();
        }
    }

    private async Task RefreshLookupItemsAsync()
    {
        _lookupItems = await ApiClient.LookupCollectionMediaAsync(
            _itemSearchText,
            EditingCollection?.Id,
            offset: 0,
            limit: 24,
            profileId: Request.ActiveProfileId);
    }

    private async Task OnItemSearchChanged(string value)
    {
        _itemSearchText = value;
        _loadingLibraryItems = true;
        StateHasChanged();

        try
        {
            await RefreshLookupItemsAsync();
        }
        finally
        {
            _loadingLibraryItems = false;
            StateHasChanged();
        }
    }

    private async Task LoadManualItemsAsync()
    {
        _loadingManualItems = true;
        StateHasChanged();

        try
        {
            if (EditingCollection is null || _storedRuleDriven)
            {
                // Nothing is stored to load. A new collection starts empty (or with the titles it was opened with); a rule-driven one keeps what is staged.
                if (EditingCollection is null && Request.InitialItems.Count == 0)
                    _manualItems = [];
                return;
            }

            var items = await ApiClient.GetCollectionItemsAsync(EditingCollection.Id, 200, Request.ActiveProfileId);
            _manualItems = items
                .OrderBy(item => item.SortOrder)
                .Select(item => new ManualCollectionItem
                {
                    Id = item.Id,
                    WorkId = item.WorkId,
                    Title = item.Title,
                    Creator = item.Creator,
                    MediaType = item.MediaType,
                    CoverUrl = item.CoverUrl,
                    SortOrder = item.SortOrder,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            ShowNotice(L["CollectionEditorShell_FailedToLoadSavedItems", ex.Message].Value, AppUiTone.Error);
        }
        finally
        {
            _loadingManualItems = false;
            StateHasChanged();
        }
    }

    private async Task AddManualItemAsync(CollectionMediaLookupDto work)
    {
        if (SelectedWorkIds.Contains(work.WorkId))
            return;

        // Every action starts clean: a failure from the last try goes as soon as the person tries again.
        ClearNotice();
        if (EditingCollection is not null && !_storedRuleDriven)
        {
            var ok = await ApiClient.AddCollectionItemAsync(EditingCollection.Id, work.WorkId, Request.ActiveProfileId);
            if (!ok)
            {
                ShowNotice(L["CollectionEditorShell_FailedToAddTheItem"].Value, AppUiTone.Error);
                return;
            }

            await LoadManualItemsAsync();
            await RefreshLookupItemsAsync();
            return;
        }

        _manualItems.Add(new ManualCollectionItem
        {
            Id = Guid.Empty,
            WorkId = work.WorkId,
            Title = work.Title,
            Creator = work.Creator,
            MediaType = work.MediaType,
            CoverUrl = work.ArtworkUrl,
            SortOrder = _manualItems.Count == 0 ? 1 : _manualItems.Max(item => item.SortOrder) + 1,
        });
        MarkDirty();

        StateHasChanged();
    }

    private async Task RemoveManualItemAsync(ManualCollectionItem item)
    {
        ClearNotice();
        if (EditingCollection is not null && !_storedRuleDriven)
        {
            var ok = await ApiClient.RemoveCollectionItemAsync(EditingCollection.Id, item.Id, Request.ActiveProfileId);
            if (!ok)
            {
                // The Engine says why when it refuses, for example when this is the collection's last item.
                ShowNotice(
                    string.IsNullOrWhiteSpace(ApiClient.LastError)
                        ? L["CollectionEditorShell_FailedToRemoveTheItem"].Value
                        : ApiClient.LastError,
                    AppUiTone.Error);
                return;
            }

            await LoadManualItemsAsync();
            await RefreshLookupItemsAsync();
            return;
        }

        _manualItems.RemoveAll(existing => existing.WorkId == item.WorkId);
        MarkDirty();
        StateHasChanged();
    }

    private async Task MoveManualItemAsync(ManualCollectionItem item, int direction)
    {
        ClearNotice();
        var ordered = _manualItems.OrderBy(existing => existing.SortOrder).ToList();
        var index = ordered.FindIndex(existing => existing.Id == item.Id && existing.WorkId == item.WorkId);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
            return;

        (ordered[index], ordered[targetIndex]) = (ordered[targetIndex], ordered[index]);

        _manualItems = ordered
            .Select((existing, order) => existing with { SortOrder = order + 1 })
            .ToList();

        if (EditingCollection is not null && !_storedRuleDriven)
        {
            var itemIds = _manualItems
                .OrderBy(existing => existing.SortOrder)
                .Select(existing => existing.Id)
                .Where(id => id != Guid.Empty)
                .ToList();
            var ok = await ApiClient.ReorderCollectionItemsAsync(EditingCollection.Id, itemIds, Request.ActiveProfileId);
            if (!ok)
            {
                ShowNotice(L["CollectionEditorShell_FailedToReorderTheCollection"].Value, AppUiTone.Error);
                await LoadManualItemsAsync();
                return;
            }
        }
        else
        {
            MarkDirty();
        }

        StateHasChanged();
    }

    private static string FormatWorkMeta(CollectionMediaLookupDto work)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(work.ParentContext))
            parts.Add(work.ParentContext);
        if (!string.IsNullOrWhiteSpace(work.Creator))
            parts.Add(work.Creator);
        if (!string.IsNullOrWhiteSpace(work.MediaType))
            parts.Add(work.MediaType);
        if (!string.IsNullOrWhiteSpace(work.Year))
            parts.Add(work.Year);
        return parts.Count == 0 ? "Library item" : string.Join("  -  ", parts);
    }

    private static string FormatManualItemMeta(ManualCollectionItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Creator))
            parts.Add(item.Creator);
        if (!string.IsNullOrWhiteSpace(item.MediaType))
            parts.Add(item.MediaType);
        return parts.Count == 0 ? "Saved item" : string.Join("  -  ", parts);
    }

    private sealed record ManualCollectionItem
    {
        public Guid Id { get; init; }
        public Guid WorkId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Creator { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? CoverUrl { get; init; }
        public int SortOrder { get; init; }
    }

    private Task OnListDraftChanged()
    {
        MarkDirty();
        return InvokeAsync(StateHasChanged);
    }

    private Task OnListSectionLoaded(bool isListCollection)
    {
        if (_isListCollection != isListCollection)
        {
            _isListCollection = isListCollection;
            if (isListCollection)
                _way = FillWay.FromList;
            return InvokeAsync(StateHasChanged);
        }

        return Task.CompletedTask;
    }

    // The list changed which titles the collection holds: read the item list again.
    private async Task OnListContentsChangedAsync()
    {
        await LoadManualItemsAsync();
        await InvokeAsync(StateHasChanged);
    }
}
