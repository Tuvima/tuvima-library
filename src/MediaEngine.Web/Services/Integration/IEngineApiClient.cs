using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Strongly-typed HTTP client for the Engine API.
/// All methods are fire-and-forget safe: they return null / empty list on failure
/// rather than throwing, so callers control error display.
/// </summary>
public partial interface IEngineApiClient
{
    string? LastError { get; }

    int? LastStatusCode { get; }

    string? LastFailedEndpoint { get; }

    string? LastFailureKind { get; }

    TimeSpan? LastRetryAfter { get; }

    string ToAbsoluteEngineUrl(string value);

    // ── Review queue (/review) ──────────────────────────────────────────────

    /// <summary>GET /review/pending?limit= — list pending review queue items.</summary>
    Task<List<ReviewItemViewModel>> GetPendingReviewsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>GET /review/pending?limit= — like <see cref="GetPendingReviewsAsync"/>, but a failed load is reported instead of returned as an empty list.</summary>
    Task<ReviewListAttempt> GetPendingReviewsAttemptAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>GET /review/pending?library= — the pending items whose file belongs to one library.</summary>
    Task<ReviewListAttempt> GetPendingReviewsForLibraryAttemptAsync(string libraryId, int limit = 50, CancellationToken ct = default);

    /// <summary>GET /review/{id} — single review item with full detail.</summary>
    Task<ReviewItemViewModel?> GetReviewItemAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /review/count — pending count for sidebar badge; null when the count could not be read (a failed read is never a count of zero).</summary>
    Task<int?> GetReviewCountAsync(CancellationToken ct = default);

    /// <summary>POST /review/{id}/resolve — resolve a review item.</summary>
    Task<bool> ResolveReviewItemAsync(Guid id, ReviewResolveRequestDto request, CancellationToken ct = default);

    /// <summary>POST /review/{id}/dismiss — dismiss a review item.</summary>
    Task<bool> DismissReviewItemAsync(Guid id, CancellationToken ct = default);

    /// <summary>POST /review/{id}/skip-universe — skip Universe matching and dismiss the item.</summary>
    Task<bool> SkipUniverseAsync(Guid id, CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/reclassify — reclassify a media asset to a different media type.</summary>
    Task<bool> ReclassifyMediaTypeAsync(Guid entityId, string mediaType, CancellationToken ct = default);

    // ── Character data (/universe, /library/characters, /library/persons) ──

    /// <summary>GET /works/{id}/cast — actor and character credits for a single work.</summary>
    Task<List<CastCreditDto>> GetWorkCastAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/artwork — grouped artwork variants for the editor.</summary>
    Task<ArtworkEditorDto?> GetArtworkAsync(Guid entityId, CancellationToken ct = default);
    // ── Universe Graph (Chronicle Explorer) ─────────────────────────────────

    /// <summary>GET /universe/{qid}/graph — fetch the universe relationship graph with optional filters.</summary>
    Task<UniverseGraphResponse?> GetUniverseGraphAsync(
        string qid,
        int? timelineYear = null,
        string? types = null,
        string? center = null,
        int? depth = null,
        bool includeSupplementalLore = false,
        CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/lore-delta — check which entities have changed on Wikidata since last enrichment.</summary>
    Task<IReadOnlyList<UniverseLoreDeltaResponse>> CheckLoreDeltaAsync(
        string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/lore-sources - admin review list for plugin lore sources.</summary>
    Task<IReadOnlyList<UniverseLoreSourceResponse>> GetUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/discover - find source candidates through lore plugins.</summary>
    Task<IReadOnlyList<UniverseLoreSourceResponse>> DiscoverUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/manual - add a plugin lore source for admin approval.</summary>
    Task<UniverseLoreSourceResponse?> AddUniverseLoreSourceAsync(
        string qid, UniverseLoreManualSourceRequest request, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/{sourceId}/approve - approve a plugin lore source.</summary>
    Task<IReadOnlyList<UniverseLoreSourceResponse>> ApproveUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/{sourceId}/reject - reject a plugin lore source.</summary>
    Task<IReadOnlyList<UniverseLoreSourceResponse>> RejectUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore/enrich - import approved plugin lore for this universe.</summary>
    Task<UniverseLoreEnrichResponse?> EnrichUniverseLoreAsync(
        string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/cast — characters with their real-world performers.</summary>
    Task<UniverseCastResponse?> GetUniverseCastAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// GET /universe/{qid}/also-appears-in — where else a character, place, organisation (<paramref name="entityQid"/>) or
    /// person (<paramref name="personId"/>) shows up across the Library. Give exactly one of the two. A 404 (unknown, or nothing the
    /// profile may see) is an empty result; null means a real failure.
    /// </summary>
    Task<EntityAppearancesDto?> GetEntityAppearancesAsync(
        string universeQid, string? entityQid, Guid? personId, CancellationToken ct = default, Guid? reachedWorkId = null);

    /// <summary>GET /universe/{qid}/adaptations — adaptation chain (based_on/derivative_work/inspired_by).</summary>
    Task<UniverseAdaptationTreeResponse?> GetUniverseAdaptationsAsync(string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/paths?from=X&amp;to=Y — find shortest paths between two entities.</summary>
    Task<UniversePathsResponse?> FindPathsAsync(
        string qid, string fromQid, string toQid, int maxHops = 4, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/family-tree?character=X — family tree rooted at a character.</summary>
    Task<UniverseFamilyTreeResponse?> GetFamilyTreeAsync(
        string qid, string characterQid, int generations = 3, CancellationToken ct = default);

    // ── Search (/search) ──────────────────────────────────────────────────

    /// <summary>GET /metadata/{qid}/aliases — fetch Wikidata aliases (alternative titles) for a QID.</summary>
    Task<WikidataAliasesResponse?> GetAliasesAsync(string qid, CancellationToken ct = default);

    /// <summary>POST /search/universe — search Wikidata for identity candidates, enriched with cover art.</summary>
    Task<SearchUniverseResponseDto?> SearchUniverseAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localAuthor = null, CancellationToken ct = default);

    /// <summary>POST /search/retail — search retail providers for cover art and basic metadata.</summary>
    Task<SearchRetailResponseDto?> SearchRetailAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localTitle = null, string? localAuthor = null, string? localYear = null,
        Dictionary<string, string>? fileHints = null,
        Dictionary<string, string>? searchFields = null,
        CancellationToken ct = default);

    /// <summary>Unified resolve search with retail + description scoring.</summary>
    Task<SearchResolveResponseDto?> SearchResolveAsync(
        string query, string mediaType, int maxCandidates,
        Dictionary<string, string>? fileHints, CancellationToken ct = default);

    /// <summary>Loads provider-specific evidence for a selected retail candidate.</summary>
    Task<RetailCandidateDetailDto?> GetRetailCandidateDetailAsync(
        RetailCandidateDetailRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/canonical-search - targeted canonical search for a field group.</summary>
    Task<ItemCanonicalSearchResponseDto?> SearchItemCanonicalAsync(
        Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/canonical-apply - apply a targeted canonical candidate.</summary>
    Task<ItemCanonicalApplyResponseDto?> ApplyItemCanonicalAsync(
        Guid entityId, ItemCanonicalApplyRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/retail-match - replace or confirm the provider match.</summary>
    Task<ItemCanonicalApplyResponseDto?> ReplaceRetailMatchAsync(
        Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct = default);

    Task<RetailMatchMovePreviewDto?> PreviewRetailMatchAsync(
        Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/wikidata-match - replace, clear, reject, or mark Wikidata missing.</summary>
    Task<ItemCanonicalApplyResponseDto?> ReplaceWikidataMatchAsync(
        Guid entityId, ReplaceWikidataMatchRequestDto request, CancellationToken ct = default);
}
