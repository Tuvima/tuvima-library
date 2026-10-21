using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>
    /// A TV season has no page of its own (D2, D13): opening one answers with its show's page, with that season
    /// selected and its episodes listed. <paramref name="selectedContainerId"/> is the show the caller already knows
    /// (<c>/details/tvshow/{show}?season={season}</c>); without it the show is the season's parent work.
    /// A season the show does not carry, or one with no episode the profile may see, answers like any unknown item.
    /// </summary>
    private async Task<DetailPageViewModel?> BuildTvSeasonAsync(
        Guid seasonId,
        DetailPresentationContext context,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlySet<Guid> favoriteWorkIds,
        Guid? profileId,
        string? selectedContainerId,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        CancellationToken ct,
        SequenceWindowRequest? sequenceWindow)
    {
        // A season id that was merged into another follows the redirect, as the access filter does, so it opens its show.
        using (var redirectConnection = _db.CreateConnection())
        {
            seasonId = WorkRedirects.Resolve(redirectConnection, seasonId);
        }

        var showId = Guid.TryParse(selectedContainerId, out var requestedShowId) && requestedShowId != Guid.Empty
            ? requestedShowId
            : await ResolveTvSeasonShowIdAsync(seasonId, ct);
        if (showId is not { } resolvedShowId)
        {
            return null;
        }

        var window = (sequenceWindow ?? SequenceWindowRequest.All()) with { SelectedSeasonId = seasonId };
        var page = await BuildCollectionAsync(
            resolvedShowId,
            DetailEntityType.TvShow,
            context,
            actionAuthorization,
            favoriteWorkIds,
            ct,
            profileId: profileId,
            authorizedWorks: authorizedWorks,
            sequenceWindow: window);

        // The groups only carry episodes the profile may see, so a season with none of them is not found (D8).
        var season = page?.SequencePlacement?.Groups.FirstOrDefault(group =>
            string.Equals(group.EntityId, seasonId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        return season is { OwnedCount: > 0 } ? page : null;
    }

    private async Task<Guid?> ResolveTvSeasonShowIdAsync(Guid seasonId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            -- readiness: detail page of one item opened by id
            SELECT season.parent_work_id
            FROM works season
            WHERE season.id = @seasonId
              AND season.media_type IN ('TV', 'Videos')
              AND season.work_kind = 'parent'
              AND season.parent_work_id IS NOT NULL
              AND COALESCE(season.is_catalog_only, 0) = 0
            LIMIT 1;
            """,
            new { seasonId = GuidSql.ToBlob(seasonId) },
            cancellationToken: ct));
    }
}
