using System.Text.Json;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    [CascadingParameter] private IAppDialogContext? DialogContext { get; set; }
    [Parameter] public CollectionEditorLaunchRequest Request { get; set; } = new();
    [Parameter] public bool Inline { get; set; }
    [Parameter] public EventCallback<bool> Closed { get; set; }

    private static readonly (string Id, string Label, string Icon)[] Tabs =
    [
        ("details", "Details", AppMaterialIcons.Outlined.Article),
        ("artwork", "Artwork", AppMaterialIcons.Outlined.PhotoLibrary),
        ("membership", "Contents", AppMaterialIcons.Outlined.Inventory2),
        ("companions", "Companions", AppMaterialIcons.Outlined.Link),
        ("history", "History", AppMaterialIcons.Outlined.History),
    ];
    private static readonly (string Id, string Label, string Icon)[] PlaylistTabs =
    [
        ("details", "Details", AppMaterialIcons.Outlined.Article),
        ("artwork", "Artwork", AppMaterialIcons.Outlined.PhotoLibrary),
        ("membership", "Contents", AppMaterialIcons.Outlined.Inventory2),
        ("history", "History", AppMaterialIcons.Outlined.History),
    ];
    private static readonly (string Slot, string Label, string Icon)[] DraftArtworkRoles =
    [
        ("poster", "Poster / Cover", AppMaterialIcons.Outlined.Image),
        ("background", "Background", AppMaterialIcons.Outlined.Panorama),
        ("logo", "Logo", AppMaterialIcons.Outlined.BrandingWatermark),
    ];
    private static readonly (string Label, string Icon)[] IconChoices =
    [
        ("Playlist", AppMaterialIcons.Outlined.QueueMusic),
        ("Rules", AppMaterialIcons.Outlined.AutoAwesomeMotion),
        ("Favorites", AppMaterialIcons.Outlined.FavoriteBorder),
        ("Recent", AppMaterialIcons.Outlined.Schedule),
        ("Albums", AppMaterialIcons.Outlined.Album),
        ("Artist", AppMaterialIcons.Outlined.Person),
        ("Mood", AppMaterialIcons.Outlined.GraphicEq),
        ("Folder", AppMaterialIcons.Outlined.Folder)
    ];

    private CollectionListItemViewModel? EditingCollection => Request.EditingCollection;
    private bool _artworkChanged;

    private bool IsPlaylistDialog => Request.Kind == ContainerEditorKind.Playlist;
    private (string Id, string Label, string Icon)[] VisibleTabs =>
        (IsPlaylistDialog ? PlaylistTabs : Tabs)
            .Where(tab => EditingCollection is not null || tab.Id != "history")
            .ToArray();

    private string _activeTab = "details";
    private string _activeArtworkSlot = "poster";
    private string _name = string.Empty;
    private string? _description;
    private string? _iconName;
    private readonly Dictionary<string, IBrowserFile> _artworkFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _artworkPreviewUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedArtworkSlots = new(StringComparer.OrdinalIgnoreCase);
    private string _collectionType = "Custom";
    private bool _rulesEnabled = true;
    private string _primaryArea = "Mixed";
    private string _ownerKind = "Profile";
    private string _audience = "Private";
    private List<ProfileViewModel> _availableAudienceProfiles = [];
    private readonly HashSet<Guid> _selectedProfileIds = [];
    private CollectionRuleDefinitionViewModel _ruleDefinition = new();
    private string _sortField = "title";
    private string _sortDirection = "asc";
    private string? _secondarySortField;
    private string? _secondarySortDirection;

    private bool _saving;
    private bool _hasUnsavedChanges;
    private IDisposable? _dialogCloseGuard;
    private CollectionPreviewResult? _previewResult;
    private string _itemSearchText = string.Empty;
    private bool _loadingLibraryItems;
    private bool _loadingManualItems;
    private bool _showItemPicker;
    private List<CollectionMediaLookupDto> _lookupItems = [];
    private List<ManualCollectionItem> _manualItems = [];
    private CollectionPersonalMediaSection? _personalMediaSection;
    private Guid? _createdCollectionId;
    // A new collection, and a stored rule-driven one being switched to manual, hold no items yet: the chosen items
    // stay in this editor and travel with the save instead of being added one by one.
    private bool _storedRuleDriven;

    private bool IsPlaylist => string.Equals(_collectionType, "Playlist", StringComparison.OrdinalIgnoreCase);
    private string PersistenceVisibility => string.Equals(_audience, "Everyone", StringComparison.OrdinalIgnoreCase) ? "shared" : "private";
    private bool SupportsManualItems => !_rulesEnabled;

    // A manually curated collection (Custom with stored members) is never allowed to be empty: the Engine refuses
    // to create or save one, and takes the collection away when its last item leaves the library. An attached
    // Gallery or smart-rule source counts as a member, so it keeps the collection from being empty.
    private bool IsEmptyCuratedCollection => !IsPlaylistDialog
        && !(IsCreateFlow && _way == FillWay.Pack)
        && string.Equals(_collectionType, "Custom", StringComparison.OrdinalIgnoreCase)
        && SupportsManualItems
        && !(IsCreateFlow && _way == FillWay.FromList)
        && _manualItems.Count == 0
        && (_personalMediaSection?.AttachedSourceCount ?? 0) == 0;
    private string EmptyCuratedCollectionMessage => EditingCollection is null
        ? L["CollectionFlow_EmptyCuratedNew"].Value
        : L["CollectionFlow_EmptyCuratedExisting"].Value;
    private bool CanDeleteCollection => EditingCollection is not null
        && EditingCollection.CollectionType is "Custom" or "Playlist" or "Smart";
    private string CollectionTypeIcon => !string.IsNullOrWhiteSpace(_iconName)
        ? _iconName
        : _collectionType switch
        {
            "Smart" => AppMaterialIcons.Outlined.AutoAwesomeMotion,
            "Playlist" => AppMaterialIcons.Outlined.QueueMusic,
            _ => AppMaterialIcons.Outlined.Collections,
        };

    private bool IsTabSelected(string tabId) =>
        string.Equals(tabId, _activeTab, StringComparison.OrdinalIgnoreCase)
        || string.Equals(tabId, "artwork", StringComparison.OrdinalIgnoreCase)
           && _activeTab.StartsWith("artwork", StringComparison.OrdinalIgnoreCase);

    private string TabClass(string tabId)
    {
        if (!IsTabSelected(tabId))
            return "app-selection-nav__item sme-section-nav__item";

        return string.Equals(tabId, "artwork", StringComparison.OrdinalIgnoreCase)
            ? "app-selection-nav__item sme-section-nav__item is-active is-group-open"
            : "app-selection-nav__item sme-section-nav__item is-active";
    }
    private string DialogEyebrow => IsPlaylistDialog ? "Playlist editor" : "Collection editor";
    private string DialogTitle => EditingCollection is not null
        ? (IsPlaylistDialog ? "Edit Playlist" : "Edit Collection")
        : IsPlaylistDialog
                ? "New playlist"
                : "New collection";
    private string EditorIdentityTitle => string.IsNullOrWhiteSpace(_name) ? DialogTitle : _name;

    private string HeaderSubtitle =>
        IsPlaylistDialog
            ? "Create a playlist, then choose manual or smart membership."
            : EditingCollection is null
                ? "Create a saved collection using either live rules or manual membership."
                : "Update the collection settings, rules, and saved items.";

    private string WayTitle => _rulesEnabled ? "Rules" : _isListCollection || (IsCreateFlow && _way == FillWay.FromList) ? "From a list" : "Hand-picked";

    private string? FooterSummary =>
        IsPlaylistDialog
            ? null
            : IsEmptyCuratedCollection
                ? EmptyCuratedCollectionMessage
            : SupportsManualItems
                ? $"{_manualItems.Count} item(s) staged for this collection."
                : _previewResult is null
                    ? "Add at least one complete rule to calculate membership."
                    : FormatMatchSummary(_previewResult);
    private string SaveButtonText => EditingCollection is not null
        ? "Save"
        : _way == FillWay.Pack
                ? "Create collections"
                : IsPlaylistDialog
                    ? "Create Playlist"
                    : "Create Collection";

    private List<CollectionMediaLookupDto> FilteredLibraryItems =>
        _lookupItems
            .Where(work => !SelectedWorkIds.Contains(work.WorkId))
            .Take(12)
            .ToList();

    private HashSet<Guid> SelectedWorkIds => _manualItems.Select(item => item.WorkId).ToHashSet();

    protected override void OnInitialized()
    {
        if (EditingCollection is null)
        {
            _name = Request.InitialTitle ?? string.Empty;
            _collectionType = IsPlaylistDialog ? "Playlist" : "Custom";
            _rulesEnabled = string.Equals(Request.InitialMembershipMode, "Smart", StringComparison.OrdinalIgnoreCase);
            _way = _rulesEnabled ? FillWay.SetRules : FillWay.PickTitles;
            if (!IsPlaylistDialog && string.Equals(Request.InitialWay, "FromList", StringComparison.Ordinal))
            {
                // Settings, Profile, Import hands over a list it already read: open on From a list with it loaded.
                _way = FillWay.FromList;
                _rulesEnabled = false;
                if (Request.InitialImport is { Preview.List: { } initialList } initialImport)
                {
                    _listDraft.Set(initialImport, initialList);
                    _step = 2;
                }
            }
            // The chips the place we came from ticks (Watch: Movies and TV), kept to the types this kind of container holds.
            CollectionRuleSentence.SetMediaTypes(_ruleDefinition, Request.InitialMediaTypes
                .Where(type => MediaChoices.Any(choice => string.Equals(choice.Key, type, StringComparison.OrdinalIgnoreCase)))
                .ToList());
            _primaryArea = IsPlaylistDialog ? "Listen" : Request.InitialPrimaryArea;
            _ownerKind = Request.InitialOwnerKind;
            _audience = Request.InitialAudience;
            if (!CanShare)
            {
                // Everyone makes their own: a collection a non-administrator creates is theirs alone.
                _ownerKind = "Profile";
                _audience = "Private";
            }

            if (Request.InitialItems.Count > 0)
            {
                // "New collection with this title": the flow opens on Pick titles with these already chosen.
                _way = FillWay.PickTitles;
                _manualItems = Request.InitialItems
                    .Where(item => item.WorkId != Guid.Empty)
                    .DistinctBy(item => item.WorkId)
                    .Select((item, index) => new ManualCollectionItem
                    {
                        WorkId = item.WorkId,
                        Title = item.Title,
                        MediaType = item.MediaType ?? string.Empty,
                        SortOrder = index + 1,
                    })
                    .ToList();
                _step = 2;
            }

            _iconName = DefaultIconForCollectionType(_collectionType);
            return;
        }

        if (EditingCollection is null)
            return;

        _name = EditingCollection.Name;
        _description = EditingCollection.Description;
        _iconName = EditingCollection.IconName;
        _collectionType = string.Equals(EditingCollection.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase) ? "Playlist" : EditingCollection.CollectionType;
        _primaryArea = EditingCollection.PrimaryArea;
        _ownerKind = EditingCollection.OwnerKind;
        _audience = EditingCollection.Audience;
        _selectedProfileIds.UnionWith(EditingCollection.SelectedProfileIds);
        _randomOrder = string.Equals(EditingCollection.SortField, "random", StringComparison.OrdinalIgnoreCase);
        _sortField = _randomOrder ? "title" : EditingCollection.SortField ?? "title";
        _minItems = EditingCollection.MinItems;
        _maxItems = EditingCollection.MaxItems ?? 0;
        LoadSeasonWindow(EditingCollection.SeasonFrom, EditingCollection.SeasonTo);
        _sortDirection = string.IsNullOrWhiteSpace(EditingCollection.SortDirection) ? "asc" : EditingCollection.SortDirection;
        _secondarySortField = EditingCollection.SecondarySortField;
        _secondarySortDirection = EditingCollection.SecondarySortDirection;
        _rulesEnabled = string.Equals(EditingCollection.MembershipMode, "Smart", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(EditingCollection.Resolution, "materialized", StringComparison.OrdinalIgnoreCase);
        _storedRuleDriven = _rulesEnabled;

        if (string.IsNullOrWhiteSpace(EditingCollection.RuleJson))
            return;

        try
        {
            var parsed = JsonSerializer.Deserialize<CollectionRuleDefinitionDto>(
                EditingCollection.RuleJson,
                MediaEngineJson.CaseInsensitive);

            if (parsed is { Groups.Count: > 0 })
            {
                _ruleDefinition = new CollectionRuleDefinitionViewModel
                {
                    Version = parsed.Version,
                    Groups = parsed.Groups.Select(group => new CollectionRuleGroupViewModel
                    {
                        Id = group.Id,
                        JoinWithPrevious = group.JoinWithPrevious,
                        MatchMode = group.MatchMode,
                        Conditions = group.Conditions.Select(rule => new CollectionRulePredicateViewModel
                        {
                            Field = rule.Field, Op = rule.Op, Value = rule.Value,
                            DisplayValue = rule.DisplayValue, Values = rule.Values,
                            DisplayValues = rule.DisplayValues,
                        }).ToList(),
                    }).ToList(),
                };
                // Exceptions fold back into one row each; several groups of any other kind open in Advanced.
                _ruleDefinition = CollectionRuleSentence.FromWire(_ruleDefinition);
                _advanced = !CollectionRuleSentence.IsPlain(_ruleDefinition);
                if (!string.Equals(EditingCollection.Resolution, "materialized", StringComparison.OrdinalIgnoreCase))
                    _rulesEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not parse collection rule JSON for {CollectionId}.", EditingCollection.Id);
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _availableAudienceProfiles = (await Orchestrator.GetProfilesAsync())
            .Where(profile => profile.Id != Request.ActiveProfileId)
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (SupportsManualItems)
            await LoadManualItemsAsync();
        await LoadShowOnAsync();
    }

    private Task OnTabChanged(int index)
    {
        if (index < 0 || index >= VisibleTabs.Length)
        {
            return Task.CompletedTask;
        }

        _activeTab = VisibleTabs[index].Id;
        if (_activeTab == "artwork" && string.IsNullOrWhiteSpace(_activeArtworkSlot)) _activeArtworkSlot = "poster";
        return Task.CompletedTask;
    }

    private void SelectDraftArtworkRole(string slot) => _activeArtworkSlot = slot;

    private void MarkDirty() => _hasUnsavedChanges = true;
    private Task MarkDirtyAsync()
    {
        MarkDirty();
        return Task.CompletedTask;
    }

    // The callback re-renders this editor, so the footer and Save button see the sources that were just loaded.
    private Task OnPersonalMediaLoadedAsync() => Task.CompletedTask;

    private void SetActiveTab(string tabId)
    {
        if (VisibleTabs.Any(tab => string.Equals(tab.Id, tabId, StringComparison.OrdinalIgnoreCase)))
            _activeTab = tabId;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Inline && DialogContext is not null)
            _dialogCloseGuard ??= DialogContext.RegisterCloseGuard(CanCloseAsync);
        if (firstRender && !IsCreateFlow && _rulesEnabled && _ruleDefinition.AllConditions.Any(IsCompleteRule))
            await RunPreview();
    }

    public void Dispose() => _dialogCloseGuard?.Dispose();
}
