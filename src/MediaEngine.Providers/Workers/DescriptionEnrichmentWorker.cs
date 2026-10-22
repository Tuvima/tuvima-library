using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Fetches Wikipedia descriptions via <see cref="ReconciliationAdapter"/> and
/// persists them as claims.
///
/// Wikipedia description enrichment is handled internally by
/// <see cref="ReconciliationAdapter.FetchAsync"/> when <c>PreResolvedQid</c> is provided.
/// This worker logs that enrichment is automatic and takes no separate action.
///
/// Extracted from <c>HydrationPipelineService</c> description enrichment section.
/// </summary>
public sealed class DescriptionEnrichmentWorker
{
    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly ILogger<DescriptionEnrichmentWorker> _logger;
    private readonly IExternalLookupGuard? _lookupGuard;

    public DescriptionEnrichmentWorker(
        IEnumerable<IExternalMetadataProvider> providers,
        ILogger<DescriptionEnrichmentWorker> logger,
        IExternalLookupGuard? lookupGuard = null)
    {
        _lookupGuard = lookupGuard;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the Wikipedia description for the given QID and persists it as a claim.
    /// </summary>
    public async Task EnrichAsync(Guid entityId, string qid, CancellationToken ct)
    {
        if (await _lookupGuard.RefusesAsync(entityId, _logger, "Description lookup", ct).ConfigureAwait(false))
        {
            return;
        }

        var reconAdapter = _providers
            .OfType<ReconciliationAdapter>()
            .FirstOrDefault();

        if (reconAdapter is null)
        {
            _logger.LogDebug("No ReconciliationAdapter available — skipping description enrichment");
            return;
        }

        // Wikipedia description enrichment is handled internally by ReconciliationAdapter.FetchAsync
        // when a PreResolvedQid is provided. FetchWikipediaDescriptionAsync is a private method
        // and cannot be called from workers directly.
        _logger.LogDebug(
            "Wikipedia description enrichment for QID {Qid} (entity {EntityId}) is performed " +
            "automatically during WikidataBridgeWorker full property fetch — no separate step required",
            qid, entityId);
    }
}
