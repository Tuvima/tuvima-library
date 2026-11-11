using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

public partial class DetailPage
{
    // Core state: the parameters the host passes, the tab and presentation values derived from the model, the lifecycle (bind the editor on start, unbind and unsubscribe on dispose), and the page-level view helpers.

    [Parameter, EditorRequired] public DetailPageViewModel? Model { get; set; }
    [Parameter] public string? ActiveTab { get; set; }
    [Parameter] public EventCallback<string> ActiveTabChanged { get; set; }

    /// <summary>The season of a show the page address names; a refresh asks the Engine for the show on that season.</summary>
    [Parameter] public Guid? SelectedSeasonId { get; set; }
    [Parameter] public EventCallback<Guid> SelectedSeasonChanged { get; set; }

    /// <summary>Raised with each payload this page fetches itself, so the host keeps the newest one.</summary>
    [Parameter] public EventCallback<DetailPageViewModel> ModelRefreshed { get; set; }
    [Parameter] public EventCallback<DetailAction> OnActionSelected { get; set; }
    private IReadOnlyList<DetailTab> VisibleTabs =>
        Model is null ? [] : DetailTabNavigation.GetVisibleTabs(Model);

    private string CurrentActiveTab =>
        VisibleTabs.Any(tab => string.Equals(tab.Key, ActiveTab, StringComparison.OrdinalIgnoreCase))
            ? VisibleTabs.First(tab => string.Equals(tab.Key, ActiveTab, StringComparison.OrdinalIgnoreCase)).Key
            : VisibleTabs.FirstOrDefault()?.Key ?? "overview";

    private DetailPresentationCapabilities PresentationCapabilities =>
        DetailPresentationCapabilities.For(Model?.EntityType ?? DetailEntityType.Work);
    private bool IsMusicAlbum => PresentationCapabilities.IsMusicAlbum;
    private bool IsAudiobook => PresentationCapabilities.IsAudiobook
        || MediaKindClassifier.IsAudiobook(Model?.Facts?.MediaKind)
        || Model?.OwnedFormats.Any(format => format.FormatType == MediaFormatType.Audiobook) == true;
    private bool ShowDetailNavigation => Model?.EntityType != DetailEntityType.Person;
    // The Engine only offers "edit" to a person who may edit; the device rule is checked here again at the handler.
    private bool CanEditMedia => DeviceContext.CanEdit && Model?.OverflowActions.Any(action =>
        !action.IsDisabled && action.Key is "edit" or "edit-media") == true;

    // The universal editor is one pop-over for the whole Dashboard (EditorHost, drawn by the layout); this page only binds itself to it when
    // the person presses Edit. These are the editor's own objects, shared by every page: they are never disposed here.
    private EditingModeContext _editing = default!;
    private EditSessionFlow _flow = default!;
    private ArtworkPickerSession _artworkPicker = default!;
    private BulkEditSelection _batch = default!;
    private EditorBinding _binding = default!;
    private Guid? _editingProfileId;

    protected override void OnInitialized()
    {
        _editing = Editor.Mode;
        _flow = Editor.Flow;
        _artworkPicker = Editor.Picker;
        _batch = Editor.Batch;
        _binding = new EditorBinding(DeviceContext)
        {
            ShowHistory = true,
            LoadFields = LoadEditingFieldsAsync,
            OpenItem = OpenEditItemAsync,
            ItemSwitched = AfterEditItemSwitchedAsync,
            Saved = RefreshAfterEditSavedAsync,
            SectionCommitted = RefreshAfterSectionCommitAsync,
            DeletedWithNotice = notice => InvokeAsync(() => _disposed ? Task.CompletedTask : LeaveOrShowRemovedAsync(notice)),
            MediaTypes = () => Model is null ? [] : [ResolveEditorMediaType(Model.EntityType, Model.PresentationContext)],
            Trail = EditTrail,
            EditRowAsync = row => RowEditEntry.StartAsync(Editor, _binding, row),
            UniverseLink = () => Model?.UniverseLink is { Qid.Length: > 0 } link ? new EditorUniverseLink(link.Qid, link.Name) : null,
        };
        Orchestrator.OnLibraryItemsRemoved += OnLibraryItemsRemoved;
        Editor.Changed += OnEditorChanged;
        UpdateEditingEligibility();
    }

    // The staged changes changed (a field typed into, a picture picked): the hero and overview show them before Save changes, behind the
    // dimmed pop-over. Nothing else on the page moves.
    private void OnEditorChanged()
    {
        if (!_disposed && ReferenceEquals(Editor.Binding, _binding))
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // A set page needs the set's own catalog entry to know whether this person may export or remove it.
        if (Model?.CollectionInfo is { IsSet: true } && Guid.TryParse(Model.Id, out var setId) && _setSummaryFor != setId)
        {
            _setSummaryFor = setId;
            var profile = await Orchestrator.GetActiveProfileAsync();
            _setSummary = await ApiClient.GetCollectionSummaryAsync(setId, profile?.Id);
        }
    }

    private Guid? _setSummaryFor;
    private CollectionManagementCatalogViewModel? _setSummary;

    protected override void OnParametersSet()
    {
        if (_undoOffer is not null && Model?.Id != _undoModelId)
        {
            _undoOffer = null;
        }

        UpdateEditingCatalog();
        UpdateEditingEligibility();
    }

    /// <summary>The details overlay this page is shown in; null when the page is rendered on its own.</summary>
    [CascadingParameter] public DetailsOverlayState? Overlay { get; set; }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        Orchestrator.OnLibraryItemsRemoved -= OnLibraryItemsRemoved;
        Editor.Changed -= OnEditorChanged;

        // The editor outlives the page. When it is open on this page it goes with it (the leave guards have already asked).
        Editor.Unbind(_binding);
    }

    private bool HasPrimaryModuleAfterNavigation => Model?.PrimaryModule.Kind is not
        (DetailPrimaryModuleKind.None or DetailPrimaryModuleKind.Tracks or DetailPrimaryModuleKind.Chapters);

    private bool ShowPrimaryModuleAfterNavigation =>
        HasPrimaryModuleAfterNavigation
        && CurrentActiveTab == "overview";

    private string DetailStageClass =>
        $"tl-detail-stage{(HasPrimaryModuleAfterNavigation ? " tl-detail-stage--with-primary" : " tl-detail-stage--identity-only")}" +
        $"{(HasPrimaryModuleAfterNavigation ? " tl-detail-stage--primary-after-navigation" : string.Empty)}" +
        $"{(string.IsNullOrWhiteSpace(PresentationCapabilities.StageModifier) ? string.Empty : $" {PresentationCapabilities.StageModifier}")}" +
        $"{(Model?.EntityType == DetailEntityType.Person ? " tl-detail-stage--person" : string.Empty)}";

    private Task SetActiveTabAsync(string key) => ActiveTabChanged.InvokeAsync(key);

    private async Task HandleContainerSelectedAsync(SequenceContainerOptionViewModel option)
    {
        if (Model is null || string.IsNullOrWhiteSpace(option.ContainerId) || !Guid.TryParse(Model.Id, out var id))
        {
            return;
        }

        var updated = await ApiClient.GetDetailPageAsync(Model.EntityType, id, Model.PresentationContext, option.ContainerId, SequenceWindowQuery.Windowed);
        if (updated is not null && IsStillShowing(id))
        {
            Model = updated;
            await ModelRefreshed.InvokeAsync(updated);
        }
    }
}
