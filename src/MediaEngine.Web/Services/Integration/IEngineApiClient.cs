using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Reports;
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

    /// <summary>GET /review/{id} — single review item with full detail.</summary>
    Task<ReviewItemViewModel?> GetReviewItemAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /review/count — pending count for sidebar badge.</summary>
    Task<int> GetReviewCountAsync(CancellationToken ct = default);

    /// <summary>POST /review/{id}/resolve — resolve a review item.</summary>
    Task<bool> ResolveReviewItemAsync(Guid id, ReviewResolveRequestDto request, CancellationToken ct = default);

    /// <summary>POST /review/{id}/dismiss — dismiss a review item.</summary>
    Task<bool> DismissReviewItemAsync(Guid id, CancellationToken ct = default);

    /// <summary>POST /review/{id}/skip-universe — skip Universe matching and dismiss the item.</summary>
    Task<bool> SkipUniverseAsync(Guid id, CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/reclassify — reclassify a media asset to a different media type.</summary>
    Task<bool> ReclassifyMediaTypeAsync(Guid entityId, string mediaType, CancellationToken ct = default);

    // ── Universe health + character data (/universe, /library/characters, /library/persons) ──

    /// <summary>GET /universe/{qid}/health — health score for a fictional universe.</summary>
    Task<UniverseHealthDto?> GetUniverseHealthAsync(string qid, CancellationToken ct = default);

    /// <summary>GET /library/universes/{universeQid}/characters - characters in a universe with default actor/portrait.</summary>
    Task<IReadOnlyList<UniverseCharacterDto>> GetUniverseCharactersAsync(string universeQid, CancellationToken ct = default);

    /// <summary>GET /library/persons/{personId}/character-roles - character roles with portraits for a person.</summary>
    Task<IReadOnlyList<CharacterRoleDto>> GetPersonCharacterRolesAsync(Guid personId, CancellationToken ct = default);

    /// <summary>GET /works/{id}/cast — actor and character credits for a single work.</summary>
    Task<List<CastCreditDto>> GetWorkCastAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/artwork — grouped artwork variants for the editor.</summary>
    Task<ArtworkEditorDto?> GetArtworkAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>POST /library/enrichment/universe/trigger - manually trigger Stage 3 universe enrichment.</summary>
    Task TriggerUniverseEnrichmentAsync(CancellationToken ct = default);
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
    Task<IReadOnlyList<LoreDeltaResultDto>> CheckLoreDeltaAsync(
        string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/lore-sources - admin review list for plugin lore sources.</summary>
    Task<IReadOnlyList<UniverseLoreSourceViewModel>> GetUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/discover - find source candidates through lore plugins.</summary>
    Task<IReadOnlyList<UniverseLoreSourceViewModel>> DiscoverUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/manual - add a plugin lore source for admin approval.</summary>
    Task<UniverseLoreSourceViewModel?> AddUniverseLoreSourceAsync(
        string qid, UniverseLoreManualSourceRequest request, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/{sourceId}/approve - approve a plugin lore source.</summary>
    Task<IReadOnlyList<UniverseLoreSourceViewModel>> ApproveUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore-sources/{sourceId}/reject - reject a plugin lore source.</summary>
    Task<IReadOnlyList<UniverseLoreSourceViewModel>> RejectUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default);

    /// <summary>POST /universe/{qid}/lore/enrich - import approved plugin lore for this universe.</summary>
    Task<UniverseLoreEnrichmentSummaryViewModel?> EnrichUniverseLoreAsync(
        string qid, CancellationToken ct = default);

    /// <summary>GET /universes — list all narrative roots (fictional universes).</summary>
    Task<IReadOnlyList<NarrativeRootDto>> GetUniversesAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /universe/entity/{qid}/deep-enrich — triggers on-demand deep enrichment for an
    /// entity and its un-enriched neighbors. Used by Chronicle Explorer when a user clicks
    /// on an entity that hasn't been deep-enriched yet.
    /// </summary>
    Task<DeepEnrichResponse?> TriggerDeepEnrichAsync(string entityQid, int depth = 2, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/cast — characters with their real-world performers.</summary>
    Task<UniverseCastResponse?> GetUniverseCastAsync(string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/adaptations — adaptation chain (based_on/derivative_work/inspired_by).</summary>
    Task<UniverseAdaptationsResponse?> GetUniverseAdaptationsAsync(string qid, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/paths?from=X&amp;to=Y — find shortest paths between two entities.</summary>
    Task<UniversePathsResponse?> FindPathsAsync(
        string qid, string fromQid, string toQid, int maxHops = 4, CancellationToken ct = default);

    /// <summary>GET /universe/{qid}/family-tree?character=X — family tree rooted at a character.</summary>
    Task<FamilyTreeResponse?> GetFamilyTreeAsync(
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

    /// <summary>POST /library/items/{entityId}/apply-match - apply a match to a library item.</summary>
    Task<ApplyMatchResponseDto?> ApplyLibraryItemMatchAsync(
        Guid entityId, ApplyMatchRequestDto request,
        CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/canonical-search - targeted canonical search for a field group.</summary>
    Task<ItemCanonicalSearchResponseDto?> SearchItemCanonicalAsync(
        Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/canonical-apply - apply a targeted canonical candidate.</summary>
    Task<ItemCanonicalApplyResponseDto?> ApplyItemCanonicalAsync(
        Guid entityId, ItemCanonicalApplyRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/retail-match - replace or confirm the provider match.</summary>
    Task<ItemCanonicalApplyResponseDto?> ReplaceRetailMatchAsync(
        Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/wikidata-match - replace, clear, reject, or mark Wikidata missing.</summary>
    Task<ItemCanonicalApplyResponseDto?> ReplaceWikidataMatchAsync(
        Guid entityId, ReplaceWikidataMatchRequestDto request, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/create-manual - create a manual metadata entry.</summary>
    Task<CreateManualResponseDto?> CreateManualEntryAsync(
        Guid entityId, CreateManualRequestDto request,
        CancellationToken ct = default);

    /// <summary>DELETE /library/items/{entityId} - permanently remove a work and all its files.</summary>
    Task<bool> DeleteLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Submit a problem report on a media item.</summary>
    Task<SubmitReportResponse?> SubmitReportAsync(SubmitReportRequest request, CancellationToken ct = default);

    /// <summary>Get all problem reports for a specific entity.</summary>
    Task<List<ReportEntryResponse>> GetReportsForEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Resolve a problem report.</summary>
    Task<bool> ResolveReportAsync(long activityId, CancellationToken ct = default);

    /// <summary>Dismiss a problem report.</summary>
    Task<bool> DismissReportAsync(long activityId, CancellationToken ct = default);

    // ── Universe Alignment ──

    /// <summary>GET /library/universe-candidates - works with universe QIDs but no collection assignment.</summary>
    Task<List<UniverseCandidateViewModel>> GetUniverseCandidatesAsync(CancellationToken ct = default);

    /// <summary>POST /library/universe-candidates/{workId}/accept - accept a universe assignment.</summary>
    Task<bool> AcceptUniverseCandidateAsync(Guid workId, string targetCollectionQid, CancellationToken ct = default);

    /// <summary>POST /library/universe-candidates/{workId}/reject - reject a universe candidate.</summary>
    Task<bool> RejectUniverseCandidateAsync(Guid workId, CancellationToken ct = default);

    /// <summary>POST /library/universe-candidates/batch-accept - batch accept universe assignments.</summary>
    Task<int> BatchAcceptUniverseCandidatesAsync(List<Guid> workIds, CancellationToken ct = default);

    /// <summary>GET /library/universe-unlinked - works with QID but no universe properties.</summary>
    Task<List<UnlinkedWorkViewModel>> GetUniverseUnlinkedAsync(CancellationToken ct = default);

    /// <summary>POST /library/universe-assign - manually assign a work to a collection.</summary>
    Task<bool> ManualUniverseAssignAsync(Guid workId, Guid collectionId, CancellationToken ct = default);
}
