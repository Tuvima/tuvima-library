using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Works one entity's canonical values out again from the claims it has now (the Priority Cascade), after claims were removed.
/// Used by Change what this folder is when a plain kind drops the details looked up for a file (Library setup D69): the file's own
/// claims (tags, file name) and a person's edits are scored again, so the file shows by them.
/// </summary>
public interface IEntityRescorer
{
    Task RescoreAsync(Guid entityId, CancellationToken ct);
}

/// <inheritdoc cref="IEntityRescorer"/>
public sealed class ClaimRescorer(
    IMetadataClaimRepository claims,
    ICanonicalValueRepository canonicals,
    IScoringEngine scoring,
    IConfigurationLoader configuration,
    IEnumerable<IExternalMetadataProvider> providers,
    ICanonicalValueArrayRepository arrays,
    ISearchIndexRepository searchIndex,
    ILogger<ClaimRescorer> logger) : IEntityRescorer
{
    public Task RescoreAsync(Guid entityId, CancellationToken ct) =>
        // No new claims: the same persist-and-score step every enrichment uses, which scores all the claims the entity holds.
        ScoringHelper.PersistClaimsAndScoreAsync(
            entityId, [], WellKnownProviders.LocalProcessor, claims, canonicals, scoring, configuration, providers, ct,
            arrays, logger, searchIndex);
}
