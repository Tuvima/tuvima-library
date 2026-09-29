using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private static readonly string[] TabDisplayOrder =
    [
        "details",
        "chapters",
        "artwork",
        "links",
        "options",
        "file",
        "history",
    ];

    private static readonly ArtworkSlotDefinition PosterCoverArtworkSlot =
        new("CoverArt", "Poster / Cover", "Primary art used on cards and detail pages.", Icons.Material.Outlined.Photo, "portrait", "fit", true, "Use a high-resolution poster, book cover, or album cover. Portrait and square sources retain their natural proportions.", "Primary");

    private static readonly ArtworkSlotDefinition BackgroundArtworkSlot =
        new("Background", "Background", "A cinematic wide image for backgrounds and immersive layouts.", Icons.Material.Outlined.Panorama, "background", "fit", true, "Best for scenic or full-bleed background art.", "Wide");

    private static readonly ArtworkSlotDefinition LogoArtworkSlot =
        new("Logo", "Logo", "Title treatment or transparent branding art.", Icons.Material.Outlined.BrandingWatermark, "logo", "logo", true, "Best for transparent logos or wordmarks.", "Logo");

    private static readonly ArtworkSlotDefinition SeasonPosterArtworkSlot =
        new("SeasonPoster", "Season Poster", "Poster art stored for the season container.", Icons.Material.Outlined.ViewAgenda, "portrait", "fit", true, "Best for season-specific poster art.", "Season");

    private static readonly ArtworkSlotDefinition SeasonThumbArtworkSlot =
        new("SeasonThumb", "Season Thumb", "A wide season still or season thumbnail.", Icons.Material.Outlined.PhotoSizeSelectLarge, "background", "fit", true, "Best for season-specific thumbnail art.", "Season");

    private static readonly ArtworkSlotDefinition EpisodeStillArtworkSlot =
        new("EpisodeStill", "Episode Still", "An episode-specific still image.", Icons.Material.Outlined.LiveTv, "background", "fit", true, "Best for episode stills or screenshots.", "Still");

    [Inject] protected IEngineApiClient ApiClient { get; set; } = null!;
    [Inject] protected UIOrchestratorService Orchestrator { get; set; } = null!;
    [Inject] protected ISnackbar Snackbar { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;
    [Inject] protected ProviderCatalogueService ProviderCatalogue { get; set; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Inline)
        {
            await JS.InvokeVoidAsync("tuvimaEditorFocus", ".sme-title");
        }
    }
    [Inject] protected IDialogService DialogService { get; set; } = null!;
    [Inject] protected ILogger<SharedMediaEditorShell> Logger { get; set; } = null!;

    [CascadingParameter] private IMudDialogInstance? MudDialog { get; set; }
    [Parameter] public MediaEditorLaunchRequest Request { get; set; } = new();
    [Parameter] public bool Inline { get; set; }
    [Parameter] public bool HeroConstrained { get; set; }
    [Parameter] public EventCallback<bool> Closed { get; set; }

    private LibraryItemDetailViewModel? _detail;
    private List<CanonicalFieldDto> _canonicalValues = [];
    private List<ClaimDto> _claims = [];
    private List<LibraryItemHistoryDto> _history = [];
    private ArtworkEditorDto? _artwork;
    private MediaEditorContextDto? _editorContext;
    private MediaEditorNavigatorDto? _navigator;
    private MediaEditorSchema _schema = MediaEditorSchemaCatalog.Resolve(null);
    private readonly Dictionary<string, string> _editedValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inlineOverrideKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clearedInlineOverrideKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingInlineRevertKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid?> _selectedMembershipTargetIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaEditorMembershipSuggestionDto> _selectedMembershipSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MediaEditorMembershipSuggestionDto>> _membershipSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrowserFile> _pendingArtworkFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingArtworkPreviewUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _artworkUploadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArtworkEditorDto> _artworkStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScopeEditorState> _scopeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, MediaEngine.Contracts.Items.ItemEditorPreferencesResponse> _profilePreferencesByWork = [];
    private readonly Dictionary<string, AudiobookChapterEdit> _audiobookChapterEdits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _audiobookChapterResetKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _audiobookChapterOverrideKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artworkApplyingKeys = new(StringComparer.OrdinalIgnoreCase);
    private ItemCanonicalSearchResponseDto? _retailSearchResponse;
    private ItemCanonicalSearchResponseDto? _wikidataSearchResponse;
    private CancellationTokenSource? _retailSearchCts;
    private CancellationTokenSource? _wikidataSearchCts;
    private CancellationTokenSource? _retailHierarchyPreviewCts;
    private RetailCandidateDetailDto? _retailCandidateDetail;
    private MediaEditorMembershipPreviewDto? _retailHierarchyPreview;
    private string? _retailHierarchyPreviewCandidateId;
    private bool _loadingRetailHierarchyPreview;
    private readonly MediaEditorTabState _tabState = new();
    private string _activeTab => _tabState.ActiveTab;
    private string _activeScopeId = string.Empty;
    private string _canonicalTargetGroup = "";
    private string _retailSearchQuery = "";
    private string _wikidataSearchQuery = "";
    private string _activeMatchSearchMode = "retail";
    private string _artworkUrlInput = string.Empty;
    private string? _artworkAddMenuAssetType;
    private string? _selectedRetailCandidateId;
    private string? _selectedWikidataCandidateId;
    private string? _selectedArtworkAssetType;
    private string? _focusedArtworkVariantKey;
    private string? _deleteConfirmArtworkVariantKey;
    private ArtworkSlotDefinition? _zoomArtworkSlot;
    private ArtworkVariantDisplayItem? _zoomArtworkVariant;
    private string EditorMediaType => NormalizeEditorMediaType(_detail?.MediaType ?? _editorContext?.MediaType ?? Request.MediaType);
    private bool _loading = true;
    private bool _saving;
    private bool _searchingRetail;
    private bool _searchingWikidata;
    private bool _loadingRetailCandidateDetail;
    private bool _artworkUrlSubmitting;
    private bool _providerArtworkRefreshing;
    private bool _retryingWriteback;
    private IReadOnlyList<TextTrackDto> _textTracks = [];
    private bool _textTracksLoading;
    private bool _textTracksRefreshing;
    private bool _textTrackImporting;
    private Guid? _textTrackPreferencePendingId;
    private string? _textTrackRefreshMessage;
    private bool _rereadingFileMetadata;
    private string? _fileMetadataRereadMessage;
    private string _historyFilter = "all";
    private bool _confirmDiscard;
    private bool _showQuarantineConfirm;
    private string? _dragTargetArtworkType;
    private string? _loadError;
    private string? _saveError;
    private bool _saveConflict;
    private string _reviewSummary = "Review the item identity.";
    private string _primaryActionLabel = "Review Metadata";
    private MediaEditorIdentityIntent _identityIntent = MediaEditorIdentityIntent.None;
    private string _lastNonFileTab => _tabState.LastNonFileTab;
    private bool _showArtworkUrlInput;
    private bool _matchActionPending;
    private bool _hasCommittedChanges;
    private string? _matchActionStatus;
    private Guid? _matchIdentityJobId;
    private MediaEditorMembershipPreviewDto? _pendingMembershipPreview;
    private string? _fieldToFocus;
    private string? _focusedAudiobookChapterKey;
    private EnrichmentRefreshScheduleDto? _refreshSchedule;
    private bool _refreshQueueing;
    private IReadOnlyList<string> _genreSuggestions = [];
    private IReadOnlyList<string> _tagSuggestions = [];
    private PendingTargetSwitch? _pendingTargetSwitch;
    private bool _switchAfterSuccessfulSave;

    protected IReadOnlyList<(string Id, string Label, string Icon)> Tabs => ResolveVisibleTabs();
    protected IReadOnlyList<(string Key, string Label)> QuickSearchTargets => ResolveQuickSearchTargets();
    protected IReadOnlyList<ArtworkSlotDefinition> ArtworkSlots => ResolveArtworkSlots(ArtworkScope);
    protected bool CanMatchCurrentTarget => EditorMediaType != "TV" || ActiveScope?.ScopeId == "episode";
    protected bool SupportsCanonicalSearch => CanMatchCurrentTarget && QuickSearchTargets.Count > 0;
    protected bool HasActiveMatch => IsWikidataSearchMode ? HasCurrentCanonicalIdentity : HasCurrentRetailMatch;
    protected bool CanEditCanonicalIdentity =>
        CanMatchCurrentTarget && string.Equals(ActiveScope?.CanonicalIdentityMode, "owned", StringComparison.OrdinalIgnoreCase);
    protected bool HasInheritedCanonicalIdentity =>
        ActiveScope?.CanonicalIdentityMode is "inherited" or "shared";
    protected MediaEditorScopeDto? CanonicalIdentityOwnerScope =>
        GetScopeById(ActiveScope?.CanonicalIdentityOwnerScopeId);
    protected bool HasInheritedArtwork =>
        string.Equals(ActiveScope?.ArtworkMode, "inherited", StringComparison.OrdinalIgnoreCase);
    protected MediaEditorScopeDto? InheritedArtworkOwnerScope => GetScopeById(ActiveScope?.ArtworkOwnerScopeId);
    protected string CanonicalIdentityHeading =>
        string.Equals(ActiveIdentitySummary?.QidResolutionMethod, "comic_series_rollup", StringComparison.OrdinalIgnoreCase)
            ? "Current series/run canonical match"
            : "Current canonical match";
    protected string RetailIdentityHeading =>
        (EditorMediaType, ActiveScope?.ScopeId) switch
        {
            ("TV", "episode") => "Episode Match",
            ("Music", "track") => "Track Match",
            ("Books", "book") => "Edition Match",
            ("Audiobooks", "audiobook") => "Audiobook Match",
            ("Comics", "issue") => "Issue Match",
            _ => "Retail Provider",
        };
    protected string RetailStatusLabel =>
        UsesParentRetailIdentityOnly
            ? "Retail exact match needed"
            : ActiveScope?.RetailIdentityMode switch
        {
            "derived" => "Retail derived",
            _ => "Retail",
        };
    protected string RetailStatusDescription =>
        UsesParentRetailIdentityOnly
            ? "Series context is available, but this episode does not have its own retail provider match."
            : ActiveScope?.RetailIdentityMode switch
            {
                "derived" => "Retail identity is derived from the selected parent context.",
                _ => HasCurrentRetailMatch ? "Retail match linked" : "No retail match",
            };
    protected string CanonicalStatusLabel =>
        ActiveScope?.CanonicalIdentityMode switch
        {
            "inherited" => "Canonical inherited",
            "shared" => "Canonical shared",
            _ => "Canonical",
        };
    protected string RetailMatchChangeDescription =>
        (EditorMediaType, ActiveScope?.ScopeId) switch
        {
            ("TV", "episode") => "Changing the episode match updates only this exact episode. The Series canonical identity remains unchanged.",
            ("Music", "track") => "Changing the track match updates only this recording. The Album canonical identity remains unchanged.",
            _ => "Changing the retail provider match updates this edition or release. The canonical Wikidata identity remains unchanged.",
        };
    protected string RetailSearchInstruction =>
        (EditorMediaType, ActiveScope?.ScopeId) switch
        {
            ("TV", "episode") => "Select the exact episode record within the matched Series and Season.",
            ("Music", "track") => "Select the exact recording within the matched Album.",
            _ => "Select the retail provider record that represents this specific edition or release.",
        };
    protected ItemCanonicalSearchResponseDto? ActiveMatchSearchResponse =>
        IsWikidataSearchMode ? _wikidataSearchResponse : _retailSearchResponse;
    protected string ActiveMatchSearchQuery =>
        IsWikidataSearchMode ? _wikidataSearchQuery : _retailSearchQuery;
    protected bool IsActiveMatchSearchPending =>
        IsWikidataSearchMode ? _searchingWikidata : _searchingRetail;
    protected string? CanonicalSearchUnavailableReason => GetCanonicalSearchUnavailableReason();
    protected IReadOnlyList<MediaEditorScopeDto> ScopeOptions =>
        (_editorContext?.Scopes ?? [])
            .OrderBy(scope => scope.Order)
            .ToList();
    protected int ActiveTabIndex => GetSelectedIndex(Tabs.Select(tab => tab.Id), _activeTab);
    protected bool IsSingleItem => IsSharedEntityMode || Request.EntityIds.Count == 1;
    protected bool IsBatchMode => !IsSharedEntityMode && (Request.Mode == SharedMediaEditorMode.Batch || Request.EntityIds.Count > 1);
    protected Guid LaunchEntityId => Request.LaunchEntityId ?? Request.EntityIds.FirstOrDefault();
    protected MediaEditorIdentityIntent EffectiveIdentityIntent =>
        _identityIntent == MediaEditorIdentityIntent.None ? Request.IdentityIntent : _identityIntent;
    protected Guid EditorContextEntityId => _editorContext?.LaunchEntityId ?? LaunchEntityId;
    protected Guid CurrentEntityId => ActiveScope?.FieldEntityId ?? EditorContextEntityId;
    private Guid CanonicalEndpointEntityId => CurrentEntityId;
    protected bool IsDirty => _editedValues.Count > 0
                              || _pendingArtworkFiles.Count > 0
                              || _audiobookChapterEdits.Count > 0
                              || _audiobookChapterResetKeys.Count > 0
                              || _sharedEntityDirty;
    protected bool ShouldShowEditorFooter =>
        _confirmDiscard
        || _pendingMembershipPreview is not null
        || IsDirty
        || Request.Mode == SharedMediaEditorMode.Review;
    private bool _automaticArtworkRestoring;
    protected bool IsArtworkBusy => _automaticArtworkRestoring || _artworkUrlSubmitting || _providerArtworkRefreshing || _artworkApplyingKeys.Count > 0;
    protected ArtworkSlotDefinition? SelectedArtworkSlot =>
        ArtworkSlots.FirstOrDefault(slot => string.Equals(slot.AssetType, _selectedArtworkAssetType, StringComparison.OrdinalIgnoreCase))
        ?? ArtworkSlots.FirstOrDefault();
    protected ArtworkVariantDisplayItem? LeadArtworkVariant =>
        SelectedArtworkSlot is null ? null : GetLeadArtworkVariant(SelectedArtworkSlot.AssetType);
    protected ArtworkSlotDefinition? ZoomArtworkSlot => _zoomArtworkSlot;
    protected ArtworkVariantDisplayItem? ZoomArtworkVariant => _zoomArtworkVariant;
    protected bool IsArtworkZoomOpen => _zoomArtworkSlot is not null && _zoomArtworkVariant is not null;
    protected bool CanZoomPrevious => GetZoomArtworkIndex() > 0;
    protected bool CanZoomNext =>
        GetZoomArtworkIndex() is var index && index >= 0 && index < GetZoomArtworkItems().Count - 1;
    protected IReadOnlyList<AppSelectOption> HistoryFilterOptions =>
    [
        new("all", "All activity"),
        new("match", "Matching"),
        new("artwork", "Artwork"),
        new("metadata", "Metadata"),
        new("file", "Files and processing"),
    ];
    protected IReadOnlyList<LibraryItemHistoryDto> FilteredHistory => _history
        .Where(entry => _historyFilter switch
        {
            "match" => string.Equals(entry.Category, "match", StringComparison.OrdinalIgnoreCase),
            "artwork" => string.Equals(entry.Category, "artwork", StringComparison.OrdinalIgnoreCase),
            "metadata" => entry.Category is "metadata" or "manual",
            "file" => string.Equals(entry.Category, "file", StringComparison.OrdinalIgnoreCase),
            _ => true,
        })
        .OrderByDescending(entry => entry.OccurredAt)
        .ToList();
    protected MediaEditorNavigatorNodeDto? NavigatorRootNode =>
        _navigator?.Nodes.FirstOrDefault(node => node.IsRoot)
        ?? _navigator?.Nodes.FirstOrDefault(node => node.ParentNodeId is null);
    protected MediaEditorNavigatorNodeDto? SelectedNavigatorNode =>
        _navigator?.Nodes.FirstOrDefault(node => node.EntityId == _navigator.SelectedEntityId)
        ?? NavigatorRootNode;
    protected bool HasContextNavigator => IsSingleItem && _navigator is { Enabled: true } && NavigatorRootNode is not null;
    protected IReadOnlyList<MediaEditorNavigatorNodeDto> ContextLevelOneNodes =>
        NavigatorRootNode is null
            ? []
            : GetNavigatorChildren(NavigatorRootNode.NodeId)
                .Where(node => node.CanSelectAsEditorTarget)
                .ToList();
    protected IReadOnlyList<EditorContextLevel> EditorContextLevels => BuildEditorContextLevels();
    protected bool HasPendingTargetSwitch => _pendingTargetSwitch is not null;
    protected string PendingTargetSwitchTitle => _pendingTargetSwitch?.Title ?? "selection";
    protected bool IsContainerEditor => string.Equals(_editorContext?.EditorMode, "container", StringComparison.OrdinalIgnoreCase);
    protected MediaEditorScopeDto? ContainerRootScope =>
        IsContainerEditor
            ? _editorContext?.Scopes
                .Where(scope => !string.Equals(scope.ScopeId, "file", StringComparison.OrdinalIgnoreCase))
                .OrderBy(scope => scope.Order)
                .FirstOrDefault()
            : null;
    protected string ContentTabId =>
        GetAvailableTabIds().FirstOrDefault(tabId =>
            string.Equals(tabId, "chapters", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    protected string ContentTabLabel => ActiveScope?.ContentTabLabel ?? GetTabLabel(ContentTabId);
    protected IReadOnlyList<MediaGroupingItemViewModel> AudiobookChapters =>
        Request.InitialMediaGroups
            .Where(group => string.Equals(group.Key, "chapters", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.Items)
            .Where(item => item.AssetId is not null && item.ChapterIndex.HasValue)
            .OrderBy(item => item.ChapterIndex)
            .ToList();
    protected MediaGroupingItemViewModel? FocusedAudiobookChapter =>
        AudiobookChapters.FirstOrDefault(chapter => string.Equals(BuildAudiobookChapterKey(chapter), _focusedAudiobookChapterKey, StringComparison.OrdinalIgnoreCase))
        ?? AudiobookChapters.FirstOrDefault();
    protected bool IsFocusedAudiobookChapter(MediaGroupingItemViewModel chapter) =>
        FocusedAudiobookChapter is { } focused
        && string.Equals(BuildAudiobookChapterKey(focused), BuildAudiobookChapterKey(chapter), StringComparison.OrdinalIgnoreCase);
    protected IReadOnlyList<MediaEditorNavigatorNodeDto> ContainerFileItems =>
        GetNavigatorDescendants(SelectedNavigatorNode)
            .Where(node => node.IsLeaf)
            .DistinctBy(node => node.EntityId)
            .ToList();
    protected int ContainerAttachedFileCount => ContainerFileItems.Count(node => node.PrimaryAssetId.HasValue);
    protected int ContainerMissingFileCount => ContainerFileItems.Count(node => !node.PrimaryAssetId.HasValue);
    protected int ContainerQuarantineCount => ContainerFileItems.Sum(node => node.QuarantineCount);
    protected IReadOnlyList<(string Label, int Count)> ContainerTechnicalBadges =>
        ContainerFileItems
            .SelectMany(node => node.TechnicalBadges)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(item => item.Item2)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    protected string CurrentTargetTitle => ActiveScope?.DisplayTitle ?? HeaderTitle;

    protected string GetContentTotalDurationLabel()
    {
        if (!string.IsNullOrWhiteSpace(_detail?.Runtime))
        {
            return _detail.Runtime!;
        }

        var totalSeconds = AudiobookChapters.Sum(chapter =>
            chapter.DurationSeconds
            ?? (chapter.StartSeconds.HasValue && chapter.EndSeconds.HasValue
                ? Math.Max(0, chapter.EndSeconds.Value - chapter.StartSeconds.Value)
                : 0));
        if (totalSeconds <= 0)
        {
            return "Unknown";
        }

        var duration = TimeSpan.FromSeconds(totalSeconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";
    }

    protected MediaEditorScopeDto? ActiveScope =>
        _editorContext?.Scopes
            .OrderBy(scope => scope.Order)
            .FirstOrDefault(scope => string.Equals(scope.ScopeId, _activeScopeId, StringComparison.OrdinalIgnoreCase))
        ?? _editorContext?.Scopes.OrderBy(scope => scope.Order).FirstOrDefault();
    protected MediaEditorIdentitySummaryDto? ActiveIdentitySummary =>
        ActiveScope?.IdentitySummary ?? _editorContext?.IdentitySummary;
    protected MediaEditorScopeDto? IdentityTargetScope =>
        (EditorMediaType, _canonicalTargetGroup) switch
        {
            ("TV", "show") => GetScopeById("series") ?? ActiveScope,
            ("Music", "album") => GetScopeById("album") ?? ActiveScope,
            _ => ActiveScope,
        };
    protected MediaEditorIdentitySummaryDto? IdentityTargetSummary =>
        IdentityTargetScope?.IdentitySummary ?? ActiveIdentitySummary;
    protected MediaEditorScopeDto? CanonicalIdentityTargetScope =>
        HasInheritedCanonicalIdentity
            ? CanonicalIdentityOwnerScope ?? IdentityTargetScope
            : IdentityTargetScope;
    protected MediaEditorIdentitySummaryDto? CanonicalIdentityTargetSummary =>
        CanonicalIdentityTargetScope?.IdentitySummary ?? IdentityTargetSummary;
    protected bool UsesParentRetailIdentityOnly
    {
        get
        {
            if (!string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ActiveScope?.ScopeId, "episode", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var summary = IdentityTargetSummary;
            var seriesSummary = GetScopeById("series")?.IdentitySummary;
            if (string.Equals(summary?.MatchLevel, "show", StringComparison.OrdinalIgnoreCase)
                || string.Equals(summary?.MatchLevel, "series", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(summary?.ProviderItemId)
                && !string.IsNullOrWhiteSpace(seriesSummary?.ProviderItemId)
                && string.Equals(summary.ProviderItemId, seriesSummary.ProviderItemId, StringComparison.OrdinalIgnoreCase);
        }
    }

    protected MediaEditorScopeDto? ArtworkScope => ActiveScope;
    protected bool IsFileScope => string.Equals(ActiveScope?.ScopeId, "file", StringComparison.OrdinalIgnoreCase);
    protected string BreadcrumbText => BuildBreadcrumbText();

    protected string HeaderKicker =>
        IsSharedEntityMode
            ? "Universe"
            : Request.Mode switch
        {
            SharedMediaEditorMode.Batch => $"{Request.EntityIds.Count} items",
            _ => NavigatorRootNode?.Label ?? ActiveScope?.Label ?? _schema.MediaType,
        };

    protected string HeaderTitle =>
        (IsSharedEntityMode ? _sharedEntityContext?.breadcrumb?.FirstOrDefault() ?? _sharedEntityContext?.label : NavigatorRootNode?.Title ?? ActiveScope?.DisplayTitle)
        ?? Request.HeaderTitle
        ?? _detail?.Title
        ?? (IsBatchMode ? $"Edit {Request.EntityIds.Count} Items" : "Edit Item");

    protected string? HeaderSubtitle =>
        (IsSharedEntityMode ? SharedEntityHeaderSubtitle : NavigatorRootNode?.Subtitle ?? ActiveScope?.DisplaySubtitle)
        ?? Request.HeaderSubtitle
        ?? (IsSingleItem ? BuildHeaderSubtitle() : string.Join(" | ", Request.PreviewItems.Take(3).Select(x => x.Title)));

    private string? SharedEntityHeaderSubtitle => Request.SharedEntityTarget?.UniverseQid;

    protected string EditorPageTitle => _activeTab switch
    {
        "artwork" => "Manage Artwork",
        "links" => "Edit Match",
        "file" => "Edit Files",
        "history" => "Edit History",
        "chapters" => "Edit Audiobook",
        _ => $"Edit {NormalizeEditorHeadingLabel(HeaderKicker)}",
    };

    protected string EditorHeaderDescription => _activeTab switch
    {
        "artwork" => "Manage artwork for this item.",
        "links" => "Review and update this item's identity.",
        "file" => "View and manage files and processing.",
        "history" => "View changes and activity for this item.",
        _ => (EditorMediaType, ContentTabLabel) switch
        {
            ("Audiobooks", "Chapters") => "Manage audiobook information and chapters.",
            _ => "Control how this item appears and behaves in your library.",
        },
    };

    private static string NormalizeEditorHeadingLabel(string value) => value switch
    {
        "Books" => "Book",
        "Movies" => "Movie",
        "Audiobooks" => "Audiobook",
        "Comics" => "Comic",
        "Music" => "Album",
        "TV" => "TV Show",
        _ => value,
    };

    protected string? CurrentCoverUrl => GetHeaderArtworkPreviewUrl();

    protected ArtworkLibraryItemDto? UnifiedArtworkItem
    {
        get
        {
            if (!IsSingleItem || ActiveScope is null)
            {
                return null;
            }

            var entityId = ActiveScope.ArtworkOwnerEntityId ?? ActiveScope.FieldEntityId;
            if (entityId == Guid.Empty)
            {
                return null;
            }

            var entityType = string.IsNullOrWhiteSpace(ActiveScope.FieldEntityKind)
                ? "Work"
                : ActiveScope.FieldEntityKind;
            var slots = GetCurrentArtworkEditor()?.Slots ?? [];
            var variants = slots.SelectMany(slot => slot.Variants).ToList();
            var automaticPreviews = AutomaticGroupArtwork.Select(item => new ArtworkLibraryPreviewItemDto(
                item.WorkId ?? Guid.Empty,
                item.AssetId,
                item.Title,
                item.ImageUrl,
                item.Shape switch { ArtworkShape.Square => "square", ArtworkShape.Wide => "wide", _ => "portrait" },
                item.Position,
                item.MediaType)).ToList();
            var primary = GetPreferredArtworkVariant("CoverArt");

            return new ArtworkLibraryItemDto(
                entityId,
                entityType,
                CurrentTargetTitle,
                EditorMediaType,
                _detail?.Year,
                ActiveScope.DisplaySubtitle,
                primary?.ImageUrl ?? CurrentCoverUrl,
                primary?.AssetType ?? "CoverArt",
                slots.Select(slot => slot.AssetType).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                variants.Count,
                automaticPreviews.Count,
                false)
            {
                IsStructural = HasAutomaticGroupArtwork,
                GroupKind = ActiveScope.ScopeId,
                ResolutionMode = primary is not null
                    ? ArtworkResolutionMode.Explicit
                    : automaticPreviews.Count > 0
                        ? ArtworkResolutionMode.AutomaticGroup
                        : ArtworkResolutionMode.None,
                PreviewItems = automaticPreviews,
                BackgroundImageUrl = GetPreferredArtworkVariant("Background")?.ImageUrl ?? _detail?.BackgroundUrl,
                LogoImageUrl = GetPreferredArtworkVariant("Logo")?.ImageUrl,
            };
        }
    }

    private async Task UnifiedArtworkChangedAsync()
    {
        await RefreshArtworkStateAsync(ActiveScope?.ScopeId, notifyParent: true);
        StateHasChanged();
    }

    private sealed class ScopeEditorState
    {
        public LibraryItemDetailViewModel? Detail { get; init; }
        public List<CanonicalFieldDto> CanonicalValues { get; init; } = [];
        public List<ClaimDto> Claims { get; init; } = [];
        public List<LibraryItemHistoryDto> History { get; init; } = [];
        public ArtworkEditorDto Artwork { get; init; } = new();
    }

    private sealed record AudiobookChapterEdit(Guid AssetId, int ChapterIndex, string Title, string TitleSource);

    private sealed record PendingTargetSwitch(Guid EntityId, string? PreferredScopeId, string Title);

    protected sealed record MatchCardDisplay(
        string Badge,
        string Title,
        string? Creator,
        string? Year,
        string? CoverUrl,
        IReadOnlyList<string> Chips,
        IReadOnlyList<IdentityLinkDisplay> Links,
        string Note);

    protected sealed record IdentityLinkDisplay(string Label, string Url);

    protected override async Task OnInitializedAsync()
    {
        _tabState.Initialize(string.IsNullOrWhiteSpace(Request.InitialTab) ? "details" : Request.InitialTab);
        _schema = MediaEditorSchemaCatalog.Resolve(Request.MediaType);
        InitializeSharedEntityMode();

        if (IsSharedEntityMode)
        {
            _loading = false;
            return;
        }

        try
        {
            await ProviderCatalogue.GetCatalogueAsync();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Provider catalogue unavailable while opening the media editor");
        }

        if (IsSingleItem)
        {
            await LoadSingleItemAsync(resetEditorState: true);
            await LoadRefreshScheduleAsync();
        }
        else
        {
            await LoadBatchAsync();
        }
    }

    private async Task LoadRefreshScheduleAsync()
    {
        var entityType = ActiveScope?.FieldEntityKind ?? "Work";
        _refreshSchedule = await ApiClient.GetEntityEnrichmentRefreshScheduleAsync(entityType, EditorContextEntityId);
        if (_refreshSchedule is null && CurrentEntityId != EditorContextEntityId)
        {
            _refreshSchedule = await ApiClient.GetEntityEnrichmentRefreshScheduleAsync("MediaAsset", CurrentEntityId);
        }
    }

    protected async Task QueueFullEnrichmentAsync()
    {
        var target = _refreshSchedule;
        if (_refreshQueueing)
        {
            return;
        }

        var entityId = target?.EntityId ?? CurrentEntityId;
        var entityType = target?.EntityType ?? ActiveScope?.FieldEntityKind ?? "Work";
        if (entityId == Guid.Empty)
        {
            return;
        }

        _refreshQueueing = true;
        try
        {
            var result = await ApiClient.QueueEnrichmentRefreshNowAsync(entityType, entityId);
            if (result is null)
            {
                Snackbar.Add("The enrichment refresh could not be queued.", Severity.Error);
                return;
            }

            Snackbar.Add(result.Message, Severity.Success);
            await LoadRefreshScheduleAsync();
        }
        finally
        {
            _refreshQueueing = false;
        }
    }

    protected static string FormatRefreshDate(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("MMM d, yyyy · h:mm tt") ?? "Not yet";

    private async Task LoadSingleItemAsync(
        Guid? targetEntityId = null,
        bool resetEditorState = false,
        string? preferredScopeId = null)
    {
        _loading = true;
        _loadError = null;
        StateHasChanged();

        try
        {
            var entityId = targetEntityId ?? LaunchEntityId;

            if (resetEditorState)
            {
                _editedValues.Clear();
                _inlineOverrideKeys.Clear();
                _clearedInlineOverrideKeys.Clear();
                _pendingInlineRevertKeys.Clear();
                _selectedMembershipTargetIds.Clear();
                _selectedMembershipSuggestions.Clear();
                _membershipSuggestions.Clear();
                _pendingArtworkFiles.Clear();
                _pendingArtworkPreviewUrls.Clear();
                _artworkUploadErrors.Clear();
                _scopeStates.Clear();
                _artworkStates.Clear();
                _profilePreferencesByWork.Clear();
                _audiobookChapterEdits.Clear();
                _audiobookChapterResetKeys.Clear();
                _audiobookChapterOverrideKeys.Clear();
                _pendingTargetSwitch = null;
                _switchAfterSuccessfulSave = false;
            }

            _editorContext = await ApiClient.GetMediaEditorContextAsync(entityId);
            _navigator = await ApiClient.GetMediaEditorNavigatorAsync(entityId);

            if (_editorContext is null)
            {
                var detailTask = ApiClient.GetLibraryItemDetailAsync(entityId);
                var canonicalTask = Orchestrator.GetCanonicalValuesAsync(entityId);
                var claimsTask = Orchestrator.GetClaimHistoryAsync(entityId);
                var historyTask = ApiClient.GetItemHistoryAsync(entityId);
                var artworkTask = ApiClient.GetArtworkAsync(entityId);

                await Task.WhenAll(detailTask, canonicalTask, claimsTask, historyTask, artworkTask);

                _detail = detailTask.Result;
                _canonicalValues = canonicalTask.Result;
                _claims = claimsTask.Result;
                _history = historyTask.Result;
                _artwork = artworkTask.Result ?? new ArtworkEditorDto { EntityId = entityId };
                _schema = MediaEditorSchemaCatalog.Resolve(EditorMediaType);
            }
            else
            {
                _schema = MediaEditorSchemaCatalog.Resolve(EditorMediaType);
                _activeScopeId = !string.IsNullOrWhiteSpace(preferredScopeId)
                    ? preferredScopeId
                    : targetEntityId is null && !string.IsNullOrWhiteSpace(Request.InitialScope)
                        ? Request.InitialScope!
                        : _editorContext.InitialScope;

                if (!_editorContext.Scopes.Any(scope => string.Equals(scope.ScopeId, _activeScopeId, StringComparison.OrdinalIgnoreCase)))
                {
                    _activeScopeId = _editorContext.Scopes.OrderBy(scope => scope.Order).FirstOrDefault()?.ScopeId ?? string.Empty;
                }

                await LoadScopeStateAsync(forceReload: true);
            }

            await LoadProfilePreferencesAsync(CurrentEntityId);
            await LoadEditorSuggestionsAsync();
            await LoadAudiobookChapterOverrideStateAsync(CurrentEntityId);
            _dragTargetArtworkType = null;
            _showQuarantineConfirm = false;
            _pendingMembershipPreview = null;
            NormalizeArtworkSelection();

            if (Request.Mode == SharedMediaEditorMode.Review)
            {
                var target = ReviewTargetResolver.Resolve(_detail?.MediaType ?? Request.MediaType, Request.ReviewTrigger ?? _detail?.ReviewTrigger);
                _tabState.Activate(string.IsNullOrWhiteSpace(Request.InitialTab) ? target.InitialTab : Request.InitialTab!);
                _canonicalTargetGroup = string.IsNullOrWhiteSpace(Request.InitialCanonicalTargetGroup)
                    ? (ActiveScope?.CanonicalTargetGroup ?? target.CanonicalTargetGroup)
                    : Request.InitialCanonicalTargetGroup!;
                _reviewSummary = target.Summary;
                _identityIntent = Request.IdentityIntent == MediaEditorIdentityIntent.None ? target.Intent : Request.IdentityIntent;
                _primaryActionLabel = target.PrimaryActionLabel;
                _activeMatchSearchMode = IsWikidataIntent ? "wikidata" : "retail";
            }
            else
            {
                _identityIntent = Request.IdentityIntent;
                _primaryActionLabel = ResolveFooterPrimaryActionLabel(_identityIntent);
                _canonicalTargetGroup = string.IsNullOrWhiteSpace(Request.InitialCanonicalTargetGroup)
                    ? (ActiveScope?.CanonicalTargetGroup ?? _schema.DefaultTargetGroup)
                    : Request.InitialCanonicalTargetGroup!;
                _activeMatchSearchMode = IsWikidataIntent ? "wikidata" : "retail";
            }

            if (IsFileScope)
            {
                _tabState.ActivateFile();
            }

            EnsureActiveTabVisible();
            InitializeMatchSearchQueries();
            InitializeMatchSearchState();
            await LoadTextTracksAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Media editor failed to load entity {EntityId}.", targetEntityId ?? LaunchEntityId);
            _loadError = "This item could not be loaded for editing.";
            Snackbar.Add(_loadError, Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task LoadProfilePreferencesAsync(Guid workId)
    {
        if (Request.ActiveProfileId is not { } profileId || workId == Guid.Empty || IsBatchMode)
        {
            return;
        }

        var preferences = await ApiClient.GetItemEditorPreferencesAsync(workId, profileId);
        if (preferences is null)
        {
            return;
        }

        _profilePreferencesByWork[workId] = preferences;
    }

    private async Task LoadEditorSuggestionsAsync()
    {
        try
        {
            var genresTask = ApiClient.GetItemEditorSuggestionsAsync("genre");
            var tagsTask = ApiClient.GetItemEditorSuggestionsAsync("tags", Request.ActiveProfileId);
            await Task.WhenAll(genresTask, tagsTask);
            _genreSuggestions = genresTask.Result;
            _tagSuggestions = tagsTask.Result;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Editor autocomplete suggestions were unavailable");
            _genreSuggestions = [];
            _tagSuggestions = [];
        }
    }

    private async Task LoadAudiobookChapterOverrideStateAsync(Guid workId)
    {
        if (!string.Equals(EditorMediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
            || AudiobookChapters.Count == 0)
        {
            return;
        }

        var overrides = await ApiClient.GetAudiobookChapterTitleOverridesAsync(workId);
        _audiobookChapterOverrideKeys.Clear();
        foreach (var chapterOverride in overrides)
        {
            _audiobookChapterOverrideKeys.Add(BuildAudiobookChapterKey(chapterOverride.AssetId, chapterOverride.ChapterIndex));
        }
    }

    protected static string BuildAudiobookChapterKey(MediaGroupingItemViewModel chapter) =>
        $"{chapter.AssetId}:{chapter.ChapterIndex}";

    private static string BuildAudiobookChapterKey(Guid assetId, int chapterIndex) =>
        $"{assetId:D}:{chapterIndex}";

    protected string GetAudiobookChapterTitle(MediaGroupingItemViewModel chapter)
    {
        var key = BuildAudiobookChapterKey(chapter);
        return _audiobookChapterEdits.TryGetValue(key, out var edit) ? edit.Title : chapter.Title;
    }

    protected bool HasAudiobookChapterOverride(MediaGroupingItemViewModel chapter) =>
        _audiobookChapterOverrideKeys.Contains(BuildAudiobookChapterKey(chapter))
        && !_audiobookChapterResetKeys.Contains(BuildAudiobookChapterKey(chapter));

    protected void ToggleAudiobookChapterEditor(MediaGroupingItemViewModel chapter)
    {
        var key = BuildAudiobookChapterKey(chapter);
        _focusedAudiobookChapterKey = string.Equals(_focusedAudiobookChapterKey, key, StringComparison.OrdinalIgnoreCase)
            ? null
            : key;
    }

    protected void OnAudiobookChapterTitleInput(MediaGroupingItemViewModel chapter, string? value)
    {
        if (!Guid.TryParse(chapter.AssetId, out var assetId) || !chapter.ChapterIndex.HasValue)
        {
            return;
        }

        var key = BuildAudiobookChapterKey(chapter);
        var normalized = (value ?? string.Empty).Trim();
        _audiobookChapterResetKeys.Remove(key);
        if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, chapter.Title, StringComparison.Ordinal))
        {
            _audiobookChapterEdits.Remove(key);
        }
        else
        {
            _audiobookChapterEdits[key] = new AudiobookChapterEdit(assetId, chapter.ChapterIndex.Value, normalized, PlaybackChapterTitleSources.Override);
        }
    }

    protected void QueueAudiobookChapterReset(MediaGroupingItemViewModel chapter)
    {
        var key = BuildAudiobookChapterKey(chapter);
        _audiobookChapterEdits.Remove(key);
        _audiobookChapterResetKeys.Add(key);
        _focusedAudiobookChapterKey = null;
    }

    protected static string FormatAudiobookChapterRange(MediaGroupingItemViewModel chapter)
    {
        return $"{FormatAudiobookChapterTime(chapter.StartSeconds)} - {FormatAudiobookChapterTime(chapter.EndSeconds)}";
    }

    protected static string FormatAudiobookChapterTime(double? seconds)
    {
        if (!seconds.HasValue)
        {
            return "--:--";
        }

        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"00:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    protected static string FormatAudiobookChapterDuration(MediaGroupingItemViewModel chapter) =>
        FormatAudiobookChapterTime(
            chapter.DurationSeconds
            ?? (chapter.StartSeconds.HasValue && chapter.EndSeconds.HasValue
                ? Math.Max(0, chapter.EndSeconds.Value - chapter.StartSeconds.Value)
                : null));

    private async Task LoadScopeStateAsync(bool forceReload = false)
    {
        if (!IsSingleItem || _editorContext is null || ActiveScope is null)
        {
            return;
        }

        var stateKey = BuildScopeStateKey(ActiveScope.FieldEntityId, ActiveScope.ScopeId);
        if (!forceReload && _scopeStates.TryGetValue(stateKey, out var cachedState))
        {
            ApplyScopeState(cachedState);
            return;
        }

        var detailTask = ApiClient.GetLibraryItemDetailAsync(ActiveScope.FieldEntityId);
        var canonicalTask = Orchestrator.GetCanonicalValuesAsync(ActiveScope.FieldEntityId);
        var claimsTask = Orchestrator.GetClaimHistoryAsync(ActiveScope.FieldEntityId);
        var historyTask = ActiveScope.AvailableTabs.Contains("history", StringComparer.OrdinalIgnoreCase)
            ? ApiClient.GetItemHistoryAsync(ActiveScope.FieldEntityId)
            : Task.FromResult<List<LibraryItemHistoryDto>>([]);
        var activeScopeSlots = ResolveArtworkSlots(ActiveScope);
        var artworkTask = ActiveScope.CanEditArtwork || activeScopeSlots.Count > 0
            ? ApiClient.GetScopeArtworkAsync(EditorContextEntityId, ActiveScope.ScopeId)
            : Task.FromResult<ArtworkEditorDto?>(new ArtworkEditorDto { EntityId = ActiveScope.ArtworkOwnerEntityId ?? ActiveScope.FieldEntityId });

        await Task.WhenAll(detailTask, canonicalTask, claimsTask, historyTask, artworkTask);

        var state = new ScopeEditorState
        {
            Detail = detailTask.Result,
            CanonicalValues = canonicalTask.Result,
            Claims = claimsTask.Result,
            History = historyTask.Result,
            Artwork = artworkTask.Result ?? new ArtworkEditorDto { EntityId = ActiveScope.ArtworkOwnerEntityId ?? ActiveScope.FieldEntityId },
        };

        _scopeStates[stateKey] = state;
        _artworkStates[stateKey] = state.Artwork;
        ApplyScopeState(state);
    }

    private void ApplyScopeState(ScopeEditorState state)
    {
        _detail = state.Detail;
        _canonicalValues = state.CanonicalValues;
        _claims = state.Claims;
        _history = state.History;
        _artwork = state.Artwork;
        if (ActiveScope is not null)
        {
            _artworkStates[BuildScopeStateKey(ActiveScope.FieldEntityId, ActiveScope.ScopeId)] = state.Artwork;
        }

        _schema = MediaEditorSchemaCatalog.Resolve(EditorMediaType);
        _canonicalTargetGroup = ActiveScope?.CanonicalTargetGroup ?? _schema.DefaultTargetGroup;
        if (!CanEditCanonicalIdentity)
        {
            _activeMatchSearchMode = "retail";
        }
        ResetMatchSearchState();
        _showQuarantineConfirm = false;
        _pendingMembershipPreview = null;
        CloseArtworkZoom();
        EnsureActiveTabVisible();
        NormalizeArtworkSelection();
    }

    protected async Task SelectScopeAsync(string scopeId)
    {
        if (string.Equals(_activeScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var wasFileScope = IsFileScope;
        _tabState.RememberCurrentNonFile();

        _activeScopeId = scopeId;
        _artworkAddMenuAssetType = null;
        _artworkUrlInput = string.Empty;
        _showArtworkUrlInput = false;

        if (string.Equals(scopeId, "file", StringComparison.OrdinalIgnoreCase))
        {
            _tabState.ActivateFile();
        }
        else if (wasFileScope && string.Equals(_activeTab, "file", StringComparison.OrdinalIgnoreCase))
        {
            _tabState.Activate(_lastNonFileTab);
        }

        _loading = true;
        StateHasChanged();

        try
        {
            await LoadScopeStateAsync();
            await LoadProfilePreferencesAsync(CurrentEntityId);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    protected async Task SelectNavigatorNodeAsync(Guid entityId)
    {
        var node = _navigator?.Nodes.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (node is null || !node.CanSelectAsEditorTarget)
        {
            return;
        }

        await RequestEditorTargetSwitchAsync(node);
    }

    protected Task SelectEditorContextTargetAsync(Guid entityId)
    {
        var node = _navigator?.Nodes.FirstOrDefault(candidate => candidate.EntityId == entityId);
        return node is null ? Task.CompletedTask : RequestEditorTargetSwitchAsync(node);
    }

    private async Task RequestEditorTargetSwitchAsync(MediaEditorNavigatorNodeDto node)
    {
        if (node.EntityId == Guid.Empty
            || node.EntityId == _navigator?.SelectedEntityId
            || !node.CanSelectAsEditorTarget)
        {
            return;
        }

        if (IsDirty)
        {
            _pendingTargetSwitch = new PendingTargetSwitch(node.EntityId, node.ScopeId, node.Title);
            StateHasChanged();
            return;
        }

        await PerformTargetSwitchAsync(node.EntityId, node.ScopeId);
    }

    protected async Task SaveAndSwitchTargetAsync()
    {
        if (_pendingTargetSwitch is null || _saving)
        {
            return;
        }

        _switchAfterSuccessfulSave = true;
        await SaveAsyncCore(applyMembershipMove: false);
    }

    protected async Task DiscardAndSwitchTargetAsync()
    {
        if (_pendingTargetSwitch is null)
        {
            return;
        }

        var pending = _pendingTargetSwitch;
        ResetEditorChanges();
        _pendingTargetSwitch = null;
        await PerformTargetSwitchAsync(pending.EntityId, pending.PreferredScopeId);
    }

    protected void KeepEditingCurrentTarget()
    {
        _switchAfterSuccessfulSave = false;
        _pendingTargetSwitch = null;
    }

    private async Task PerformTargetSwitchAsync(Guid entityId, string? preferredScopeId)
    {
        var previousContext = _editorContext;
        var previousNavigator = _navigator;
        var previousScopeId = _activeScopeId;
        var previousTab = _activeTab;
        var previousState = new ScopeEditorState { Detail = _detail, CanonicalValues = _canonicalValues,
            Claims = _claims, History = _history, Artwork = _artwork ?? new ArtworkEditorDto() };
        await LoadSingleItemAsync(entityId, resetEditorState: false, preferredScopeId: preferredScopeId);
        if (_loadError is not null)
        {
            _editorContext = previousContext;
            _navigator = previousNavigator;
            _activeScopeId = previousScopeId;
            ApplyScopeState(previousState);
            _tabState.Activate(previousTab);
            _loadError = null; // LoadSingleItemAsync already logged and displayed the recoverable error.
        }
        EnsureActiveTabVisible();
    }

    private async Task<bool> CompletePendingTargetSwitchAsync()
    {
        if (!_switchAfterSuccessfulSave || _pendingTargetSwitch is null)
        {
            return false;
        }

        var pending = _pendingTargetSwitch;
        _hasCommittedChanges = true;
        ResetEditorChanges();
        _switchAfterSuccessfulSave = false;
        _pendingTargetSwitch = null;
        await PerformTargetSwitchAsync(pending.EntityId, pending.PreferredScopeId);
        return true;
    }

    private MediaEditorNavigatorNodeDto? GetSelectedContextNodeAtDepth(int depth)
    {
        var current = SelectedNavigatorNode;
        while (current is not null && current.Depth > depth && current.ParentNodeId is Guid parentNodeId)
        {
            current = _navigator?.Nodes.FirstOrDefault(node => node.NodeId == parentNodeId);
        }

        return current?.Depth == depth ? current : null;
    }

    private IReadOnlyList<EditorContextLevel> BuildEditorContextLevels()
    {
        if (_navigator is not { Enabled: true } navigator || NavigatorRootNode is null)
        {
            return [];
        }

        var discoveredMaxDepth = navigator.Nodes.Count == 0
            ? 0
            : navigator.Nodes.Max(node => node.Depth);
        var maximumDepth = string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(2, discoveredMaxDepth)
            : discoveredMaxDepth;
        var levels = new List<EditorContextLevel>(maximumDepth + 1);

        for (var depth = 1; depth <= maximumDepth; depth++)
        {
            var selectedNode = GetSelectedContextNodeAtDepth(depth);
            var parentNode = depth == 0 ? null : GetSelectedContextNodeAtDepth(depth - 1);
            var optionNodes = depth == 0 || parentNode is null
                ? []
                : GetNavigatorChildren(parentNode.NodeId)
                    .Where(node => node.CanSelectAsEditorTarget)
                    .ToList();
            var representativeNode = selectedNode
                ?? optionNodes.FirstOrDefault()
                ?? navigator.Nodes.FirstOrDefault(node => node.Depth == depth);
            var nodeKind = representativeNode?.NodeKind
                ?? GetExpectedEditorContextNodeKind(EditorMediaType, depth);
            var label = GetContextLevelLabel(nodeKind);
            var options = optionNodes
                .Select(node => new EditorContextOption(
                    node.EntityId,
                    GetContextLevelLabel(node.NodeKind),
                    node.Title,
                    node.Subtitle,
                    node.EntityId == selectedNode?.EntityId,
                    node.CanSelectAsEditorTarget,
                    GetContextArtworkUrl(node),
                    node.EntityId == selectedNode?.EntityId ? GetContextRetailStatus(node) : null,
                    node.EntityId == selectedNode?.EntityId ? GetContextCanonicalStatus(node) : null,
                    IsTextOnlyContextNode(node)))
                .ToList();

            levels.Add(new EditorContextLevel(
                label,
                nodeKind,
                selectedNode?.Title ?? $"Select {label.ToLowerInvariant()}",
                selectedNode?.Subtitle,
                selectedNode?.EntityId,
                selectedNode?.EntityId == navigator.SelectedEntityId,
                selectedNode is { CanSelectAsEditorTarget: true },
                depth > 0,
                options,
                GetContextArtworkUrl(selectedNode),
                selectedNode is null ? null : GetContextRetailStatus(selectedNode),
                selectedNode is null ? null : GetContextCanonicalStatus(selectedNode),
                IsTextOnlyContextNode(representativeNode)));
        }

        return levels;
    }

    private string? GetContextArtworkUrl(MediaEditorNavigatorNodeDto? node) =>
        node is null || IsTextOnlyContextNode(node) || string.IsNullOrWhiteSpace(node.ArtworkUrl)
            ? null : ApiClient.ToAbsoluteEngineUrl(node.ArtworkUrl);

    protected bool HasAutomaticGroupArtwork => ActiveScope?.ArtworkPresentation == "automatic_group";
    protected bool HasParentGroupArtwork => NavigatorRootNode is { NodeKind: "series" or "film_series" } && EditorMediaType != "TV";
    protected IReadOnlyList<ArtworkStackItem> AutomaticGroupArtwork => BuildAutomaticGroupArtwork(SelectedNavigatorNode);
    protected IReadOnlyList<ArtworkStackItem> ParentGroupArtwork => BuildAutomaticGroupArtwork(NavigatorRootNode);

    private IReadOnlyList<ArtworkStackItem> BuildAutomaticGroupArtwork(MediaEditorNavigatorNodeDto? owner) =>
        GetNavigatorDescendants(owner).Where(node => node.IsLeaf && node.IsOwned && !string.IsNullOrWhiteSpace(node.ArtworkUrl))
            .DistinctBy(node => node.EntityId).Take(4).Select(node => new ArtworkStackItem
            {
                Id = node.EntityId.ToString(), WorkId = node.EntityId, AssetId = node.PrimaryAssetId,
                Title = node.Title, ImageUrl = GetContextArtworkUrl(node) ?? string.Empty, MediaType = EditorMediaType,
                Shape = node.ArtworkShape switch { "wide" => ArtworkShape.Wide, "square" => ArtworkShape.Square, _ => ArtworkShape.Portrait },
            }).ToList();

    protected async Task RestoreAutomaticArtworkAsync()
    {
        if (!HasAutomaticGroupArtwork || IsArtworkBusy || _artwork is null) return;
        var variants = _artwork.Slots.Where(slot => slot.AssetType == "CoverArt")
            .SelectMany(slot => slot.Variants).Where(variant => variant.Id != Guid.Empty).ToList();
        if (variants.Any(variant => !variant.CanDelete)) return;
        if (await DialogService.ShowMessageBoxAsync("Restore automatic artwork?",
            "Remove this shelf's custom covers and use artwork from its owned titles?",
            yesText: "Restore automatic artwork", cancelText: "Keep custom covers") != true) return;

        _automaticArtworkRestoring = true;
        try
        {
            foreach (var variant in variants)
            {
                if (!await ApiClient.DeleteArtworkAsync(variant.Id))
                {
                    Snackbar.Add("Could not restore automatic artwork. Please try again.", Severity.Error);
                    await RefreshArtworkStateAsync(notifyParent: true);
                    return;
                }
            }
            await RefreshArtworkStateAsync(notifyParent: true);
        }
        finally
        {
            _automaticArtworkRestoring = false;
        }
    }

    protected bool IsParentEditorTarget => HasContextNavigator && NavigatorRootNode?.EntityId == _navigator?.SelectedEntityId;

    private string GetContextRetailStatus(MediaEditorNavigatorNodeDto? node)
    {
        var sourceScope = GetScopeById(node?.ScopeId) ?? ActiveScope;
        if (string.Equals(sourceScope?.RetailIdentityMode, "derived", StringComparison.OrdinalIgnoreCase))
        {
            return "Inherited";
        }

        var scope = ResolveContextIdentityScope(node, canonical: false);
        if (scope is null)
        {
            return "Unknown";
        }

        if (string.Equals(scope.RetailIdentityMode, "derived", StringComparison.OrdinalIgnoreCase))
        {
            return "Inherited";
        }

        return HasRetailIdentity(scope.IdentitySummary) ? "Matched" : "Unmatched";
    }

    private string GetContextCanonicalStatus(MediaEditorNavigatorNodeDto? node)
    {
        var sourceScope = GetScopeById(node?.ScopeId) ?? ActiveScope;
        if (sourceScope?.CanonicalIdentityMode is "inherited" or "shared")
        {
            return "Inherited";
        }

        var scope = ResolveContextIdentityScope(node, canonical: true);
        if (scope is null)
        {
            return "Unknown";
        }

        return !string.IsNullOrWhiteSpace(scope.IdentitySummary?.WikidataQid)
            ? "Matched"
            : "Unmatched";
    }

    private MediaEditorScopeDto? ResolveContextIdentityScope(MediaEditorNavigatorNodeDto? node, bool canonical)
    {
        var scope = GetScopeById(node?.ScopeId) ?? ActiveScope;
        if (scope is null)
        {
            return null;
        }

        if (canonical && !string.IsNullOrWhiteSpace(scope.CanonicalIdentityOwnerScopeId))
        {
            return GetScopeById(scope.CanonicalIdentityOwnerScopeId) ?? scope;
        }

        if (!canonical && string.Equals(scope.RetailIdentityMode, "derived", StringComparison.OrdinalIgnoreCase))
        {
            var ownerScopeId = node?.NodeKind?.ToLowerInvariant() switch
            {
                "episode" or "season" => "series",
                "track" => "album",
                _ => null,
            };
            return GetScopeById(ownerScopeId) ?? scope;
        }

        return scope;
    }

    private static bool HasRetailIdentity(MediaEditorIdentitySummaryDto? summary) =>
        !string.IsNullOrWhiteSpace(summary?.ProviderItemId)
        || !string.IsNullOrWhiteSpace(summary?.MatchSource)
        || !string.IsNullOrWhiteSpace(summary?.ProviderName);

    private static bool IsTextOnlyContextNode(MediaEditorNavigatorNodeDto? node) =>
        string.Equals(node?.NodeKind, "track", StringComparison.OrdinalIgnoreCase);

    private static string GetExpectedEditorContextNodeKind(string mediaType, int depth) =>
        (mediaType, depth) switch
        {
            ("TV", 0) => "series",
            ("TV", 1) => "season",
            ("TV", 2) => "episode",
            _ => "item",
        };

    private static string GetContextLevelLabel(string? nodeKind) =>
        (nodeKind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "season" => "Season",
            "episode" => "Episode",
            "volume" => "Volume",
            "album" => "Album",
            "track" => "Track",
            "series" => "Series",
            "film_series" => "Film Series",
            "movie" => "Movie",
            "book" => "Book",
            "audiobook" => "Audiobook",
            "issue" => "Issue",
            "artist" => "Artist",
            "release_group" => "Release group",
            _ => "Item",
        };

    protected async Task SelectArtworkScopeAsync(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return;
        }

        await SwitchToOwningScopeAsync(scopeId, "artwork");
    }

    protected Task EditCanonicalIdentityOwnerAsync() =>
        SwitchToOwningScopeAsync(ActiveScope?.CanonicalIdentityOwnerScopeId, "links");

    protected Task EditInheritedArtworkOwnerAsync() =>
        SwitchToOwningScopeAsync(ActiveScope?.ArtworkOwnerScopeId, "artwork");

    private async Task SwitchToOwningScopeAsync(string? scopeId, string preferredTab)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return;
        }

        var ownerNode = GetSelectedPathNodes()
            .FirstOrDefault(node => string.Equals(node.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            ?? _navigator?.Nodes.FirstOrDefault(node => string.Equals(node.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase) && node.IsRoot);
        if (ownerNode is null)
        {
            return;
        }

        _tabState.Activate(preferredTab);
        await RequestEditorTargetSwitchAsync(ownerNode);
    }

    private IReadOnlyList<MediaEditorNavigatorNodeDto> GetSelectedPathNodes()
    {
        var path = new List<MediaEditorNavigatorNodeDto>();
        var current = SelectedNavigatorNode;
        while (current is not null)
        {
            path.Add(current);
            current = current.ParentNodeId is Guid parentNodeId
                ? _navigator?.Nodes.FirstOrDefault(node => node.NodeId == parentNodeId)
                : null;
        }

        path.Reverse();
        return path;
    }

    protected void SelectCanonicalTargetGroup(string targetGroup)
    {
        if (string.IsNullOrWhiteSpace(targetGroup)
            || string.Equals(_canonicalTargetGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _canonicalTargetGroup = targetGroup;
        ResetMatchSearchState();
        _matchActionStatus = null;
        StateHasChanged();
    }

    protected string GetIdentityTargetImpactText() =>
        (EditorMediaType, ActiveScope?.ScopeId, _canonicalTargetGroup) switch
        {
            ("TV", "series", _) => "Series identity and provider links are shared by every owned episode in this show.",
            ("TV", "season", _) => "Season placement and artwork live here; show identity is managed on the Series scope.",
            ("TV", "episode", "show") => "This updates the parent Series identity shared by every owned episode.",
            ("TV", "episode", _) => "This updates only the episode identity and episode still. A different series must be selected through the Details placement controls first.",
            ("Music", "album", _) => "Album identity and artwork are shared by every owned track on this release.",
            ("Music", "track", "album") => "This updates the parent Album identity shared by its tracks.",
            ("Music", "track", _) => "This updates only the recording/track identity. A different album must be selected through the Details placement controls first.",
            _ => ActiveScope?.ScopeSummary ?? "This match applies to the selected editor scope.",
        };

    private async Task LoadArtworkStateAsync(string? scopeId, bool forceReload = false)
    {
        if (!IsSingleItem || string.IsNullOrWhiteSpace(scopeId))
        {
            return;
        }

        var scope = GetScopeById(scopeId);
        if (scope is null)
        {
            return;
        }

        var stateKey = BuildScopeStateKey(scope.FieldEntityId, scope.ScopeId);
        if (!forceReload && _artworkStates.ContainsKey(stateKey))
        {
            return;
        }

        var slots = ResolveArtworkSlots(scope);
        if (!scope.CanEditArtwork && slots.Count == 0)
        {
            _artworkStates[stateKey] = new ArtworkEditorDto { EntityId = scope.ArtworkOwnerEntityId ?? scope.FieldEntityId };
            return;
        }

        var artwork = await ApiClient.GetScopeArtworkAsync(EditorContextEntityId, scope.ScopeId)
                      ?? new ArtworkEditorDto { EntityId = scope.ArtworkOwnerEntityId ?? scope.FieldEntityId };
        _artworkStates[stateKey] = artwork;
    }

    private Task LoadBatchAsync()
    {
        var mediaTypes = Request.PreviewItems.Select(x => x.MediaType ?? Request.MediaType ?? "Books");
        _schema = MediaEditorSchemaCatalog.Resolve(mediaTypes.FirstOrDefault());
        _canonicalTargetGroup = string.IsNullOrWhiteSpace(Request.InitialCanonicalTargetGroup) ? _schema.DefaultTargetGroup : Request.InitialCanonicalTargetGroup!;
        _loading = false;
        return Task.CompletedTask;
    }

    protected bool IsTabDisabled(string tabId) => !IsTabVisible(tabId);

    protected IEnumerable<MediaEditorFieldGroup> GetGroupsForTab(string tabId)
    {
        if (!IsBatchMode)
        {
            var visibleKeys = GetVisibleFieldKeysForScope();
            var sourceTabIds = tabId switch
            {
                "details" => new[] { "details", "options", "sorting" },
                "options" => new[] { "options", "sorting" },
                _ => new[] { tabId },
            };

            return _schema.Groups
                .Where(group => sourceTabIds.Contains(group.TabId, StringComparer.OrdinalIgnoreCase))
                .Select(group => new MediaEditorFieldGroup
                {
                    Id = group.Id,
                    Label = string.Equals(tabId, "options", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(group.TabId, "sorting", StringComparison.OrdinalIgnoreCase)
                        ? "Sort Order"
                        : group.Label,
                    TabId = tabId,
                    Fields = group.Fields
                        .Where(field => visibleKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
                        .ToList(),
                })
                .Where(group => group.Fields.Count > 0)
                .ToList();
        }

        var batchFields = MediaEditorSchemaCatalog.ResolveBatchFields(Request.PreviewItems.Select(x => x.MediaType ?? Request.MediaType ?? "Books"));

        return tabId switch
        {
            "details" =>
            [
                new MediaEditorFieldGroup
                {
                    Id = "batch_details",
                    Label = "Shared Fields",
                    TabId = "details",
                    Fields = batchFields
                        .Where(field => !field.Key.StartsWith("sort_", StringComparison.OrdinalIgnoreCase)
                                        && field.Key is not ("description" or "rating"))
                        .ToList(),
                },
            ],
            "options" =>
                new[]
                {
                    new MediaEditorFieldGroup
                    {
                        Id = "batch_options",
                        Label = "Batch Options",
                        TabId = "options",
                        Fields = batchFields.Where(field => field.Key is "description" or "rating").ToList(),
                    },
                    new MediaEditorFieldGroup
                    {
                        Id = "batch_sorting",
                        Label = "Sort Order",
                        TabId = "options",
                        Fields = batchFields.Where(field => field.Key.StartsWith("sort_", StringComparison.OrdinalIgnoreCase)).ToList(),
                    },
                }.Where(group => group.Fields.Count > 0).ToList(),
            _ => [],
        };
    }

    protected IReadOnlyList<MediaEditorFieldDefinition> GetParentPositionFields() =>
        (EditorMediaType, ActiveScope?.ScopeId) switch
        {
            ("TV", "episode") =>
            [
                new() { Key = "show_name", Label = "Parent show", Placeholder = "Search shows" },
                new() { Key = "season_number", Label = "Season", Placeholder = "Season number" },
                new() { Key = "episode_number", Label = "Episode", Placeholder = "Episode number" },
            ],
            ("Music", "track") =>
            [
                new() { Key = "album", Label = "Parent album", Placeholder = "Search albums" },
                new() { Key = "disc_number", Label = "Disc", Placeholder = "Disc number" },
                new() { Key = "track_number", Label = "Track", Placeholder = "Track number" },
            ],
            _ => [],
        };

    protected static string GetParentPositionHelp(string key) => key switch
    {
        "show_name" or "album" => "Changing the parent is a structural move and requires confirmation.",
        _ => "Controls this item's position inside its parent.",
    };

    protected IEnumerable<MediaEditorFieldGroup> GetAdditionalOptionsGroups() =>
        GetGroupsForTab("options")
            .Select(group => new MediaEditorFieldGroup
            {
                Id = group.Id,
                Label = group.Label,
                TabId = group.TabId,
                Fields = group.Fields
                    .Where(field => !IsLocalOptionsField(field.Key))
                    .ToList(),
            })
            .Where(group => group.Fields.Count > 0)
            .ToList();

    private static bool IsLocalOptionsField(string key) =>
        key is "genre" or "custom_tags" or "rating";

    protected string GetPlaceholder(MediaEditorFieldDefinition field) =>
        IsBatchMode ? "Leave unchanged" : (field.Placeholder ?? field.Label);

    protected static int GetFieldLineCount(MediaEditorFieldDefinition field)
    {
        if (!string.Equals(field.InputKind, "textarea", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return string.Equals(field.Key, "description", StringComparison.OrdinalIgnoreCase) ? 7 : 4;
    }

    protected string GetEditableValue(string key)
    {
        var scopedKey = BuildScopedFieldKey(key);

        if (_editedValues.TryGetValue(scopedKey, out var edited) && !_clearedInlineOverrideKeys.Contains(scopedKey))
        {
            return edited;
        }

        if (IsBatchMode)
        {
            return string.Empty;
        }

        if (_profilePreferencesByWork.TryGetValue(CurrentEntityId, out var preferences))
        {
            if (string.Equals(key, "custom_tags", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(", ", preferences.LocalTags);
            }
        }

        if (!_clearedInlineOverrideKeys.Contains(scopedKey)
            && _editorContext?.DisplayOverrides.TryGetValue(key, out var overrideValue) == true)
        {
            return overrideValue;
        }

        var values = MediaEditorSchemaCatalog.BuildValueMap(_detail, _canonicalValues);
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    protected void OnFieldInput(string key, string? value)
    {
        var normalized = IsMultilineField(key)
            ? NormalizeMultilineInput(key, value)
            : (value ?? string.Empty).Trim();
        var baseline = IsBatchMode ? string.Empty : GetBaselineValue(key);
        var scopedKey = BuildScopedFieldKey(key);
        _clearedInlineOverrideKeys.Remove(scopedKey);

        if (string.Equals(normalized, baseline, StringComparison.Ordinal))
        {
            _editedValues.Remove(scopedKey);
        }
        else if (string.IsNullOrWhiteSpace(normalized))
        {
            if (ShouldSaveAsDisplayOverride(scopedKey, key) && !string.IsNullOrWhiteSpace(baseline))
            {
                _editedValues[scopedKey] = string.Empty;
            }
            else
            {
                _editedValues.Remove(scopedKey);
            }
        }
        else
        {
            _editedValues[scopedKey] = normalized;
        }
    }

    protected Task SaveAsync() => SaveAsyncCore(applyMembershipMove: false);

    protected Task ConfirmMembershipMoveAsync() => SaveAsyncCore(applyMembershipMove: true);

    private async Task SaveAsyncCore(bool applyMembershipMove)
    {
        if (IsSharedEntityMode)
        {
            if (_sharedEntityWorkspace is null || !IsDirty)
                return;
            _saving = true;
            try
            {
                if (await _sharedEntityWorkspace.SaveAsync())
                    _hasCommittedChanges = true;
            }
            finally
            {
                _saving = false;
                StateHasChanged();
            }
            return;
        }

        if (!IsDirty && (!applyMembershipMove || _pendingMembershipPreview is null))
        {
            if (Request.Mode == SharedMediaEditorMode.Review)
            {
                await ResolveReviewWithoutChangesAsync();
                return;
            }

            await CloseEditorAsync(applied: false);
            return;
        }

        _saving = true;
        _saveError = null;
        _saveConflict = false;
        StateHasChanged();

        try
        {
            if (IsBatchMode)
            {
                if (_editedValues.Count == 0)
                {
                    await CloseEditorAsync(applied: false);
                    return;
                }

                var result = await ApiClient.BatchEditAsync(Request.EntityIds, new Dictionary<string, string>(_editedValues, StringComparer.OrdinalIgnoreCase));
                if (result is null)
                {
                    Snackbar.Add("Batch edit failed.", Severity.Error);
                    return;
                }

                Snackbar.Add($"Updated {result.UpdatedCount} item(s).", Severity.Success);
                await CloseEditorAsync(applied: true);
                return;
            }

            if (!applyMembershipMove)
            {
                var membershipPreview = await PreviewMembershipChangeAsync();
                if (membershipPreview is not null
                    && !string.Equals(membershipPreview.Action, "none", StringComparison.OrdinalIgnoreCase))
                {
                    if (!membershipPreview.CanApply)
                    {
                        Snackbar.Add(membershipPreview.ConflictMessage ?? membershipPreview.Message, Severity.Error);
                        return;
                    }

                    _pendingMembershipPreview = membershipPreview;
                    return;
                }
            }

            var savedAnything = false;

            if (applyMembershipMove && _pendingMembershipPreview is not null)
            {
                var membershipResult = await ApiClient.ApplyMediaEditorMembershipAsync(CurrentEntityId, BuildMembershipPreviewRequest());
                if (membershipResult is null || !membershipResult.CanApply)
                {
                    Snackbar.Add(membershipResult?.ConflictMessage ?? membershipResult?.Message ?? "Membership update failed.", Severity.Error);
                    return;
                }

                savedAnything = true;
            }

            var fieldChangesByScope = _editedValues
                .Select(entry => (RawKey: entry.Key, Key: ParseScopedKey(entry.Key), Value: entry.Value))
                .Where(entry => entry.Key.EntityId != Guid.Empty)
                .GroupBy(entry => new ScopedEditorKey(entry.Key.EntityId, entry.Key.ScopeId), ScopedEditorKeyComparer.Instance)
                .ToList();

            var profileSavedEntities = new HashSet<Guid>();
            if (Request.Mode == SharedMediaEditorMode.Normal
                && Request.ActiveProfileId.HasValue
                && fieldChangesByScope.Any(group => group.Key.EntityId == CurrentEntityId))
            {
                var currentEntries = fieldChangesByScope
                    .Where(group => group.Key.EntityId == CurrentEntityId)
                    .SelectMany(group => group)
                    .ToList();
                var currentOverrideFields = currentEntries
                    .Where(entry => ShouldSaveAsDisplayOverride(entry.RawKey, entry.Key.Key))
                    .ToDictionary(entry => entry.Key.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                var currentPreferenceFields = currentEntries
                    .Where(entry => !ShouldSaveAsDisplayOverride(entry.RawKey, entry.Key.Key))
                    .ToDictionary(entry => entry.Key.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

                if (!await SaveProfileEditorPreferencesAsync(CurrentEntityId, currentOverrideFields, currentPreferenceFields))
                {
                    return;
                }

                savedAnything = true;
                profileSavedEntities.Add(CurrentEntityId);
            }

            foreach (var scopeGroup in fieldChangesByScope)
            {
                if (profileSavedEntities.Contains(scopeGroup.Key.EntityId))
                {
                    continue;
                }

                var overrideFields = scopeGroup
                    .Where(entry => ShouldSaveAsDisplayOverride(entry.RawKey, entry.Key.Key))
                    .ToDictionary(entry => entry.Key.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                var preferenceFields = scopeGroup
                    .Where(entry => !ShouldSaveAsDisplayOverride(entry.RawKey, entry.Key.Key))
                    .ToDictionary(entry => entry.Key.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

                if (Request.Mode == SharedMediaEditorMode.Normal && Request.ActiveProfileId.HasValue)
                {
                    if (!await SaveProfileEditorPreferencesAsync(scopeGroup.Key.EntityId, overrideFields, preferenceFields))
                    {
                        return;
                    }

                    savedAnything = true;
                    profileSavedEntities.Add(scopeGroup.Key.EntityId);
                    continue;
                }

                if (preferenceFields.Count > 0)
                {
                    var saved = await ApiClient.SaveItemPreferencesAsync(scopeGroup.Key.EntityId, preferenceFields);
                    if (!saved)
                    {
                        Snackbar.Add($"Preference save failed for {scopeGroup.Key.ScopeId}.", Severity.Error);
                        return;
                    }

                    savedAnything = true;
                }

                if (overrideFields.Count > 0)
                {
                    var savedOverrides = await ApiClient.SaveItemDisplayOverridesAsync(scopeGroup.Key.EntityId, overrideFields);
                    if (!savedOverrides)
                    {
                        Snackbar.Add($"Display override save failed for {scopeGroup.Key.ScopeId}.", Severity.Error);
                        return;
                    }

                    savedAnything = true;
                }
            }

            if (_audiobookChapterResetKeys.Count > 0 || _audiobookChapterEdits.Count > 0)
            {
                foreach (var key in _audiobookChapterResetKeys)
                {
                    var chapter = AudiobookChapters.FirstOrDefault(item => BuildAudiobookChapterKey(item) == key);
                    if (chapter is null || !Guid.TryParse(chapter.AssetId, out var assetId) || !chapter.ChapterIndex.HasValue)
                    {
                        continue;
                    }

                    if (!await ApiClient.DeleteAudiobookChapterTitleOverrideAsync(CurrentEntityId, assetId, chapter.ChapterIndex.Value))
                    {
                        _saveError = $"The title for chapter {chapter.ChapterIndex.Value + 1} could not be restored.";
                        await FocusSaveErrorAsync();
                        return;
                    }
                }

                foreach (var chapterEdit in _audiobookChapterEdits.Values)
                {
                    var saved = await ApiClient.UpsertAudiobookChapterTitleOverrideAsync(
                        CurrentEntityId,
                        new UpsertAudiobookChapterTitleOverrideRequestDto
                        {
                            ProfileId = Request.ActiveProfileId,
                            AssetId = chapterEdit.AssetId,
                            ChapterIndex = chapterEdit.ChapterIndex,
                            Title = chapterEdit.Title,
                            TitleSource = chapterEdit.TitleSource,
                        });
                    if (saved is null)
                    {
                        _saveError = $"The title for chapter {chapterEdit.ChapterIndex + 1} could not be saved.";
                        await FocusSaveErrorAsync();
                        return;
                    }
                }

                savedAnything = true;
            }

            foreach (var pendingArtwork in _pendingArtworkFiles.ToList())
            {
                var parsedKey = ParseScopedKey(pendingArtwork.Key);
                if (parsedKey.EntityId == Guid.Empty || string.IsNullOrWhiteSpace(parsedKey.ScopeId))
                {
                    continue;
                }

                await using var stream = pendingArtwork.Value.OpenReadStream(10 * 1024 * 1024);
                var uploaded = await ApiClient.UploadScopeArtworkVariantAsync(
                    parsedKey.EntityId,
                    parsedKey.ScopeId,
                    parsedKey.Key,
                    stream,
                    pendingArtwork.Value.Name);

                if (!uploaded)
                {
                    Snackbar.Add($"{parsedKey.Key} upload failed.", Severity.Error);
                    return;
                }

                savedAnything = true;
            }

            if (!savedAnything)
            {
                if (Request.Mode == SharedMediaEditorMode.Review)
                {
                    await ResolveReviewWithoutChangesAsync();
                    return;
                }

                if (await CompletePendingTargetSwitchAsync())
                {
                    return;
                }

                await CloseEditorAsync(applied: false);
                return;
            }

            if (Request.Mode == SharedMediaEditorMode.Review)
            {
                if (!await ResolveReviewCoreAsync())
                {
                    return;
                }

                Snackbar.Add(applyMembershipMove && _pendingMembershipPreview is not null
                    ? "Changes saved, membership updated, and review resolved."
                    : "Changes saved and review resolved.", Severity.Success);
                if (await CompletePendingTargetSwitchAsync())
                {
                    return;
                }

                await CloseEditorAsync(applied: true);
                return;
            }

            Snackbar.Add(applyMembershipMove && _pendingMembershipPreview is not null
                ? "Changes saved and membership updated."
                : "Changes saved.", Severity.Success);
            if (await CompletePendingTargetSwitchAsync())
            {
                return;
            }

            await CloseEditorAsync(applied: true);
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<bool> SaveProfileEditorPreferencesAsync(
        Guid entityId,
        Dictionary<string, string> displayOverrides,
        Dictionary<string, string> preferenceFields)
    {
        if (Request.ActiveProfileId is not { } profileId)
        {
            return false;
        }

        if (!_profilePreferencesByWork.TryGetValue(entityId, out var baseline))
        {
            baseline = await ApiClient.GetItemEditorPreferencesAsync(entityId, profileId);
            if (baseline is null)
            {
                _saveError = ApiClient.LastError ?? "Profile preferences could not be loaded before saving.";
                await FocusSaveErrorAsync();
                return false;
            }

            _profilePreferencesByWork[entityId] = baseline;
        }

        var localTags = preferenceFields.TryGetValue("custom_tags", out var tagValue)
            ? ParseLocalTags(tagValue)
            : baseline.LocalTags;
        var result = await ApiClient.SaveItemEditorPreferencesAsync(
            entityId,
            profileId,
            new MediaEngine.Contracts.Items.ItemEditorPreferencesRequest
            {
                ExpectedRevision = baseline.Revision,
                DisplayOverrides = displayOverrides,
                LocalTags = localTags.ToList(),
            });

        if (result.Preferences is not null)
        {
            _profilePreferencesByWork[entityId] = result.Preferences;
            if (_editorContext is not null && entityId == CurrentEntityId)
            {
                foreach (var (key, value) in result.Preferences.DisplayOverrides)
                {
                    _editorContext.DisplayOverrides[key] = value;
                }
            }
        }

        if (result.Conflict)
        {
            _saveConflict = true;
            _saveError = result.Error ?? "The item changed while you were editing. Review your values and save again, or reload the latest values.";
            await FocusSaveErrorAsync();
            return false;
        }

        if (!result.Saved)
        {
            _saveError = result.Error ?? ApiClient.LastError ?? "Changes could not be saved.";
            await FocusSaveErrorAsync();
            return false;
        }

        return true;
    }

    private async Task FocusSaveErrorAsync()
    {
        await InvokeAsync(StateHasChanged);
        await JS.InvokeVoidAsync("tuvimaEditorFocus", ".sme-save-error");
    }

    private static IReadOnlyList<string> ParseLocalTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    protected async Task ResolveReviewWithoutChangesAsync()
    {
        if (Request.Mode != SharedMediaEditorMode.Review)
        {
            return;
        }

        _saving = true;
        StateHasChanged();

        try
        {
            if (!await ResolveReviewCoreAsync())
            {
                return;
            }

            Snackbar.Add("Review resolved.", Severity.Success);
            await CloseEditorAsync(applied: true);
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    protected async Task HandleReviewPrimaryActionAsync()
    {
        switch (EffectiveIdentityIntent)
        {
            case MediaEditorIdentityIntent.FixRetailMatch:
            case MediaEditorIdentityIntent.ConfirmRetailMatch:
                _activeMatchSearchMode = "retail";
                break;

            case MediaEditorIdentityIntent.FixWikidataMatch:
            case MediaEditorIdentityIntent.ConfirmWikidataMatch:
                _activeMatchSearchMode = "wikidata";
                break;

            default:
                await ResolveReviewWithoutChangesAsync();
                return;
        }

        StateHasChanged();
        await JS.InvokeVoidAsync("tuvimaEditorFocus", ".sme-match-search-panel .sme-text-input input");
    }

    private async Task<bool> ResolveReviewCoreAsync(string? providerName = null, string? providerItemId = null, string? selectedQid = null)
    {
        if (Request.ReviewItemId is not { } reviewItemId)
        {
            Snackbar.Add("Review was not resolved because this editor was not opened from a review item.", Severity.Error);
            return false;
        }

        var overrides = BuildReviewFieldOverrides();
        var resolved = await Orchestrator.ResolveReviewAsync(
            reviewItemId,
            new ReviewResolveRequestDto
            {
                SelectedQid = selectedQid,
                ProviderName = providerName,
                ProviderItemId = providerItemId,
                FieldOverrides = overrides.Count == 0 ? null : overrides,
            });

        if (!resolved)
        {
            Snackbar.Add("Review was not resolved because changes could not be saved.", Severity.Error);
            return false;
        }

        _editedValues.Clear();
        _pendingArtworkFiles.Clear();
        _pendingArtworkPreviewUrls.Clear();
        _audiobookChapterEdits.Clear();
        _audiobookChapterResetKeys.Clear();
        _pendingMembershipPreview = null;
        return true;
    }

    private List<FieldOverrideDto> BuildReviewFieldOverrides() =>
        _editedValues
            .Select(entry => (Key: ParseScopedKey(entry.Key).Key, entry.Value))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(entry => new FieldOverrideDto { Key = entry.Key, Value = entry.Value })
            .ToList();

    protected async Task HandleClose()
    {
        CancelAllMatchSearches();
        if (IsDirty)
        {
            _confirmDiscard = true;
            return;
        }

        if (_hasCommittedChanges)
        {
            await CloseEditorAsync(applied: true);
        }
        else
        {
            await CloseEditorAsync(applied: false);
        }
    }

    protected void ResetEditorChanges()
    {
        if (IsSharedEntityMode)
        {
            _sharedEntityWorkspace?.ResetEditorChanges();
            _sharedEntityDirty = false;
            StateHasChanged();
            return;
        }

        _editedValues.Clear();
        _pendingArtworkFiles.Clear();
        _pendingArtworkPreviewUrls.Clear();
        _audiobookChapterEdits.Clear();
        _audiobookChapterResetKeys.Clear();
        _pendingMembershipPreview = null;
        _confirmDiscard = false;
        _saveError = null;
        _saveConflict = false;
    }

    protected async Task ReloadAfterSaveConflictAsync()
    {
        ResetEditorChanges();
        await LoadSingleItemAsync(CurrentEntityId, resetEditorState: true);
    }

    protected async Task DiscardAndClose()
    {
        if (_hasCommittedChanges)
        {
            await CloseEditorAsync(applied: true);
        }
        else
        {
            await CloseEditorAsync(applied: false);
        }
    }

    private async Task CloseEditorAsync(bool applied)
    {
        if (Inline)
        {
            await Closed.InvokeAsync(applied);
            return;
        }

        if (applied)
        {
            MudDialog?.Close(DialogResult.Ok(true));
        }
        else
        {
            MudDialog?.Cancel();
        }
    }

    protected async Task ConfirmNavigationWithUnsavedChanges(LocationChangingContext context)
    {
        if (IsDirty && !await JS.InvokeAsync<bool>("confirm", "You have unsaved changes. Leave without saving?"))
        {
            context.PreventNavigation();
        }
    }

    protected async Task HandleArtworkSelectedAsync(string assetType, InputFileChangeEventArgs args)
    {
        var file = args.File;
        if (file is null)
        {
            ClearArtworkSelection(assetType);
            return;
        }

        var scope = ArtworkScope;
        if (scope is null)
        {
            return;
        }

        var scopedKey = BuildScopedArtworkKey(scope.ScopeId, assetType);

        _pendingArtworkFiles[scopedKey] = file;
        _artworkUploadErrors.Remove(scopedKey);
        await using var stream = file.OpenReadStream(10 * 1024 * 1024);
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType;
        _pendingArtworkPreviewUrls[scopedKey] = $"data:{contentType};base64,{Convert.ToBase64String(ms.ToArray())}";
        _dragTargetArtworkType = null;
        _selectedArtworkAssetType = assetType;
        _artworkAddMenuAssetType = null;
        _showArtworkUrlInput = false;
        _artworkUrlInput = string.Empty;
        NormalizeArtworkSelection();
        await UploadPendingArtworkAsync(scope, assetType, file, scopedKey);
    }

    protected void ClearArtworkSelection(string assetType)
    {
        var scopedKey = BuildScopedArtworkKey(ArtworkScope?.ScopeId, assetType);
        _pendingArtworkFiles.Remove(scopedKey);
        _pendingArtworkPreviewUrls.Remove(scopedKey);
        _artworkUploadErrors.Remove(scopedKey);
        if (string.Equals(_dragTargetArtworkType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            _dragTargetArtworkType = null;
        }

        if (_zoomArtworkVariant is { IsPending: true } && string.Equals(_zoomArtworkVariant.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            CloseArtworkZoom();
        }

        NormalizeArtworkSelection();
    }

    protected bool HasPendingArtwork(string assetType) =>
        _pendingArtworkFiles.ContainsKey(BuildScopedArtworkKey(ArtworkScope?.ScopeId, assetType));

    protected string? GetPendingArtworkFileName(string assetType) =>
        _pendingArtworkFiles.TryGetValue(BuildScopedArtworkKey(ArtworkScope?.ScopeId, assetType), out var file) ? file.Name : null;

    protected string? GetArtworkUploadError(string assetType) =>
        _artworkUploadErrors.TryGetValue(BuildScopedArtworkKey(ArtworkScope?.ScopeId, assetType), out var error) ? error : null;

    protected bool HasArtworkUploadError(string assetType) =>
        !string.IsNullOrWhiteSpace(GetArtworkUploadError(assetType));

    protected string GetArtworkGalleryTitle(ArtworkSlotDefinition slot) =>
        slot.AssetType switch
        {
            "Background" => "Background Gallery",
            "Logo" => "Logo Gallery",
            _ => $"{slot.Label} Gallery",
        };

    protected string GetArtworkCardSource(ArtworkVariantDisplayItem item) =>
        string.IsNullOrWhiteSpace(item.ProviderName)
            ? item.Origin
            : item.ProviderName!;

    protected static string GetArtworkImageSize(ArtworkVariantDisplayItem? item) =>
        item?.WidthPx is > 0 && item.HeightPx is > 0
            ? $"{item.WidthPx.Value:N0} x {item.HeightPx.Value:N0} px"
            : "Not reported";

    protected static string GetArtworkPreviewClass(
        ArtworkSlotDefinition slot,
        ArtworkVariantDisplayItem? item)
    {
        if (!string.Equals(slot.AssetType, "CoverArt", StringComparison.OrdinalIgnoreCase)
            || item?.WidthPx is not > 0
            || item.HeightPx is not > 0)
        {
            return slot.PreviewClass;
        }

        var ratio = item.WidthPx.Value / (double)item.HeightPx.Value;
        return ratio switch
        {
            >= 1.2 => "landscape",
            >= 0.85 => "square",
            _ => "portrait",
        };
    }

    protected static string GetArtworkGuidance(ArtworkSlotDefinition slot) =>
        slot.AssetType switch
        {
            "CoverArt" => "Use the image's natural shape: portrait for books and posters, square for albums, or another proportion when the item calls for it. Prefer at least 1200 px on the longest edge and keep key subjects away from the crop boundary.",
            "Background" => "Use a wide, text-free image with room for foreground copy. A 16:9 source at 1920 x 1080 px or larger works best.",
            "Logo" => "Use a transparent PNG containing only the title treatment or mark. Leave clear padding around the artwork and aim for at least 1200 px wide.",
            "SeasonPoster" => "Use portrait season art at 1200 x 1800 px or larger. Keep season identity legible at card size.",
            "SeasonThumb" => "Use a wide season image at 1920 x 1080 px or larger and avoid important details near the edges.",
            "EpisodeStill" => "Use a clean 16:9 episode still at 1280 x 720 px or larger without subtitles or playback controls.",
            _ => "Use a high-resolution JPG or PNG and keep important content away from the edges.",
        };

    protected string GetArtworkAcceptedTypes(string assetType) =>
        string.Equals(assetType, "Logo", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/png,image/jpeg";

    protected void SetArtworkDragTarget(string assetType) => _dragTargetArtworkType = assetType;

    protected void ClearArtworkDragTarget(string assetType)
    {
        if (string.Equals(_dragTargetArtworkType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            _dragTargetArtworkType = null;
        }
    }

    protected bool IsArtworkDragTarget(string assetType) =>
        string.Equals(_dragTargetArtworkType, assetType, StringComparison.OrdinalIgnoreCase);

    private ArtworkEditorDto? GetCurrentArtworkEditor()
    {
        if (ArtworkScope is null)
        {
            return _artwork;
        }

        return _artworkStates.TryGetValue(BuildScopeStateKey(ArtworkScope.FieldEntityId, ArtworkScope.ScopeId), out var artwork)
            ? artwork
            : _artwork;
    }

    protected IReadOnlyList<ArtworkVariantDto> GetArtworkVariants(string assetType) =>
        GetCurrentArtworkEditor()?.Slots.FirstOrDefault(slot =>
            string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase))?.Variants
        ?? [];

    protected static string? GetArtworkThumbnailUrl(ArtworkVariantDisplayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ImageUrl)
            || !item.ImageUrl.StartsWith("/stream/artwork/", StringComparison.OrdinalIgnoreCase))
        {
            return item.ImageUrl;
        }

        return $"{item.ImageUrl.Split('?')[0]}?size=m";
    }

    protected ArtworkVariantDto? GetPreferredArtworkVariant(string assetType) =>
        GetArtworkVariants(assetType)
            .OrderByDescending(variant => variant.IsPreferred)
            .ThenByDescending(variant => variant.CreatedAt)
            .FirstOrDefault();

    protected IReadOnlyList<ArtworkVariantDisplayItem> GetArtworkGalleryItems(string assetType)
    {
        var items = new List<ArtworkVariantDisplayItem>();

        if (_pendingArtworkPreviewUrls.TryGetValue(BuildScopedArtworkKey(ArtworkScope?.ScopeId, assetType), out var pendingPreview))
        {
            items.Add(new ArtworkVariantDisplayItem(
                BuildPendingArtworkKey(assetType),
                Guid.Empty,
                assetType,
                pendingPreview,
                IsPreferred: false,
                IsPending: true,
                CanDelete: false,
                Origin: "Pending",
                ProviderName: null,
                CreatedAt: null,
                WidthPx: null,
                HeightPx: null));
        }

        items.AddRange(GetArtworkVariants(assetType).Select(variant => new ArtworkVariantDisplayItem(
            BuildVariantKey(variant.Id),
            variant.Id,
            assetType,
            variant.ImageUrl,
            variant.IsPreferred,
            IsPending: false,
            CanDelete: variant.CanDelete,
            Origin: variant.Origin,
            ProviderName: variant.ProviderName,
            CreatedAt: variant.CreatedAt,
            WidthPx: variant.WidthPx,
            HeightPx: variant.HeightPx)));

        return items;
    }

    protected IReadOnlyList<ArtworkVariantDisplayItem> GetArtworkRowItems(string assetType) =>
        GetArtworkGalleryItems(assetType)
            .OrderByDescending(item => item.IsPending)
            .ThenBy(item => item.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

    protected ArtworkVariantDisplayItem? GetLeadArtworkVariant(string assetType)
    {
        var items = GetArtworkGalleryItems(assetType);
        if (items.Count == 0)
        {
            return null;
        }

        return items.FirstOrDefault(item => string.Equals(item.Key, _focusedArtworkVariantKey, StringComparison.Ordinal))
               ?? items.FirstOrDefault(item => item.IsPending)
               ?? items.FirstOrDefault(item => item.IsPreferred)
               ?? items.FirstOrDefault();
    }

    protected void SelectArtworkSlot(string assetType) => ApplyArtworkSlotSelection(assetType);

    protected string GetSectionNavClass(string tabId)
    {
        if (!string.Equals(tabId, _activeTab, StringComparison.OrdinalIgnoreCase))
        {
            return "app-selection-nav__item sme-section-nav__item";
        }

        return string.Equals(tabId, "artwork", StringComparison.OrdinalIgnoreCase)
            ? "app-selection-nav__item sme-section-nav__item is-active is-group-open"
            : "app-selection-nav__item sme-section-nav__item is-active";
    }

    protected Task OnTabChanged(int index)
    {
        if (index < 0 || index >= Tabs.Count)
        {
            return Task.CompletedTask;
        }

        return SelectTabInternalAsync(Tabs[index].Id);
    }

    protected Task OpenArtworkTabAsync()
    {
        var artworkTabIndex = Tabs
            .Select((tab, index) => (tab, index))
            .Where(item => string.Equals(item.tab.Id, "artwork", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

        return OnTabChanged(artworkTabIndex);
    }

    protected Task OpenDetailsTabAsync() => SelectTabInternalAsync("details");

    private void ApplyArtworkSlotSelection(string assetType, bool clearTransientUi = true)
    {
        _selectedArtworkAssetType = assetType;
        if (clearTransientUi)
        {
            _artworkAddMenuAssetType = null;
            _artworkUrlInput = string.Empty;
            _showArtworkUrlInput = false;
            _deleteConfirmArtworkVariantKey = null;
        }

        _focusedArtworkVariantKey = GetArtworkGalleryItems(assetType).FirstOrDefault(item => item.IsPending)?.Key
                                    ?? GetArtworkGalleryItems(assetType).FirstOrDefault(item => item.IsPreferred)?.Key
                                    ?? GetArtworkGalleryItems(assetType).FirstOrDefault()?.Key;
    }

    protected void FocusArtworkVariant(string variantKey) => _focusedArtworkVariantKey = variantKey;

    protected bool IsFocusedArtworkVariant(string variantKey) =>
        string.Equals(_focusedArtworkVariantKey, variantKey, StringComparison.Ordinal);

    protected bool IsArtworkDeleteConfirming(ArtworkVariantDisplayItem item) =>
        string.Equals(_deleteConfirmArtworkVariantKey, item.Key, StringComparison.Ordinal);

    protected async Task DeleteArtworkVariantWithConfirmationAsync(ArtworkVariantDisplayItem item)
    {
        if (item.VariantId == Guid.Empty)
        {
            return;
        }

        FocusArtworkVariant(item.Key);
        if (!IsArtworkDeleteConfirming(item))
        {
            _deleteConfirmArtworkVariantKey = item.Key;
            StateHasChanged();
            return;
        }

        await ConfirmArtworkDeleteAsync(item);
    }

    protected void CancelArtworkDelete(ArtworkVariantDisplayItem item)
    {
        if (IsArtworkDeleteConfirming(item))
        {
            _deleteConfirmArtworkVariantKey = null;
        }
    }

    protected async Task ConfirmArtworkDeleteAsync(ArtworkVariantDisplayItem item)
    {
        if (item.VariantId == Guid.Empty)
        {
            return;
        }

        _deleteConfirmArtworkVariantKey = null;
        await DeleteArtworkVariantAsync(item.VariantId);
    }

    private ArtworkVariantDisplayItem? GetArtworkActionTarget(string assetType)
    {
        var items = GetArtworkRowItems(assetType);
        return items.FirstOrDefault(item => string.Equals(item.Key, _focusedArtworkVariantKey, StringComparison.Ordinal))
               ?? items.FirstOrDefault(item => item.IsPreferred)
               ?? items.FirstOrDefault();
    }

    protected async Task SetPreferredArtworkVariantAsync(Guid variantId)
    {
        if (variantId == Guid.Empty)
        {
            return;
        }

        var ok = await ApiClient.SetPreferredArtworkAsync(variantId);
        if (!ok)
        {
            Snackbar.Add("Could not change the preferred artwork.", Severity.Error);
            return;
        }

        await RefreshArtworkStateAsync(notifyParent: true);
    }

    protected async Task RetryArtworkUploadAsync(string assetType)
    {
        var scope = ArtworkScope;
        if (scope is null)
        {
            return;
        }

        var scopedKey = BuildScopedArtworkKey(scope.ScopeId, assetType);
        if (!_pendingArtworkFiles.TryGetValue(scopedKey, out var file))
        {
            return;
        }

        await UploadPendingArtworkAsync(scope, assetType, file, scopedKey);
    }

    protected async Task ApplyArtworkUrlAsync()
    {
        var scope = ArtworkScope;
        var slot = SelectedArtworkSlot;
        if (scope is null || slot is null || string.IsNullOrWhiteSpace(_artworkUrlInput))
        {
            return;
        }

        _artworkUrlSubmitting = true;
        _artworkUploadErrors.Remove(BuildScopedArtworkKey(scope.ScopeId, slot.AssetType));
        StateHasChanged();

        try
        {
            var ok = await ApiClient.UploadScopeArtworkFromUrlAsync(
                EditorContextEntityId,
                scope.ScopeId,
                slot.AssetType,
                _artworkUrlInput.Trim());

            if (!ok)
            {
                var error = ApiClient.LastError ?? "Remote artwork download failed.";
                _artworkUploadErrors[BuildScopedArtworkKey(scope.ScopeId, slot.AssetType)] = error;
                Snackbar.Add(error, Severity.Error);
                return;
            }

            _artworkUrlInput = string.Empty;
            _artworkAddMenuAssetType = null;
            _showArtworkUrlInput = false;
            await RefreshArtworkStateAsync(scope.ScopeId, notifyParent: true);
            Snackbar.Add($"{slot.Label} updated.", Severity.Success);
        }
        finally
        {
            _artworkUrlSubmitting = false;
            StateHasChanged();
        }
    }

    protected async Task RefreshProviderArtworkAsync()
    {
        var scope = ArtworkScope;
        if (scope is null || !CanRefreshProviderArtwork(scope))
        {
            return;
        }

        _providerArtworkRefreshing = true;
        StateHasChanged();

        try
        {
            var result = await ApiClient.RefreshScopeProviderArtworkAsync(EditorContextEntityId, scope.ScopeId);
            if (result is null)
            {
                Snackbar.Add(ApiClient.LastError ?? "Provider artwork refresh failed.", Severity.Error);
                return;
            }

            await RefreshArtworkStateAsync(scope.ScopeId, notifyParent: true);
            Snackbar.Add(BuildProviderArtworkRefreshMessage(result), GetProviderArtworkRefreshSeverity(result));
        }
        finally
        {
            _providerArtworkRefreshing = false;
            StateHasChanged();
        }
    }

    protected bool CanRefreshProviderArtwork(MediaEditorScopeDto? scope) =>
        IsSingleItem
        && scope?.CanEditArtwork == true
        && ((EditorMediaType, scope.ScopeId) is
            ("Movies", "item")
            or ("TV", "series")
            or ("TV", "season")
            or ("TV", "episode")
            or ("Music", "album")
            or ("Books", "item")
            or ("Audiobooks", "item")
            or ("Comics", "item"));

    private static Severity GetProviderArtworkRefreshSeverity(ProviderArtworkRefreshDto result) =>
        result.Success && result.DownloadedCount > 0
            ? Severity.Success
            : result.Success || string.Equals(result.Status, "NoImages", StringComparison.OrdinalIgnoreCase)
                ? Severity.Info
                : Severity.Warning;

    private static string BuildProviderArtworkRefreshMessage(ProviderArtworkRefreshDto result)
    {
        var provider = string.IsNullOrWhiteSpace(result.ProviderName) ? "Provider" : result.ProviderName;
        if (result.DownloadedCount > 0)
        {
            return $"{provider} added {result.DownloadedCount} artwork variant{(result.DownloadedCount == 1 ? string.Empty : "s")}.";
        }

        if (result.Success || string.Equals(result.Status, "NoImages", StringComparison.OrdinalIgnoreCase))
        {
            return $"{provider} checked; no new artwork was added.";
        }

        return $"{provider} skipped: {FormatProviderArtworkSkipReason(result.SkippedReason)}";
    }

    private static string FormatProviderArtworkSkipReason(string? reason) =>
        reason switch
        {
            "missing_bridge_id" => "provider bridge ID required.",
            "missing_api_key" => "TMDB is not configured.",
            "provider_no_result" => "provider returned no artwork.",
            "provider_empty_response" => "provider returned an empty response.",
            "missing_representative_asset" => "no owned file was found for this scope.",
            "unsupported_scope" => "this scope is not supported.",
            "missing_media_path" => "no media file path was available.",
            null or "" => "no compatible artwork was available.",
            _ => reason.Replace('_', ' '),
        };

    protected void ToggleArtworkUrlInput()
    {
        _showArtworkUrlInput = !_showArtworkUrlInput;
        if (!_showArtworkUrlInput)
        {
            _artworkUrlInput = string.Empty;
            _artworkAddMenuAssetType = null;
        }
        else
        {
            _artworkAddMenuAssetType = _selectedArtworkAssetType;
        }
    }

    private async Task UploadPendingArtworkAsync(MediaEditorScopeDto scope, string assetType, IBrowserFile file, string scopedKey)
    {
        _artworkApplyingKeys.Add(scopedKey);
        _artworkUploadErrors.Remove(scopedKey);
        StateHasChanged();

        try
        {
            await using var stream = file.OpenReadStream(10 * 1024 * 1024);
            var uploaded = await ApiClient.UploadScopeArtworkVariantAsync(
                EditorContextEntityId,
                scope.ScopeId,
                assetType,
                stream,
                file.Name);

            if (!uploaded)
            {
                var error = ApiClient.LastError ?? $"{assetType} upload failed.";
                _artworkUploadErrors[scopedKey] = error;
                Snackbar.Add(error, Severity.Error);
                return;
            }

            _pendingArtworkFiles.Remove(scopedKey);
            _pendingArtworkPreviewUrls.Remove(scopedKey);
            _artworkUploadErrors.Remove(scopedKey);
            await RefreshArtworkStateAsync(scope.ScopeId, notifyParent: true);
            Snackbar.Add($"{ResolveArtworkSlots(scope).FirstOrDefault(slot => string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase))?.Label ?? assetType} updated.", Severity.Success);
        }
        finally
        {
            _artworkApplyingKeys.Remove(scopedKey);
            StateHasChanged();
        }
    }

    protected async Task HandleArtworkVariantClickAsync(
        ArtworkSlotDefinition slot,
        ArtworkVariantDisplayItem item)
    {
        FocusArtworkVariant(item.Key);

        if (item.IsPending || item.VariantId == Guid.Empty || item.IsPreferred)
        {
            return;
        }

        await SetPreferredArtworkVariantAsync(item.VariantId);
    }

    protected async Task DeleteArtworkVariantAsync(Guid variantId)
    {
        if (variantId == Guid.Empty)
        {
            return;
        }

        var ok = await ApiClient.DeleteArtworkAsync(variantId);
        if (!ok)
        {
            Snackbar.Add("Could not delete the artwork variant.", Severity.Error);
            return;
        }

        _deleteConfirmArtworkVariantKey = null;
        if (string.Equals(_focusedArtworkVariantKey, BuildVariantKey(variantId), StringComparison.Ordinal))
        {
            _focusedArtworkVariantKey = null;
        }

        await RefreshArtworkStateAsync(notifyParent: true);
    }

    protected void OpenArtworkZoom(ArtworkSlotDefinition slot, ArtworkVariantDisplayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ImageUrl))
        {
            return;
        }

        _zoomArtworkSlot = slot;
        _zoomArtworkVariant = item;
    }

    protected void CloseArtworkZoom()
    {
        _zoomArtworkSlot = null;
        _zoomArtworkVariant = null;
    }

    protected void ShowPreviousArtworkVariant() => MoveArtworkZoom(-1);

    protected void ShowNextArtworkVariant() => MoveArtworkZoom(1);

    private void MoveArtworkZoom(int offset)
    {
        var items = GetZoomArtworkItems();
        var index = GetZoomArtworkIndex();
        var nextIndex = index + offset;
        if (nextIndex >= 0 && nextIndex < items.Count)
        {
            _zoomArtworkVariant = items[nextIndex];
            _focusedArtworkVariantKey = _zoomArtworkVariant.Key;
        }
    }

    private IReadOnlyList<ArtworkVariantDisplayItem> GetZoomArtworkItems() =>
        _zoomArtworkSlot is null ? [] : GetArtworkRowItems(_zoomArtworkSlot.AssetType);

    private int GetZoomArtworkIndex()
    {
        if (_zoomArtworkVariant is null)
        {
            return -1;
        }

        var items = GetZoomArtworkItems();
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Key, _zoomArtworkVariant.Key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    protected string BuildArtworkVariantHoverLabel(ArtworkSlotDefinition slot, ArtworkVariantDisplayItem item)
    {
        return BuildArtworkVariantSummary(slot, item);
    }

    protected string BuildArtworkVariantSummary(ArtworkSlotDefinition slot, ArtworkVariantDisplayItem item)
    {
        var parts = new List<string> { slot.Label };

        if (item.IsPreferred)
        {
            parts.Add("Primary");
        }
        else if (item.IsPending)
        {
            parts.Add("Pending");
        }
        else if (!string.IsNullOrWhiteSpace(item.Origin))
        {
            parts.Add(item.Origin);
        }

        if (!string.IsNullOrWhiteSpace(item.ProviderName))
        {
            parts.Add(item.ProviderName!);
        }

        if (item.CreatedAt is DateTimeOffset createdAt)
        {
            parts.Add(createdAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        }

        return string.Join(" | ", parts);
    }

    protected static string GetArtworkRemovalLabel(ArtworkVariantDisplayItem item) =>
        string.Equals(item.Origin, "Uploaded", StringComparison.OrdinalIgnoreCase)
            ? "Delete uploaded image"
            : "Remove from item";

    protected static string GetArtworkRemovalQuestion(ArtworkVariantDisplayItem item) =>
        string.Equals(item.Origin, "Uploaded", StringComparison.OrdinalIgnoreCase)
            ? "Delete this uploaded image from Tuvima Library?"
            : "Remove this provider artwork from the item? The shared cached file may remain available.";

    protected static string GetArtworkRemovalConfirmLabel(ArtworkVariantDisplayItem item) =>
        string.Equals(item.Origin, "Uploaded", StringComparison.OrdinalIgnoreCase)
            ? "Confirm delete uploaded image"
            : "Confirm remove from item";

    protected async Task SearchCanonicalAsync()
    {
        if (!IsSingleItem || IsFileScope || !SupportsCanonicalSearch)
        {
            return;
        }

        var isWikidataSearch = IsWikidataSearchMode;
        CancelMatchSearch(isWikidataSearch);
        var searchCts = new CancellationTokenSource();
        SetMatchSearchCts(isWikidataSearch, searchCts);
        SetMatchSearchPending(isWikidataSearch, true);
        StateHasChanged();

        try
        {
            var response = await ApiClient.SearchItemCanonicalAsync(
                CanonicalEndpointEntityId,
                new ItemCanonicalSearchRequestDto
                {
                    MediaType = EditorMediaType,
                    TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                    TargetFieldGroup = _canonicalTargetGroup,
                    DraftFields = BuildDraftFields(),
                    QueryOverride = string.IsNullOrWhiteSpace(GetMatchSearchQuery(isWikidataSearch))
                        ? null
                        : GetMatchSearchQuery(isWikidataSearch).Trim(),
                    SearchMode = GetCanonicalSearchMode(isWikidataSearch),
                }, searchCts.Token);

            if (searchCts.IsCancellationRequested)
            {
                return;
            }

            SetMatchSearchResponse(isWikidataSearch, response);
            ClearMatchSelection(isWikidataSearch);
            if (response is null)
            {
                Snackbar.Add(ApiClient.LastError ?? "Canonical search failed.", Severity.Error);
                return;
            }

            // Search ranking is advisory. The user must explicitly select a candidate
            // before either identity workflow exposes an applicable confirmation action.
        }
        finally
        {
            if (ReferenceEquals(GetMatchSearchCts(isWikidataSearch), searchCts))
            {
                SetMatchSearchCts(isWikidataSearch, null);
                SetMatchSearchPending(isWikidataSearch, false);
                searchCts.Dispose();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected string GetCanonicalSearchSubtitle()
    {
        var label = GetCanonicalTargetLabel(_canonicalTargetGroup);
        if (IsWikidataSearchMode)
        {
            return $"Search Wikidata directly for the correct {label.ToLowerInvariant()} identity.";
        }

        return Request.Mode == SharedMediaEditorMode.Review
            ? $"Resolve the blocking {label.ToLowerInvariant()} identity."
            : $"Find the canonical {label.ToLowerInvariant()} and apply only the fields you want.";
    }

    private static string NormalizeMultilineInput(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();

        return string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)
            ? NormalizeDescriptionParagraphs(normalized)
            : normalized;
    }

    private static string NormalizeDescriptionParagraphs(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join("\n\n",
            value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph)));
    }

    private bool IsWikidataIntent =>
        EffectiveIdentityIntent is MediaEditorIdentityIntent.FixWikidataMatch
            or MediaEditorIdentityIntent.ConfirmWikidataMatch
            or MediaEditorIdentityIntent.MarkWikidataMissing;

    private bool IsWikidataSearchMode =>
        string.Equals(_activeMatchSearchMode, "wikidata", StringComparison.OrdinalIgnoreCase);

    private static string GetCanonicalSearchMode(bool isWikidataSearch) =>
        isWikidataSearch ? "wikidata_only" : "retail_only";

    protected string GetCanonicalTargetLabel(string targetGroup) =>
        QuickSearchTargets.FirstOrDefault(target => string.Equals(target.Key, targetGroup, StringComparison.OrdinalIgnoreCase)).Label
        ?? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(targetGroup.Replace('_', ' '));

    protected string BuildRetailCandidateSubtitle(ItemCanonicalRetailCandidateDto candidate)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(candidate.Author))
        {
            parts.Add(candidate.Author);
        }
        else if (!string.IsNullOrWhiteSpace(candidate.Director))
        {
            parts.Add(candidate.Director);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            parts.Add(candidate.Year);
        }

        if (!string.IsNullOrWhiteSpace(candidate.ProviderName))
        {
            parts.Add(FormatProviderName(candidate.ProviderName, EditorMediaType));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "Provider candidate";
    }

    protected string BuildWikidataCandidateSubtitle(ItemCanonicalLinkedCandidateDto candidate)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(candidate.InstanceOf))
        {
            parts.Add(candidate.InstanceOf);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            parts.Add(candidate.Year);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Author))
        {
            parts.Add(candidate.Author);
        }
        else if (!string.IsNullOrWhiteSpace(candidate.Director))
        {
            parts.Add(candidate.Director);
        }

        parts.Add("Wikidata");

        return string.Join(" · ", parts);
    }

    protected IReadOnlyList<(string Label, string Value)> GetMatchStatusStripRows()
    {
        var summary = IdentityTargetSummary;
        var canonicalSummary = CanonicalIdentityTargetSummary;
        var provider = UsesParentRetailIdentityOnly ? null : GetRetailMatchDisplayName(summary);
        var providerId = UsesParentRetailIdentityOnly ? null : summary?.ProviderItemId;
        var qid = StringHelpers.FirstNonBlank(canonicalSummary?.WikidataQid, HasInheritedCanonicalIdentity ? null : _detail?.WikidataQid, HasInheritedCanonicalIdentity ? null : GetBaselineValue("wikidata_qid"))?.Trim();
        var universe = StringHelpers.FirstNonBlank(canonicalSummary?.UniverseName, _detail?.UniverseSummary?.UniverseName, GetBaselineValue("series"))?.Trim();
        var hasProvider = !string.IsNullOrWhiteSpace(provider) || !string.IsNullOrWhiteSpace(providerId);
        var hasQid = !string.IsNullOrWhiteSpace(qid);

        var rows = new List<(string Label, string Value)>
        {
            ("Status", GetMatchStatusLabel(summary, hasProvider, hasQid)),
            ("Retail Provider", !string.IsNullOrWhiteSpace(provider) ? provider : "Not linked"),
            ("Provider ID", !string.IsNullOrWhiteSpace(providerId) ? providerId! : "Not linked"),
            ("Canonical Identity", hasQid ? "Matched" : "Not matched"),
        };

        if (!string.IsNullOrWhiteSpace(universe))
        {
            rows.Add(("Universe / Series", universe!));
        }

        return rows;
    }

    protected MatchCardDisplay BuildCurrentRetailMatchCard()
    {
        var summary = IdentityTargetSummary;
        var provider = UsesParentRetailIdentityOnly ? null : GetRetailMatchDisplayName(summary);
        var providerId = UsesParentRetailIdentityOnly ? null : StringHelpers.FirstNonBlank(
            summary?.ProviderItemId,
            GetBaselineValue("tmdb_id"),
            GetBaselineValue("imdb_id"),
            GetBaselineValue("comicvine_id"),
            GetBaselineValue("musicbrainz_release_id"),
            GetBaselineValue("musicbrainz_release_group_id"),
            GetBaselineValue("musicbrainz_recording_id"),
            GetBaselineValue("musicbrainz_id"),
            GetBaselineValue("asin"),
            GetBaselineValue("isbn"))?.Trim();
        provider = UsesParentRetailIdentityOnly
            ? null
            : StringHelpers.FirstNonBlank(provider, InferProviderNameFromIdentifierFields())?.Trim() ?? provider;
        var title = StringHelpers.FirstNonBlank(CurrentTargetTitle, _detail?.Title, GetBaselineValue("title"), "Untitled item")?.Trim()!;
        var creator = StringHelpers.FirstNonBlank(_detail?.Author, _detail?.Director, GetBaselineValue("author"), GetBaselineValue("director"), GetBaselineValue("artist"), GetBaselineValue("narrator"))?.Trim();
        var year = StringHelpers.FirstNonBlank(_detail?.Year, GetBaselineValue("year"), GetBaselineValue("release_date"))?.Trim();
        var chips = new List<string>();
        var links = BuildRetailIdentityLinks(provider, providerId);

        if (!string.IsNullOrWhiteSpace(providerId) && links.Count == 0)
        {
            chips.Add(FormatRetailIdentifierChip(providerId!));
        }

        var hasProvider = !string.IsNullOrWhiteSpace(provider);

        return new MatchCardDisplay(
            Badge: UsesParentRetailIdentityOnly ? "Exact match needed" : hasProvider ? provider! : !string.IsNullOrWhiteSpace(providerId) ? "Retail identifier" : "No retail match",
            Title: title,
            Creator: creator,
            Year: year,
            CoverUrl: HasContextNavigator ? GetContextArtworkUrl(SelectedNavigatorNode) : CurrentCoverUrl,
            Chips: chips,
            Links: links,
            Note: UsesParentRetailIdentityOnly
                ? "The Series match supplies search context, but this episode does not yet have its own retail provider record."
                : hasProvider
                ? "This file is currently matched to the retail provider record shown below."
                : !string.IsNullOrWhiteSpace(providerId)
                    ? "This file has a local retail identifier, but no confirmed retail provider match yet."
                : "This file does not have a confirmed retail provider match yet.");
    }

    protected string CurrentProviderId =>
        UsesParentRetailIdentityOnly
            ? "Not linked"
            : StringHelpers.FirstNonBlank(
            IdentityTargetSummary?.ProviderItemId,
            GetBaselineValue("tmdb_id"),
            GetBaselineValue("imdb_id"),
            GetBaselineValue("comicvine_id"),
            GetBaselineValue("musicbrainz_release_id"),
            GetBaselineValue("musicbrainz_release_group_id"),
            GetBaselineValue("musicbrainz_recording_id"),
            GetBaselineValue("musicbrainz_id"),
            GetBaselineValue("asin"),
            GetBaselineValue("isbn"))?.Trim() ?? "Not linked";

    protected string CurrentCanonicalQid =>
        StringHelpers.FirstNonBlank(
            CanonicalIdentityTargetSummary?.WikidataQid,
            HasInheritedCanonicalIdentity ? null : _detail?.WikidataQid,
            HasInheritedCanonicalIdentity ? null : GetBaselineValue("wikidata_qid"))?.Trim() ?? "Not linked";

    protected bool HasCurrentRetailMatch =>
        !UsesParentRetailIdentityOnly
        && (!string.Equals(CurrentProviderId, "Not linked", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(GetRetailMatchDisplayName(IdentityTargetSummary)));

    protected bool HasCurrentCanonicalIdentity =>
        !string.Equals(CurrentCanonicalQid, "Not linked", StringComparison.OrdinalIgnoreCase);

    protected MatchCardDisplay BuildCurrentWikidataMatchCard()
    {
        var summary = CanonicalIdentityTargetSummary;
        var qid = StringHelpers.FirstNonBlank(summary?.WikidataQid, HasInheritedCanonicalIdentity ? null : _detail?.WikidataQid, HasInheritedCanonicalIdentity ? null : GetBaselineValue("wikidata_qid"))?.Trim();
        var title = StringHelpers.FirstNonBlank(CanonicalIdentityTargetScope?.DisplayTitle, CurrentTargetTitle, GetBaselineValue("title"), _detail?.Title, "No Wikidata identity")?.Trim()!;
        var type = StringHelpers.FirstNonBlank(GetBaselineValue("instance_of"), summary?.MatchLevel, _detail?.MediaType)?.Trim();
        var year = StringHelpers.FirstNonBlank(_detail?.Year, GetBaselineValue("year"), GetBaselineValue("release_date"))?.Trim();
        var chips = new List<string>();
        var links = BuildWikidataIdentityLinks(qid);

        var providerId = summary?.ProviderItemId;
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            chips.Add(providerId!);
        }

        var provider = GetRetailMatchDisplayName(summary);
        if (!string.IsNullOrWhiteSpace(provider))
        {
            chips.Add(provider);
        }

        return new MatchCardDisplay(
            Badge: !string.IsNullOrWhiteSpace(qid) ? "Matched" : "Not matched",
            Title: title,
            Creator: type,
            Year: year,
            CoverUrl: null,
            Chips: chips,
            Links: links,
            Note: !string.IsNullOrWhiteSpace(qid)
                ? "This file is currently linked to the Wikidata item shown below."
                : "This file is not linked to a Wikidata item yet.");
    }

    private IReadOnlyList<IdentityLinkDisplay> BuildRetailIdentityLinks(string? providerName, string? providerItemId)
    {
        var identifiers = BuildCurrentIdentityIdentifiers();
        identifiers.Remove("wikidata_qid");
        identifiers.Remove("wikipedia_url");
        AddProviderItemIdentifier(identifiers, providerName, providerItemId);
        return ProviderCatalogue
            .GetExternalUrls(identifiers, EditorMediaType, providerName)
            .Select(link => new IdentityLinkDisplay(link.Label, link.Url))
            .ToList();
    }

    private IReadOnlyList<IdentityLinkDisplay> BuildWikidataIdentityLinks(string? qid)
    {
        if (string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        var identifiers = BuildCurrentIdentityIdentifiers();
        identifiers["wikidata_qid"] = qid.Trim();
        return ProviderCatalogue
            .GetExternalUrls(identifiers, EditorMediaType, "wikidata_reconciliation")
            .Select(link => new IdentityLinkDisplay(link.Label, link.Url))
            .ToList();
    }

    private Dictionary<string, string> BuildCurrentIdentityIdentifiers()
    {
        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "wikidata_qid",
            "wikipedia_url",
            "tmdb_id",
            "tmdb_movie_id",
            "tmdb_tv_id",
            "imdb_id",
            "comicvine_id",
            "issue_source_url",
            "musicbrainz_release_id",
            "musicbrainz_release_group_id",
            "musicbrainz_recording_id",
            "musicbrainz_artist_id",
        })
        {
            var value = GetBaselineValue(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                identifiers[key] = value.Trim();
            }
        }

        return identifiers;
    }

    private void AddProviderItemIdentifier(
        IDictionary<string, string> identifiers,
        string? providerName,
        string? providerItemId)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerItemId))
        {
            return;
        }

        var provider = NormalizeProviderKey(providerName);
        var id = NormalizeProviderItemId(provider, providerItemId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var key = provider switch
        {
            "musicbrainz" when string.Equals(_canonicalTargetGroup, "track", StringComparison.OrdinalIgnoreCase)
                => "musicbrainz_recording_id",
            "musicbrainz" => "musicbrainz_release_id",
            "tmdb" => "tmdb_id",
            "imdb" => "imdb_id",
            "comicvine" or "comic_vine" => "comicvine_id",
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(key))
        {
            identifiers.TryAdd(key, id);
        }
    }

    private static string NormalizeProviderKey(string? providerName) =>
        (providerName ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);

    private static string NormalizeProviderItemId(string providerKey, string providerItemId)
    {
        var id = providerItemId.Trim();
        if (providerKey == "imdb" && id.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            id = id[5..];
        }

        if (providerKey == "tmdb" && id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            id = id[5..];
        }

        return id;
    }

    private string? InferProviderNameFromIdentifierFields()
    {
        if (!string.IsNullOrWhiteSpace(GetBaselineValue("tmdb_id")))
        {
            return "tmdb";
        }

        if (!string.IsNullOrWhiteSpace(GetBaselineValue("imdb_id")))
        {
            return "imdb";
        }

        if (!string.IsNullOrWhiteSpace(GetBaselineValue("comicvine_id")))
        {
            return "comicvine";
        }

        if (!string.IsNullOrWhiteSpace(GetBaselineValue("musicbrainz_release_id"))
            || !string.IsNullOrWhiteSpace(GetBaselineValue("musicbrainz_release_group_id"))
            || !string.IsNullOrWhiteSpace(GetBaselineValue("musicbrainz_recording_id"))
            || !string.IsNullOrWhiteSpace(GetBaselineValue("musicbrainz_id")))
        {
            return "musicbrainz";
        }

        if (!string.IsNullOrWhiteSpace(GetBaselineValue("asin")))
        {
            return "apple_api";
        }

        return null;
    }

    private string FormatRetailIdentifierChip(string providerItemId)
    {
        if (string.Equals(providerItemId, GetBaselineValue("isbn"), StringComparison.OrdinalIgnoreCase))
        {
            return $"ISBN: {providerItemId}";
        }

        if (string.Equals(providerItemId, GetBaselineValue("asin"), StringComparison.OrdinalIgnoreCase))
        {
            return $"ASIN: {providerItemId}";
        }

        if (string.Equals(providerItemId, GetBaselineValue("tmdb_id"), StringComparison.OrdinalIgnoreCase))
        {
            return $"TMDB: {providerItemId}";
        }

        if (string.Equals(providerItemId, GetBaselineValue("imdb_id"), StringComparison.OrdinalIgnoreCase))
        {
            return $"IMDB: {providerItemId}";
        }

        if (string.Equals(providerItemId, GetBaselineValue("comicvine_id"), StringComparison.OrdinalIgnoreCase))
        {
            return $"ComicVine: {providerItemId}";
        }

        if (string.Equals(providerItemId, GetBaselineValue("musicbrainz_id"), StringComparison.OrdinalIgnoreCase))
        {
            return $"MusicBrainz: {providerItemId}";
        }

        return providerItemId;
    }

    protected IReadOnlyList<string> BuildCandidateChips(ItemCanonicalRetailCandidateDto candidate)
    {
        var chips = new List<string>();
        AddCandidateChips(chips, candidate.BridgeIds);
        AddCandidateChips(chips, candidate.QidFields);
        return chips;
    }

    protected IReadOnlyList<string> BuildCandidateChips(ItemCanonicalLinkedCandidateDto candidate)
    {
        var chips = new List<string>();
        AddCandidateChips(chips, candidate.BridgeIds);
        AddCandidateChips(chips, candidate.QidFields);
        return chips;
    }

    protected static double GetRetailCandidateScore(ItemCanonicalRetailCandidateDto candidate) =>
        candidate.CompositeScore > 0 ? candidate.CompositeScore : candidate.Confidence;

    protected static string FormatCandidateScore(double score) =>
        score > 0 ? score.ToString("P0", CultureInfo.InvariantCulture) : "—";

    protected static string GetCandidateConfidenceLabel(double score) =>
        score switch
        {
            >= 0.9 => "Very high confidence",
            >= 0.75 => "High confidence",
            >= 0.55 => "Moderate confidence",
            > 0 => "Low confidence",
            _ => "Confidence unavailable",
        };

    protected static string GetCandidateConfidenceSummary(double score) =>
        score switch
        {
            >= 0.95 => "Exact match",
            >= 0.75 => "Strong match",
            >= 0.55 => "Possible match",
            > 0 => "Low-confidence match",
            _ => "Not scored",
        };

    protected static string GetMatchQualityLabel(double score) =>
        score switch
        {
            >= 0.75 => "Strong",
            >= 0.55 => "Possible",
            _ => "Low",
        };

    protected static string GetMatchQualityClass(double score) =>
        GetMatchQualityLabel(score).ToLowerInvariant();

    protected IReadOnlyList<CandidateConfidenceSignal> BuildCanonicalConfidenceSignals(ItemCanonicalLinkedCandidateDto candidate)
    {
        var draft = BuildDraftFields();
        var title = FirstDraftValue(draft, "title", "album", "show_name", "series", "episode_title");
        var creator = FirstDraftValue(draft, "author", "director", "artist", "narrator");
        var candidateCreator = !string.IsNullOrWhiteSpace(candidate.Author) ? candidate.Author : candidate.Director;
        var year = FirstDraftValue(draft, "year", "release_date");

        return
        [
            BuildTextEvidence("Title", title, candidate.Label),
            BuildTextEvidence("Creator", creator, candidateCreator),
            BuildTextEvidence("Year", year, candidate.Year),
            new CandidateConfidenceSignal(
                "Type compatible",
                candidate.IsApplicable ? "Compatible" : candidate.BlockedReason ?? "Not applicable",
                candidate.IsApplicable ? 1 : 0),
        ];
    }

    protected IReadOnlyList<CandidateComparisonRow> BuildCanonicalComparisonRows(ItemCanonicalLinkedCandidateDto candidate)
    {
        var draft = BuildDraftFields();
        var scores = candidate.MatchScores;
        var rows = new List<CandidateComparisonRow>
        {
            BuildComparisonRow(
                "Title",
                FirstDraftValue(draft, "title", "album", "show_name", "series", "episode_title"),
                candidate.Label,
                scores?.TitleScore),
            BuildComparisonRow(
                "Work type",
                GetLocalCanonicalTypeLabel(),
                FirstNamedCandidateValue(candidate, "canonical_kind") ?? candidate.InstanceOf,
                scores?.FormatScore ?? (candidate.IsApplicable ? 1 : 0)),
            BuildComparisonRow(
                "Creator",
                FirstDraftValue(draft, "author", "director", "artist"),
                FirstNamedCandidateValue(candidate, "author", "creator", "director")
                    ?? candidate.Author
                    ?? candidate.Director,
                scores?.AuthorScore),
            BuildComparisonRow(
                "Published",
                FirstDraftValue(draft, "year", "release_date"),
                candidate.Year,
                scores?.YearScore),
        };

        AddOptionalComparisonRow(rows, "Narrator",
            StringHelpers.FirstNonBlank(
                FirstDraftValue(draft, "narrator"),
                _detail?.Narrator,
                GetBaselineValue("narrator")),
            FirstNamedCandidateValue(candidate, "narrator", "narrators"));
        AddOptionalComparisonRow(rows, "Publisher",
            FirstDraftValue(draft, "publisher"),
            FirstNamedCandidateValue(candidate, "publisher"));
        AddOptionalComparisonRow(rows, "Language",
            FirstDraftValue(draft, "language"),
            FirstNamedCandidateValue(candidate, "language"));
        AddOptionalComparisonRow(rows, "ISBN",
            FirstDraftValue(draft, "isbn", "isbn_13", "isbn_10"),
            FirstNamedCandidateValue(candidate, "isbn", "isbn_13", "isbn_10"));

        return rows;
    }

    protected IReadOnlyList<CandidateComparisonRow> BuildRetailComparisonRows(ItemCanonicalRetailCandidateDto candidate)
    {
        var draft = BuildDraftFields();
        var scores = candidate.MatchScores;
        var rows = new List<CandidateComparisonRow>
        {
            BuildComparisonRow("Title", FirstDraftValue(draft, "title", "album", "show_name", "episode_title"), candidate.Title, scores?.TitleScore),
            BuildComparisonRow("Creator", FirstDraftValue(draft, "author", "director", "artist"), candidate.Author ?? candidate.Director, scores?.AuthorScore),
            BuildComparisonRow("Published", FirstDraftValue(draft, "year", "release_date"), candidate.Year, scores?.YearScore),
            BuildComparisonRow("Format", GetLocalCanonicalTypeLabel(), FirstNamedCandidateValue(candidate, "format", "kind", "media_type") ?? GetLocalCanonicalTypeLabel(), scores?.FormatScore),
        };

        AddOptionalComparisonRow(rows, "Narrator",
            StringHelpers.FirstNonBlank(
                FirstDraftValue(draft, "narrator"),
                _detail?.Narrator,
                GetBaselineValue("narrator")),
            FirstNamedCandidateValue(candidate, "narrator", "narrators"));
        AddOptionalComparisonRow(rows, "Publisher",
            FirstDraftValue(draft, "publisher"),
            FirstNamedCandidateValue(candidate, "publisher"));
        AddOptionalComparisonRow(rows, "Language",
            FirstDraftValue(draft, "language"),
            FirstNamedCandidateValue(candidate, "language"));
        AddOptionalComparisonRow(rows, "Genre",
            FirstDraftValue(draft, "genre", "genres"),
            FirstNamedCandidateValue(candidate, "genre", "genres"));
        AddOptionalComparisonRow(rows, "ISBN",
            FirstDraftValue(draft, "isbn", "isbn_13", "isbn_10"),
            FirstNamedCandidateValue(candidate, "isbn", "isbn_13", "isbn_10"));

        return rows;
    }

    private CandidateComparisonRow BuildComparisonRow(
        string label,
        string? localValue,
        string? candidateValue,
        double? suppliedScore = null)
    {
        candidateValue = NormalizeNamedCandidateValue(candidateValue);
        var evidence = BuildTextEvidence(label, localValue, candidateValue);
        var score = suppliedScore is >= 0 ? suppliedScore.Value : evidence.Score;
        return new CandidateComparisonRow(label, DisplayComparisonValue(localValue), DisplayComparisonValue(candidateValue), score);
    }

    private void AddOptionalComparisonRow(
        ICollection<CandidateComparisonRow> rows,
        string label,
        string? localValue,
        string? candidateValue)
    {
        candidateValue = NormalizeNamedCandidateValue(candidateValue);
        if (string.IsNullOrWhiteSpace(localValue) && string.IsNullOrWhiteSpace(candidateValue))
        {
            return;
        }

        rows.Add(BuildComparisonRow(label, localValue, candidateValue));
    }

    private string GetLocalCanonicalTypeLabel() =>
        ReviewTargetResolver.NormalizeMediaType(EditorMediaType) switch
        {
            "Audiobooks" => "Audiobook",
            "Books" => "Book",
            "Movies" => "Movie",
            "TV" => "TV",
            "Music" => "Music",
            "Comics" => "Comic",
            var value => value,
        };

    private static string? FirstNamedCandidateValue(ItemCanonicalLinkedCandidateDto candidate, params string[] keys) =>
        FirstNamedCandidateValue([candidate.MediaTypeMetadata, candidate.RequiredFields, candidate.SuggestedFields], keys);

    private static string? FirstNamedCandidateValue(ItemCanonicalRetailCandidateDto candidate, params string[] keys) =>
        FirstNamedCandidateValue([candidate.ExtraFields, candidate.RequiredFields, candidate.SuggestedFields], keys);

    private static string? FirstNamedCandidateValue(
        IEnumerable<IReadOnlyDictionary<string, string>> sources,
        params string[] keys)
    {
        foreach (var source in sources)
        {
            foreach (var key in keys)
            {
                if (source.TryGetValue(key, out var value)
                    && !string.IsNullOrWhiteSpace(NormalizeNamedCandidateValue(value)))
                {
                    return NormalizeNamedCandidateValue(value);
                }
            }
        }

        return null;
    }

    private static string? NormalizeNamedCandidateValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var tokens = Regex.Split(trimmed, @"\s*[,;|]\s*")
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        return tokens.Count > 0 && tokens.All(token => Regex.IsMatch(token, @"^Q\d+$", RegexOptions.IgnoreCase))
            ? null
            : trimmed;
    }

    protected static string DisplayComparisonValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    protected static string GetComparisonScoreLabel(double score) =>
        score switch
        {
            >= 0.95 => "Exact",
            >= 0.7 => "Close",
            >= 0 => "Different",
            _ => "Not compared",
        };

    private static CandidateConfidenceSignal BuildTextEvidence(string label, string? localValue, string? candidateValue)
    {
        if (string.IsNullOrWhiteSpace(localValue) || string.IsNullOrWhiteSpace(candidateValue))
        {
            return new CandidateConfidenceSignal($"{label} unavailable", "Not compared", -1);
        }

        var local = NormalizeEvidenceValue(localValue);
        var candidate = NormalizeEvidenceValue(candidateValue);
        if (string.Equals(local, candidate, StringComparison.Ordinal))
        {
            return new CandidateConfidenceSignal($"{label} exact", "Exact", 1);
        }

        if (local.Contains(candidate, StringComparison.Ordinal) || candidate.Contains(local, StringComparison.Ordinal))
        {
            return new CandidateConfidenceSignal($"{label} close", "Close", 0.75);
        }

        return new CandidateConfidenceSignal($"{label} differs", "Different", 0.2);
    }

    private static string FirstDraftValue(IReadOnlyDictionary<string, string> values, params string[] keys) =>
        keys.Select(key => values.TryGetValue(key, out var value) ? value : null)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
        ?? string.Empty;

    private static string NormalizeEvidenceValue(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    protected IReadOnlyList<CandidateConfidenceSignal> BuildRetailConfidenceSignals(ItemCanonicalRetailCandidateDto candidate)
    {
        var scores = candidate.MatchScores;
        var signals = scores is null
            ? new List<CandidateConfidenceSignal>
            {
                new("Title match", candidate.Title.Length > 0 ? "Provider result" : "Unavailable", candidate.Title.Length > 0 ? 1 : -1),
                new("Creator match", string.IsNullOrWhiteSpace(candidate.Author ?? candidate.Director) ? "Unavailable" : "Provider result", string.IsNullOrWhiteSpace(candidate.Author ?? candidate.Director) ? -1 : 1),
                new("Year match", string.IsNullOrWhiteSpace(candidate.Year) ? "Unavailable" : "Provider result", string.IsNullOrWhiteSpace(candidate.Year) ? -1 : 1),
            }
            : new List<CandidateConfidenceSignal>
            {
                new("Title match", FormatSignalScore(scores.TitleScore), scores.TitleScore),
                new("Creator match", FormatSignalScore(scores.AuthorScore), scores.AuthorScore),
                new("Year match", FormatSignalScore(scores.YearScore), scores.YearScore),
                new("Format match", FormatSignalScore(scores.FormatScore), scores.FormatScore),
                new("Artwork match", scores.CoverScore > 0 ? FormatSignalScore(scores.CoverScore) : "Not compared", scores.CoverScore > 0 ? scores.CoverScore : -1),
            };

        if (string.Equals(ReviewTargetResolver.NormalizeMediaType(EditorMediaType), "Audiobooks", StringComparison.OrdinalIgnoreCase))
        {
            var localNarrator = StringHelpers.FirstNonBlank(
                FirstDraftValue(BuildDraftFields(), "narrator"),
                _detail?.Narrator,
                GetBaselineValue("narrator"));
            var candidateNarrator = GetCandidateExtraValue(candidate, "narrator", "narrators");
            if (string.IsNullOrWhiteSpace(candidateNarrator))
            {
                signals.Add(new CandidateConfidenceSignal("Narrator not provided", "Not compared", -1));
            }
            else if (!string.IsNullOrWhiteSpace(localNarrator))
            {
                signals.Add(BuildTextEvidence("Narrator", localNarrator, candidateNarrator));
            }
            else
            {
                signals.Add(new CandidateConfidenceSignal("Narrator provided", candidateNarrator, 1));
            }
        }

        return signals;
    }

    private static string? GetCandidateExtraValue(ItemCanonicalRetailCandidateDto candidate, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (candidate.ExtraFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    protected string? GetRetailCandidateEvidenceNotice(ItemCanonicalRetailCandidateDto candidate)
    {
        if (!string.Equals(ReviewTargetResolver.NormalizeMediaType(EditorMediaType), "Audiobooks", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidateNarrator = GetCandidateExtraValue(candidate, "narrator", "narrators");
        return string.IsNullOrWhiteSpace(candidateNarrator)
            ? "Narrator was not provided by this result. It remains useful matching evidence, but it does not prevent you from selecting this retail edition."
            : null;
    }

    private static string FormatSignalScore(double score) =>
        score switch
        {
            >= 0.95 => "Exact",
            >= 0.7 => "Close",
            >= 0 => score.ToString("P0", CultureInfo.InvariantCulture),
            _ => "Unavailable",
        };

    protected async Task SaveAsPreferenceOnlyAsync()
    {
        if (_editedValues.Count == 0)
        {
            Snackbar.Add("There are no preference changes to save.", Severity.Info);
            return;
        }

        await SaveAsync();
    }

    protected async Task ApplyUnlinkedCanonicalAsync()
    {
        if (!CanEditCanonicalIdentity) return;
        var searchResponse = ActiveMatchSearchResponse;
        if (searchResponse is null)
        {
            return;
        }

        var fields = searchResponse.UnlinkedFields.Count > 0
            ? searchResponse.UnlinkedFields
            : BuildDraftFields()
                .Where(pair => MediaEditorSchemaCatalog.GetStrongFieldKeys(_canonicalTargetGroup).Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (fields.Count == 0)
        {
            Snackbar.Add("There are no canonical fields to apply.", Severity.Warning);
            return;
        }

        await ApplyCanonicalAsync(
            linkState: "text_only",
            providerName: null,
            providerItemId: null,
            requiredFields: fields,
            suggestedFields: [],
            acceptedSuggestedKeys: [],
            bridgeIds: [],
            qidFields: []);
    }

    protected async Task SelectCandidateAsync(ItemCanonicalRetailCandidateDto candidate)
    {
        var candidateId = GetCandidateId(candidate);
        CancelRetailHierarchyPreview(clearSelection: false);
        _selectedRetailCandidateId = candidateId;
        _retailCandidateDetail = null;
        var previewTask = LoadRetailHierarchyPreviewAsync(candidate);

        if (!string.Equals(ReviewTargetResolver.NormalizeMediaType(EditorMediaType), "Music", StringComparison.OrdinalIgnoreCase))
        {
            await previewTask;
            return;
        }

        _loadingRetailCandidateDetail = true;
        try
        {
            var detail = await ApiClient.GetRetailCandidateDetailAsync(new RetailCandidateDetailRequestDto
            {
                ProviderName = candidate.ProviderName,
                ProviderItemId = candidate.ProviderItemId,
                MediaType = EditorMediaType,
                ExtraFields = new Dictionary<string, string>(candidate.ExtraFields, StringComparer.OrdinalIgnoreCase),
            });
            if (string.Equals(_selectedRetailCandidateId, candidateId, StringComparison.Ordinal))
            {
                _retailCandidateDetail = detail;
            }
        }
        finally
        {
            if (string.Equals(_selectedRetailCandidateId, candidateId, StringComparison.Ordinal))
            {
                _loadingRetailCandidateDetail = false;
            }
        }

        await previewTask;
    }

    private async Task LoadRetailHierarchyPreviewAsync(ItemCanonicalRetailCandidateDto candidate)
    {
        var request = BuildRetailHierarchyPreviewRequest(candidate);
        if (request is null)
        {
            return;
        }

        var candidateId = GetCandidateId(candidate);
        var entityId = _navigator?.SelectedEntityId is { } selectedEntityId && selectedEntityId != Guid.Empty
            ? selectedEntityId
            : SelectedNavigatorNode?.EntityId is { } nodeEntityId && nodeEntityId != Guid.Empty
                ? nodeEntityId
                : CurrentEntityId;
        if (entityId == Guid.Empty)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _retailHierarchyPreviewCts = cancellation;
        _retailHierarchyPreviewCandidateId = candidateId;
        _retailHierarchyPreview = null;
        _loadingRetailHierarchyPreview = true;
        StateHasChanged();

        try
        {
            var preview = await ApiClient.PreviewMediaEditorMembershipAsync(entityId, request, cancellation.Token);
            if (!cancellation.IsCancellationRequested
                && ReferenceEquals(_retailHierarchyPreviewCts, cancellation)
                && string.Equals(_selectedRetailCandidateId, candidateId, StringComparison.Ordinal))
            {
                _retailHierarchyPreview = preview;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Retail hierarchy preview was unavailable for candidate {CandidateId}.", candidateId);
        }
        finally
        {
            if (ReferenceEquals(_retailHierarchyPreviewCts, cancellation))
            {
                _retailHierarchyPreviewCts = null;
                _loadingRetailHierarchyPreview = false;
                StateHasChanged();
            }

            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        CancelRetailHierarchyPreview();
        CancelAllMatchSearches();
    }

    private MediaEditorMembershipPreviewRequestDto? BuildRetailHierarchyPreviewRequest(ItemCanonicalRetailCandidateDto candidate)
    {
        var fields = BuildRetailCandidateFieldValues(candidate);
        string suggestionKey;
        string suggestionKind;
        string label;
        string? subtitle = null;
        string externalIdKey;
        string? externalIdValue;

        if (IsRetailSeriesLeafScope()
            && fields.ContainsKey("series")
            && !string.IsNullOrWhiteSpace(fields["series"]))
        {
            return new MediaEditorMembershipPreviewRequestDto
            {
                ScopeId = ActiveScope?.ScopeId,
                FieldValues = fields.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase),
            };
        }

        if (string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveScope?.ScopeId, "episode", StringComparison.OrdinalIgnoreCase)
            && fields.TryGetValue("show_name", out var showName)
            && !string.IsNullOrWhiteSpace(showName))
        {
            suggestionKey = "show";
            suggestionKind = "show";
            label = showName;
            externalIdKey = BridgeIdKeys.TmdbId;
            externalIdValue = GetCandidateBridgeId(candidate, externalIdKey);
        }
        else if (string.Equals(EditorMediaType, "Music", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(ActiveScope?.ScopeId, "track", StringComparison.OrdinalIgnoreCase)
                 && fields.TryGetValue("album", out var album)
                 && !string.IsNullOrWhiteSpace(album))
        {
            suggestionKey = "album";
            suggestionKind = "album";
            label = album;
            subtitle = fields.GetValueOrDefault("artist");
            externalIdKey = BridgeIdKeys.MusicBrainzReleaseGroupId;
            externalIdValue = GetCandidateBridgeId(candidate, externalIdKey);
        }
        else
        {
            return null;
        }

        var source = string.IsNullOrWhiteSpace(externalIdValue) ? "local" : "retail";
        return new MediaEditorMembershipPreviewRequestDto
        {
            ScopeId = ActiveScope?.ScopeId,
            FieldValues = fields.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase),
            SelectedSuggestions = new Dictionary<string, MediaEditorMembershipSuggestionDto>(StringComparer.OrdinalIgnoreCase)
            {
                [suggestionKey] = new MediaEditorMembershipSuggestionDto
                {
                    Source = source,
                    LocalExisting = false,
                    Kind = suggestionKind,
                    Label = label,
                    Subtitle = subtitle,
                    ProviderName = candidate.ProviderName,
                    ProviderItemId = candidate.ProviderItemId,
                    ExternalIdKey = string.IsNullOrWhiteSpace(externalIdValue) ? null : externalIdKey,
                    ExternalIdValue = externalIdValue,
                },
            },
        };
    }

    private bool IsRetailSeriesLeafScope() =>
        (EditorMediaType, ActiveScope?.ScopeId?.ToLowerInvariant()) switch
        {
            ("Books", "book") => true,
            ("Audiobooks", "audiobook") => true,
            ("Comics", "issue") => true,
            ("Movies", "movie") => true,
            ("Movies", "item") => true,
            _ => false,
        };

    private static Dictionary<string, string> BuildRetailCandidateFieldValues(ItemCanonicalRetailCandidateDto candidate) =>
        candidate.RequiredFields
            .Concat(candidate.SuggestedFields)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

    private static string? GetCandidateBridgeId(ItemCanonicalRetailCandidateDto candidate, string key) =>
        candidate.BridgeIds.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private void CancelRetailHierarchyPreview(bool clearSelection = true)
    {
        var cancellation = _retailHierarchyPreviewCts;
        _retailHierarchyPreviewCts = null;
        _loadingRetailHierarchyPreview = false;
        _retailHierarchyPreview = null;
        _retailHierarchyPreviewCandidateId = null;
        if (clearSelection)
        {
            _selectedRetailCandidateId = null;
        }

        cancellation?.Cancel();
    }

    protected async Task ToggleRetailCandidateDetailsAsync(ItemCanonicalRetailCandidateDto candidate)
    {
        var candidateId = GetCandidateId(candidate);
        if (IsCandidateSelected(candidateId))
        {
            _selectedRetailCandidateId = null;
            _retailCandidateDetail = null;
            CancelRetailHierarchyPreview();
            return;
        }

        await SelectCandidateAsync(candidate);
    }

    protected static string GetConfidenceSignalClass(double score) =>
        score switch
        {
            >= 0.7 => "is-positive",
            >= 0 => "is-warning",
            _ => "is-muted",
        };

    protected static string GetConfidenceSignalIcon(double score) =>
        score switch
        {
            >= 0.7 => Icons.Material.Outlined.CheckCircle,
            >= 0 => Icons.Material.Outlined.ChangeCircle,
            _ => Icons.Material.Outlined.RemoveCircleOutline,
        };

    protected void SelectCandidate(ItemCanonicalLinkedCandidateDto candidate)
    {
        _selectedWikidataCandidateId = GetCandidateId(candidate);
    }

    protected bool CanApplyRetailCandidate(ItemCanonicalRetailCandidateDto candidate) =>
        CanMatchCurrentTarget && IsCandidateSelected(GetCandidateId(candidate))
        && candidate.IsApplicable
        && RetailHierarchyPreviewPolicy.CanApply(
            previewRequired: BuildRetailHierarchyPreviewRequest(candidate) is not null,
            loading: IsRetailHierarchyPreviewLoading(candidate),
            candidatePreviewMatches: string.Equals(_retailHierarchyPreviewCandidateId, GetCandidateId(candidate), StringComparison.Ordinal),
            preview: _retailHierarchyPreview)
        && !IsDirty
        && !_matchActionPending
        && !string.IsNullOrWhiteSpace(candidate.ProviderName)
        && !string.IsNullOrWhiteSpace(candidate.ProviderItemId);

    protected MediaEditorMembershipPreviewDto? GetRetailHierarchyImpactPreview(ItemCanonicalRetailCandidateDto candidate)
    {
        var candidateId = GetCandidateId(candidate);
        if (!string.Equals(_selectedRetailCandidateId, candidateId, StringComparison.Ordinal)
            || !string.Equals(_retailHierarchyPreviewCandidateId, candidateId, StringComparison.Ordinal)
            || !RetailHierarchyPreviewPolicy.ShouldShowImpactNotice(_retailHierarchyPreview))
        {
            return null;
        }

        return _retailHierarchyPreview;
    }

    protected string? GetRetailHierarchyPreviewStatus(ItemCanonicalRetailCandidateDto candidate) =>
        RetailHierarchyPreviewPolicy.GetStatus(
            previewRequired: BuildRetailHierarchyPreviewRequest(candidate) is not null,
            loading: IsRetailHierarchyPreviewLoading(candidate),
            candidatePreviewMatches: string.Equals(_retailHierarchyPreviewCandidateId, GetCandidateId(candidate), StringComparison.Ordinal),
            preview: _retailHierarchyPreview);

    private bool IsRetailHierarchyPreviewLoading(ItemCanonicalRetailCandidateDto candidate) =>
        _loadingRetailHierarchyPreview
        && string.Equals(_retailHierarchyPreviewCandidateId, GetCandidateId(candidate), StringComparison.Ordinal);

    protected bool CanApplyLinkedCandidate(ItemCanonicalLinkedCandidateDto candidate) =>
        CanMatchCurrentTarget && CanEditCanonicalIdentity && IsCandidateSelected(GetCandidateId(candidate))
        && candidate.IsApplicable
        && !_matchActionPending
        && (!string.IsNullOrWhiteSpace(candidate.Qid)
            || candidate.QidFields.TryGetValue("wikidata_qid", out var qid) && !string.IsNullOrWhiteSpace(qid));

    protected void SelectMatchSearchMode(string mode)
    {
        var normalized = string.Equals(mode, "wikidata", StringComparison.OrdinalIgnoreCase)
            ? "wikidata"
            : "retail";
        if (normalized == "wikidata" && !CanEditCanonicalIdentity)
        {
            return;
        }

        if (string.Equals(_activeMatchSearchMode, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeMatchSearchMode = normalized;
    }

    private void InitializeMatchSearchState()
    {
        if (!string.Equals(_activeTab, "links", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeMatchSearchMode = CanEditCanonicalIdentity && !HasCurrentCanonicalIdentity && HasCurrentRetailMatch
            ? "wikidata"
            : "retail";
    }

    protected sealed record CandidateConfidenceSignal(string Label, string Value, double Score);
    protected sealed record CandidateComparisonRow(string Label, string LocalValue, string CandidateValue, double Score);

    protected void SetActiveMatchSearchQuery(string? value)
    {
        if (IsWikidataSearchMode)
        {
            _wikidataSearchQuery = value ?? string.Empty;
        }
        else
        {
            _retailSearchQuery = value ?? string.Empty;
        }
    }

    protected void CancelActiveMatchSearch() => CancelMatchSearch(IsWikidataSearchMode);

    private void CancelMatchSearch(bool isWikidataSearch)
    {
        var cts = GetMatchSearchCts(isWikidataSearch);
        SetMatchSearchCts(isWikidataSearch, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        SetMatchSearchPending(isWikidataSearch, false);
    }

    private void CancelAllMatchSearches()
    {
        CancelMatchSearch(false);
        CancelMatchSearch(true);
    }

    private void ResetMatchSearchState()
    {
        CancelAllMatchSearches();
        CancelRetailHierarchyPreview();
        _retailSearchResponse = null;
        _wikidataSearchResponse = null;
        _selectedRetailCandidateId = null;
        _selectedWikidataCandidateId = null;
        _retailCandidateDetail = null;
        InitializeMatchSearchQueries();
    }

    private void InitializeMatchSearchQueries()
    {
        var suggestedQuery = BuildSuggestedSearchQuery();
        _retailSearchQuery = suggestedQuery;
        _wikidataSearchQuery = suggestedQuery;
    }

    private string GetMatchSearchQuery(bool isWikidataSearch) =>
        isWikidataSearch ? _wikidataSearchQuery : _retailSearchQuery;

    private CancellationTokenSource? GetMatchSearchCts(bool isWikidataSearch) =>
        isWikidataSearch ? _wikidataSearchCts : _retailSearchCts;

    private void SetMatchSearchCts(bool isWikidataSearch, CancellationTokenSource? cts)
    {
        if (isWikidataSearch)
        {
            _wikidataSearchCts = cts;
        }
        else
        {
            _retailSearchCts = cts;
        }
    }

    private void SetMatchSearchPending(bool isWikidataSearch, bool pending)
    {
        if (isWikidataSearch)
        {
            _searchingWikidata = pending;
        }
        else
        {
            _searchingRetail = pending;
        }
    }

    private void SetMatchSearchResponse(bool isWikidataSearch, ItemCanonicalSearchResponseDto? response)
    {
        if (isWikidataSearch)
        {
            _wikidataSearchResponse = response;
        }
        else
        {
            _retailSearchResponse = response;
        }
    }

    private void ClearMatchSelection(bool isWikidataSearch)
    {
        if (isWikidataSearch)
        {
            _selectedWikidataCandidateId = null;
        }
        else
        {
            _selectedRetailCandidateId = null;
            _retailCandidateDetail = null;
            CancelRetailHierarchyPreview();
        }
    }

    protected string FormatCandidateDuration(double? seconds)
    {
        if (seconds is not > 0)
        {
            return "—";
        }

        var duration = TimeSpan.FromSeconds(seconds.Value);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    protected IReadOnlyList<(string Label, string Value)> BuildRetailCandidateFacts(ItemCanonicalRetailCandidateDto candidate)
    {
        var facts = new List<(string Label, string Value)>
        {
            ("Provider", FormatProviderName(candidate.ProviderName, EditorMediaType)),
            ("Provider ID", candidate.ProviderItemId ?? candidate.ProviderId),
        };

        AddCandidateFact(facts, candidate, "Format", "format", "media_type");
        AddCandidateFact(facts, candidate, "Edition", "edition", "edition_type");
        AddCandidateFact(facts, candidate, "Language", "language", "language_name");
        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            facts.Add(("Published", candidate.Year));
        }

        var includedLabels = facts.Select(fact => fact.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in candidate.ExtraFields
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                     .Where(pair => !pair.Key.Contains("url", StringComparison.OrdinalIgnoreCase))
                     .Where(pair => pair.Key is not "format" and not "media_type" and not "edition" and not "edition_type" and not "language" and not "language_name" and not "provider_item_id" and not "apple_books_id" and not "audible_id")
                     .Take(4))
        {
            var label = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
            if (includedLabels.Add(label))
            {
                facts.Add((label, FormatCandidateFactValue(key, value)));
            }
        }
        return facts;
    }

    private void AddCandidateFact(List<(string Label, string Value)> facts, ItemCanonicalRetailCandidateDto candidate, string label, params string[] keys)
    {
        var value = GetCandidateExtraValue(candidate, keys);
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add((label, FormatCandidateFactValue(keys[0], value)));
        }
    }

    private string FormatCandidateFactValue(string key, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return value;
        }

        return key.ToLowerInvariant() switch
        {
            "duration" when numeric > 10_000 => FormatCandidateDuration(numeric / 1000d),
            "duration_sec" or "duration_seconds" => FormatCandidateDuration(numeric),
            _ => value,
        };
    }

    protected bool IsCandidateSelected(string candidateId) =>
        string.Equals(_selectedRetailCandidateId, candidateId, StringComparison.Ordinal)
        || string.Equals(_selectedWikidataCandidateId, candidateId, StringComparison.Ordinal);

    protected async Task ApplyRetailCandidateAsync(ItemCanonicalRetailCandidateDto candidate)
    {
        if (!CanApplyRetailCandidate(candidate))
        {
            return;
        }

        _matchActionPending = true;
        _matchActionStatus = $"Applying {GetCanonicalTargetLabel(_canonicalTargetGroup).ToLowerInvariant()} retail identity...";
        StateHasChanged();

        var response = await ApiClient.ReplaceRetailMatchAsync(
            CanonicalEndpointEntityId,
            new ReplaceRetailMatchRequestDto
            {
                TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                TargetFieldGroup = _canonicalTargetGroup,
                TargetScopeId = ActiveScope?.ScopeId ?? string.Empty,
                ProviderId = candidate.ProviderId,
                ProviderName = candidate.ProviderName,
                ProviderItemId = candidate.ProviderItemId ?? string.Empty,
                CoverUrl = candidate.CoverUrl,
                RequiredFields = new Dictionary<string, string>(candidate.RequiredFields, StringComparer.OrdinalIgnoreCase),
                SuggestedFields = new Dictionary<string, string>(candidate.SuggestedFields, StringComparer.OrdinalIgnoreCase),
                BridgeIds = candidate.BridgeIds,
                ReviewItemId = Request.ReviewItemId,
            });

        await FinishMatchActionAsync(
            response,
            "Match confirmed. This file was queued for the full enrichment cycle.",
            reloadFromSelectedEntity: true);
    }

    protected async Task ApplyLinkedCandidateAsync(ItemCanonicalLinkedCandidateDto candidate)
    {
        if (!CanApplyLinkedCandidate(candidate))
        {
            return;
        }

        var qid = candidate.QidFields.TryGetValue("wikidata_qid", out var qidField)
            ? qidField
            : candidate.Qid;
        var candidateFields = candidate.RequiredFields
            .Concat(candidate.SuggestedFields)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        _matchActionPending = true;
        _matchActionStatus = "Applying canonical Wikidata identity...";
        StateHasChanged();

        var response = await ApiClient.ReplaceWikidataMatchAsync(
            CanonicalEndpointEntityId,
            new ReplaceWikidataMatchRequestDto
            {
                TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                TargetFieldGroup = _canonicalTargetGroup,
                TargetScopeId = ActiveScope?.ScopeId ?? string.Empty,
                Action = "replace",
                Qid = qid,
                SuggestedFields = candidateFields,
                AcceptedSuggestedKeys = candidateFields.Keys.ToList(),
                ReviewItemId = Request.ReviewItemId,
            });

        await FinishMatchActionAsync(response, "Match confirmed. This file was queued for the full enrichment cycle.");
    }

    protected async Task MarkWikidataMissingAsync()
    {
        if (_matchActionPending)
        {
            return;
        }

        _matchActionPending = true;
        _matchActionStatus = "Updating Wikidata identity state...";
        var response = await ApiClient.ReplaceWikidataMatchAsync(
            CanonicalEndpointEntityId,
            new ReplaceWikidataMatchRequestDto
            {
                TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                TargetFieldGroup = _canonicalTargetGroup,
                TargetScopeId = ActiveScope?.ScopeId ?? string.Empty,
                Action = "mark_missing",
                ReviewItemId = Request.ReviewItemId,
            });

        await FinishMatchActionAsync(response, "Retail match kept; Wikidata marked missing.");
    }

    protected async Task ClearWikidataMatchAsync()
    {
        if (_matchActionPending)
        {
            return;
        }

        _matchActionPending = true;
        _matchActionStatus = "Clearing Wikidata identity...";
        var response = await ApiClient.ReplaceWikidataMatchAsync(
            CanonicalEndpointEntityId,
            new ReplaceWikidataMatchRequestDto
            {
                TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                TargetFieldGroup = _canonicalTargetGroup,
                TargetScopeId = ActiveScope?.ScopeId ?? string.Empty,
                Action = "clear",
                ReviewItemId = Request.ReviewItemId,
            });

        await FinishMatchActionAsync(response, "Wikidata identity cleared; retail match kept.");
    }

    private async Task FinishMatchActionAsync(
        ItemCanonicalApplyResponseDto? response,
        string fallbackMessage,
        bool reloadFromSelectedEntity = false)
    {
        if (response is null)
        {
            _matchActionPending = false;
            _matchActionStatus = ApiClient.LastError ?? "Match update failed.";
            Snackbar.Add(ApiClient.LastError ?? "Match update failed.", Severity.Error);
            return;
        }

        _matchIdentityJobId = response.IdentityJobId;
        _hasCommittedChanges = true;
        _matchActionStatus = response.IdentityJobId.HasValue
            ? response.ArtworkChanged
                ? $"{response.ArtworkMessage ?? "Identity and artwork saved."} The full enrichment cycle is continuing in the background."
                : "Identity saved. The full enrichment cycle is continuing in the background."
            : string.IsNullOrWhiteSpace(response.Message) ? fallbackMessage : response.Message;

        try
        {
            if (reloadFromSelectedEntity)
            {
                await ReloadAfterRetailMatchAsync(response);
            }
            else
            {
                await NotifyParentArtworkChangedAsync();

                var refreshedContext = await ApiClient.GetMediaEditorContextAsync(EditorContextEntityId);
                if (refreshedContext is not null)
                {
                    _editorContext = refreshedContext;
                    if (!_editorContext.Scopes.Any(scope => string.Equals(scope.ScopeId, _activeScopeId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _activeScopeId = _editorContext.InitialScope;
                    }
                }

                _scopeStates.Clear();
                _artworkStates.Clear();
                await LoadScopeStateAsync(forceReload: true);
            }
        }
        finally
        {
            _matchActionPending = false;
        }

        Snackbar.Add(string.IsNullOrWhiteSpace(response.Message) ? fallbackMessage : response.Message, Severity.Success);
        StateHasChanged();
    }

    private async Task ReloadAfterRetailMatchAsync(ItemCanonicalApplyResponseDto response)
    {
        var targetEntityId = response.SelectedEntityId != Guid.Empty
            ? response.SelectedEntityId
            : EditorContextEntityId;

        CancelRetailHierarchyPreview();
        _retailCandidateDetail = null;

        // Apply is disabled while the editor is dirty. Keep the normal target-switch
        // safeguard as a backstop if a user edits while the request is in flight.
        if (IsDirty)
        {
            ResetMatchSearchState();
            _scopeStates.Clear();
            _artworkStates.Clear();
            _pendingTargetSwitch = new PendingTargetSwitch(
                targetEntityId,
                null,
                response.TargetPath ?? response.Message ?? "updated item");
            _navigator = null;
            await NotifyParentArtworkChangedAsync();
            StateHasChanged();
            return;
        }

        _navigator = null;
        _editorContext = null;
        _detail = null;
        _canonicalValues = [];
        _claims = [];
        _history = [];
        _artwork = null;
        _pendingMembershipPreview = null;
        ResetMatchSearchState();

        await LoadSingleItemAsync(
            targetEntityId,
            resetEditorState: true,
            preferredScopeId: _activeScopeId);
        await NotifyParentArtworkChangedAsync();
    }

    private async Task NotifyParentArtworkChangedAsync()
    {
        if (Request.OnArtworkChanged is null)
        {
            return;
        }

        try
        {
            await Request.OnArtworkChanged.Invoke();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"The match was saved, but the detail page could not refresh: {ex.Message}", Severity.Warning);
        }
    }

    private async Task ApplyCanonicalAsync(
        string linkState,
        string? providerName,
        string? providerItemId,
        Dictionary<string, string> requiredFields,
        Dictionary<string, string> suggestedFields,
        List<string> acceptedSuggestedKeys,
        Dictionary<string, string> bridgeIds,
        Dictionary<string, string> qidFields)
    {
        var response = await ApiClient.ApplyItemCanonicalAsync(
            CanonicalEndpointEntityId,
            new ItemCanonicalApplyRequestDto
            {
                TargetKind = GetCanonicalTargetKind(_canonicalTargetGroup),
                TargetFieldGroup = _canonicalTargetGroup,
                LinkState = linkState,
                ProviderName = providerName,
                ProviderItemId = providerItemId,
                RequiredFields = requiredFields,
                SuggestedFields = suggestedFields,
                AcceptedSuggestedKeys = acceptedSuggestedKeys,
                BridgeIds = bridgeIds,
                QidFields = qidFields,
            });

        if (response is null)
        {
            Snackbar.Add(ApiClient.LastError ?? "Canonical apply failed.", Severity.Error);
            return;
        }

        if (Request.Mode == SharedMediaEditorMode.Review)
        {
            if (!await ResolveReviewCoreAsync(providerName, providerItemId, qidFields.Values.FirstOrDefault()))
            {
                return;
            }

            Snackbar.Add($"{response.Message} Review resolved.", Severity.Success);
            await CloseEditorAsync(applied: true);
            return;
        }

        Snackbar.Add(response.Message, Severity.Success);
        await CloseEditorAsync(applied: true);
    }

    private string GetCandidateId(ItemCanonicalRetailCandidateDto candidate) =>
        $"retail:{candidate.CandidateId}:{candidate.ProviderName}:{candidate.ProviderItemId}:{string.Join('|', candidate.BridgeIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))}";

    private string GetCandidateId(ItemCanonicalLinkedCandidateDto candidate) =>
        $"linked:{candidate.CandidateId}:{candidate.Qid}";

    private string BuildHeaderSubtitle()
    {
        if (_detail is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        var creator = _detail.MediaType switch
        {
            "Music" => GetBaselineValue("artist"),
            "Movies" => _detail.Director,
            "TV" => GetBaselineValue("show_name"),
            "Audiobooks" => _detail.Narrator ?? _detail.Author,
            _ => _detail.Author,
        };

        if (!string.IsNullOrWhiteSpace(creator))
        {
            parts.Add(creator);
        }

        if (!string.IsNullOrWhiteSpace(GetBaselineValue("album")) && _detail.MediaType == "Music")
        {
            parts.Add(GetBaselineValue("album"));
        }

        if (!string.IsNullOrWhiteSpace(_detail.Year))
        {
            parts.Add(_detail.Year);
        }

        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private string BuildSuggestedSearchQuery()
    {
        var values = BuildDraftFields();
        string[] parts = _canonicalTargetGroup switch
        {
            "album" => new[] { GetValue(values, "artist"), GetValue(values, "album") },
            "artist" => new[] { GetValue(values, "artist") },
            "track" => new[] { GetValue(values, "artist"), GetValue(values, "title") },
            "movie_identity" => new[] { GetValue(values, "title"), GetValue(values, "year") },
            "show_episode" => new[] { GetValue(values, "show_name"), SeasonEpisodeLabel(values) },
            "show" => new[] { GetValue(values, "show_name") },
            "book_identity" => new[] { GetValue(values, "title"), GetValue(values, "author") },
            "audiobook_identity" => new[] { GetValue(values, "title"), GetValue(values, "author") },
            "narrator" => new[] { GetValue(values, "narrator") },
            "series" => new[] { GetValue(values, "series") },
            "issue" => new[] { GetValue(values, "series"), GetValue(values, "series_position"), GetValue(values, "title") },
            _ => new[] { GetValue(values, "title") },
        };

        return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private Dictionary<string, string> BuildDraftFields()
    {
        var merged = MediaEditorSchemaCatalog.BuildValueMap(_detail, _canonicalValues)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var edit in _editedValues
                     .Where(entry =>
                     {
                         var parsed = ParseScopedKey(entry.Key);
                         return parsed.EntityId == CurrentEntityId
                                && string.Equals(parsed.ScopeId, ActiveScope?.ScopeId, StringComparison.OrdinalIgnoreCase);
                     }))
        {
            merged[ParseScopedKey(edit.Key).Key] = edit.Value;
        }

        return merged
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private string GetBaselineValue(string key)
    {
        if (IsBatchMode)
        {
            return string.Empty;
        }

        if (_profilePreferencesByWork.TryGetValue(CurrentEntityId, out var preferences))
        {
            if (string.Equals(key, "custom_tags", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(", ", preferences.LocalTags);
            }
        }

        if (_editorContext?.DisplayOverrides.TryGetValue(key, out var overrideValue) == true)
        {
            return overrideValue;
        }

        var values = MediaEditorSchemaCatalog.BuildValueMap(_detail, _canonicalValues);
        return values.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static void AddCandidateChips(List<string> chips, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var label = key
                .Replace("_id", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_qid", "", StringComparison.OrdinalIgnoreCase)
                .Replace('_', ' ')
                .Trim();

            label = string.IsNullOrWhiteSpace(label)
                ? "ID"
                : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label);

            var chip = $"{label}: {value}";
            if (!chips.Contains(chip, StringComparer.OrdinalIgnoreCase))
            {
                chips.Add(chip);
            }
        }
    }

    private static string SeasonEpisodeLabel(IReadOnlyDictionary<string, string> values)
    {
        var season = GetValue(values, "season_number");
        var episode = GetValue(values, "episode_number");
        var title = GetValue(values, "episode_title");
        return string.Join(" ", new[] { season, episode, title }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string GetCanonicalTargetKind(string targetGroup) =>
        targetGroup switch
        {
            "artist" or "narrator" => "person",
            "album" or "series" or "show" => "container",
            _ => "item",
        };

    private static string FormatProviderName(string? providerName, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return "Provider";
        }

        return providerName switch
        {
            "tmdb" => "TMDB",
            "fanart_tv" => "Fanart.tv",
            "imdb" => "IMDb",
            "comicvine" => "Comic Vine",
            "comic_vine" => "Comic Vine",
            "musicbrainz" => "MusicBrainz",
            "apple_api" => ReviewTargetResolver.NormalizeMediaType(mediaType) == "Music" ? "Apple Music" : "Apple Books",
            "apple_music" => "Apple Music",
            "apple_books" => "Apple Books",
            "wikidata_reconciliation" => "Wikidata",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(providerName.Replace('_', ' ')),
        };
    }

    private IReadOnlyList<(string Id, string Label, string Icon)> ResolveVisibleTabs()
    {
        if (IsBatchMode)
        {
            return [("details", "Details", GetTabIcon("details")), ("options", "Options", GetTabIcon("options"))];
        }

        return GetAvailableTabIds()
            .OrderBy(tabId => Array.IndexOf(TabDisplayOrder, tabId))
            .Where(IsTabVisible)
            .Select(tabId => (tabId, GetTabLabel(tabId), GetTabIcon(tabId)))
            .ToList();
    }

    private IReadOnlyList<(string Key, string Label)> ResolveQuickSearchTargets()
    {
        if (IsBatchMode)
        {
            return _schema.QuickSearchTargets;
        }

        return (EditorMediaType, ActiveScope?.ScopeId) switch
        {
            ("TV", "series") => [],
            ("TV", "season") => [],
            ("TV", "episode") => [("show_episode", "Episode")],
            ("Music", "album") => [("album", "Album")],
            ("Music", "track") => [("track", "Track")],
            ("Movies", _) => [("movie_identity", "Movie")],
            ("Comics", _) => [("issue", "Issue")],
            ("Audiobooks", _) => [("audiobook_identity", "Audiobook")],
            ("Books", _) => [("book_identity", "Book")],
            _ => _schema.QuickSearchTargets,
        };
    }

    private IEnumerable<string> GetVisibleFieldKeysForScope()
    {
        if (IsFileScope || ActiveScope is null)
        {
            return [];
        }

        return (EditorMediaType, ActiveScope.ScopeId) switch
        {
            (_, "series") when EditorMediaType != "TV" => ["title", "description", "custom_tags", "sort_title"],
            ("TV", "series") => ["show_name", "tagline", "year", "network", "runtime", "genre", "custom_tags", "language", "description", "rating", "sort_series"],
            ("TV", "season") => ["title", "description", "custom_tags"],
            ("TV", "episode") => ["episode_title", "description", "custom_tags", "sort_title"],
            ("Music", "album") => ["album", "tagline", "album_artist", "artist", "genre", "custom_tags", "year", "language", "description", "rating", "sort_album"],
            ("Music", "track") => ["title", "tagline", "artist", "album", "composer", "track_number", "disc_number", "duration", "custom_tags", "rating", "sort_title"],
            ("Movies", "item") => _schema.Groups.SelectMany(group => group.Fields).Select(field => field.Key),
            ("Books", "item") => _schema.Groups.SelectMany(group => group.Fields).Select(field => field.Key),
            ("Audiobooks", "item") => _schema.Groups.SelectMany(group => group.Fields).Select(field => field.Key),
            ("Comics", "item") => _schema.Groups.SelectMany(group => group.Fields).Select(field => field.Key),
            _ => _schema.Groups.SelectMany(group => group.Fields).Select(field => field.Key),
        };
    }

    private string GetArtworkTabExplanation()
    {
        if (ArtworkScope is null)
        {
            return "Artwork follows the scope selected above.";
        }

        return (EditorMediaType, ArtworkScope.ScopeId) switch
        {
            ("TV", "series") =>
                "Series scope manages poster/cover, background, and logo artwork for the show. Those images are shared across episodes.",
            ("TV", "season") =>
                "Season scope manages the portrait season poster and wide season thumbnail. Series branding and episode stills remain in their own scopes.",
            ("TV", "episode") =>
                "Episode scope manages only the episode still. Series and season artwork remain inherited and can be opened from this panel.",
            ("Music", "album") =>
                "Album scope manages cover, background, and logo artwork shared by its tracks. Cover artwork keeps its natural aspect ratio.",
            ("Music", "track") =>
                "Track scope inherits art from the album. The Details page derives its music palette from that managed album cover.",
            ("Movies", "item") =>
                "Movie scope manages poster/cover, background, and logo artwork for the movie.",
            ("Books", "item") or ("Audiobooks", "item") or ("Comics", "item") =>
                "Item scope manages cover, background, and logo artwork for this title.",
            _ =>
                ArtworkScope.ScopeSummary ?? "Showing the artwork slots available for the selected owner.",
        };
    }

    protected string GetArtworkReadOnlyTitle() =>
        (EditorMediaType, ArtworkScope?.ScopeId) switch
        {
            ("Music", "track") => "Track artwork is inherited",
            _ => "This owner does not manage artwork",
        };

    protected string GetArtworkReadOnlyText() =>
        (EditorMediaType, ArtworkScope?.ScopeId) switch
        {
            ("Music", "track") => "Tracks inherit their visuals from the album owner. Switch back to the album to update shared artwork.",
            _ => ArtworkScope?.ReadOnlyHint ?? "This owner does not expose editable artwork.",
        };

    protected IReadOnlyList<MediaEditorScopeDto> GetArtworkReadOnlySwitchTargets()
    {
        if (_editorContext is null)
        {
            return [];
        }

        if (EditorMediaType == "Music" && string.Equals(ArtworkScope?.ScopeId, "track", StringComparison.OrdinalIgnoreCase))
        {
            return _editorContext.Scopes
                .Where(scope => string.Equals(scope.ScopeId, "album", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return [];
    }

    protected IReadOnlyList<MediaEditorScopeDto> GetRelatedArtworkScopeTargets()
    {
        if (_editorContext is null)
        {
            return [];
        }

        return (EditorMediaType, ArtworkScope?.ScopeId) switch
        {
            ("TV", "episode") => _editorContext.Scopes
                .Where(scope => scope.ScopeId is "series" or "season")
                .OrderBy(scope => scope.Order)
                .ToList(),
            ("TV", "season") => _editorContext.Scopes
                .Where(scope => string.Equals(scope.ScopeId, "series", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            ("Music", "track") => _editorContext.Scopes
                .Where(scope => string.Equals(scope.ScopeId, "album", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            _ => [],
        };
    }

    private bool IsTabVisible(string tabId)
    {
        if (IsBatchMode)
        {
            return tabId is "details" or "options";
        }

        return (tabId != "links" || CanMatchCurrentTarget)
            && GetAvailableTabIds().Contains(tabId, StringComparer.OrdinalIgnoreCase);
    }

    private string? GetCanonicalSearchUnavailableReason()
    {
        if (!IsSingleItem || IsFileScope || SupportsCanonicalSearch || ActiveScope is null)
        {
            return null;
        }

        return "Canonical search is not available for this selection.";
    }

    private void EnsureActiveTabVisible()
    {
        _tabState.EnsureVisible(IsTabVisible, Tabs.Select(tab => tab.Id));
    }

    private IReadOnlyList<string> GetAvailableTabIds() =>
        ActiveScope?.AvailableTabs?.Count > 0
            ? ActiveScope.AvailableTabs
            : _editorContext?.AvailableTabs?.Count > 0
                ? _editorContext.AvailableTabs
            : ["details", "artwork", "links", "history"];

    private string GetTabLabel(string tabId) =>
        tabId switch
        {
            "details" => "Details",
            "chapters" => "Chapters",
            "artwork" => "Artwork",
            "links" => "Match & Identity",
            "options" => "Options",
            "file" => "Files",
            "history" => "History",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tabId.Replace('_', ' ')),
        };

    private static string GetTabIcon(string tabId) =>
        tabId switch
        {
            "details" => Icons.Material.Outlined.Article,
            "chapters" => Icons.Material.Outlined.FormatListNumbered,
            "artwork" => Icons.Material.Outlined.PhotoLibrary,
            "links" => Icons.Material.Outlined.TravelExplore,
            "options" => Icons.Material.Outlined.Tune,
            "file" => Icons.Material.Outlined.InsertDriveFile,
            "history" => Icons.Material.Outlined.History,
            _ => Icons.Material.Outlined.Tab,
        };

    protected static string GetFileFormat(string? fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) ? "Unknown" : extension.TrimStart('.').ToUpperInvariant();
    }

    protected static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value < 0)
        {
            return "Unknown";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    protected string FormatLastFileActivity() =>
        _history.OrderByDescending(entry => entry.OccurredAt).FirstOrDefault() is { } entry
            ? entry.OccurredAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
            : "Not recorded";

    protected async Task RetryWritebackAsync()
    {
        if (_retryingWriteback || _detail is null)
        {
            return;
        }

        _retryingWriteback = true;
        try
        {
            var assetId = SelectedNavigatorNode?.PrimaryAssetId ?? _detail.EntityId;
            var succeeded = await ApiClient.RetryRetagForAssetAsync(assetId);
            _matchActionStatus = succeeded
                ? "Write-back retry queued. The review remains open until the file is updated successfully."
                : "Write-back retry could not be queued. No review state was changed.";
        }
        catch (Exception ex)
        {
            _matchActionStatus = $"Write-back retry failed: {ex.Message}";
        }
        finally
        {
            _retryingWriteback = false;
        }
    }

    protected Guid? CurrentTextTrackAssetId =>
        SelectedNavigatorNode?.PrimaryAssetId
        ?? NavigatorRootNode?.PrimaryAssetId;

    protected bool SupportsTextTracks =>
        string.Equals(EditorMediaType, "Music", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EditorMediaType, "Movies", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase);

    protected bool UsesLyrics =>
        string.Equals(EditorMediaType, "Music", StringComparison.OrdinalIgnoreCase);

    protected string TextTrackHeading => UsesLyrics ? "Lyrics" : "Subtitles";

    protected string TextTrackRefreshLabel =>
        _textTracksRefreshing ? $"Finding {TextTrackHeading.ToLowerInvariant()}..." : $"Find {TextTrackHeading.ToLowerInvariant()}";

    private async Task LoadTextTracksAsync()
    {
        _textTrackRefreshMessage = null;
        if (!SupportsTextTracks || CurrentTextTrackAssetId is not { } assetId)
        {
            _textTracks = [];
            return;
        }

        _textTracksLoading = true;
        try
        {
            _textTracks = await ApiClient.GetTextTracksAsync(assetId);
        }
        finally
        {
            _textTracksLoading = false;
        }
    }

    protected async Task RefreshTextTracksAsync()
    {
        if (_textTracksRefreshing || CurrentTextTrackAssetId is not { } assetId)
        {
            return;
        }

        _textTracksRefreshing = true;
        _textTrackRefreshMessage = null;
        try
        {
            var kind = UsesLyrics ? "lyrics" : "subtitles";
            var result = await ApiClient.RefreshTextTracksAsync(assetId, kind);
            if (result is null)
            {
                _textTrackRefreshMessage = $"{TextTrackHeading} could not be refreshed.";
                return;
            }

            _textTrackRefreshMessage = result.message;
            _textTracks = await ApiClient.GetTextTracksAsync(assetId);
            Snackbar.Add(result.message, result.refreshed ? Severity.Success : Severity.Info);
        }
        finally
        {
            _textTracksRefreshing = false;
        }
    }

    protected async Task ImportTextTrackAsync(InputFileChangeEventArgs args)
    {
        if (_textTrackImporting || CurrentTextTrackAssetId is not { } assetId)
        {
            return;
        }

        var file = args.File;
        _textTrackImporting = true;
        try
        {
            await using var stream = file.OpenReadStream(5 * 1024 * 1024);
            var result = await ApiClient.ImportTextTrackAsync(
                assetId,
                UsesLyrics ? "lyrics" : "subtitles",
                "und",
                file.Name,
                file.ContentType,
                stream);
            if (result is null)
            {
                _textTrackRefreshMessage = $"{TextTrackHeading} could not be imported.";
                Snackbar.Add(_textTrackRefreshMessage, Severity.Error);
                return;
            }

            _textTrackRefreshMessage = result.message;
            _textTracks = await ApiClient.GetTextTracksAsync(assetId);
            Snackbar.Add(result.message, result.refreshed ? Severity.Success : Severity.Warning);
        }
        finally
        {
            _textTrackImporting = false;
        }
    }

    protected async Task SetPreferredTextTrackAsync(Guid trackId)
    {
        if (_textTrackPreferencePendingId.HasValue || CurrentTextTrackAssetId is not { } assetId)
        {
            return;
        }

        _textTrackPreferencePendingId = trackId;
        try
        {
            if (!await ApiClient.SetPreferredTextTrackAsync(assetId, trackId))
            {
                Snackbar.Add("The preferred text track could not be changed.", Severity.Error);
                return;
            }

            _textTracks = await ApiClient.GetTextTracksAsync(assetId);
            Snackbar.Add($"Preferred {TextTrackHeading.ToLowerInvariant()} updated.", Severity.Success);
        }
        finally
        {
            _textTrackPreferencePendingId = null;
        }
    }

    protected async Task RereadFileMetadataAsync()
    {
        if (_rereadingFileMetadata || CurrentTextTrackAssetId is not { } assetId)
        {
            return;
        }

        _rereadingFileMetadata = true;
        _fileMetadataRereadMessage = null;
        try
        {
            var result = await ApiClient.RereadAssetMetadataAsync(assetId);
            if (result is null)
            {
                _fileMetadataRereadMessage = "The file metadata refresh could not be started.";
                Snackbar.Add(_fileMetadataRereadMessage, Severity.Error);
                return;
            }

            _fileMetadataRereadMessage = result.Message;
            Snackbar.Add(result.Message, result.Refreshed ? Severity.Success : Severity.Warning);
            if (result.Refreshed)
            {
                await LoadSingleItemAsync(resetEditorState: false);
            }
        }
        finally
        {
            _rereadingFileMetadata = false;
        }
    }

    protected static string FormatHistoryDate(DateTime date) =>
        date.Date == DateTime.Today ? "Today" : date.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : date.ToString("D", CultureInfo.CurrentCulture);

    protected static string FormatRelativeHistoryAge(DateTimeOffset occurredAt)
    {
        var age = DateTimeOffset.Now - occurredAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        }

        return $"{Math.Max(1, (int)age.TotalDays)}d ago";
    }

    protected static string GetHistoryIcon(string? category) => category?.ToLowerInvariant() switch
    {
        "artwork" => Icons.Material.Outlined.Image,
        "match" => Icons.Material.Outlined.Link,
        "file" => Icons.Material.Outlined.Description,
        "review" => Icons.Material.Outlined.TaskAlt,
        "manual" => Icons.Material.Outlined.Notes,
        "error" => Icons.Material.Outlined.ErrorOutline,
        _ => Icons.Material.Outlined.EditNote,
    };

    protected static string GetHistoryToneClass(string? category) => category?.ToLowerInvariant() switch
    {
        "artwork" => "is-artwork",
        "match" => "is-match",
        "file" => "is-file",
        "review" => "is-review",
        "manual" => "is-manual",
        "error" => "is-error",
        _ => "is-metadata",
    };

    protected string GetDirtySaveLabel() =>
        IsBatchMode
            ? $"Apply ({Request.EntityIds.Count})"
            : ResolveFooterPrimaryActionLabel(EffectiveIdentityIntent);

    protected string GetResolveReviewLabel() =>
        string.IsNullOrWhiteSpace(_primaryActionLabel)
            ? "Resolve Review"
            : _primaryActionLabel;

    protected IReadOnlyList<MediaEditorFieldDefinition> GetDetailsMetadataFields() =>
        GetGroupsForTab("details")
            .SelectMany(group => group.Fields)
            .DistinctBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    protected IReadOnlyList<MediaEditorFieldDefinition> GetAppearanceFields() =>
        GetDetailsMetadataFields()
            .Where(field => field.Key is not ("custom_tags" or "genre"))
            .ToList();

    protected IReadOnlyList<MediaEditorFieldDefinition> GetLibraryFields() =>
        GetDetailsMetadataFields()
            .Where(field => field.Key is "custom_tags")
            .ToList();

    protected IReadOnlyList<(string Label, string Value, string? Url)> GetSourceFacts()
    {
        if (_detail is null)
        {
            return [];
        }

        var facts = new List<(string Label, string Value, string? Url)>();
        AddSourceFact(facts, "Release", _detail.ReleaseDate ?? _detail.Year);
        if (string.Equals(EditorMediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveScope?.ScopeId, "episode", StringComparison.OrdinalIgnoreCase))
        {
            AddSourceFact(facts, "Episode", FormatEpisodeIdentity(_detail.SeasonNumber, _detail.EpisodeNumber));
            AddSourceFact(facts, "Actors", NormalizeContributorDisplay(_detail.Cast));
        }
        AddSourceFact(facts, "Runtime", FormatRuntimeFact(_detail.Runtime));
        AddSourceFact(facts, "Pages", FormatCountFact(GetBaselineValue("page_count"), "page", "pages"));
        AddSourceFact(facts, "Language", _detail.Language);
        AddSourceFact(facts, "Rating", FormatRatingValue(_detail.Rating));
        AddSourceFact(facts, "Provider", GetSourceProviderDisplayName());
        if (UsesParentRetailIdentityOnly)
        {
            AddExternalSourceFact(facts, "Series TMDB", "tmdb_id", GetScopeById("series")?.IdentitySummary?.ProviderItemId);
        }
        else
        {
            AddExternalSourceFact(facts, "TMDB", "tmdb_id", GetBaselineValue("tmdb_id"));
        }
        AddExternalSourceFact(facts, "IMDb", "imdb_id", GetBaselineValue("imdb_id"));
        AddExternalSourceFact(
            facts,
            HasInheritedCanonicalIdentity ? "Series Wikidata" : "Wikidata",
            "wikidata_qid",
            HasInheritedCanonicalIdentity
                ? CanonicalIdentityTargetSummary?.WikidataQid
                : _detail.WikidataQid ?? GetBaselineValue("wikidata_qid"));
        AddExternalSourceFact(facts, "Open Library", "open_library_id", GetBaselineValue("open_library_id"));
        AddExternalSourceFact(facts, "MusicBrainz", "musicbrainz_id", GetBaselineValue("musicbrainz_id"));
        AddExternalSourceFact(facts, "Apple Books", "apple_books_id", GetBaselineValue("apple_books_id"));
        return facts;
    }

    protected static string GetSourceFactIcon(string label) =>
        label switch
        {
            "Release" => Icons.Material.Outlined.CalendarToday,
            "Episode" => Icons.Material.Outlined.OndemandVideo,
            "Actors" => Icons.Material.Outlined.Groups,
            "Runtime" => Icons.Material.Outlined.Schedule,
            "Pages" => Icons.Material.Outlined.MenuBook,
            "Language" => Icons.Material.Outlined.Translate,
            "Rating" => Icons.Material.Outlined.StarOutline,
            "Provider" => Icons.Material.Outlined.MovieCreation,
            _ => Icons.Material.Outlined.Info,
        };

    private string? GetSourceProviderDisplayName()
    {
        if (UsesParentRetailIdentityOnly)
        {
            var seriesProvider = GetRetailMatchDisplayName(GetScopeById("series")?.IdentitySummary);
            return string.IsNullOrWhiteSpace(seriesProvider) ? null : $"{seriesProvider} (Series context)";
        }

        var provider = GetRetailMatchDisplayName(IdentityTargetSummary);
        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider;
        }

        var providerKey = StringHelpers.FirstNonBlank(
            InferProviderNameFromIdentifierFields(),
            NormalizeRetailProviderLabel(_detail?.MatchSource));

        return string.IsNullOrWhiteSpace(providerKey)
            ? null
            : FormatProviderName(providerKey, EditorMediaType);
    }

    private static string? FormatEpisodeIdentity(string? seasonNumber, string? episodeNumber)
    {
        if (string.IsNullOrWhiteSpace(seasonNumber) && string.IsNullOrWhiteSpace(episodeNumber))
        {
            return null;
        }

        return $"S{(string.IsNullOrWhiteSpace(seasonNumber) ? "?" : seasonNumber.Trim())} E{(string.IsNullOrWhiteSpace(episodeNumber) ? "?" : episodeNumber.Trim())}";
    }

    private static string? FormatRuntimeFact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (Regex.IsMatch(normalized, @"[A-Za-z]"))
        {
            return normalized;
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return $"{minutes:0.#} min";
        }

        return normalized;
    }

    private static string? FormatCountFact(string? value, string singular, string plural)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (Regex.IsMatch(normalized, @"[A-Za-z]"))
        {
            return normalized;
        }

        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? $"{count:N0} {(count == 1 ? singular : plural)}"
            : normalized;
    }

    private static void AddSourceFact(List<(string Label, string Value, string? Url)> facts, string label, string? value, string? url = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add((label, value.Trim(), url));
        }
    }

    private void AddExternalSourceFact(
        List<(string Label, string Value, string? Url)> facts,
        string label,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var link = LibraryHelpers.BuildProviderUrl(key, value.Trim(), EditorMediaType);
        AddSourceFact(facts, label, value, link?.Url);
    }

    protected static IReadOnlyList<string> SplitDisplayList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : Regex.Split(value, @"\s*[;,|]\s*")
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    protected static string NormalizeContributorDisplay(string? value) =>
        NormalizeDelimitedDisplay(value, "; ");

    protected static string NormalizeDisplaySpacing(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value.Trim(), @";\s*", "; ");

    private static string NormalizeDelimitedDisplay(string? value, string separator) =>
        string.Join(separator, SplitDisplayList(value));

    private static string? FormatRatingValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
            ? rating.ToString("0.0", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    protected string ArtworkShapeClass => GetArtworkShapeClass(EditorMediaType, NavigatorRootNode?.ScopeId ?? ActiveScope?.ScopeId);

    private static string GetArtworkShapeClass(string? mediaType, string? scopeId)
    {
        var normalized = ReviewTargetResolver.NormalizeMediaType(mediaType);
        if (string.Equals(normalized, "Music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Audiobooks", StringComparison.OrdinalIgnoreCase))
        {
            return "is-square";
        }

        if (string.Equals(normalized, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(scopeId, "episode", StringComparison.OrdinalIgnoreCase))
        {
            return "is-landscape";
        }

        return "is-portrait";
    }

    private bool IsMultilineField(string key) =>
        GetGroupsForTab("details")
            .SelectMany(group => group.Fields)
            .Any(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(field.InputKind, "textarea", StringComparison.OrdinalIgnoreCase));

    protected bool IsInlineFieldReadOnly(string key)
    {
        if (!IsFieldLocked(key))
        {
            return false;
        }

        var scopedKey = BuildScopedFieldKey(key);
        return !_inlineOverrideKeys.Contains(scopedKey)
               && !HasActiveDisplayOverride(key)
               && !(_editedValues.ContainsKey(scopedKey) && !_clearedInlineOverrideKeys.Contains(scopedKey));
    }

    protected bool IsInlineFieldAutoFocus(string key)
    {
        var scopedKey = BuildScopedFieldKey(key);
        if (!string.Equals(_fieldToFocus, scopedKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _fieldToFocus = null;
        return true;
    }

    protected string? GetInlineFieldHelpText(MediaEditorFieldDefinition field)
    {
        if (HasActiveDisplayOverride(field.Key))
        {
            return "Local override. Revert to use the canonical value.";
        }

        if (IsInlineOverrideEnabled(field.Key))
        {
            return "Local override is enabled. Changes will be saved locally.";
        }

        if (IsFieldLocked(field.Key))
        {
            return "Canonical value. Unlock to save a local override.";
        }

        return field.IdentityField ? "Used for matching and identity." : null;
    }

    protected string? GetInlineFieldActionIcon(string key)
    {
        if (HasActiveDisplayOverride(key))
        {
            return Icons.Material.Outlined.Restore;
        }

        if (IsInlineOverrideEnabled(key))
        {
            return Icons.Material.Outlined.LockOpen;
        }

        return IsFieldLocked(key) ? Icons.Material.Outlined.Lock : null;
    }

    protected string GetInlineFieldActionLabel(string key, string label) =>
        HasActiveDisplayOverride(key)
            ? $"Remove local override for {label}"
            : $"Use local override for {label}";

    protected AppUiTone GetInlineFieldActionTone(string key) =>
        HasActiveDisplayOverride(key)
            ? AppUiTone.Warning
            : IsInlineOverrideEnabled(key) ? AppUiTone.Warning : AppUiTone.Neutral;

    protected Task HandleInlineFieldActionAsync(string key, string label)
    {
        var scopedKey = BuildScopedFieldKey(key);

        if (HasActiveDisplayOverride(key))
        {
            _pendingInlineRevertKeys.Add(scopedKey);
            StateHasChanged();
            return Task.CompletedTask;
        }

        if (IsInlineOverrideEnabled(key))
        {
            _inlineOverrideKeys.Remove(scopedKey);
            _pendingInlineRevertKeys.Remove(scopedKey);
            if (string.Equals(_fieldToFocus, scopedKey, StringComparison.OrdinalIgnoreCase))
            {
                _fieldToFocus = null;
            }

            return InvokeAsync(StateHasChanged);
        }

        _inlineOverrideKeys.Add(scopedKey);
        _clearedInlineOverrideKeys.Remove(scopedKey);
        _pendingInlineRevertKeys.Remove(scopedKey);
        _fieldToFocus = scopedKey;
        return InvokeAsync(StateHasChanged);
    }

    protected bool IsInlineFieldConfirmingRevert(string key) =>
        _pendingInlineRevertKeys.Contains(BuildScopedFieldKey(key));

    protected Task ConfirmInlineFieldRevertAsync(string key)
    {
        var scopedKey = BuildScopedFieldKey(key);
        if (HasSavedDisplayOverride(key))
        {
            _editedValues[scopedKey] = string.Empty;
            _clearedInlineOverrideKeys.Add(scopedKey);
        }
        else
        {
            _editedValues.Remove(scopedKey);
            _clearedInlineOverrideKeys.Remove(scopedKey);
        }

        _inlineOverrideKeys.Remove(scopedKey);
        _pendingInlineRevertKeys.Remove(scopedKey);
        return InvokeAsync(StateHasChanged);
    }

    protected void CancelInlineFieldRevert(string key)
    {
        _pendingInlineRevertKeys.Remove(BuildScopedFieldKey(key));
        StateHasChanged();
    }

    protected IReadOnlyList<(string Label, string Value)> GetMatchInformationRows()
    {
        var summary = ActiveIdentitySummary;
        var canonicalSummary = CanonicalIdentityTargetSummary;
        var provider = GetRetailMatchDisplayName(summary);
        var hasProviderEvidence = !string.IsNullOrWhiteSpace(provider) || !string.IsNullOrWhiteSpace(summary?.ProviderItemId);
        var hasQid = !string.IsNullOrWhiteSpace(canonicalSummary?.WikidataQid);

        return
        [
            ("Retail Match", !string.IsNullOrWhiteSpace(provider) ? provider : hasProviderEvidence ? "Retail matched" : "Not linked"),
            ("Canonical Identity", hasQid ? "Matched" : "Not matched"),
            ("Status", GetMatchStatusLabel(summary, hasProviderEvidence, hasQid)),
            ("File Metadata", _editorContext?.FileMetadataSyncStatus ?? "Unknown"),
            ("Universe", string.IsNullOrWhiteSpace(summary?.UniverseName) ? "-" : summary!.UniverseName!),
        ];
    }

    private string GetMatchStatusLabel(MediaEditorIdentitySummaryDto? summary, bool hasProvider, bool hasQid)
    {
        if (Request.Mode == SharedMediaEditorMode.Review)
        {
            return "Needs review";
        }

        var wikidataStatus = NormalizeIdentityStatus(summary?.WikidataStatus);
        if (wikidataStatus is "user_confirmed" or "user_replaced")
        {
            return "User reviewed";
        }

        if (wikidataStatus is "auto_aligned" or "confirmed")
        {
            return "Identified";
        }

        if (wikidataStatus is "missing" or "provider_only")
        {
            return "Provider only";
        }

        if (wikidataStatus is "user_rejected")
        {
            return "Rejected";
        }

        if (!string.IsNullOrWhiteSpace(_detail?.Status))
        {
            var detailStatus = FormatDetailStatus(_detail.Status);
            if (!string.IsNullOrWhiteSpace(detailStatus))
            {
                return detailStatus;
            }
        }

        if (hasProvider && hasQid)
        {
            return "Identified";
        }

        if (hasProvider)
        {
            return "Pending";
        }

        return "Pending";
    }

    private static string? FormatDetailStatus(string? status)
    {
        var normalized = NormalizeIdentityStatus(status);
        return normalized switch
        {
            "" => null,
            "work" or "edition" or "asset" or "confirmed" or "identified" => "Identified",
            "pending" or "processing" => "Pending",
            "needs_review" or "review" => "Needs review",
            "provider_only" => "Provider only",
            "rejected" => "Rejected",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.Replace('_', ' ')),
        };
    }

    private string GetRetailMatchDisplayName(MediaEditorIdentitySummaryDto? summary)
    {
        var providerName = NormalizeRetailProviderLabel(summary?.ProviderName);
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            return FormatProviderName(providerName, EditorMediaType);
        }

        return string.Empty;
    }

    private static string? NormalizeRetailProviderLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "provider", StringComparison.OrdinalIgnoreCase)
            || IsBridgeOnlyProvider(trimmed)
            || LooksLikeBridgeIdentifier(trimmed)
            || Guid.TryParse(trimmed, out _))
        {
            return null;
        }

        return trimmed;
    }

    private static bool IsBridgeOnlyProvider(string value) =>
        value is "imdb" or "imdb_id" or "isbn" or "isbn_10" or "isbn_13" or "asin" or "wikidata" or "wikidata_qid"
        || string.Equals(value, "imdb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "imdb_id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "isbn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "isbn_10", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "isbn_13", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "asin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "wikidata", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "wikidata_qid", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBridgeIdentifier(string value) =>
        value.Contains(':', StringComparison.Ordinal)
        || value.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || value.Contains("_id:", StringComparison.OrdinalIgnoreCase);

    protected string GetMatchInformationNote()
    {
        var note = HasCanonicalWikidataIdentity
            ? "Canonical identity is matched. Locked fields use canonical values unless you add a local override."
            : "Retail data is available. Wikidata identity is not confirmed yet.";

        if (Request.Mode == SharedMediaEditorMode.Review)
        {
            note += " Resolve the review after confirming the match.";
        }

        return note;
    }

    private static string ResolveFooterPrimaryActionLabel(MediaEditorIdentityIntent intent) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch => "Use Selected Retail Match",
            MediaEditorIdentityIntent.ConfirmRetailMatch => "Confirm Retail Match",
            MediaEditorIdentityIntent.FixWikidataMatch => "Replace Wikidata Match",
            MediaEditorIdentityIntent.MarkWikidataMissing => "Mark Provider-Only",
            MediaEditorIdentityIntent.ConfirmArtwork => "Confirm Artwork",
            MediaEditorIdentityIntent.ReviewClassification => "Review classification",
            MediaEditorIdentityIntent.ResolveWriteback => "Retry Writeback",
            _ => "Save Local Changes",
        };

    private static string NormalizeIdentityStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

    private bool IsDisplayOverrideKey(string key) =>
        _editorContext?.DisplayOverrideKeys.Contains(key, StringComparer.OrdinalIgnoreCase) == true;

    private bool HasSavedDisplayOverride(string key) =>
        _editorContext?.DisplayOverrides.ContainsKey(key) == true;

    protected bool HasActiveDisplayOverride(string key)
    {
        var scopedKey = BuildScopedFieldKey(key);
        if (_clearedInlineOverrideKeys.Contains(scopedKey))
        {
            return false;
        }

        return HasSavedDisplayOverride(key)
               || (_editedValues.ContainsKey(scopedKey) && ShouldSaveAsDisplayOverride(scopedKey, key));
    }

    private bool IsInlineOverrideEnabled(string key)
    {
        var scopedKey = BuildScopedFieldKey(key);
        return _inlineOverrideKeys.Contains(scopedKey)
               && !HasActiveDisplayOverride(key)
               && !_clearedInlineOverrideKeys.Contains(scopedKey);
    }

    protected IReadOnlyList<string> GetActiveOverrideLabels()
    {
        var labels = GetGroupsForTab("details")
            .SelectMany(group => group.Fields)
            .Where(field => HasActiveDisplayOverride(field.Key))
            .Select(field => field.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in _editorContext?.DisplayOverrideKeys ?? [])
        {
            if (!HasActiveDisplayOverride(key))
            {
                continue;
            }

            var label = GetDisplayOverrideLabel(key);
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    private bool ShouldSaveAsDisplayOverride(string scopedKey, string key) =>
        IsDisplayOverrideKey(key)
        || _inlineOverrideKeys.Contains(scopedKey)
        || _clearedInlineOverrideKeys.Contains(scopedKey)
        || HasSavedDisplayOverride(key);

    protected bool IsFieldLocked(string key) =>
        (IsDisplayOverrideKey(key)
            && !HasActiveDisplayOverride(key)
            && !IsInlineOverrideEnabled(key))
        || (_editorContext?.FieldLockMap.TryGetValue(key, out var locked) == true && locked)
        || (HasCanonicalWikidataIdentity
            && IsIdentityField(key)
            && !IsDisplayOverrideKey(key));

    private bool HasCanonicalWikidataIdentity =>
        !string.IsNullOrWhiteSpace(CanonicalIdentityTargetSummary?.WikidataQid)
        && (CanonicalIdentityTargetSummary?.WikidataStatus is "confirmed" or "auto_aligned" or "user_confirmed" or "user_replaced"
            || !string.IsNullOrWhiteSpace(CanonicalIdentityTargetSummary?.WikidataQid));

    private bool IsIdentityField(string key) =>
        _schema.Groups
            .SelectMany(group => group.Fields)
            .Any(field => field.IdentityField && string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string GetArtworkEmptyStateLabel(string assetType) =>
        assetType switch
        {
            "Background" => "No background art stored yet.",
            "Logo" => "No logo art stored yet.",
            "CoverArt" => "No cover art stored yet.",
            "SeasonPoster" => "No season poster stored yet.",
            "SeasonThumb" => "No season thumb stored yet.",
            "EpisodeStill" => "No episode still stored yet.",
            _ => "No artwork stored yet.",
        };

    private static string? GetScopePickerTitle(MediaEditorScopeDto? scope)
    {
        if (scope is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(scope.DisplayTitle)
            && !string.Equals(scope.DisplayTitle, scope.Label, StringComparison.OrdinalIgnoreCase))
        {
            return scope.DisplayTitle;
        }

        return string.IsNullOrWhiteSpace(scope.DisplaySubtitle) ? null : scope.DisplaySubtitle;
    }

    private IReadOnlyList<ArtworkSlotDefinition> ResolveArtworkSlots(MediaEditorScopeDto? scope) =>
        scope is not { CanEditArtwork: true } ? [] : scope.ArtworkSlots
            .Select(type => type switch
            {
                "CoverArt" => PosterCoverArtworkSlot,
                "Background" => BackgroundArtworkSlot,
                "Logo" => LogoArtworkSlot,
                "SeasonPoster" => SeasonPosterArtworkSlot,
                "SeasonThumb" => SeasonThumbArtworkSlot,
                "EpisodeStill" => EpisodeStillArtworkSlot,
                _ => null,
            }).OfType<ArtworkSlotDefinition>().ToList();

    private IReadOnlyList<string> GetPlacementFieldKeysForActiveScope()
    {
        if (ActiveScope is null)
        {
            return [];
        }

        return (EditorMediaType, ActiveScope.ScopeId) switch
        {
            ("TV", "episode") => ["show_name", "season_number", "episode_number", "episode_title"],
            ("Music", "track") => ["artist", "album", "track_number", "disc_number"],
            _ => [],
        };
    }

    private bool SupportsMembershipSuggestions(string fieldKey) =>
        !string.IsNullOrWhiteSpace(GetMembershipSuggestionField(fieldKey));

    private string? GetMembershipSuggestionField(string fieldKey) =>
        (EditorMediaType, ActiveScope?.ScopeId, fieldKey) switch
        {
            ("TV", "episode", "show_name") => "show",
            ("TV", "episode", "season_number") => "season",
            ("Music", "track", "artist") => "artist",
            ("Music", "track", "album") => "album",
            _ => null,
        };

    private async Task OnMembershipFieldInputAsync(string key, string? value)
    {
        OnFieldInput(key, value);

        var scopedKey = BuildScopedFieldKey(key);
        _pendingMembershipPreview = null;
        _selectedMembershipTargetIds.Remove(scopedKey);
        _selectedMembershipSuggestions.Remove(scopedKey);
        ClearDependentMembershipSelections(key);

        var suggestionField = GetMembershipSuggestionField(key);
        var query = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(suggestionField) || query.Length < 2)
        {
            _membershipSuggestions.Remove(scopedKey);
            return;
        }

        var parentContext = ResolveMembershipSuggestionParentContext(key);
        var suggestions = await ApiClient.GetMediaEditorMembershipSuggestionsAsync(
            CurrentEntityId,
            suggestionField,
            query,
            source: "local",
            parentEntityId: parentContext.ParentEntityId,
            parentValue: parentContext.ParentValue);

        _membershipSuggestions[scopedKey] = suggestions;
        StateHasChanged();
    }

    private async Task ApplyMembershipSuggestionAsync(string key, MediaEditorMembershipSuggestionDto suggestion)
    {
        OnFieldInput(key, suggestion.Label);

        var scopedKey = BuildScopedFieldKey(key);
        _selectedMembershipTargetIds[scopedKey] = suggestion.EntityId;
        _selectedMembershipSuggestions[scopedKey] = suggestion;
        _membershipSuggestions.Remove(scopedKey);
        _pendingMembershipPreview = null;

        ClearDependentMembershipSelections(key);

        if (string.Equals(key, "season_number", StringComparison.OrdinalIgnoreCase))
        {
            _editedValues[scopedKey] = ExtractLeadingNumber(suggestion.Label);
        }

        await InvokeAsync(StateHasChanged);
    }

    private IReadOnlyList<MediaEditorMembershipSuggestionDto> GetMembershipSuggestions(string key) =>
        _membershipSuggestions.TryGetValue(BuildScopedFieldKey(key), out var suggestions)
            ? suggestions
            : [];

    private void ClearMembershipSuggestions(string key) =>
        _membershipSuggestions.Remove(BuildScopedFieldKey(key));

    private (Guid? ParentEntityId, string? ParentValue) ResolveMembershipSuggestionParentContext(string fieldKey)
    {
        if (string.Equals(fieldKey, "season_number", StringComparison.OrdinalIgnoreCase))
        {
            var showKey = BuildScopedFieldKey("show_name");
            return (
                _selectedMembershipTargetIds.TryGetValue(showKey, out var targetId) ? targetId : null,
                GetEditableValue("show_name"));
        }

        if (string.Equals(fieldKey, "album", StringComparison.OrdinalIgnoreCase))
        {
            return (null, GetEditableValue("artist"));
        }

        return (null, null);
    }

    protected bool CanSearchRetailMembership(string fieldKey) =>
        (EditorMediaType, ActiveScope?.ScopeId, fieldKey) switch
        {
            ("TV", "episode", "show_name") => true,
            ("Music", "track", "artist") => true,
            ("Music", "track", "album") => true,
            _ => false,
        };

    protected async Task SearchRetailMembershipAsync(string fieldKey)
    {
        if (!CanSearchRetailMembership(fieldKey))
        {
            return;
        }

        var query = GetEditableValue(fieldKey).Trim();
        if (query.Length < 2)
        {
            Snackbar.Add("Enter at least two characters before searching retail results.", Severity.Info);
            return;
        }

        var suggestionField = GetMembershipSuggestionField(fieldKey);
        if (string.IsNullOrWhiteSpace(suggestionField))
        {
            return;
        }

        var parentContext = ResolveMembershipSuggestionParentContext(fieldKey);
        var scopedKey = BuildScopedFieldKey(fieldKey);
        var suggestions = await ApiClient.GetMediaEditorMembershipSuggestionsAsync(
            CurrentEntityId,
            suggestionField,
            query,
            source: "retail",
            parentEntityId: parentContext.ParentEntityId,
            parentValue: parentContext.ParentValue);

        _membershipSuggestions[scopedKey] = suggestions;
        StateHasChanged();
    }

    private void ClearDependentMembershipSelections(string fieldKey)
    {
        if (string.Equals(fieldKey, "show_name", StringComparison.OrdinalIgnoreCase))
        {
            _selectedMembershipTargetIds.Remove(BuildScopedFieldKey("season_number"));
            _selectedMembershipSuggestions.Remove(BuildScopedFieldKey("season_number"));
            _membershipSuggestions.Remove(BuildScopedFieldKey("season_number"));
        }
        else if (string.Equals(fieldKey, "artist", StringComparison.OrdinalIgnoreCase))
        {
            _selectedMembershipTargetIds.Remove(BuildScopedFieldKey("album"));
            _selectedMembershipSuggestions.Remove(BuildScopedFieldKey("album"));
            _membershipSuggestions.Remove(BuildScopedFieldKey("album"));
        }
    }

    private async Task<MediaEditorMembershipPreviewDto?> PreviewMembershipChangeAsync()
    {
        if (!IsSingleItem || ActiveScope is null)
        {
            return null;
        }

        var placementFields = GetPlacementFieldKeysForActiveScope();
        if (placementFields.Count == 0)
        {
            return null;
        }

        var hasPlacementEdit = placementFields.Any(field =>
        {
            var scopedKey = BuildScopedFieldKey(field);
            return _editedValues.ContainsKey(scopedKey) || _selectedMembershipTargetIds.ContainsKey(scopedKey);
        });

        if (!hasPlacementEdit)
        {
            return null;
        }

        var preview = await ApiClient.PreviewMediaEditorMembershipAsync(CurrentEntityId, BuildMembershipPreviewRequest());
        return preview;
    }

    private MediaEditorMembershipPreviewRequestDto BuildMembershipPreviewRequest()
    {
        var fieldValues = BuildDraftFields()
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);

        var targetIds = _selectedMembershipTargetIds
            .Where(entry =>
            {
                var parsed = ParseScopedKey(entry.Key);
                return parsed.EntityId == CurrentEntityId
                       && string.Equals(parsed.ScopeId, ActiveScope?.ScopeId, StringComparison.OrdinalIgnoreCase);
            })
            .ToDictionary(entry => ParseScopedKey(entry.Key).Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var selectedSuggestions = _selectedMembershipSuggestions
            .Where(entry =>
            {
                var parsed = ParseScopedKey(entry.Key);
                return parsed.EntityId == CurrentEntityId
                       && string.Equals(parsed.ScopeId, ActiveScope?.ScopeId, StringComparison.OrdinalIgnoreCase);
            })
            .ToDictionary(entry => ParseScopedKey(entry.Key).Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        return new MediaEditorMembershipPreviewRequestDto
        {
            ScopeId = ActiveScope?.ScopeId,
            FieldValues = fieldValues,
            SelectedTargetIds = targetIds,
            SelectedSuggestions = selectedSuggestions,
        };
    }

    private IReadOnlyList<MediaEditorNavigatorNodeDto> GetNavigatorChildren(Guid? parentNodeId)
    {
        if (_navigator is null)
        {
            return [];
        }

        return _navigator.Nodes
            .Where(node => node.ParentNodeId == parentNodeId)
            .OrderBy(node => node.Depth)
            .ThenBy(node => ParseOrdinalLabel(node.OrdinalLabel))
            .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<MediaEditorNavigatorNodeDto> GetNavigatorDescendants(MediaEditorNavigatorNodeDto? parent)
    {
        if (parent is null || _navigator is null)
        {
            return [];
        }

        var descendants = new List<MediaEditorNavigatorNodeDto>();
        var pending = new Queue<Guid>();
        pending.Enqueue(parent.NodeId);
        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            foreach (var child in GetNavigatorChildren(parentId))
            {
                descendants.Add(child);
                pending.Enqueue(child.NodeId);
            }
        }

        return descendants;
    }

    private IReadOnlyList<MediaEditorNavigatorNodeDto> GetQuarantineTargetNodes(MediaEditorNavigatorNodeDto node)
    {
        if (_navigator is null)
        {
            return [];
        }

        if (node.IsLeaf)
        {
            return node.CanQuarantine ? [node] : [];
        }

        var descendants = new List<MediaEditorNavigatorNodeDto>();
        foreach (var child in GetNavigatorChildren(node.NodeId))
        {
            descendants.AddRange(GetQuarantineTargetNodes(child));
        }

        return descendants
            .Where(item => item.CanQuarantine)
            .GroupBy(item => item.EntityId)
            .Select(group => group.First())
            .ToList();
    }

    private IReadOnlyList<Guid> GetQuarantineTargetEntityIds()
    {
        if (IsBatchMode || ActiveScope is null)
        {
            return [];
        }

        if (IsContainerEditor && SelectedNavigatorNode is not null)
        {
            return GetQuarantineTargetNodes(SelectedNavigatorNode).Select(node => node.EntityId).ToList();
        }

        return CurrentEntityId == Guid.Empty ? [] : [CurrentEntityId];
    }

    protected async Task ToggleQuarantineConfirmAsync()
    {
        _showQuarantineConfirm = !_showQuarantineConfirm;
        if (!_showQuarantineConfirm)
        {
            return;
        }

        await InvokeAsync(StateHasChanged);
    }

    protected async Task QuarantineCurrentSelectionAsync()
    {
        var entityIds = GetQuarantineTargetEntityIds();
        if (entityIds.Count == 0)
        {
            return;
        }

        StateHasChanged();

        try
        {
            var response = entityIds.Count == 1
                ? await ApiClient.RejectLibraryCatalogItemAsync(entityIds[0])
                : await ApiClient.BatchRejectLibraryCatalogItemsAsync(entityIds.ToArray());

            if (response is null)
            {
                Snackbar.Add("Quarantine failed.", Severity.Error);
                return;
            }

            Snackbar.Add(entityIds.Count == 1 ? "Item quarantined." : $"{entityIds.Count} items quarantined.", Severity.Success);

            if (IsContainerEditor)
            {
                var fallbackEntityId = ResolveNavigatorFallbackEntityAfterQuarantine();
                if (fallbackEntityId != Guid.Empty)
                {
                    await LoadSingleItemAsync(fallbackEntityId, resetEditorState: false);
                    return;
                }
            }

            await CloseEditorAsync(applied: true);
        }
        finally
        {
            _showQuarantineConfirm = false;
            StateHasChanged();
        }
    }

    private Guid ResolveNavigatorFallbackEntityAfterQuarantine()
    {
        var currentNode = SelectedNavigatorNode;
        if (currentNode is null || _navigator is null)
        {
            return Guid.Empty;
        }

        if (currentNode.ParentNodeId is Guid parentNodeId)
        {
            var parentNode = _navigator.Nodes.FirstOrDefault(node => node.NodeId == parentNodeId);
            if (parentNode is not null)
            {
                return parentNode.EntityId;
            }
        }

        return NavigatorRootNode?.EntityId ?? Guid.Empty;
    }

    protected string GetDisplayOverrideLabel(string key) =>
        key switch
        {
            "title" => "Display title",
            "description" => "Description",
            "tagline" => "Tagline",
            "subtitle" => "Subtitle",
            "sort_title" => "Sort Title",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace('_', ' ')),
        };

    private static string NormalizeTabId(string? tabId) => MediaEditorTabState.Normalize(tabId);

    private async Task SelectTabInternalAsync(string tabId)
    {
        if (IsTabDisabled(tabId))
        {
            return;
        }

        var normalized = NormalizeTabId(tabId);
        if (string.Equals(normalized, "file", StringComparison.OrdinalIgnoreCase))
        {
            _tabState.ActivateFile();
            await JS.InvokeVoidAsync("tuvimaEditorScrollTop");
            return;
        }

        _tabState.Activate(normalized);
        await JS.InvokeVoidAsync("tuvimaEditorScrollTop");

        if (string.Equals(normalized, "links", StringComparison.OrdinalIgnoreCase))
        {
            InitializeMatchSearchState();
        }

        if (IsFileScope)
        {
            var fallbackScopeId = _editorContext?.Scopes
                .OrderBy(scope => scope.Order)
                .FirstOrDefault(scope => !string.Equals(scope.ScopeId, "file", StringComparison.OrdinalIgnoreCase))
                ?.ScopeId;

            if (!string.IsNullOrWhiteSpace(fallbackScopeId))
            {
                await SelectScopeAsync(fallbackScopeId);
            }
        }
    }

    private static int ParseOrdinalLabel(string? ordinalLabel)
    {
        if (string.IsNullOrWhiteSpace(ordinalLabel))
        {
            return int.MaxValue;
        }

        var digits = new string(ordinalLabel.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : int.MaxValue;
    }

    private static string ExtractLeadingNumber(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var digits = new string(input.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? input : digits;
    }

    private async Task RefreshArtworkStateAsync(string? scopeId = null, bool notifyParent = false)
    {
        if (!IsSingleItem)
        {
            return;
        }

        var targetScopeId = scopeId ?? ArtworkScope?.ScopeId;
        if (string.IsNullOrWhiteSpace(targetScopeId))
        {
            return;
        }

        var targetScope = GetScopeById(targetScopeId);
        if (targetScope is null)
        {
            return;
        }

        var stateKey = BuildScopeStateKey(targetScope.FieldEntityId, targetScope.ScopeId);

        _artworkStates.Remove(stateKey);
        _scopeStates.Remove(stateKey);

        if (string.Equals(ActiveScope?.ScopeId, targetScopeId, StringComparison.OrdinalIgnoreCase))
        {
            await LoadScopeStateAsync(forceReload: true);
        }
        else
        {
            await LoadArtworkStateAsync(targetScopeId, forceReload: true);
        }

        if (HasContextNavigator)
            _navigator = await ApiClient.GetMediaEditorNavigatorAsync(EditorContextEntityId) ?? _navigator;
        CloseArtworkZoom();
        NormalizeArtworkSelection();
        StateHasChanged();

        if (notifyParent && Request.OnArtworkChanged is not null)
        {
            await Request.OnArtworkChanged.Invoke();
        }
    }

    private void NormalizeArtworkSelection()
    {
        var availableSlots = ArtworkSlots;
        if (availableSlots.Count == 0)
        {
            _artworkAddMenuAssetType = null;
            _artworkUrlInput = string.Empty;
            _showArtworkUrlInput = false;
            _selectedArtworkAssetType = null;
            _focusedArtworkVariantKey = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedArtworkAssetType)
            || !availableSlots.Any(slot => string.Equals(slot.AssetType, _selectedArtworkAssetType, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedArtworkAssetType = availableSlots[0].AssetType;
        }

        if (!string.IsNullOrWhiteSpace(_artworkAddMenuAssetType)
            && !availableSlots.Any(slot => string.Equals(slot.AssetType, _artworkAddMenuAssetType, StringComparison.OrdinalIgnoreCase)))
        {
            _artworkAddMenuAssetType = null;
            _artworkUrlInput = string.Empty;
            _showArtworkUrlInput = false;
        }

        var selectedAssetType = _selectedArtworkAssetType!;
        var validKeys = GetArtworkGalleryItems(selectedAssetType).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        if (validKeys.Count == 0)
        {
            _focusedArtworkVariantKey = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_focusedArtworkVariantKey) || !validKeys.Contains(_focusedArtworkVariantKey))
        {
            _focusedArtworkVariantKey = GetArtworkGalleryItems(selectedAssetType).FirstOrDefault(item => item.IsPending)?.Key
                                        ?? GetArtworkGalleryItems(selectedAssetType).FirstOrDefault(item => item.IsPreferred)?.Key
                                        ?? GetArtworkGalleryItems(selectedAssetType).First().Key;
        }
    }

    private static string BuildPendingArtworkKey(string assetType) =>
        $"pending:{assetType.ToLowerInvariant()}";

    private static string BuildVariantKey(Guid variantId) =>
        variantId == Guid.Empty ? "synthetic" : variantId.ToString("D");

    private static string? FormatCountBadge(int count) => count > 0 ? count.ToString() : null;

    private string BuildScopedFieldKey(string key) =>
        IsBatchMode || ActiveScope is null
            ? key
            : $"{ActiveScope.FieldEntityId:D}|{ActiveScope.ScopeId}|{key}";

    private string BuildScopedArtworkKey(string? scopeId, string assetType) =>
        IsBatchMode || string.IsNullOrWhiteSpace(scopeId)
            ? assetType
            : $"{(GetScopeById(scopeId)?.FieldEntityId ?? EditorContextEntityId):D}|{scopeId}|{assetType}";

    private static (Guid EntityId, string ScopeId, string Key) ParseScopedKey(string compositeKey)
    {
        var segments = compositeKey.Split('|', 3, StringSplitOptions.None);
        if (segments.Length == 3 && Guid.TryParse(segments[0], out var entityId))
        {
            return (entityId, segments[1], segments[2]);
        }

        if (segments.Length == 2)
        {
            return (Guid.Empty, segments[0], segments[1]);
        }

        return (Guid.Empty, string.Empty, compositeKey);
    }

    private static string BuildScopeStateKey(Guid entityId, string scopeId) =>
        $"{entityId:D}|{scopeId}";

    private readonly record struct ScopedEditorKey(Guid EntityId, string ScopeId);

    private sealed class ScopedEditorKeyComparer : IEqualityComparer<ScopedEditorKey>
    {
        public static ScopedEditorKeyComparer Instance { get; } = new();

        public bool Equals(ScopedEditorKey x, ScopedEditorKey y) =>
            x.EntityId == y.EntityId && string.Equals(x.ScopeId, y.ScopeId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ScopedEditorKey obj) =>
            HashCode.Combine(obj.EntityId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ScopeId ?? string.Empty));
    }

    private MediaEditorScopeDto? GetScopeById(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId)
            ? null
            : _editorContext?.Scopes.FirstOrDefault(scope =>
                string.Equals(scope.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase));

    private bool ActiveScopeExists(string scopeId) => GetScopeById(scopeId) is not null;

    private string BuildBreadcrumbText()
    {
        if (_editorContext is null || ActiveScope is null)
        {
            return string.Empty;
        }

        var breadcrumbParts = _editorContext.Scopes
            .Where(scope => !string.Equals(scope.ScopeId, "file", StringComparison.OrdinalIgnoreCase)
                            && scope.Order <= ActiveScope.Order
                            && !string.IsNullOrWhiteSpace(scope.BreadcrumbLabel))
            .OrderBy(scope => scope.Order)
            .Select(scope => scope.BreadcrumbLabel)
            .ToList();

        if (IsFileScope)
        {
            breadcrumbParts.Add("File");
        }

        return string.Join(" > ", breadcrumbParts);
    }

    private string? GetHeaderArtworkPreviewUrl()
    {
        if (HasContextNavigator && NavigatorRootNode?.EntityId != ActiveScope?.FieldEntityId)
            return GetContextArtworkUrl(NavigatorRootNode);
        var headerScope = ActiveScope;
        foreach (var slot in ResolveArtworkSlots(headerScope))
        {
            var previewUrl = GetHeaderArtworkPreviewUrl(headerScope, slot.AssetType);
            if (!string.IsNullOrWhiteSpace(previewUrl))
            {
                return previewUrl;
            }
        }

        return HasContextNavigator ? GetContextArtworkUrl(NavigatorRootNode) : Request.CoverUrl;
    }

    private string? GetHeaderArtworkPreviewUrl(MediaEditorScopeDto? scope, string assetType)
    {
        var artwork = scope is null
            ? _artwork
            : _artworkStates.TryGetValue(BuildScopeStateKey(scope.FieldEntityId, scope.ScopeId), out var scopedArtwork)
                ? scopedArtwork
                : _artwork;

        return artwork?.Slots.FirstOrDefault(slot =>
            string.Equals(slot.AssetType, assetType, StringComparison.OrdinalIgnoreCase))?.Variants
            .OrderByDescending(variant => variant.IsPreferred)
            .ThenByDescending(variant => variant.CreatedAt)
            .FirstOrDefault()?.ImageUrl;
    }

    protected sealed record ArtworkSlotDefinition(
        string AssetType,
        string Label,
        string Description,
        string Icon,
        string PreviewClass,
        string ImageClass,
        bool UploadEnabled,
        string UploadHelp,
        string MetaLabel);

    protected sealed record ArtworkStateBadge(string Label, string Tone);
    protected sealed record ArtworkVariantDisplayItem(
        string Key,
        Guid VariantId,
        string AssetType,
        string? ImageUrl,
        bool IsPreferred,
        bool IsPending,
        bool CanDelete,
        string Origin,
        string? ProviderName,
        DateTimeOffset? CreatedAt,
        int? WidthPx,
        int? HeightPx);

    protected string GetArtworkSlotCount(ArtworkSlotDefinition slot) =>
        FormatCountBadge(GetArtworkGalleryItems(slot.AssetType).Count) ?? "0";

    private static string NormalizeEditorMediaType(string? mediaType)
    {
        var normalized = ReviewTargetResolver.NormalizeMediaType(mediaType);
        return normalized switch
        {
            "Unknown" => "Books",
            "Movie" => "Movies",
            "Audiobook" => "Audiobooks",
            "Comic" => "Comics",
            _ when string.IsNullOrWhiteSpace(normalized) => "Books",
            _ => normalized,
        };
    }

    private static int GetSelectedIndex(IEnumerable<string?> keys, string? activeKey)
    {
        var index = 0;
        foreach (var key in keys)
        {
            if (string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return 0;
    }
}
