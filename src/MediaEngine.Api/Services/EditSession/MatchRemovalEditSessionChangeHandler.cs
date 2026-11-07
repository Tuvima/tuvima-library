using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves "Remove match" from the Match tab (kind <c>match_removal</c>): the item stops being a record of any provider. The provider record
/// ids (TMDB, TheTVDB, MusicBrainz, Comic Vine, Open Library, Apple, YouTube and the other bridge ids the Engine stored when it matched) are
/// no longer the item's; its details, locked fields, pictures and Wikidata id stay, no file moves, and the item is not sent to Needs Review.
/// <para>
/// Claims are only ever appended to: the claims that carried the ids are retired (kept as history, left out of scoring), never deleted. The
/// canonical values, bridge ids and the Work's external-id list that were built from them are removed, so every page reads "Not matched yet".
/// The decision is pinned (<c>match_pinned</c>), so a later scan or a folder hint does not match the item again by itself.
/// </para>
/// <para>No undo: like choosing a match, removing one rewrites claims, bridge ids and a Work's list in several steps, so the session runs it last among the data changes.</para>
/// </summary>
public sealed class MatchRemovalEditSessionChangeHandler(IServiceScopeFactory scopes) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.MatchRemoval;

    /// <summary>
    /// The ids a match leaves on an item, with the name of the provider each one comes from. The Wikidata id is not one of them: it is
    /// not a retail match and is aligned on its own.
    /// </summary>
    internal static readonly IReadOnlyList<(string Key, string Provider)> ProviderIdKeys =
    [
        (BridgeIdKeys.TvdbEpisodeId, "TheTVDB"),
        (BridgeIdKeys.TvdbSeasonId, "TheTVDB"),
        (BridgeIdKeys.TvdbId, "TheTVDB"),
        (BridgeIdKeys.TmdbEpisodeId, "TMDB"),
        (BridgeIdKeys.TmdbId, "TMDB"),
        (BridgeIdKeys.ImdbId, "IMDb"),
        (BridgeIdKeys.MusicBrainzRecordingId, "MusicBrainz"),
        (BridgeIdKeys.MusicBrainzReleaseId, "MusicBrainz"),
        (BridgeIdKeys.MusicBrainzReleaseGroupId, "MusicBrainz"),
        (BridgeIdKeys.MusicBrainzWorkId, "MusicBrainz"),
        (BridgeIdKeys.MusicBrainzId, "MusicBrainz"),
        (BridgeIdKeys.Isrc, "MusicBrainz"),
        (BridgeIdKeys.ComicVineId, "Comic Vine"),
        (BridgeIdKeys.ComicVineVolumeId, "Comic Vine"),
        (BridgeIdKeys.OpenLibraryId, "Open Library"),
        (BridgeIdKeys.AppleBooksId, "Apple Books"),
        (BridgeIdKeys.AppleMusicId, "Apple Music"),
        (BridgeIdKeys.AppleMusicCollectionId, "Apple Music"),
        (BridgeIdKeys.GoodreadsId, "Goodreads"),
        (BridgeIdKeys.AudibleId, "Audible"),
        (BridgeIdKeys.SpotifyId, "Spotify"),
        (BridgeIdKeys.Isbn, "Open Library"),
        (BridgeIdKeys.Isbn13, "Open Library"),
        (BridgeIdKeys.Isbn10, "Open Library"),
        (BridgeIdKeys.Asin, "Audible"),
        (MetadataFieldConstants.YoutubeVideoId, "YouTube"),
    ];

    private static readonly string[] ClaimKeys =
    [
        .. ProviderIdKeys.Select(pair => pair.Key),
        MetadataFieldConstants.IdentityProvider,
        MetadataFieldConstants.IdentityProviderItemId,
    ];

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var plan = await PlanAsync(scope.ServiceProvider, change.EntityId, ct).ConfigureAwait(false);
        return plan is null ? EditSessionReasons.Gone : plan.Was is null ? EditSessionReasons.NoMatchToRemove : null;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var plan = await PlanAsync(services, change.EntityId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(EditSessionReasons.Gone);
        if (plan.Was is null)
        {
            throw new InvalidOperationException(EditSessionReasons.NoMatchToRemove);
        }

        var canonicals = services.GetRequiredService<ICanonicalValueRepository>();
        var claims = services.GetRequiredService<IMetadataClaimRepository>();
        var bridges = services.GetRequiredService<IBridgeIdRepository>();
        var works = services.GetRequiredService<IWorkRepository>();
        var now = DateTimeOffset.UtcNow;

        foreach (var target in plan.Targets)
        {
            // Retire, never delete: every provider's current claim for these keys stays as history and drops out of scoring.
            var current = await claims.GetByEntityAsync(target, ct).ConfigureAwait(false);
            foreach (var provider in current
                         .Where(claim => ClaimKeys.Contains(claim.ClaimKey, StringComparer.OrdinalIgnoreCase))
                         .Select(claim => claim.ProviderId)
                         .Distinct())
            {
                await claims.ReplaceCurrentProviderClaimsAsync(target, provider, ClaimKeys, [], ct).ConfigureAwait(false);
            }

            foreach (var key in ClaimKeys)
            {
                await canonicals.DeleteByKeyAsync(target, key, ct).ConfigureAwait(false);
            }

            await bridges.DeleteByTypesAsync(target, ClaimKeys, ct).ConfigureAwait(false);
        }

        await works.RemoveExternalIdentifiersAsync(plan.WorkId, ClaimKeys, ct).ConfigureAwait(false);

        // The person's decision is pinned, as a chosen match is: no scan or folder hint matches the item again by itself.
        await claims.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = plan.WorkId,
                ProviderId = WellKnownProviders.UserManual,
                DecisionSourceProviderId = WellKnownProviders.UserManual,
                ClaimKey = MetadataFieldConstants.MatchPinned,
                ClaimValue = "true",
                ClaimedAt = now,
                Confidence = 1.0,
            },
        ], ct).ConfigureAwait(false);
        await canonicals.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = plan.WorkId,
                Key = MetadataFieldConstants.MatchPinned,
                Value = "true",
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.UserManual,
            },
        ], ct).ConfigureAwait(false);

        var activity = services.GetRequiredService<ISystemActivityRepository>();
        await activity.LogAsync(new SystemActivityEntry
        {
            OccurredAt = now,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = plan.WorkId,
            EntityType = "Work",
            Detail = $"Match removed (was {plan.Was}).",
        }, ct).ConfigureAwait(false);
        return null;
    }

    private sealed record Plan(Guid WorkId, IReadOnlyList<Guid> Targets, string? Was);

    // The work the item is, plus the asset it is shown from, and what the match was ("TMDB · 603"), read from the ids the Engine stored.
    private static async Task<Plan?> PlanAsync(IServiceProvider services, Guid entityId, CancellationToken ct)
    {
        var launch = await services.GetRequiredService<IMetadataEditorRepository>().ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        if (launch is null)
        {
            return null;
        }

        var targets = new[] { launch.WorkId, launch.RepresentativeAssetId ?? Guid.Empty, launch.LaunchEntityId }
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var canonicals = services.GetRequiredService<ICanonicalValueRepository>();
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            foreach (var value in await canonicals.GetByEntityAsync(target, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(value.Value))
                {
                    stored.TryAdd(value.Key, value.Value);
                }
            }
        }

        var was = ProviderIdKeys
            .Where(pair => stored.ContainsKey(pair.Key))
            .Select(pair => $"{pair.Provider} \u00b7 {stored[pair.Key]}")
            .FirstOrDefault();
        return new Plan(launch.WorkId, targets, was);
    }
}
