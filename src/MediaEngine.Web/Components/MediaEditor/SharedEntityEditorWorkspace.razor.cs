using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.MediaEditor;

public enum SharedEntityEditorCommitKind
{
    Details,
    Artwork,
}

public partial class SharedEntityEditorWorkspace : IDisposable
{
    private const int SelectorPageSize = 50;
    private static readonly string[] ArtworkTypes = ["CoverArt", "Background", "Logo"];
    private static readonly (string Id, string Label)[] CategoryLabels =
    [
        ("Character", "Characters"),
        ("Location", "Locations/Places"),
        ("Organization", "Organizations/Groups"),
        ("Event", "Events"),
        ("Object", "Objects"),
    ];

    [Inject] private IEngineApiClient ApiClient { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Parameter, EditorRequired] public SharedEntityEditorTargetDto InitialTarget { get; set; } = null!;
    [Parameter] public EventCallback<bool> DirtyChanged { get; set; }
    [Parameter] public EventCallback<SharedEntityEditorContextDto?> ContextChanged { get; set; }
    [Parameter] public EventCallback<SharedEntityEditorCommitKind> Committed { get; set; }

    private SharedEntityEditorTargetDto _target = new(SharedEntityEditorTargetKinds.Universe, string.Empty, null, null);
    private SharedEntityEditorContextDto? _context;
    private SharedEntityDetailsDto? _details;
    private IReadOnlyList<SharedEntityCategorySummaryDto> _categories = [];
    private readonly List<SharedEntitySelectorItemDto> _selectorItems = [];
    private IReadOnlyList<SharedEntityArtworkDto> _artwork = [];
    private IReadOnlyList<SharedEntityAppearanceDto> _appearances = [];
    private IReadOnlyList<SharedEntityRelationshipDto> _relationships = [];
    private IReadOnlyList<SharedEntityTimelineEntryDto> _timeline = [];
    private IReadOnlyList<SharedEntitySourceDto> _sources = [];
    private IReadOnlyList<SharedEntityHistoryEntryDto> _history = [];
    private SharedEntityEnrichmentStatusDto? _enrichment;
    private SharedEntityEditorTargetDto? _pendingTarget;
    private string _pendingTargetLabel = "the selected entity";
    private string _activeSection = SharedEntityEditorSections.Details;
    private string? _selectedCategory;
    private string _search = string.Empty;
    private string _label = string.Empty;
    private string _description = string.Empty;
    private string _savedLabel = string.Empty;
    private string _savedDescription = string.Empty;
    private string _uploadType = "CoverArt";
    private string? _error;
    private string? _message;
    private int _selectorOffset;
    private bool _selectorHasMore;
    private bool _showSelectorPopover;
    private ElementReference _categoryRail;
    private string? _popoverAnchorId;
    private bool _popoverPositioned;
    private bool _popoverNeedsFocus;
    private bool _loading;
    private bool _selectorLoading;
    private bool _saving;
    private bool _refreshing;
    private bool _initialized;
    private CancellationTokenSource? _searchCts;

    private bool IsUniverse => string.Equals(_target.Kind, SharedEntityEditorTargetKinds.Universe, StringComparison.OrdinalIgnoreCase);
    private bool _dirty => !string.Equals(_label, _savedLabel, StringComparison.Ordinal) || !string.Equals(_description, _savedDescription, StringComparison.Ordinal);
    private IEnumerable<SharedEntityEditorCapabilityDto> ReadableCapabilities => (_context?.capabilities ?? []).Where(capability => capability.readable);
    private bool _detailsEditable => Capability(SharedEntityEditorSections.Details)?.editable == true;
    private bool _artworkEditable => Capability(SharedEntityEditorSections.Artwork)?.editable == true;
    private bool CanRefresh => Capability(SharedEntityEditorSections.Enrichment)?.readable == true;
    private string UniverseBreadcrumbLabel => _context?.breadcrumb?.FirstOrDefault() ?? "Universe";
    private string EnrichmentLabel => _enrichment is null
        ? _context?.enrichment_status ?? "Status unavailable"
        : $"{_enrichment.status} · {(_enrichment.enriched_at?.ToLocalTime().ToString("g") ?? "Not yet enriched")}";
    private IEnumerable<SharedEntityRelationshipDto> ActiveRelationshipRows => _activeSection switch
    {
        SharedEntityEditorSections.Members => _relationships.Where(row => IsMembershipRelationship(row.type)),
        SharedEntityEditorSections.Participants => _relationships.Where(row => IsParticipantRelationship(row.type)),
        _ => _relationships,
    };

    protected override async Task OnParametersSetAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        _target = InitialTarget;
        await LoadTargetAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showSelectorPopover && !string.IsNullOrWhiteSpace(_popoverAnchorId))
        {
            await JS.InvokeVoidAsync("tuvimaPositionSharedEntityPopover", _popoverAnchorId, "see-selector-popover");
            _popoverPositioned = true;
            if (_popoverNeedsFocus)
            {
                _popoverNeedsFocus = false;
                await JS.InvokeVoidAsync("tuvimaFocusById", "see-selector-popover");
            }
        }
        else if (_popoverPositioned)
        {
            await JS.InvokeVoidAsync("tuvimaRemoveSharedEntityPopoverPosition", "see-selector-popover");
            _popoverPositioned = false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (!_dirty)
        {
            return true;
        }
        return await SaveDetailsAsync();
    }

    public void ResetEditorChanges()
    {
        _label = _savedLabel;
        _description = _savedDescription;
        _ = NotifyDirtyAsync(false);
    }

    private async Task LoadTargetAsync()
    {
        _loading = true;
        _error = null;
        _message = null;
        _pendingTarget = null;
        _showSelectorPopover = false;
        _popoverAnchorId = null;
        _selectorItems.Clear();
        _categories = [];
        _appearances = [];
        _relationships = [];
        _timeline = [];
        _sources = [];
        _history = [];
        _artwork = [];
        _details = null;
        _enrichment = null;
        await NotifyDirtyAsync(false);
        try
        {
            _context = await ApiClient.GetSharedEntityEditorContextAsync(_target);
            if (_context is null)
            {
                _error = "The shared entity editor context could not be loaded.";
                return;
            }
            await ContextChanged.InvokeAsync(_context);
            var sections = ReadableCapabilities.Select(capability => capability.section).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!sections.Contains(_activeSection))
            {
                _activeSection = sections.FirstOrDefault() ?? SharedEntityEditorSections.Details;
            }
            _details = await ApiClient.GetSharedEntityDetailsAsync(_target);
            _label = _details?.label ?? _context.label;
            _description = _details?.description ?? string.Empty;
            _savedLabel = _label;
            _savedDescription = _description;
            if (sections.Contains(SharedEntityEditorSections.Artwork))
            {
                _artwork = await ApiClient.GetSharedEntityArtworkAsync(_target);
            }
            if (IsUniverse)
            {
                _categories = await ApiClient.GetSharedEntityCategoriesAsync(_target.UniverseQid);
                _selectedCategory ??= _categories.FirstOrDefault()?.category;
                if (_categories.Count > 0)
                {
                    await LoadSelectorPageAsync(reset: true);
                }
            }
            else
            {
                _selectedCategory = _context.category;
                await LoadSelectorPageAsync(reset: true);
            }
            if (sections.Contains(SharedEntityEditorSections.Enrichment))
            {
                _enrichment = await ApiClient.GetSharedEntityEnrichmentAsync(_target);
            }
            await LoadProjectionAsync(_activeSection);
        }
        catch (Exception ex)
        {
            _error = $"Could not load this shared entity: {ex.Message}";
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task LoadProjectionAsync(string section)
    {
        try
        {
            switch (section)
            {
                case SharedEntityEditorSections.Appearances when !IsUniverse:
                    _appearances = await ApiClient.GetSharedEntityAppearancesAsync(_target);
                    break;
                case SharedEntityEditorSections.Members:
                case SharedEntityEditorSections.Participants:
                case SharedEntityEditorSections.Relationships:
                    _relationships = await ApiClient.GetSharedEntityRelationshipsAsync(_target);
                    break;
                case SharedEntityEditorSections.Timeline:
                    _timeline = await ApiClient.GetSharedEntityTimelineAsync(_target);
                    break;
                case SharedEntityEditorSections.Sources:
                    _sources = await ApiClient.GetSharedEntitySourcesAsync(_target);
                    break;
                case SharedEntityEditorSections.History:
                    _history = await ApiClient.GetSharedEntityHistoryAsync(_target);
                    break;
                case SharedEntityEditorSections.Enrichment:
                    _enrichment = await ApiClient.GetSharedEntityEnrichmentAsync(_target);
                    break;
            }
        }
        catch (Exception ex)
        {
            _error = $"This section could not be loaded: {ex.Message}";
        }
    }

    private async Task SelectSectionAsync(string section)
    {
        if (!ReadableCapabilities.Any(capability => string.Equals(capability.section, section, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _activeSection = section;
        _error = null;
        await LoadProjectionAsync(section);
    }

    private async Task SelectCategoryAsync(string category)
    {
        _selectedCategory = category;
        await LoadSelectorPageAsync(reset: true);
    }

    private async Task ToggleCategorySelectorAsync(string category)
    {
        var alreadyOpen = _showSelectorPopover && string.Equals(category, _selectedCategory, StringComparison.OrdinalIgnoreCase);
        _selectedCategory = category;
        _showSelectorPopover = !alreadyOpen;
        _popoverAnchorId = $"see-category-{category}";
        _popoverNeedsFocus = _showSelectorPopover;
        await LoadSelectorPageAsync(reset: true);
    }

    private Task ToggleSiblingSelectorAsync()
    {
        _showSelectorPopover = !_showSelectorPopover;
        _popoverAnchorId = "see-sibling-toggle";
        _popoverNeedsFocus = _showSelectorPopover;
        return Task.CompletedTask;
    }

    private async Task CloseSelectorPopoverAsync()
    {
        _showSelectorPopover = false;
        _popoverNeedsFocus = false;
        var focusTarget = _popoverAnchorId;
        await InvokeAsync(StateHasChanged);
        if (!string.IsNullOrWhiteSpace(focusTarget))
        {
            await JS.InvokeVoidAsync("tuvimaFocusById", focusTarget);
        }
    }

    private Task OnSelectorKeyDown(KeyboardEventArgs args) =>
        string.Equals(args.Key, "Escape", StringComparison.Ordinal)
            ? CloseSelectorPopoverAsync()
            : Task.CompletedTask;

    private async Task SelectEntityAsync(SharedEntitySelectorItemDto item)
    {
        _showSelectorPopover = false;
        await RequestTargetSwitchAsync(ToEntityTarget(item));
    }

    private Task ScrollCategoryRailAsync(int direction) => JS.InvokeVoidAsync("tuvimaScrollElementBy", _categoryRail, direction * 420).AsTask();

    private Task ScrollCategoryWheelAsync(WheelEventArgs args) => ScrollCategoryRailAsync(args.DeltaY >= 0 ? 1 : -1);

    private async Task SearchChangedAsync(string? value)
    {
        _search = value ?? string.Empty;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            await Task.Delay(250, token);
            await LoadSelectorPageAsync(reset: true, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadMoreAsync() => await LoadSelectorPageAsync(reset: false);

    private async Task LoadSelectorPageAsync(bool reset, CancellationToken cancellationToken = default)
    {
        if (_target is null)
        {
            return;
        }
        if (reset)
        {
            _selectorOffset = 0;
            _selectorItems.Clear();
        }
        _selectorLoading = true;
        try
        {
            var page = await ApiClient.GetSharedEntitySelectorPageAsync(_target.UniverseQid, _selectedCategory, _search, _selectorOffset, SelectorPageSize, cancellationToken);
            if (page is null)
            {
                _selectorHasMore = false;
                return;
            }
            _selectorItems.AddRange(page.items);
            _selectorOffset = page.offset + page.items.Count;
            _selectorHasMore = page.has_more;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = $"Entities could not be loaded: {ex.Message}"; }
        finally
        {
            _selectorLoading = false;
        }
    }

    private async Task RequestTargetSwitchAsync(SharedEntityEditorTargetDto target)
    {
        if (SameTarget(_target, target))
        {
            return;
        }
        if (_dirty)
        {
            _pendingTarget = target;
            _pendingTargetLabel = _selectorItems.FirstOrDefault(item => item.id == target.FictionalEntityId)?.label ?? "the Universe";
            return;
        }
        _target = target;
        await LoadTargetAsync();
    }

    private Task ReturnToUniverseAsync() => RequestTargetSwitchAsync(new SharedEntityEditorTargetDto(
        SharedEntityEditorTargetKinds.Universe, _target.UniverseQid, null, _target.UniverseQid));

    private void CancelTargetSwitch() => _pendingTarget = null;

    private async Task DiscardAndSwitchAsync()
    {
        if (_pendingTarget is null)
        {
            return;
        }
        _target = _pendingTarget;
        await LoadTargetAsync();
    }

    private async Task SaveAndSwitchAsync()
    {
        var target = _pendingTarget;
        if (target is null || !await SaveAsync())
        {
            return;
        }
        _target = target;
        await LoadTargetAsync();
    }

    private async Task<bool> SaveDetailsAsync()
    {
        if (!_dirty || !_detailsEditable)
        {
            return true;
        }
        _saving = true;
        _error = null;
        _message = null;
        try
        {
            var result = await ApiClient.UpdateSharedEntityDetailsAsync(_target, new SharedEntityDetailsUpdateRequest(_label.Trim(), string.IsNullOrWhiteSpace(_description) ? null : _description.Trim()));
            if (result is null)
            {
                _error = "Details were not saved.";
                return false;
            }
            await LoadTargetAsync();
            await Committed.InvokeAsync(SharedEntityEditorCommitKind.Details);
            _message = "Details saved.";
            return true;
        }
        catch (Exception ex)
        {
            _error = $"Details were not saved: {ex.Message}";
            return false;
        }
        finally { _saving = false; }
    }

    private async Task UploadArtworkAsync(InputFileChangeEventArgs args)
    {
        var file = args.File;
        if (file is null || !_artworkEditable)
        {
            return;
        }
        if (file.ContentType is not ("image/jpeg" or "image/png"))
        {
            _error = "Choose a JPEG or PNG image.";
            return;
        }
        _saving = true;
        _error = null;
        _message = null;
        try
        {
            await using var stream = file.OpenReadStream(20 * 1024 * 1024);
            var updated = await ApiClient.UploadSharedEntityArtworkAsync(_target, _uploadType, stream, file.Name, file.ContentType);
            if (updated is null)
            {
                _error = "Artwork upload did not complete.";
                return;
            }
            _artwork = updated;
            await Committed.InvokeAsync(SharedEntityEditorCommitKind.Artwork);
            _message = "Artwork uploaded and saved.";
        }
        catch (Exception ex) { _error = $"Artwork upload failed: {ex.Message}"; }
        finally { _saving = false; }
    }

    private async Task RefreshEnrichmentAsync()
    {
        _refreshing = true;
        _error = null;
        _message = null;
        try
        {
            var result = await ApiClient.RefreshSharedEntityAsync(_target);
            _message = result?.message ?? "Refresh request was not accepted.";
            _enrichment = await ApiClient.GetSharedEntityEnrichmentAsync(_target);
        }
        catch (Exception ex) { _error = $"Enrichment refresh failed: {ex.Message}"; }
        finally { _refreshing = false; }
    }

    private async Task LabelChangedAsync(string? value) { _label = value ?? string.Empty; await NotifyDirtyAsync(_dirty); }
    private async Task DescriptionChangedAsync(string? value) { _description = value ?? string.Empty; await NotifyDirtyAsync(_dirty); }
    private void SelectUploadType(string type) => _uploadType = type;
    private async Task NotifyDirtyAsync(bool dirty) => await DirtyChanged.InvokeAsync(dirty);

    private async Task OnCategoryItemKeyDown(KeyboardEventArgs args, string currentCategory)
    {
        if (_categories.Count == 0 || args.Key is not ("ArrowLeft" or "ArrowRight" or "Home" or "End"))
        {
            return;
        }
        var index = _categories.ToList().FindIndex(category => category.category == currentCategory);
        index = args.Key switch
        {
            "Home" => 0,
            "End" => _categories.Count - 1,
            "ArrowLeft" => (index - 1 + _categories.Count) % _categories.Count,
            _ => (index + 1) % _categories.Count,
        };
        var nextCategory = _categories[index].category;
        await SelectCategoryAsync(nextCategory);
        await JS.InvokeVoidAsync("tuvimaFocusById", $"see-category-{nextCategory}");
    }

    private SharedEntityEditorCapabilityDto? Capability(string section) => _context?.capabilities.FirstOrDefault(capability => string.Equals(capability.section, section, StringComparison.OrdinalIgnoreCase));
    private static bool SameTarget(SharedEntityEditorTargetDto left, SharedEntityEditorTargetDto right) =>
        string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) && left.FictionalEntityId == right.FictionalEntityId && string.Equals(left.Qid, right.Qid, StringComparison.OrdinalIgnoreCase);
    private SharedEntityEditorTargetDto ToEntityTarget(SharedEntitySelectorItemDto item) => new(SharedEntityEditorTargetKinds.FictionalEntity, _target.UniverseQid, item.id, item.qid);
    private static string CategoryLabel(SharedEntityCategorySummaryDto? category) => category is null ? "Entities" : CategoryLabels.FirstOrDefault(entry => string.Equals(entry.Id, category.category, StringComparison.OrdinalIgnoreCase)).Label is { Length: > 0 } label ? label : category.label;
    private static string CategoryName(string category) => CategoryLabels.FirstOrDefault(entry => string.Equals(entry.Id, category, StringComparison.OrdinalIgnoreCase)).Label is { Length: > 0 } label ? label : category;
    private static bool IsMembershipRelationship(string? relationshipType) => NormalizeRelationshipType(relationshipType) is
        "member_of" or "has_part" or "has_parts" or "has_member" or "has_members" or "membership" or "member";
    private static bool IsParticipantRelationship(string? relationshipType) => NormalizeRelationshipType(relationshipType) is
        "participant" or "participants" or "participant_in" or "has_participant" or "has_participants" or "participates_in";
    private static string NormalizeRelationshipType(string? relationshipType) =>
        (relationshipType ?? string.Empty).Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
    private static string SectionLabel(string section, string? category) => section switch
    {
        SharedEntityEditorSections.Entities => "Entities",
        SharedEntityEditorSections.Details => "Details",
        SharedEntityEditorSections.Artwork => "Artwork",
        SharedEntityEditorSections.Appearances => "Appearances",
        SharedEntityEditorSections.Members => "Members",
        SharedEntityEditorSections.Participants => "Participants",
        SharedEntityEditorSections.Relationships => "Relationships",
        SharedEntityEditorSections.Timeline => "In-universe timeline",
        SharedEntityEditorSections.Sources => "Sources & provenance",
        SharedEntityEditorSections.History => "History",
        SharedEntityEditorSections.Enrichment => "Enrichment",
        _ => section,
    };
    private static string ArtworkLabel(string assetType) => assetType switch { "CoverArt" => "Cover art", "Background" => "Background", "Logo" => "Logo", _ => assetType };
    private string? AbsoluteArtworkUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : ApiClient.ToAbsoluteEngineUrl(url);
    private static string JoinNonEmpty(params string?[] values) => string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        if (_popoverPositioned)
        {
            _ = JS.InvokeVoidAsync("tuvimaRemoveSharedEntityPopoverPosition", "see-selector-popover");
        }
    }
}
