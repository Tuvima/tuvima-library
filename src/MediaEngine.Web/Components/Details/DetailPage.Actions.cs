using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
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
    // Notices, the Undo line and the dispatcher for the actions the hero and "..." menu offer, with the personal status and Unmatched item handlers and the page refresh.

    // What the last action had to say, shown at the top of the page until the next action or the close button.
    private string? _notice;
    private AppUiTone _noticeTone = AppUiTone.Warning;

    private void ShowNotice(string text, AppUiTone tone = AppUiTone.Warning)
    {
        _notice = text;
        _noticeTone = tone;
    }

    private void ClearNotice() => _notice = null;

    // The Undo line after a personal status change (one at a time) and the item it was made on.
    private UndoOffer? _undoOffer;
    private string? _undoModelId;
    private Task ClearUndo() { _undoOffer = null; return Task.CompletedTask; }

    // Every edit handler asks the device rule again (a hidden button is never the only protection); the refusal is shown in place.
    private bool RefuseEditing()
    {
        if (!EditorDeviceRule.TryRefuseItems(DeviceContext))
        {
            return false;
        }

        ShowNotice(EditorDeviceRule.Message, AppUiTone.Info);
        return true;
    }

    private async Task HandleDetailActionAsync(DetailAction action)
    {
        if (Model is null)
        {
            return;
        }
        ClearNotice();
        if (action.Key == DetailActionKeys.PlayFromBeginning)
        {
            if (IsAudiobook) { await PlayAudiobookAsync(preferResume: false); return; }
            if (Model.EntityType == DetailEntityType.MusicAlbum) { await PlayAlbumAsync(shuffle: false); return; }
            if (Model.EntityType is DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work)
            { await OpenReaderAsync(action, restart: true); return; }
        }
        if (action.Key == DetailActionKeys.Read)
        {
            await OpenReaderAsync(action);
            return;
        }

        if (action.Key is DetailActionKeys.PlayNext or DetailActionKeys.AddQueue)
        {
            var tracks = Model.MediaGroups.SelectMany(g => g.Items)
                .Where(item => item.EntityType == DetailEntityType.Work && item.IsOwned)
                .OrderBy(TrackSortKey).Select(ToQueueItem).Where(item => item.WorkId != Guid.Empty).ToList();
            if (action.Key == DetailActionKeys.PlayNext)
            {
                tracks.Reverse();
            }
            foreach (var track in tracks)
            {
                await Playback.AddQueueItemAsync(track, next: action.Key == DetailActionKeys.PlayNext);
            }
            // The queue in the player shows the added tracks; that is the confirmation.
            return;
        }
        if (action.Key == DetailActionKeys.AddToCollection)
        {
            await AddToCollectionAsync();
            return;
        }
        if (action.Key == DetailActionKeys.ExportSetPack)
        {
            await ExportSetAsync();
            return;
        }
        if (action.Key == DetailActionKeys.RemoveSet)
        {
            await RemoveSetAsync();
            return;
        }
        if (action.Key == DetailActionKeys.AddPlaylist)
        {
            IReadOnlyList<Guid> works = Model.MediaGroups.SelectMany(g => g.Items)
                .Where(item => item.IsOwned && Guid.TryParse(item.Id, out _))
                .OrderBy(TrackSortKey)
                .Select(item => Guid.Parse(item.Id))
                .Distinct()
                .ToList();
            await Dialogs.ShowAsync<MediaMembershipDialog>(action.Label, new AppDialogParameters<MediaMembershipDialog> {
                { d => d.Playlist, true }, { d => d.WorkIds, works },
            });
            return;
        }
        if (DetailActionKeys.IsPersonalStatus(action.Key))
        {
            await HandlePersonalStatusAsync(action);
            return;
        }
        if (DetailActionKeys.IsUnmatchedAction(action.Key))
        {
            await HandleUnmatchedActionAsync(action);
            return;
        }
        if (action.Key is DetailActionKeys.EditMedia or DetailActionKeys.Edit)
        {
            if (action.IsDisabled)
            {
                return;
            }
            if (RefuseEditing())
            {
                return;
            }
            if (!CanEditMedia)
            {
                return;
            }

            // Items, people, series and curated collections all edit in the pop-over, which opens at once over this page (on Match when the
            // item is not matched yet). The page itself does not change.
            await OpenEditorAsync();
            return;
        }

        if (action.Key is DetailActionKeys.Identify or DetailActionKeys.ChangeThumbnail)
        {
            await OpenEditShortcutAsync(action);
            return;
        }

        if (action.Key is DetailActionKeys.DeleteFromLibrary or DetailActionKeys.DeleteSeason)
        {
            await DeleteFromLibraryAsync(action.Key == DetailActionKeys.DeleteSeason);
            return;
        }

        if (action.Key == DetailActionKeys.MyList)
        {
            await ToggleFavoriteAsync(action);
            return;
        }

        if (action.Key is DetailActionKeys.ReactionDislike or DetailActionKeys.ReactionLike)
        {
            await SetReactionAsync(action);
            return;
        }

        if (action.Key == DetailActionKeys.Details)
        {
            await SetActiveTabAsync("details");
            return;
        }

        if (Model?.EntityType == DetailEntityType.MusicAlbum && action.Key is DetailActionKeys.PlayAlbum or DetailActionKeys.Shuffle)
        {
            await PlayAlbumAsync(shuffle: action.Key == DetailActionKeys.Shuffle);
            return;
        }

        if (action.Key == DetailActionKeys.ShuffleCollection)
        {
            await ShuffleStructuralContainerAsync();
            return;
        }

        var isAudiobookListenAction = string.Equals(action.Key, DetailActionKeys.Listen, StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Label, "Listen", StringComparison.OrdinalIgnoreCase);
        if (Model is not null && IsAudiobook && !action.IsDisabled
            && (isAudiobookListenAction || IsContinueAction(action)))
        {
            await PlayAudiobookAsync(IsContinueAction(action));
            return;
        }

        if (!string.IsNullOrWhiteSpace(action.Route))
        {
            Nav.NavigateTo(action.Route);
            return;
        }

        await OnActionSelected.InvokeAsync(action);
    }

    private bool _changingPersonalStatus;
    private async Task HandlePersonalStatusAsync(DetailAction action)
    {
        if (Model is null || _changingPersonalStatus)
        {
            return;
        }
        _changingPersonalStatus = true;
        try
        {
            if (action.Key == DetailActionKeys.StatusHistory)
            {
                var items = await ApiClient.GetPersonalHistoryAsync(Model.EntityType, Guid.Parse(Model.Id));
                await Dialogs.ShowAsync<PersonalHistoryDialog>(action.Label,
                    new AppDialogParameters<PersonalHistoryDialog> { { d => d.Items, items } });
                return;
            }
            var command = action.Key switch
            {
                DetailActionKeys.StatusComplete => PersonalStatusAction.Complete,
                DetailActionKeys.StatusReset => PersonalStatusAction.Reset,
                DetailActionKeys.StatusHide => PersonalStatusAction.HideContinue,
                DetailActionKeys.StatusShow => PersonalStatusAction.ShowContinue,
                _ => throw new InvalidOperationException("Unknown personal status action."),
            };
            var status = await ApiClient.GetPersonalStatusAsync(Model.EntityType, Guid.Parse(Model.Id))
                ?? throw new InvalidOperationException("Personal status could not be loaded. Please try again.");
            var result = await ApiClient.ChangePersonalStatusAsync(new(Guid.NewGuid(), Model.EntityType,
                Guid.Parse(Model.Id), command, status.Revision));
            ClearNotice();
            _undoModelId = Model.Id;
            _undoOffer = new UndoOffer(L["DetailPage_StatusUpdatedForOwned", result.AffectedCount].Value, async () =>
            {
                await ApiClient.UndoPersonalStatusAsync(result.CommandId);
                await RefreshCurrentDetailAsync();
            });
            await RefreshCurrentDetailAsync();
        }
        catch (Exception ex) { ShowNotice(ex.Message, AppUiTone.Error); }
        finally { _changingPersonalStatus = false; }
    }

    // Retry now, Keep as is and Look up again (Library setup spec D34, D40): each calls the Engine's Unmatched item route for every
    // file on this page it applies to (not-kept files for Retry and Keep, kept files for Look up again), then refreshes the page.
    // They are administrator actions, not edits, so the device rule does not apply; the Engine sends them only to administrators.
    private bool _changingUnmatched;
    private async Task HandleUnmatchedActionAsync(DetailAction action)
    {
        if (Model is null || _changingUnmatched)
        {
            return;
        }
        var lookUpAgain = action.Key == DetailActionKeys.UnmatchedLookUpAgain;
        var targets = Model.UnmatchedAssets
            .Where(asset => asset.KeptAsIs == lookUpAgain)
            .Select(asset => asset.AssetId)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _changingUnmatched = true;
        try
        {
            if (action.Key == DetailActionKeys.UnmatchedKeep
                && await AppConfirmDialog.ShowAsync(
                    Dialogs,
                    "Keep as it is?",
                    "Stop trying to match this? It stays out of Needs Review until you show kept items again. You can look it up again later.",
                    "Keep as it is",
                    "Cancel") != AppConfirmResult.Primary)
            {
                return;
            }

            var failed = 0;
            foreach (var assetId in targets)
            {
                var result = action.Key switch
                {
                    DetailActionKeys.UnmatchedRetry => await ApiClient.RetryUnmatchedItemAsync(assetId),
                    DetailActionKeys.UnmatchedKeep => await ApiClient.KeepUnmatchedItemAsync(assetId),
                    _ => await ApiClient.LookUpUnmatchedItemAgainAsync(assetId),
                };
                if (result is null)
                {
                    failed++;
                }
            }

            if (failed == targets.Count)
            {
                ShowNotice(L["DetailPage_ThatCouldNotBeDone"].Value);
            }
            else if (failed > 0)
            {
                ShowNotice(L["DetailPage_UnmatchedPartlyDone", targets.Count - failed, targets.Count, failed].Value);
            }
            else if (action.Key != DetailActionKeys.UnmatchedKeep)
            {
                // Looking it up again runs in the background and the page does not change at once, so this one says it started.
                // Keep as is needs no line: the refreshed page shows the file as kept.
                ShowNotice(
                    action.Key == DetailActionKeys.UnmatchedRetry ? L["DetailPage_RetryStarted"].Value : L["DetailPage_LookUpAgainStarted"].Value,
                    AppUiTone.Info);
            }

            await RefreshCurrentDetailAsync();
        }
        finally { _changingUnmatched = false; }
    }

    private async Task RefreshCurrentDetailAsync()
    {
        if (Model is null || !Guid.TryParse(Model.Id, out var entityId))
        {
            return;
        }

        // A show page the person moved to a season asks for that season again, so the refresh keeps it (D19).
        var updated = SelectedSeasonId is { } seasonId && Model.EntityType == DetailEntityType.TvShow
            ? await ApiClient.GetDetailPageAsync(DetailEntityType.TvSeason, seasonId, Model.PresentationContext, containerId: entityId.ToString("D"), sequence: SequenceWindowQuery.Windowed)
            : await ApiClient.GetDetailPageAsync(Model.EntityType, entityId, Model.PresentationContext, containerId: Model.EntityType == DetailEntityType.TvEpisode ? Model.SequencePlacement?.ContainerId : null, sequence: SequenceWindowQuery.Windowed);
        if (updated is not null && IsStillShowing(entityId))
        {
            Model = updated;
            await ModelRefreshed.InvokeAsync(updated);
            StateHasChanged();
        }
    }

    // A refresh that lands after the person moved on (this page disposed, or a different item put in its place)
    // must not replace what they are looking at now.
    private bool IsStillShowing(Guid entityId) =>
        !_disposed && Model is not null && Guid.TryParse(Model.Id, out var currentId) && currentId == entityId;
}
