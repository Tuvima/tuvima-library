using MediaEngine.Web.Services.Editing;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>
/// The Editing Mode drawer hosts this same shell next to the unchanged detail page: the header, the section rail and
/// History are replaced by the drawer's own header and a three-tab strip, and the sections themselves (Details,
/// Artwork, Match and Identity) are the existing ones. Nothing here changes how any section saves.
/// </summary>
public partial class SharedMediaEditorShell
{
    private int _appliedRequestVersion;
    private bool _lastReportedDirty;
    private int _lastReportedCommit;

    /// <summary>Renders the drawer layout (tab strip instead of the section rail, no History, no editor header). Implies <see cref="Inline"/>.</summary>
    [Parameter] public bool Drawer { get; set; }

    /// <summary>The tab the host asks for after the shell is already open (details, artwork or links).</summary>
    [Parameter] public string? RequestedTab { get; set; }

    /// <summary>The artwork asset type to select when the requested tab is Artwork.</summary>
    [Parameter] public string? RequestedArtworkSlot { get; set; }

    /// <summary>Changes on every host request, so asking for the same tab and slot again is applied again.</summary>
    [Parameter] public int RequestVersion { get; set; }

    /// <summary>Raised when unsaved input appears or is cleared, so the host can guard Done, close and navigation.</summary>
    [Parameter] public EventCallback<bool> DirtyChanged { get; set; }

    /// <summary>Raised after each saved change so the host can refresh what the page shows behind the drawer.</summary>
    [Parameter] public EventCallback Committed { get; set; }

    /// <summary>Raised when the person switches tab inside the drawer, so the drawer header can say where they are.</summary>
    [Parameter] public EventCallback<string> TabChanged { get; set; }

    private Task NotifyDrawerTabAsync(string tabId) => Drawer ? TabChanged.InvokeAsync(tabId) : Task.CompletedTask;

    /// <summary>The host's close request (X, Esc or Done): asks first when there is unsaved input, then raises <see cref="Closed"/>.</summary>
    public async Task RequestCloseAsync()
    {
        await HandleClose();
        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized() => _appliedRequestVersion = RequestVersion;

    protected override async Task OnParametersSetAsync()
    {
        if (!Drawer || RequestVersion == _appliedRequestVersion)
        {
            return;
        }

        _appliedRequestVersion = RequestVersion;
        if (_loading)
        {
            return;
        }

        var tab = MediaEditorTabState.Normalize(RequestedTab);
        await SelectTabInternalAsync(tab);
        if (string.Equals(_activeTab, "artwork", StringComparison.OrdinalIgnoreCase) && HasArtworkSlot(RequestedArtworkSlot))
        {
            ApplyArtworkSlotSelection(RequestedArtworkSlot!);
        }
    }

    private bool HasArtworkSlot(string? assetType) =>
        !string.IsNullOrWhiteSpace(assetType)
        && ArtworkSlots.Any(slot => string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase));

    private async Task ReportDrawerStateAsync()
    {
        if (!Drawer)
        {
            return;
        }

        var dirty = HasPendingNavigationChanges;
        if (dirty != _lastReportedDirty)
        {
            _lastReportedDirty = dirty;
            await DirtyChanged.InvokeAsync(dirty);
        }

        if (_commitVersion != _lastReportedCommit)
        {
            _lastReportedCommit = _commitVersion;
            await Committed.InvokeAsync();
        }
    }
}
