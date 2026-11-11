using MediaEngine.Web.Components.Rules;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    // Start from a pack (Collections and lists D14): the packs the library supports, and which are ticked.
    private IReadOnlyList<CollectionPackSummaryDto> _packs = [];
    private readonly HashSet<string> _selectedPacks = new(StringComparer.Ordinal);
    private bool _packsLoading;
    private string? _packsError;

    private string PackSelectionSummary
    {
        get
        {
            var total = _packs.Where(pack => _selectedPacks.Contains(pack.Id)).Sum(pack => pack.WouldCreateCount);
            var ticked = _selectedPacks.Count == 1 ? L["CollectionFlow_PackTickedOne"].Value : L["CollectionFlow_PackTickedMany", _selectedPacks.Count].Value;
            var upTo = total == 1 ? L["CollectionFlow_UpToOneCollection"].Value : L["CollectionFlow_UpToManyCollections", total].Value;
            return $"{ticked}, {upTo}.";
        }
    }

    private IReadOnlyList<string> PackMediaTypes =>
        IsPlaylistDialog ? new List<string> { "Music" } : CollectionRuleSentence.MediaTypes(_ruleDefinition).ToList();

    private async Task LoadPacksAsync()
    {
        _packsLoading = true;
        _packsError = null;
        StateHasChanged();
        try
        {
            _packs = await ApiClient.GetCollectionPacksAsync(PackMediaTypes);
            if (_packs.Count == 0 && !string.IsNullOrWhiteSpace(ApiClient.LastError))
            {
                _packsError = ApiClient.LastError;
            }

            _selectedPacks.RemoveWhere(id => !_packs.Any(pack => pack.Id == id && pack.WouldCreateCount > 0));
        }
        finally
        {
            _packsLoading = false;
        }
    }

    private Task OnPackToggledAsync((string Id, bool Selected) change)
    {
        if (change.Selected)
        {
            _selectedPacks.Add(change.Id);
        }
        else
        {
            _selectedPacks.Remove(change.Id);
        }

        MarkDirty();
        return Task.CompletedTask;
    }

    private async Task CreatePacksAsync()
    {
        ClearNotice();
        if (_selectedPacks.Count == 0)
        {
            ShowNotice(L["CollectionPack_TickFirst"].Value, AppUiTone.Warning);
            _step = 2;
            return;
        }

        if (_ownerKind == "Profile" && _audience == "SelectedProfiles" && _selectedProfileIds.Count == 0)
        {
            ShowNotice(L["CollectionPack_ChooseProfiles"].Value, AppUiTone.Warning);
            return;
        }

        if (string.Equals(PersistenceVisibility, "private", StringComparison.OrdinalIgnoreCase) && !Request.ActiveProfileId.HasValue)
        {
            ShowNotice(L["CollectionEditorShell_AnActiveProfileIsRequired"].Value, AppUiTone.Warning);
            return;
        }

        _saving = true;
        StateHasChanged();
        try
        {
            var response = await ApiClient.CreateCollectionPacksAsync(
                new CollectionPackCreateRequest
                {
                    PackIds = _selectedPacks.ToList(),
                    MediaTypes = PackMediaTypes.ToList(),
                    OwnerKind = _ownerKind,
                    Audience = _ownerKind == "Library" ? "Everyone" : _audience,
                    SelectedProfileIds = _selectedProfileIds.ToList(),
                    ShowOn = _showOn.ToList(),
                },
                Request.ActiveProfileId);

            if (response is null)
            {
                ShowNotice(
                    string.IsNullOrWhiteSpace(ApiClient.LastError) ? L["CollectionFlow_PacksNotCreated"].Value : ApiClient.LastError,
                    AppUiTone.Error);
                return;
            }

            var made = response.Collections.Count;
            foreach (var created in response.Collections)
            {
                RememberSaved(created.Id);
            }

            if (made == 0)
            {
                // Nothing was made: stay on this step and say why in place, so the person can change the packs.
                var reasons = response.Skipped.Take(3).Select(skipped => $"{skipped.Name}: {skipped.Reason}");
                ShowNotice(string.Join(" ", new[] { L["CollectionFlow_NothingNewCreated"].Value }.Concat(reasons)), AppUiTone.Warning);
                return;
            }

            foreach (var skipped in response.Skipped)
            {
                Logger.LogInformation("The pack {Pack} made no collection: {Reason}", skipped.Name, skipped.Reason);
            }

            // Some packs made nothing: the editor stays open on one summary naming each. Done closes it and the made tiles glow.
            if (response.Skipped.Count > 0)
            {
                ShowSaveSummary(made, response.Skipped.Count, response.Skipped.Select(skipped => $"{skipped.Name}: {skipped.Reason}").ToList());
                return;
            }

            _hasUnsavedChanges = false;
            await CloseEditorAsync(applied: true);
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    // ── Feedback in place of toasts (Collections and lists spec 4b) ─────────────────────────────────────────────────
    // A problem shows as an alert at the top of the step; a save is confirmed by the saved collection glowing on the
    // Collections page, which reads Request.SavedCollectionIds after the editor closes.
}
