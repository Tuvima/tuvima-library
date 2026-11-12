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
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
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
    // Collection and set actions in the "..." menu: Add to collection, and Export as a pack file and Remove set for a set.

    /// <summary>
    /// "Add to collection…" in a single title's "..." menu: any adult profile on a desktop (Collections and lists spec 4a, D19). The list
    /// it opens holds only the collections the Engine says this person may edit, and the Engine checks every add again.
    /// </summary>
    private bool CanAddToCollection =>
        Model is not null
        && !_removed
        && Guid.TryParse(Model.Id, out _)
        && LibraryItemDeletionFlow.IsSingleTitle(Model.EntityType)
        // A song goes into a playlist, never a collection (Listen music spec D9).
        && !(Model.EntityType == DetailEntityType.Work
            && string.Equals(ResolveEditorMediaType(Model.EntityType, Model.PresentationContext), "Music", StringComparison.OrdinalIgnoreCase))
        && EditorDeviceRule.CanEditItems(DeviceContext)
        && Services.GetService<DashboardSessionAccessor>() is { } session
        && CollectionAuthoringRule.CanCreate(session);

    private async Task AddToCollectionAsync()
    {
        if (Model is null || !CanAddToCollection || !Guid.TryParse(Model.Id, out var workId))
        {
            return;
        }

        if (await CollectionMembershipFlow.RunAsync(
                Services,
                [new CollectionEditorSeedItem(workId, Model.Title, ResolveEditorMediaType(Model.EntityType, Model.PresentationContext))],
                DetailMenuGrouping.AddToCollectionAction.Label,
                Model.Title) is { } refusal)
        {
            ShowNotice(refusal, AppUiTone.Info);
        }
    }

    /// <summary>
    /// "Export as a pack file" and "Remove set…" in a set's "..." menu: on a device that may edit items, for a person the Engine says may edit this set.
    /// The Engine checks again on every call.
    /// </summary>
    private IReadOnlyList<DetailAction>? SetMenuActions =>
        Model?.CollectionInfo is { IsSet: true }
        && !_removed
        && _setSummary is { CanEdit: true }
        && EditorDeviceRule.CanEditItems(DeviceContext)
            ? [DetailMenuGrouping.ExportSetPackAction, DetailMenuGrouping.RemoveSetAction]
            : null;

    private async Task ExportSetAsync()
    {
        if (Model is null || SetMenuActions is null || !Guid.TryParse(Model.Id, out var setId)
            || RefuseEditing())
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        var export = await ApiClient.ExportCollectionSetAsync(setId, profile?.Id);
        if (export is null)
        {
            ShowNotice(ApiClient.LastError ?? L["DetailPage_SetActionFailed"].Value);
            return;
        }

        await using var stream = new MemoryStream(CollectionSetPackFile.Write(export.File), writable: false);
        using var reference = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("tuvimaDownloads.fromStream", CollectionSetPackFile.FileNameFor(Model.Title), reference, CollectionSetPackFile.ContentType);
        // The file landing in the browser's downloads is the confirmation; only what was left out is worth a line.
        if (export.Omitted.Count > 0)
        {
            ShowNotice(L["DetailPage_ExportSetOmitted", string.Join(", ", export.Omitted)].Value, AppUiTone.Info);
        }
    }

    private async Task RemoveSetAsync()
    {
        if (Model is null || SetMenuActions is null || !Guid.TryParse(Model.Id, out var setId)
            || RefuseEditing())
        {
            return;
        }

        if (await AppConfirmDialog.ShowAsync(
                Dialogs,
                L["DetailPage_RemoveSetTitle"].Value,
                L["DetailPage_RemoveSetMessage", Model.Title].Value,
                L["DetailPage_RemoveSetConfirm"].Value,
                L["DetailPage_RemoveSetCancel"].Value) != AppConfirmResult.Primary)
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        var removed = await ApiClient.RemoveCollectionSetAsync(setId, profile?.Id);
        if (removed is null)
        {
            ShowNotice(ApiClient.LastError ?? L["DetailPage_SetActionFailed"].Value);
            return;
        }

        // The set is gone, so the page goes with it; that is the confirmation.
        _leaving = true;
        await LeaveAfterRemovalAsync();
    }
}
