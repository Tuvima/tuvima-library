using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

public partial class DetailPage
{
    // My List and Like or Dislike: toggling them and keeping the action buttons' selected state in step.

    private async Task ToggleFavoriteAsync(DetailAction action)
    {
        if (Model is null)
        {
            return;
        }

        var profile = await Orchestrator.GetActiveProfileAsync();
        if (!TryResolveProfileEntity(out var entityKind, out var entityId))
        {
            ShowNotice(L["DetailPage_ThisItemCannotBeAdded"].Value);
            return;
        }

        if (profile is null)
        {
            ShowNotice(L["DetailPage_MyListRequiresAnActive"].Value);
            return;
        }

        var membership = await SavedItems.ToggleAsync(entityKind, entityId);
        if (membership is null)
        {
            ShowNotice(L["DetailPage_MyListCouldNotBe"].Value);
            return;
        }

        SetFavoriteSelection(Model.PrimaryActions, membership.IsSaved);
        SetFavoriteSelection(Model.SecondaryActions, membership.IsSaved);
        SetFavoriteSelection(Model.OverflowActions, membership.IsSaved);
        action.IsSelected = membership.IsSaved;
        // The heart now shows whether it is on My List; that is the confirmation.
        StateHasChanged();
        await OnActionSelected.InvokeAsync(action);
    }

    private bool TryResolveProfileEntity(out ProfileEntityKind entityKind, out Guid entityId)
    {
        entityKind = Model?.EntityType switch
        {
            DetailEntityType.Movie => ProfileEntityKind.Movie,
            DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => ProfileEntityKind.TvShow,
            DetailEntityType.Book or DetailEntityType.Work => ProfileEntityKind.Book,
            DetailEntityType.ComicIssue => ProfileEntityKind.Comic,
            DetailEntityType.Audiobook => ProfileEntityKind.Audiobook,
            DetailEntityType.MusicAlbum => ProfileEntityKind.Album,
            DetailEntityType.Collection => ProfileEntityKind.Collection,
            _ => default,
        };
        entityId = Guid.Empty;
        return Model is not null
               && Model.EntityType is DetailEntityType.Movie
                   or DetailEntityType.TvShow
                   or DetailEntityType.TvSeason
                   or DetailEntityType.TvEpisode
                   or DetailEntityType.Book
                   or DetailEntityType.Work
                   or DetailEntityType.ComicIssue
                   or DetailEntityType.Audiobook
                   or DetailEntityType.MusicAlbum
                   or DetailEntityType.Collection
               && Guid.TryParse(Model.Id, out entityId);
    }

    private static void SetFavoriteSelection(IEnumerable<DetailAction> actions, bool isFavorite)
    {
        foreach (var item in actions)
        {
            if (item.Key == "my-list")
            {
                item.IsSelected = isFavorite;
            }

            SetFavoriteSelection(item.Children, isFavorite);
        }
    }

    private async Task SetReactionAsync(DetailAction action)
    {
        if (!TryResolveProfileEntity(out var entityKind, out var workId))
        {
            ShowNotice(L["DetailPage_ThisItemCannotBeRated"].Value);
            return;
        }

        var reaction = action.Key switch
        {
            "reaction-dislike" => MediaReaction.Dislike,
            "reaction-like" => MediaReaction.Like,
            _ => MediaReaction.Neutral,
        };

        var profile = await Orchestrator.GetActiveProfileAsync();
        var current = await Reactions.GetReactionAsync(workId, profile?.Id, entityKind);
        var next = MediaReactionService.ToggleRating(current, reaction);
        await Reactions.SetReactionAsync(workId, next, profile?.Id, entityKind);
        if (Model is not null)
        {
            SetReactionSelection(Model.PrimaryActions, next);
            SetReactionSelection(Model.SecondaryActions, next);
            SetReactionSelection(Model.OverflowActions, next);
        }
        StateHasChanged();
        await OnActionSelected.InvokeAsync(action);
    }

    private static void SetReactionSelection(IEnumerable<DetailAction> actions, MediaReaction selected)
    {
        foreach (var item in actions)
        {
            item.IsSelected = item.Key switch
            {
                "reaction-menu" => selected == MediaReaction.Like,
                "reaction-dislike" => selected == MediaReaction.Dislike,
                "reaction-like" => selected == MediaReaction.Like,
                _ => item.IsSelected,
            };
            SetReactionSelection(item.Children, selected);
        }
    }
}
