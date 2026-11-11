using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
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
    // Delete from the library (a title or a whole group) and the page-removed notice when someone else deleted the title: deletion state, the delete handlers, the live removal event, and leaving the page.

    /// <summary>
    /// Delete from library, in the page's "..." menu: on a desktop
    /// for an effective administrator, and for a page that is one title (details overlay spec D18, D19). The Engine and
    /// <see cref="DeleteFromLibraryAsync"/> check again.
    /// </summary>

    private bool CanDeleteFromLibrary =>
        Model is not null
        && !_removed
        && (LibraryItemDeletionFlow.IsSingleTitle(Model.EntityType) || GroupTargets.Count > 0)
        && LibraryItemDeletionFlow.CanDelete(DeviceContext, Services.GetService<DashboardSessionAccessor>());

    /// <summary>
    /// The groups this page can delete (Delete a show, season, album or Series D2): a show page offers its show, and also the season
    /// selected in the season control, which has no page of its own; an album page its album; a book or comic Series page its Series.
    /// The season comes first so the menu reads "Delete Season 2…" above "Delete show…".
    /// </summary>
    private IReadOnlyList<LibraryGroupTarget> GroupTargets
    {
        get
        {
            if (Model is null || !Guid.TryParse(Model.Id, out var id) || !LibraryItemDeletionFlow.IsGroupPage(Model.EntityType))
            {
                return [];
            }

            var kind = Model.EntityType switch
            {
                DetailEntityType.TvShow => LibraryGroupKinds.Show,
                DetailEntityType.MusicAlbum => LibraryGroupKinds.Album,
                _ => LibraryGroupKinds.Series,
            };
            var owned = (Model.SequencePlacement?.Groups ?? [])
                .SelectMany(group => group.Items)
                .Concat(Model.SequencePlacement?.OrderedItems ?? [])
                .Where(item => item.IsOwned && Guid.TryParse(item.Id, out _))
                .Select(item => Guid.Parse(item.Id))
                .Distinct()
                .ToList();
            var targets = new List<LibraryGroupTarget>();
            if (kind == LibraryGroupKinds.Show
                && SelectedSeasonId is { } seasonId
                && Model.SequencePlacement?.Groups.FirstOrDefault(group =>
                    Guid.TryParse(group.EntityId, out var groupId) && groupId == seasonId && group.OwnedCount > 0) is { } season)
            {
                targets.Add(new LibraryGroupTarget(seasonId, LibraryGroupKinds.Season, season.Title, season.Items.Where(item => item.IsOwned && Guid.TryParse(item.Id, out _)).Select(item => Guid.Parse(item.Id)).ToList()));
            }

            var isVideoFolder = kind == LibraryGroupKinds.Show
                && VideoFolderWording.IsVideoFolderList(Model.SequencePlacement?.ContainerLabel, Model.SequencePlacement?.ItemLabel);
            targets.Add(new LibraryGroupTarget(id, kind, Model.Title, owned, isVideoFolder));
            return targets;
        }
    }

    // The "..." entries for a group page; null for a single title, whose menu keeps "Delete from library…".
    private IReadOnlyList<DetailAction>? GroupDeleteMenuActions
    {
        get
        {
            var targets = GroupTargets;
            return targets.Count == 0
                ? null
                : [.. targets.Select(target => DetailMenuGrouping.DeleteGroupAction(GroupDeleteLabel(target), target.Kind == LibraryGroupKinds.Season))];
        }
    }

    private static string GroupDeleteLabel(LibraryGroupTarget target) => target.Kind switch
    {
        LibraryGroupKinds.Season => $"Delete {target.Title}…",
        LibraryGroupKinds.Show when target.IsVideoFolder => "Delete folder…",
        LibraryGroupKinds.Show => "Delete show…",
        LibraryGroupKinds.Album => "Delete album…",
        _ => "Delete Series…",
    };

    // Delete from library: the dialog is the only confirmation. While it is open (or once the page is on its way out) a live "removed"
    // event for this title must not also toast and navigate: the dialog's own answer does that.
    private bool _deleting;
    private bool _leaving;
    private bool _removedWhileDeleting;

    // Someone else deleted this title while the overlay showed it: the notice stands in for the page.
    private bool _removed;
    private string? _removedNote;
    private string? _removedInputLabel;
    private string? _removedInputText;
    private bool _removedLostSectionInput;


    private async Task DeleteFromLibraryAsync(bool season = false)
    {
        if (Model is null || _deleting || _leaving || _removed || !Guid.TryParse(Model.Id, out var workId))
        {
            return;
        }

        // The entry only shows to an administrator on a desktop; a handler never trusts that.
        if (LibraryItemDeletionFlow.Refusal(DeviceContext, Services.GetService<DashboardSessionAccessor>()) is { } refusal)
        {
            ShowNotice(refusal);
            return;
        }

        if (!LibraryItemDeletionFlow.IsSingleTitle(Model.EntityType))
        {
            await DeleteGroupAsync(season);
            return;
        }

        _deleting = true;
        var result = new LibraryItemDeletionFlowResult(LibraryItemDeletionFlowOutcome.Canceled);
        try
        {
            result = await LibraryItemDeletionFlow.RunAsync(Dialogs, [workId], Model.Title);
        }
        finally
        {
            _leaving = result.Outcome != LibraryItemDeletionFlowOutcome.Canceled;
            _deleting = false;
        }

        if (_leaving)
        {
            await LeaveOrShowRemovedAsync(result.Notice);
        }
        else if (_removedWhileDeleting && !_disposed)
        {
            // Cancelled, but someone else deleted this title while the dialog was open.
            await HandleRemovedByAnotherAsync();
        }
    }

    // Delete show, season, album or Series: the dialog asks the Engine for the whole group. Deleting a season leaves the show, so the page
    // stays and shows the show again; deleting anything else (or the last season) takes the page's own subject, so it closes as for a title.
    private async Task DeleteGroupAsync(bool season)
    {
        var target = GroupTargets.FirstOrDefault(candidate => (candidate.Kind == LibraryGroupKinds.Season) == season);
        if (target is null)
        {
            return;
        }

        _deleting = true;
        LibraryItemDeletionDialogResult? answer = null;
        try
        {
            answer = await LibraryItemDeletionFlow.RunGroupAsync(Dialogs, target);
        }
        finally
        {
            _leaving = answer is not null && (target.Kind != LibraryGroupKinds.Season || answer.RemovedParent);
            _deleting = false;
        }

        if (_leaving)
        {
            await LeaveOrShowRemovedAsync(LibraryItemDeletionFlow.NoticeFor(answer));
        }
        else if (answer?.Deleted is not null && target.Kind == LibraryGroupKinds.Season && Guid.TryParse(Model?.Id, out var showId))
        {
            // The season is gone; the show page opens on its first season again.
            if (LibraryItemDeletionFlow.NoticeFor(answer) is { } seasonNotice)
            {
                ShowNotice(seasonNotice);
            }

            DetailsOverlayNavigation.ReplaceAddress(Nav, Overlay, $"/details/tvshow/{showId:D}");
        }
        else if (_removedWhileDeleting && !_disposed)
        {
            await HandleRemovedByAnotherAsync();
        }
    }

    // Another window (or another person) deleted the title this page shows.
    private void OnLibraryItemsRemoved(LibraryItemsRemovedEvent removed) => _ = InvokeAsync(async () =>
    {
        if (_disposed || _leaving || _removed || Model is null || !Guid.TryParse(Model.Id, out var id))
        {
            return;
        }

        if (!removed.WorkIds.Contains(id) && removed.GroupIds?.Contains(id) != true)
        {
            // A show, album, series or collection page that lists a deleted title refreshes so the row leaves it.
            if (!_deleting && ListsRemovedTitle(removed))
            {
                try
                {
                    await RefreshCurrentDetailAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "The detail page could not refresh after titles were deleted from the library");
                }
            }

            return;
        }

        if (_deleting || (ReferenceEquals(Editor.Binding, _binding) && Editor.IsDeleting))
        {
            // This page's own delete, or one started from the editor open on it: the editor moves to the parent when it is done.
            _removedWhileDeleting = true;
            return;
        }

        await HandleRemovedByAnotherAsync();
    });

    // Not this page's own delete. The person may have been typing, so the page stays with the "no longer in your library" notice in its
    // place (and a way to keep the typed text); its Close goes back to where the person came from.
    private Task HandleRemovedByAnotherAsync()
    {
        ShowRemovedNotice();
        return Task.CompletedTask;
    }

    // This page's own delete worked: the page goes back at once, unless something needs saying (files left on disk, titles already gone),
    // and then the removed notice stands in its place and carries the line.
    private async Task LeaveOrShowRemovedAsync(string? notice)
    {
        if (notice is null)
        {
            await LeaveAfterRemovalAsync();
            return;
        }

        // "Already gone" is what the notice itself says; only a different line (files left on disk) is added to it.
        _removedNote = notice == LibraryItemDeletionText.NoLongerInLibrary ? null : notice;
        ShowRemovedNotice();
    }

    // The editor closes without asking when it was open on this page (the item is gone, so there is nothing to save or discard); staged text
    // is kept for the notice. An open section's unsaved input lives inside the editor and cannot be read from here.
    private void ShowRemovedNotice()
    {
        if (ReferenceEquals(Editor.Binding, _binding) && _editing is { IsOn: true } editing)
        {
            var fields = editing.Fields;
            KeepStagedTextForNotice(fields);

            _removedLostSectionInput = editing.SectionDirty;
            editing.Leave();
        }

        _removed = true;
        StateHasChanged();
    }

    // Changes staged in the universal editor are gone with the item; the notice offers their text so it can still be copied.
    private void KeepStagedTextForNotice(ItemFieldEditSession fields)
    {
        var staged = _flow.Session.Pending.Changes
            .Where(change => change.Kind == EditSessionChangeKinds.Details)
            .SelectMany(change => change.Fields)
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .ToList();
        if (staged.Count == 0)
        {
            return;
        }

        string Label(EditSessionFieldChange field) => fields.Find(field.Key)?.Label ?? field.Key;
        _removedInputLabel = staged.Count == 1 ? Label(staged[0]) : "Your changes";
        _removedInputText = staged.Count == 1
            ? staged[0].Value
            : string.Join(Environment.NewLine, staged.Select(field => $"{Label(field)}: {field.Value}"));
    }

    // Close is the overlay's own close path (or the way back on a page of its own); nothing typed on a deleted title needs a guard.
    private async Task CloseRemovedNoticeAsync()
    {
        if (Overlay is { IsOpen: true } overlay)
        {
            await overlay.DismissAsync(force: true);
            return;
        }

        _leaving = true;
        await LeaveAfterRemovalAsync();
    }

    private bool ListsRemovedTitle(LibraryItemsRemovedEvent removed) => Model is not null
        && (Model.MediaGroups.SelectMany(group => group.Items)
                .Any(item => Guid.TryParse(item.Id, out var workId) && removed.WorkIds.Contains(workId))
            || (Model.SequencePlacement?.Groups ?? []).SelectMany(group => group.Items)
                .Any(item => Guid.TryParse(item.Id, out var workId) && removed.WorkIds.Contains(workId)));

    // The title is gone: close the editor (nothing typed matters now, and it must not ask) and go back to where the person came from.
    private async Task LeaveAfterRemovalAsync()
    {
        if (Model is null)
        {
            return;
        }

        var fallback = DetailOriginInterop.FallbackRoute(Model.EntityType, Model.Facts?.MediaKind);
        if (ReferenceEquals(Editor.Binding, _binding))
        {
            _editing.Leave();
        }

        if (Overlay is { IsOpen: true } overlay)
        {
            // In the overlay, "leave" is the close button's path: dismiss it entirely, back to the page under it.
            // Nothing typed on a deleted title matters, so no guard is asked.
            await overlay.DismissAsync(force: true);
            return;
        }

        if (!await JS.TryBackAsync(fallback, Logger) && !_disposed)
        {
            Nav.NavigateTo(fallback);
        }
    }
}
