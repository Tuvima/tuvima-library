using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// Wikidata adapter using the <see cref="WikidataReconciler"/> from the Tuvima.Wikidata library.
///
/// <para>
/// This adapter replaces the SPARQL-based WikidataAdapter. Instead of custom SPARQL queries
/// it uses <see cref="WikidataReconciler.ReconcileAsync"/> for entity search and
/// <see cref="WikidataReconciler.GetPropertiesAsync"/> for property extension.
/// </para>
///
/// <para>
/// Primary operations:
/// <list type="bullet">
///   <item>Reconcile entity names to Wikidata QIDs via the Wikibase wbsearchentities API.</item>
///   <item>Extend QIDs with structured property values via the Wikibase wbgetentities API.</item>
///   <item>Filter candidates by media type using P31 (instance_of) + P279 (subclass_of) walks.</item>
///   <item>Discover audiobook editions via P747 (has_edition_or_translation) + P31 filtering.</item>
///   <item>Download person headshots from Wikimedia Commons.</item>
/// </list>
/// </para>
///
/// Spec: Phase 2 – ReconciliationAdapter replacing WikidataAdapter.
/// </summary>
public sealed partial class ReconciliationAdapter : IExternalMetadataProvider, IFictionalEntityGraphEvidenceProvider
{
    private readonly ReconciliationProviderConfig _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ReconciliationAdapter> _logger;
    private readonly IProviderResponseCacheRepository? _responseCache;
    private readonly IConfigurationLoader? _configLoader;
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly WikidataReconciler? _reconciler;
    private readonly CommonsImageResolver _commonsImageResolver;

    // Parsed once at construction.
    private readonly Guid _providerId;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public ReconciliationAdapter(
        ReconciliationProviderConfig config,
        IHttpClientFactory httpFactory,
        ILogger<ReconciliationAdapter> logger,
        IFuzzyMatchingService fuzzy,
        IProviderResponseCacheRepository? responseCache = null,
        IConfigurationLoader? configLoader = null,
        WikidataReconciler? reconciler = null,
        CommonsImageResolver? commonsImageResolver = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fuzzy);

        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
        _fuzzy = fuzzy;
        _responseCache = responseCache;
        _configLoader = configLoader;
        _reconciler = reconciler;
        _commonsImageResolver = commonsImageResolver ?? new CommonsImageResolver(
            config,
            httpFactory,
            NullLogger<CommonsImageResolver>.Instance);

        _providerId = !string.IsNullOrEmpty(config.ProviderId)
            ? Guid.Parse(config.ProviderId)
            : Guid.NewGuid();
    }

    /// <summary>
    /// The current <c>providers/wikidata_reconciliation.json</c> contents. The adapter is a
    /// singleton built from the config present at startup, so the type allow/deny lists
    /// (<c>instance_of_classes</c>, <c>exclude_classes</c>, <c>edition_pivot</c>) are re-read here
    /// to honour config hot reload. Falls back to the startup config when no loader is wired or
    /// the file is missing/invalid.
    /// </summary>
    private ReconciliationProviderConfig CurrentConfig =>
        _configLoader?.LoadConfig<ReconciliationProviderConfig>("providers", "wikidata_reconciliation")
        ?? _config;

    // ── IExternalMetadataProvider ─────────────────────────────────────────────

    public string Name => _config.Name;

    public ProviderDomain Domain => ProviderDomain.Universal;

    public IReadOnlyList<string> CapabilityTags =>
        _config.DataExtension.PropertyLabels.Values.Distinct().ToList();

    public Guid ProviderId => _providerId;

    /// <summary>Universal provider: handles all media types.</summary>
    public bool CanHandle(MediaType mediaType) => true;

    /// <summary>
    /// The minimum reconciliation score (0–100) for a candidate to be auto-accepted.
    /// Exposed so batch callers (e.g. <see cref="WikidataBridgeWorker"/>) can apply
    /// the same threshold when evaluating batch reconciliation results.
    /// </summary>
    public double ReviewThreshold => _config.Reconciliation.ReviewThreshold;

    /// <summary>
    /// Handles MediaAsset, Person, and Stage 3 fictional entity types.
    /// </summary>
    public bool CanHandle(EntityType entityType) =>
        entityType is EntityType.MediaAsset
            or EntityType.Person
            or EntityType.Character
            or EntityType.Location
            or EntityType.Organization
            or EntityType.Event
            or EntityType.Object;

    /// <summary>
    /// Fetches metadata claims by reconciling the entity against Wikidata and
    /// extending the resolved QID with structured property values.
    ///
    /// For Person entities: reconciles by person name with occupation/notable-work constraints.
    /// For MediaAsset entities: reconciles by title with author constraint, then extends.
    ///
    /// All exceptions are caught and an empty list is returned on failure.
    /// </summary>
    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        ProviderLookupRequest request,
        CancellationToken ct = default)
    {
        if (!CanHandle(request.EntityType))
        {
            return [];
        }

        try
        {
            return request.EntityType switch
            {
                EntityType.Person => await FetchPersonAsync(request, ct).ConfigureAwait(false),
                EntityType.Character or EntityType.Location or EntityType.Organization or EntityType.Event or EntityType.Object
                    => await FetchFictionalEntityAsync(request, ct).ConfigureAwait(false),
                _ => await FetchWorkAsync(request, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: FetchAsync failed for entity {EntityId}",
                Name, request.EntityId);
            return [];
        }
    }

    /// <summary>
    /// Searches Wikidata via the Reconciliation API and returns multiple result candidates
    /// for user selection in the Needs Review resolution panel.
    /// </summary>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        ProviderLookupRequest request,
        int limit = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return [];
        }

        try
        {
            var constraints = BuildTitleSearchConstraints(request);

            // Use the shared ReconcileAsync which applies type filtering via CirrusSearch
            var candidates = await ReconcileAsync(
                request.Title, constraints, ct, request.MediaType).ConfigureAwait(false);

            _logger.LogInformation(
                "{Provider}: SearchAsync '{Query}' ({MediaType}) — {Count} reconciliation candidate(s)",
                Name, request.Title, request.MediaType, candidates.Count);

            if (candidates.Count == 0)
            {
                return [];
            }

            // Optionally filter by media type using P31, with title/author/ISBN hints
            // for composite scoring (boosts candidates with matching metadata).
            if (request.MediaType != MediaType.Unknown)
            {
                var filtered = await FilterByMediaTypeAsync(
                    candidates, request.MediaType, ct,
                    request.Title, request.Author, request.Isbn, request.Year).ConfigureAwait(false);

                // For audiobooks: if audiobook-specific filtering eliminates everything,
                // fall back to Books classes (an audiobook is a format of a literary work).
                if (filtered.Count == 0 && request.MediaType == MediaType.Audiobooks)
                {
                    _logger.LogDebug("{Provider}: audiobook filter returned 0 results, falling back to Books classes",
                        Name);
                    filtered = await FilterByMediaTypeAsync(
                        candidates, MediaType.Books, ct,
                        request.Title, request.Author, request.Isbn, request.Year).ConfigureAwait(false);
                }

                // Strict filtering: only return candidates that positively match the
                // expected instance_of classes. If nothing matches, return empty — the
                // user gets 0 results, which is correct (no book match found).
                _logger.LogInformation(
                    "{Provider}: SearchAsync type filter ({MediaType}) — {Kept}/{Total} candidates survived",
                    Name, request.MediaType, filtered.Count, candidates.Count);
                candidates = filtered;
            }

            // Display-friendly titles: the library's MatchedLabel on each ReconciliationResult
            // already contains the alias or sitelink that matched — no additional call needed.

            // For audiobook searches: discover audiobook editions via P747 for work-level results.
            // Edition results go first (they're more specific), work fallbacks come after.
            if (request.MediaType == MediaType.Audiobooks)
            {
                var editionResults = new List<SearchResultItem>();
                var workResults = new List<SearchResultItem>();

                foreach (var c in candidates.Take(limit))
                {
                    // Try to discover audiobook editions for this candidate.
                    var editions = await DiscoverAudiobookEditionsAsync(c.Id, null, ct)
                        .ConfigureAwait(false);

                    if (editions.Count > 0)
                    {
                        foreach (var ed in editions)
                        {
                            // Build a rich description with audiobook-specific details.
                            var parts = new List<string>();
                            if (!string.IsNullOrEmpty(ed.Narrator))
                            {
                                parts.Add($"Narrated by {ed.Narrator}");
                            }

                            if (IsDisplayableSearchMetadata(ed.Duration))
                            {
                                parts.Add($"Duration: {ed.Duration}");
                            }

                            if (!string.IsNullOrEmpty(ed.Publisher))
                            {
                                parts.Add($"Publisher: {ed.Publisher}");
                            }

                            if (!string.IsNullOrEmpty(c.Description))
                            {
                                parts.Add(c.Description);
                            }

                            var editionDesc = parts.Count > 0
                                ? string.Join(" · ", parts)
                                : c.Description;

                            editionResults.Add(new SearchResultItem(
                                Title: c.Name,
                                Author: null,
                                Description: editionDesc,
                                Year: null,
                                ThumbnailUrl: null,
                                ProviderItemId: ed.EditionQid ?? c.Id,
                                Confidence: c.Score / 100.0,
                                ProviderName: Name,
                                ResultType: "audiobook_edition",
                                ExtraFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["canonical_kind"] = "Audiobook edition",
                                    ["narrator"] = ed.Narrator ?? string.Empty,
                                    ["duration"] = ed.Duration ?? string.Empty,
                                    ["publisher"] = ed.Publisher ?? string.Empty,
                                    ["asin"] = ed.ASIN ?? string.Empty,
                                }.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
                        }
                    }

                    // Always include the work as a fallback.
                    workResults.Add(new SearchResultItem(
                        Title: c.Name,
                        Author: null,
                        Description: c.Description,
                        Year: null,
                        ThumbnailUrl: null,
                        ProviderItemId: c.Id,
                        Confidence: c.Score / 100.0,
                        ProviderName: Name,
                        ResultType: "work",
                        ExtraFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["canonical_kind"] = "Work",
                        }));
                }

                // Editions first, then work fallbacks.
                var combined = editionResults.Concat(workResults).Take(limit).ToList();
                return combined.Count > 0 ? combined : workResults.Take(limit).ToList();
            }

            return candidates
                .Take(limit)
                .Select(c => new SearchResultItem(
                    Title: c.Name,
                    Author: null,
                    Description: c.Description,
                    Year: null,
                    ThumbnailUrl: null,
                    ProviderItemId: c.Id,
                    Confidence: c.Score / 100.0,
                    ProviderName: Name))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: SearchAsync failed", Name);
            return [];
        }
    }

    private static bool IsDisplayableSearchMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Regex.IsMatch(value.Trim(), @"^Q\d+$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        var compact = Regex.Replace(value, @"\D", string.Empty);
        return compact.Length <= 8;
    }

    // ── Public direct-call methods (used by the hydration pipeline) ───────────

    /// <summary>
    /// Reconciles a single query string to Wikidata candidates.
    /// Returns up to <c>config.reconciliation.max_candidates</c> results.
    /// When <paramref name="mediaType"/> is specified, CirrusSearch pre-filters by
    /// the configured <c>instance_of_classes</c> for that media type — the same logic
    /// used by <see cref="SearchAsync"/> for manual searches.
    /// </summary>
    /// <param name="query">The entity name to reconcile (e.g. "Dune", "Frank Herbert").</param>
    /// <param name="propertyConstraints">Optional P-code → value constraints to narrow the search.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaType">Media type for CirrusSearch type pre-filtering (default: Unknown = no filter).</param>
    /// <remarks>
    /// SOURCE OF TRUTH: All manual search requests must flow through
    /// <see cref="BuildManualSearchRequest"/>. Do not construct
    /// <c>ReconciliationRequest</c> instances by hand in new code; extend the
    /// builder instead. Parity is enforced by <c>WikidataParityTests</c>.
    /// </remarks>
}
