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
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

public partial class DetailPage
{
    // The universal editor binding: the trail and catalog of the page's item, loading the fields of whatever is being edited, opening the editor from the page, the Extras "See all" browser, and refreshing after a save. The page itself is never put into an editing state.

    // Where the item being edited sits (spec D7): the page's own item with its parents (show > season > episode, artist > album > track), read
    // from the page the Dashboard already has. The same lists give each step of the bar the items it can switch to.
    private IReadOnlyList<EditTrailItem> EditTrail() =>
        _binding.Catalog.PageTrail.Count > 0
            ? _binding.Catalog.PageTrail
            : [new EditTrailItem(_editing?.Fields.EntityId ?? Guid.Empty, Model?.Title ?? "This item", "Item")];

    // Children found after the page loaded (the episodes of a season that was not open), kept across page refreshes.
    private readonly Dictionary<Guid, IReadOnlyList<EditTrailItem>> _lateChildren = [];
    private IReadOnlyList<EditTrailItem> _engineChain = [];
    private DetailPageViewModel? _catalogModel;

    private void UpdateEditingCatalog()
    {
        if (_flow is null || ReferenceEquals(_catalogModel, Model))
        {
            return;
        }

        _catalogModel = Model;
        var catalog = EditTrailCatalog.From(Model);
        catalog.AdoptAncestors(_engineChain);
        foreach (var (parent, children) in _lateChildren)
        {
            catalog.AddChildren(parent, children);
        }

        _binding.Catalog = catalog;
    }

    private bool IsPageItem(EditTrailItem item) =>
        _binding.Catalog.PageTrail is not { Count: > 0 } trail || trail[^1].EntityId == item.EntityId;

    // The fields of whatever the person is editing now: after a save or a section commit the editor reloads the item it is on, not the page's.
    private Task LoadEditingFieldsAsync() =>
        _flow.Session is { IsActive: true, ActiveItem: { } active } && !IsPageItem(active)
            ? LoadOtherItemFieldsAsync(active)
            : LoadPageItemFieldsAsync();

    // The person switched to an item of the page (spec D7): its fields load, the panel, the Artwork picker and Match follow, and nothing is
    // saved. Loading only asks for what the item needs; the library's genre and tag lists are reused. False keeps the person on their item.
    private async Task<bool> OpenEditItemAsync(EditTrailItem item)
    {
        if (_editing is null || Model is null || _disposed)
        {
            return false;
        }

        if (IsPageItem(item))
        {
            await LoadPageItemFieldsAsync();
        }
        else
        {
            await LoadOtherItemFieldsAsync(item);
        }

        // Another item must come back as the very item asked for: its changes are staged under that id.
        var fields = _editing.Fields;
        var ready = !_disposed && fields.Loaded && (IsPageItem(item) || fields.EntityId == item.EntityId);
        if (ready && item.Kind == EditTrailKinds.Season)
        {
            await EnsureSeasonChildrenAsync(item.EntityId);
        }

        return ready;
    }

    private async Task AfterEditItemSwitchedAsync()
    {
        // The Artwork picker keeps its tab where the new item has it; the History block rebuilds for the new item through its item key.
        if (_flow.Session.ActiveTool == EditTool.Artwork)
        {
            await _artworkPicker.FollowItemAsync();
        }
    }

    // A folder of videos lists its sections and videos in the show's places, but its editor scopes are Series, Section and Video.
    private string? ScopeFor(EditTrailItem item) => EditTrailKinds.ScopeOf(
        item.Kind,
        VideoFolderWording.IsVideoFolderList(Model?.SequencePlacement?.ContainerLabel, Model?.SequencePlacement?.ItemLabel)
            ? VideoFolderWording.FolderMediaType
            : null);

    private async Task LoadOtherItemFieldsAsync(EditTrailItem item)
    {
        if (_editing is null || Model is null)
        {
            return;
        }

        if (item.Kind == EditTrailKinds.Person)
        {
            await _editing.Fields.LoadSubjectAsync(item.EntityId);
            return;
        }

        await _editing.Fields.LoadAsync(
            item.EntityId,
            ResolveEditorMediaType(Model.EntityType, Model.PresentationContext),
            ScopeFor(item),
            _editingProfileId);
    }

    // A show page lists the episodes of the season it has open; another season's are fetched the first time the person switches to it.
    private async Task EnsureSeasonChildrenAsync(Guid seasonId)
    {
        if (Model is not { EntityType: DetailEntityType.TvShow } || !Guid.TryParse(Model.Id, out var showId) || _binding.Catalog.ChildrenOf(seasonId).Count > 0)
        {
            return;
        }

        try
        {
            var page = await ApiClient.GetDetailPageAsync(
                DetailEntityType.TvSeason, seasonId, Model.PresentationContext, containerId: showId.ToString("D"), sequence: EditTrailCatalog.SeasonWindow);
            var group = page?.SequencePlacement?.Groups.FirstOrDefault(candidate => Guid.TryParse(candidate.EntityId, out var id) && id == seasonId);
            var episodes = group is null ? [] : EditTrailCatalog.EpisodesOf(group);
            if (episodes.Count > 0 && !_disposed)
            {
                _lateChildren[seasonId] = episodes;
                _binding.Catalog.AddChildren(seasonId, episodes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "The episodes of season {SeasonId} could not be listed for the editing trail", seasonId);
        }
    }

    // The page's own item has loaded: the page keeps showing its staged values while another item is edited, and the Engine's chain for the
    // item fills in the parents the page does not name.
    private void RememberPageItem()
    {
        if (_editing is not { Fields.Loaded: true } editing)
        {
            return;
        }

        var fields = editing.Fields;
        var chain = fields.ScopeChain;
        if (chain.Count > 1 && chain[^1].EntityId == fields.EntityId)
        {
            _engineChain = chain;
            _binding.Catalog.AdoptAncestors(chain);
        }
    }

    private void UpdateEditingEligibility()
    {
        var editAction = Model?.OverflowActions.FirstOrDefault(action => action.Key is "edit" or "edit-media");
        _binding.EntityId = Model is not null && Guid.TryParse(Model.Id, out var pageId) ? pageId : Guid.Empty;
        _binding.HasPermission = editAction is not null && SupportsEditingMode;
        _binding.UnavailableReason = editAction is { IsDisabled: true }
            ? editAction.Tooltip ?? "Editing is not available right now."
            : null;
        _binding.IsUnmatched = Model?.OverflowActions.Any(action => DetailActionKeys.IsUnmatchedAction(action.Key)) == true;
        _binding.BulkMediaType = Model is null ? null : ResolveEditorMediaType(Model.EntityType, Model.PresentationContext);
        _binding.Extras = Model?.Extras;
        if (ReferenceEquals(Editor.Binding, _binding))
        {
            Editor.Bind(_binding);
        }
    }

    // "See all" on the Extras row (universal editor v5 item 16) opens the supporting-files list of the whole title, read-only: no ticks, no bulk bar,
    // no edit actions. A row opens its file in the Extras viewer. An episode's own page lists only its files, so it offers no "See all".
    private bool CanSeeAllExtras => Model is { EntityType: not DetailEntityType.TvEpisode } && Guid.TryParse(Model.Id, out _);

    private async Task OpenAllExtrasAsync()
    {
        if (Model is null || !Guid.TryParse(Model.Id, out var titleId))
        {
            return;
        }

        var browser = EditorHost.CreateReadOnlyFileBrowser(ApiClient);
        var reading = browser.OpenAsync(titleId, Model.Title, EditorBrowserView.Files, mediaType: ResolveEditorMediaType(Model.EntityType, Model.PresentationContext));
        var dialog = await Dialogs.ShowAsync<EditorFileBrowserDialog>(
            string.Empty,
            new AppDialogParameters<EditorFileBrowserDialog>
            {
                { component => component.State, browser },
                { component => component.ReadOnly, true },
                { component => component.CanEdit, false },
            },
            new AppDialogOptions { NoHeader = true, FullWidth = true, Size = AppWindowSize.ExtraLarge, BackdropClick = false, CloseOnEscapeKey = true });
        await reading;
        if (await dialog.Result is { Canceled: false, Data: EditorFileBrowserOutcome { Action: EditorFileBrowserAction.OpenFile } outcome })
        {
            Nav.NavigateTo(ExtrasRowRules.ViewerHref(outcome.Id.ToString("D")));
        }
    }

    // Edit, Identify and Change thumbnail open the pop-over over this page: the page keeps its state, its scroll position and its rows.
    private async Task OpenEditorAsync(EditTool? tool = null, string? artworkSlot = null)
    {
        if (Model is null || !Guid.TryParse(Model.Id, out var entityId))
        {
            return;
        }

        UpdateEditingEligibility();
        await Editor.OpenAsync(_binding, entityId, tool, trail: null, artworkSlot);
    }

    private bool SupportsEditingMode => Model is not null && Guid.TryParse(Model.Id, out _);

    // People, series and curated collections edit through the same bar; the Engine describes what each one offers.
    private static bool IsSubjectKind(string entityKind) =>
        entityKind is "Person" or "StructuralCollection" or "Collection";

    private async Task LoadPageItemFieldsAsync()
    {
        if (_editing is null || Model is null || !Guid.TryParse(Model.Id, out var entityId))
        {
            return;
        }

        var target = ResolveEditorTarget(entityId);
        if (_editingProfileId is null)
        {
            var profile = await Orchestrator.GetActiveProfileAsync();
            _editingProfileId = profile?.Id;
        }

        if (IsSubjectKind(target.EntityKind))
        {
            await _editing.Fields.LoadSubjectAsync(target.EntityId);
            RememberPageItem();
            return;
        }

        await _editing.Fields.LoadAsync(
            target.EntityId,
            ResolveEditorMediaType(Model.EntityType, Model.PresentationContext),
            target.InitialScope,
            _editingProfileId);
        RememberPageItem();
    }

    // Save changes went through: refresh what the page shows, and reload the editor's fields so the next edit starts from the saved values.
    // A failed refresh never undoes the save; the page keeps what it had until the next load.
    private async Task RefreshAfterEditSavedAsync()
    {
        // Whatever the bulk panel read is out of date now (a change that failed stays staged and is read again on top of the saved values).
        _batch.NotifySaved();
        try
        {
            await RefreshCurrentDetailAsync();
            await LoadEditingFieldsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The detail page could not refresh after Save changes");
            ShowNotice(L["Common_SavedButThePageCould"].Value);
        }
    }

    private async Task RefreshAfterSectionCommitAsync()
    {
        try
        {
            await RefreshCurrentDetailAsync();
            await LoadEditingFieldsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The detail page could not refresh after an editing section saved a change");
            ShowNotice(L["Common_SavedButThePageCould"].Value);
        }
    }

    /// <summary>
    /// A video's "..." shortcuts (Videos spec D42): open the pop-over on Match (Identify) or on the cover in Artwork (Change thumbnail).
    /// The device rule is checked here again, like Edit.
    /// </summary>
    private async Task OpenEditShortcutAsync(DetailAction action)
    {
        if (action.IsDisabled || RefuseEditing() || !CanEditMedia)
        {
            return;
        }

        if (action.Key == DetailActionKeys.Identify)
        {
            await OpenEditorAsync(EditTool.Match);
        }
        else
        {
            await OpenEditorAsync(EditTool.Artwork, "CoverArt");
        }
    }

    private (Guid EntityId, string EntityKind, string ContainerMode, string? InitialTab, string? InitialScope) ResolveEditorTarget(Guid fallbackEntityId)
    {
        var target = Model?.EditorTarget;
        if (Model?.EntityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries)
        {
            return (fallbackEntityId, "StructuralCollection", "Collection", "details", null);
        }
        return target is not null && Guid.TryParse(target.EntityId, out var targetId)
            ? (targetId, target.EntityKind, target.ContainerMode, target.InitialTab, target.InitialScope)
            : (fallbackEntityId, Model?.EntityType.ToString() ?? "Work", "Canonical", "details", null);
    }

    private static string ResolveEditorMediaType(DetailEntityType entityType, DetailPresentationContext context)
        => entityType switch
        {
            DetailEntityType.Movie or DetailEntityType.MovieSeries => "Movies",
            DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "TV",
            DetailEntityType.Audiobook => "Audiobooks",
            DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => "Comics",
            DetailEntityType.MusicAlbum => "Music",
            DetailEntityType.Book or DetailEntityType.BookSeries => "Books",
            _ when context == DetailPresentationContext.Watch => "Movies",
            _ when context == DetailPresentationContext.Listen => "Music",
            _ when context == DetailPresentationContext.Comics => "Comics",
            _ => "Books",
        };
}
