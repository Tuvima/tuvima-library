using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Progress;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private static DetailAction BuildRestartAction(string? route) => new()
    {
        Key = "play-from-beginning", Label = "Restart from beginning", Icon = "restart_alt",
        Route = route is null ? null : Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(route, "restart", "true"),
    };

    private async Task AddPersonalActionsAsync(
        DetailPageViewModel model,
        Guid profileId,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlySet<Guid>? authorizedAssetIds,
        CancellationToken ct)
    {
        var actions = new List<DetailAction>();
        var primary = model.PrimaryActions.FirstOrDefault();
        if (model.Progress?.Percent is > 0 && primary is not null)
            actions.Add(BuildRestartAction(primary.Route));
        var media = PersonalStatusPolicy.MediaTypeFor(model.EntityType);
        if (media != MediaType.Unknown && Guid.TryParse(model.Id, out var id))
        {
            var target = new PersonalStatusTarget(id, media)
            {
                AuthorizedAssetIds = authorizedAssetIds,
            };
            var status = await new PersonalStatusRepository(_db).ReadAsync(profileId, target, ct);
            if (status.OwnedCount > 0)
            {
                model.PersonalStatus = new(status.Revision, status.OwnedCount, status.StartedCount, status.CompletedCount, status.Hidden);
                var completed = media switch { MediaType.Books or MediaType.Comics => "read", MediaType.Audiobooks => "finished", MediaType.Music => "listened", _ => "watched" };
                var reset = media switch { MediaType.Books or MediaType.Comics => "unread", MediaType.Audiobooks => "unfinished", MediaType.Music => "unlistened", _ => "unwatched" };
                var noun = media == MediaType.TV ? "episodes" : media == MediaType.Music ? "tracks" : "titles";
                var scope = model.EntityType is DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.MusicAlbum
                    or DetailEntityType.BookSeries or DetailEntityType.MovieSeries or DetailEntityType.ComicSeries
                    ? $"owned {noun} as {{0}} ({status.OwnedCount})" : "as {0}";
                if (status.CompletedCount < status.OwnedCount)
                {
                    actions.Add(new() { Key = "status-complete", Label = "Mark " + string.Format(scope, completed), Icon = "check" });
                }

                if (status.StartedCount > 0)
                {
                    actions.Add(new() { Key = "status-reset", Label = "Mark " + string.Format(scope, reset), Icon = "restart_alt" });
                }

                if (status.StartedCount > status.CompletedCount || status.Hidden)
                {
                    actions.Add(new() { Key = status.Hidden ? "status-show" : "status-hide", Label = status.Hidden ? "Show in Continue" : "Hide from Continue", Icon = "visibility" });
                }

                actions.Add(new() { Key = "status-history", Label = media is MediaType.Books or MediaType.Comics ? "Reading history" : media is MediaType.Audiobooks or MediaType.Music ? "Listening history" : "Viewing history", Icon = "history" });
            }
        }
        if (model.EntityType == DetailEntityType.MusicAlbum && model.PersonalStatus?.OwnedCount > 0)
        {
            actions.Add(new() { Key = "play-next", Label = "Play next", Icon = "queue_play_next" });
            actions.Add(new() { Key = "add-queue", Label = "Add to queue", Icon = "queue_music" });
            actions.Add(new() { Key = "add-playlist", Label = "Add to playlist", Icon = "playlist_add" });
        }
        actions.Add(new() { Key = "copy-link", Label = "Copy library link", Icon = "link" });
        actions.AddRange(model.OverflowActions);
        model.OverflowActions = actions;
    }
}
