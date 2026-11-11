using MediaEngine.Web.Models.ViewDTOs;
using DayMonth = MediaEngine.Domain.Models.DayMonth;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private async Task SaveCollection()
    {
        ClearNotice();
        if (IsCreateFlow && _way == FillWay.Pack)
        {
            await CreatePacksAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_name))
        {
            ShowNotice(L["CollectionEditorShell_PleaseEnterANameFor"].Value, AppUiTone.Warning);
            _nameMissing = true;
            _activeTab = "details";
            return;
        }

        if (string.Equals(PersistenceVisibility, "private", StringComparison.OrdinalIgnoreCase) && !Request.ActiveProfileId.HasValue)
        {
            ShowNotice(L["CollectionEditorShell_AnActiveProfileIsRequired"].Value, AppUiTone.Warning);
            _activeTab = "details";
            return;
        }

        if (_rulesEnabled && !_ruleDefinition.AllConditions.Any(IsCompleteRule))
        {
            ShowNotice(L["CollectionEditorShell_AddAtLeastOneComplete2"].Value, AppUiTone.Warning);
            _activeTab = "membership";
            _step = 2;
            return;
        }

        if (IsEmptyCuratedCollection)
        {
            ShowNotice(EmptyCuratedCollectionMessage, AppUiTone.Warning);
            _activeTab = "membership";
            _step = 2;
            return;
        }

        if (_seasonal && (!new DayMonth(_seasonFromMonth, _seasonFromDay).IsValid || !new DayMonth(_seasonToMonth, _seasonToDay).IsValid))
        {
            ShowNotice(L["CollectionEditorShell_ChooseRealDaysForThe"].Value, AppUiTone.Warning);
            _activeTab = "details";
            return;
        }

        if (_ownerKind == "Profile" && _audience == "SelectedProfiles" && _selectedProfileIds.Count == 0)
        {
            ShowNotice(L["CollectionEditorShell_ChooseAtLeastOneOther"].Value, AppUiTone.Warning);
            _activeTab = "details";
            return;
        }

        if (_personalMediaSection is not null && !_personalMediaSection.Validate(out var personalMediaError))
        {
            ShowNotice(personalMediaError ?? L["CollectionFlow_CompleteCompanion"].Value, AppUiTone.Warning);
            _activeTab = "companions";
            return;
        }

        if (IsCreateFlow && _way == FillWay.FromList)
        {
            await SaveListCollectionsAsync();
            return;
        }

        _saving = true;
        StateHasChanged();

        try
        {
            var definition = _rulesEnabled ? BuildPersistableDefinition() : new CollectionRuleDefinitionViewModel();

            var normalizedSortField = _randomOrder ? "random" : string.IsNullOrWhiteSpace(_sortField) ? null : _sortField;
            // Update treats empty text on both edges as "remove the window"; create just leaves it out.
            var seasonFrom = _seasonal ? SeasonText(_seasonFromMonth, _seasonFromDay) : string.Empty;
            var seasonTo = _seasonal ? SeasonText(_seasonToMonth, _seasonToDay) : string.Empty;
            Guid? savedCollectionId = EditingCollection?.Id ?? _createdCollectionId;
            var ok = false;

            if (savedCollectionId.HasValue)
            {
                ok = await ApiClient.UpdateCollectionAsync(
                    savedCollectionId.Value,
                    _name,
                    _description,
                    _iconName,
                    definition,
                    PersistenceVisibility,
                    normalizedSortField,
                    _sortDirection,
                    true,
                    null,
                    Request.ActiveProfileId,
                    secondarySortField: _secondarySortField,
                    secondarySortDirection: _secondarySortDirection,
                    membershipMode: _rulesEnabled ? "Smart" : "Manual",
                    primaryArea: _primaryArea,
                    ownerKind: _ownerKind,
                    audience: _ownerKind == "Library" ? "Everyone" : _audience,
                    selectedProfileIds: _selectedProfileIds.ToList(),
                    // Switching a rule-driven collection to manual brings its staged items in the same save, so it
                    // is never manual and empty at any point.
                    workIds: _storedRuleDriven && !_rulesEnabled ? _manualItems.Select(item => item.WorkId).ToList() : null,
                    minItems: _minItems,
                    maxItems: _maxItems,
                    seasonFrom: seasonFrom,
                    seasonTo: seasonTo,
                    showOn: _showOnLoaded ? _showOn.ToList() : null);
            }
            else
            {
                savedCollectionId = await ApiClient.CreateCollectionAndReturnIdAsync(
                    _name,
                    _description,
                    _iconName,
                    _collectionType,
                    definition,
                    normalizedSortField,
                    _sortDirection,
                    PersistenceVisibility,
                    Request.ActiveProfileId,
                    secondarySortField: _secondarySortField,
                    secondarySortDirection: _secondarySortDirection,
                    membershipMode: _rulesEnabled ? "Smart" : "Manual",
                    primaryArea: _primaryArea,
                    ownerKind: _ownerKind,
                    audience: _ownerKind == "Library" ? "Everyone" : _audience,
                    selectedProfileIds: _selectedProfileIds.ToList(),
                    // Curated items travel with the create call, so an empty curated collection never exists.
                    workIds: SupportsManualItems ? _manualItems.Select(item => item.WorkId).ToList() : null,
                    minItems: _minItems,
                    maxItems: _maxItems,
                    seasonFrom: _seasonal ? seasonFrom : null,
                    seasonTo: _seasonal ? seasonTo : null,
                    showOn: _showOn.ToList());
                _createdCollectionId = savedCollectionId;
                ok = savedCollectionId.HasValue;
            }

            if (!ok)
            {
                ShowNotice(
                    string.IsNullOrWhiteSpace(ApiClient.LastError)
                        ? L["CollectionFlow_SaveFailed"].Value
                        : ApiClient.LastError,
                    AppUiTone.Error);
                return;
            }

            if (savedCollectionId.HasValue)
            {
                RememberSaved(savedCollectionId.Value);
                if (_personalMediaSection is not null
                    && !await _personalMediaSection.SaveAsync(savedCollectionId.Value))
                {
                    ShowNotice(
                        string.IsNullOrWhiteSpace(_personalMediaSection.LastError)
                            ? L["CollectionFlow_PersonalMediaNotSaved"].Value
                            : _personalMediaSection.LastError,
                        AppUiTone.Error);
                    _activeTab = "companions";
                    return;
                }

                var artworkFailures = new List<string>();
                foreach (var (slot, file) in _artworkFiles)
                {
                    await using var stream = file.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024);
                    if (!await ApiClient.UploadCollectionArtworkAsync(savedCollectionId.Value, slot, stream, file.Name, Request.ActiveProfileId))
                    {
                        artworkFailures.Add(slot);
                    }
                }

                if (artworkFailures.Count > 0)
                {
                    // The collection is saved, so the editor stays open on this note; Save changes tries the pictures again.
                    ShowNotice(L["CollectionFlow_ArtworkNotUploaded", string.Join(", ", artworkFailures)].Value, AppUiTone.Warning);
                    return;
                }
            }

            // The confirmation is the saved collection glowing on the Collections page; there is no toast.
            _hasUnsavedChanges = false;
            await CloseEditorAsync(applied: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A collection could not be saved");
            ShowNotice(L["CollectionFlow_SaveFailedWithReason", ex.Message].Value, AppUiTone.Error);
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    // From a list (Collections and lists spec 4b): one collection for the whole list, or one for each shelf ticked. Each collection is
    // created with the titles Library owns, then the list is attached as its source. If attaching fails the collection is taken
    // away again, so a half-made collection never stays behind.
    private async Task SaveListCollectionsAsync()
    {
        ClearNotice();
        if (_listDraft.Recognition is not { } source || !ListReady)
        {
            ShowNotice(L["CollectionEditor_ListNeedsOwned"].Value, AppUiTone.Warning);
            _step = 2;
            return;
        }

        _saving = true;
        StateHasChanged();

        var created = new List<Guid>();
        var skipped = new List<string>();
        var artworkProblems = new List<string>();
        string? failure = null;
        var shelfCount = 0;
        try
        {
            var shelves = _listDraft.Shelves.Count == 0
                ? new List<string?> { null }
                : _listDraft.Shelves.OrderBy(shelf => shelf, StringComparer.OrdinalIgnoreCase).Select(shelf => (string?)shelf).ToList();
            shelfCount = shelves.Count;
            var seasonFrom = _seasonal ? SeasonText(_seasonFromMonth, _seasonFromDay) : null;
            var seasonTo = _seasonal ? SeasonText(_seasonToMonth, _seasonToDay) : null;
            var sortField = _randomOrder ? "random" : string.IsNullOrWhiteSpace(_sortField) ? null : _sortField;

            foreach (var shelf in shelves)
            {
                var preview = await ApiClient.PreviewCollectionListAsync(new ListImportPreviewRequest
                {
                    FileName = source.FileName,
                    ContentBase64 = source.ContentBase64,
                    Text = source.Text,
                    Shelf = shelf,
                    Overrides = _listDraft.Overrides,
                    ProfileId = Request.ActiveProfileId,
                });
                if (preview is null)
                {
                    failure = ApiClient.LastError ?? L["CollectionFlow_ListReadAgainFailed"].Value;
                    break;
                }

                if (preview.MatchedWorkIds.Count == 0 && !preview.WillTryAgain)
                {
                    skipped.Add(shelf ?? "the list");
                    continue;
                }

                var name = shelves.Count > 1 ? $"{_name.Trim()}: {shelf}" : _name.Trim();
                var collectionId = await ApiClient.CreateCollectionAndReturnIdAsync(
                    name,
                    _description,
                    _iconName,
                    _collectionType,
                    new CollectionRuleDefinitionViewModel(),
                    sortField,
                    _sortDirection,
                    PersistenceVisibility,
                    Request.ActiveProfileId,
                    secondarySortField: _secondarySortField,
                    secondarySortDirection: _secondarySortDirection,
                    membershipMode: "Manual",
                    primaryArea: _primaryArea,
                    ownerKind: _ownerKind,
                    audience: _ownerKind == "Library" ? "Everyone" : _audience,
                    selectedProfileIds: _selectedProfileIds.ToList(),
                    workIds: preview.MatchedWorkIds,
                    minItems: _minItems,
                    maxItems: _maxItems,
                    seasonFrom: seasonFrom,
                    seasonTo: seasonTo,
                    showOn: _showOn.ToList());
                if (collectionId is not { } id)
                {
                    failure = string.IsNullOrWhiteSpace(ApiClient.LastError) ? L["CollectionFlow_SaveFailed"].Value : ApiClient.LastError;
                    break;
                }

                var attached = await ApiClient.ImportCollectionListAsync(id, new ListImportRequest
                {
                    FileName = source.FileName,
                    ContentBase64 = source.ContentBase64,
                    Text = source.Text,
                    Shelf = shelf,
                    Order = _listDraft.Order,
                    Refresh = _listDraft.Refresh,
                    SyncMode = _listDraft.SyncMode,
                    Overrides = _listDraft.Overrides,
                    ProfileId = Request.ActiveProfileId,
                });
                if (attached is null)
                {
                    var reason = ApiClient.LastError;
                    await ApiClient.DeleteCollectionAsync(id, Request.ActiveProfileId);
                    failure = string.IsNullOrWhiteSpace(reason) ? L["CollectionFlow_ListNotAttached"].Value : reason;
                    break;
                }

                created.Add(id);
                RememberSaved(id);
                var failedSlots = new List<string>();
                foreach (var (slot, file) in _artworkFiles)
                {
                    await using var stream = file.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024);
                    if (!await ApiClient.UploadCollectionArtworkAsync(id, slot, stream, file.Name, Request.ActiveProfileId))
                    {
                        Logger.LogWarning("A list collection was made but its {Slot} artwork could not be uploaded", slot);
                        failedSlots.Add(slot);
                    }
                }

                if (failedSlots.Count > 0)
                {
                    artworkProblems.Add(L["CollectionFlow_SaveSummaryArtwork", name, string.Join(", ", failedSlots)].Value);
                }
            }

            if (skipped.Count > 0)
            {
                Logger.LogInformation("No collection was made for {Shelves}: nothing on it is in the library", string.Join(", ", skipped));
            }

            // Nothing was made: stay on the last step with the reason in place so the person can fix it and try again.
            if (created.Count == 0)
            {
                if (failure is not null)
                {
                    ShowNotice(failure, AppUiTone.Error);
                }
                else if (skipped.Count > 0)
                {
                    ShowNotice(L["CollectionFlow_NothingOwnedOnShelves", string.Join(", ", skipped)].Value, AppUiTone.Warning);
                }

                return;
            }

            // Something was made but something was not (a shelf failed, a shelf owned nothing, or a picture would not upload):
            // the editor stays open on one summary, so nothing is lost in a log. Done then closes it and the made tiles glow.
            var reasons = new List<string>();
            if (failure is not null)
            {
                reasons.Add(failure);
            }

            if (skipped.Count > 0)
            {
                reasons.Add(L["CollectionFlow_NothingOwnedOnShelves", string.Join(", ", skipped)].Value);
            }

            reasons.AddRange(artworkProblems);
            if (reasons.Count > 0)
            {
                ShowSaveSummary(created.Count, failure is null && skipped.Count == 0 ? 0 : shelfCount - created.Count, reasons);
                return;
            }

            _hasUnsavedChanges = false;
            await CloseEditorAsync(applied: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A list collection could not be saved");
            var reason = L["CollectionFlow_SaveFailedWithReason", ex.Message].Value;
            if (created.Count > 0)
            {
                ShowSaveSummary(created.Count, Math.Max(shelfCount - created.Count, 1), [reason]);
            }
            else
            {
                ShowNotice(reason, AppUiTone.Error);
            }
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    private async Task DeleteCollectionAsync()
    {
        if (!CanDeleteCollection || EditingCollection is null)
        {
            return;
        }

        ClearNotice();
        if (!await ConfirmDeleteCollectionAsync(EditingCollection.Name))
        {
            return;
        }

        _saving = true;
        try
        {
            if (!await ApiClient.DeleteCollectionAsync(EditingCollection.Id, Request.ActiveProfileId))
            {
                ShowNotice(L["CollectionEditorShell_TheCollectionCouldNotBe"].Value, AppUiTone.Error);
                return;
            }

            _hasUnsavedChanges = false;
            await CloseEditorAsync(applied: true);
        }
        finally
        {
            _saving = false;
        }
    }
}
