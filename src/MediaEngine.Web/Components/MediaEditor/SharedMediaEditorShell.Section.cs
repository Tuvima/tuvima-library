using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>
/// An Editing Mode section slot hosts this same shell in line, directly below the detail hero: one section (Details for a
/// Universe, otherwise Artwork, Match and Identity or History) with no editor header and no section rail, because the page's
/// banner and the slot's own heading do that job. The sections themselves are the existing ones and nothing here changes how
/// any of them saves.
/// </summary>
public partial class SharedMediaEditorShell
{
    private int _appliedRequestVersion;
    private bool _lastReportedDirty;
    private bool _lastReportedSaving;
    private int _lastReportedCommit;

    /// <summary>The one section to show (details, artwork, links or history). Setting it makes the shell a section slot, which is always hosted <see cref="Inline"/>.</summary>
    [Parameter] public string? Section { get; set; }

    /// <summary>The artwork asset type to select when the section is Artwork, and again on each later <see cref="RequestVersion"/>.</summary>
    [Parameter] public string? RequestedArtworkSlot { get; set; }

    /// <summary>Changes on every host request, so asking for the same section and slot again is applied again.</summary>
    [Parameter] public int RequestVersion { get; set; }

    /// <summary>Raised when unsaved input appears or is cleared, so the host can guard Done, Esc, Back and closing.</summary>
    [Parameter] public EventCallback<bool> DirtyChanged { get; set; }

    /// <summary>Raised when the section's own save starts or finishes, so the host can keep Done from starting a second save.</summary>
    [Parameter] public EventCallback<bool> SavingChanged { get; set; }

    /// <summary>Raised after each saved change so the host can refresh what the page shows behind the section.</summary>
    [Parameter] public EventCallback Committed { get; set; }

    /// <summary>True when the shell is an Editing Mode section slot.</summary>
    protected bool IsSection => !string.IsNullOrWhiteSpace(Section);

    /// <summary>Whether the section holds input that has not been saved (the host asks "Save or discard?" about it).</summary>
    public bool HasUnsavedChanges => HasPendingNavigationChanges;

    /// <summary>
    /// The host's "Save" answer: saves what the section holds the way its own Save button would. Returns true when nothing
    /// unsaved is left; a failed save (or a move that still needs its confirmation) returns false and keeps the input.
    /// </summary>
    public async Task<bool> SaveChangesAsync()
    {
        if (!HasPendingNavigationChanges)
        {
            return true;
        }

        _saveError = null;
        if (HasPendingDetailsInlineEdit)
        {
            if (FindDetailsField(_detailsInlineFieldKey!) is { } detailsField)
            {
                await SaveDetailsInlineFieldAsync(detailsField);
            }

            if (_detailsInlineFieldKey is not null || _detailsInlineError is not null)
            {
                return false;
            }
        }

        if (HasMatchDraft)
        {
            await SaveSelectedMatchAsync();
        }
        else if (HasStagedEditorChanges)
        {
            await SaveAsyncCore(applyMembershipMove: false);
        }

        return !HasPendingNavigationChanges && string.IsNullOrWhiteSpace(_saveError);
    }

    /// <summary>The host's "Discard" answer: drops everything the section holds.</summary>
    public void DiscardChanges()
    {
        ResetEditorChanges();
        StateHasChanged();
    }

    protected override void OnInitialized() => _appliedRequestVersion = RequestVersion;

    protected override async Task OnParametersSetAsync()
    {
        if (!IsSection || RequestVersion == _appliedRequestVersion)
        {
            return;
        }

        _appliedRequestVersion = RequestVersion;
        if (_loading)
        {
            return;
        }

        // The same section asked for again (a hero art chip while Artwork is open) only re-applies the artwork slot.
        if (string.Equals(_activeTab, "artwork", StringComparison.OrdinalIgnoreCase) && HasArtworkSlot(RequestedArtworkSlot))
        {
            ApplyArtworkSlotSelection(RequestedArtworkSlot!);
        }
    }

    private bool HasArtworkSlot(string? assetType) =>
        !string.IsNullOrWhiteSpace(assetType)
        && ArtworkSlots.Any(slot => string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A person's Series choice was saved or released in the Match section's Series block. Like any other saved change it is
    /// counted, so the modal refreshes the page when it closes and an Editing Mode host refreshes the page behind the section.
    /// </summary>
    protected void OnSeriesPlacementApplied()
    {
        _hasCommittedChanges = true;
        StateHasChanged();
    }

    private async Task ReportHostStateAsync()
    {
        if (!IsSection)
        {
            return;
        }

        var dirty = HasPendingNavigationChanges;
        if (dirty != _lastReportedDirty)
        {
            _lastReportedDirty = dirty;
            await DirtyChanged.InvokeAsync(dirty);
        }

        var saving = _saving || _matchActionPending;
        if (saving != _lastReportedSaving)
        {
            _lastReportedSaving = saving;
            await SavingChanged.InvokeAsync(saving);
        }

        if (_commitVersion != _lastReportedCommit)
        {
            _lastReportedCommit = _commitVersion;
            await Committed.InvokeAsync();
        }
    }
}
