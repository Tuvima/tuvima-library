using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Persons;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    // ── Persons by Collection (/persons/by-collection) ────────────────────────────────

    /// <summary>GET /persons?role={role}&amp;limit={limit} — list persons from the shared wire contract.</summary>
    Task<IReadOnlyList<PersonListItemResponse>?> GetPersonsAsync(string? role = null, int offset = 0, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// GET /persons?catalog=true — page through canonical contributors on owned works.
    /// </summary>
    Task<PagedResponse<PersonListItemResponse>?> GetPersonsPageAsync(
        string? search = null,
        string? role = null,
        int offset = 0,
        int limit = 100,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default);

    /// <summary>GET /persons?role={role}&amp;limit={limit}  -  list persons filtered by role.</summary>
    Task<List<PersonViewModel>> GetPersonsByRoleAsync(
        string role, int limit = 50, CancellationToken ct = default);

    /// <summary>GET /persons/by-collection/{collectionId} — all persons linked to works in a collection.</summary>
    Task<List<PersonViewModel>> GetPersonsByCollectionAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>GET /persons/by-work/{workId} — all persons linked to a specific work.</summary>
    Task<List<PersonViewModel>> GetPersonsByWorkAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /persons/role-counts — count of persons per role.</summary>
    Task<Dictionary<string, int>> GetPersonRoleCountsAsync(CancellationToken ct = default);

    /// <summary>GET /persons/presence?ids=... — media type counts per person.</summary>
    Task<Dictionary<string, Dictionary<string, int>>> GetPersonPresenceAsync(IEnumerable<Guid> personIds, CancellationToken ct = default);


    // ── Related collections (/collections/{id}/related) ────────────────────────────────────

    /// <summary>GET /collections/{id}/related?limit= — related collections by series/author/genre cascade.</summary>
    Task<RelatedCollectionsViewModel?> GetRelatedCollectionsAsync(Guid collectionId, int limit = 20, CancellationToken ct = default);

    // \u2500\u2500 Person detail (/persons/{id}) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    /// <summary>GET /persons/{id} \u2014 full person detail with social links and enrichment data.</summary>
    Task<PersonDetailViewModel?> GetPersonDetailAsync(Guid personId, CancellationToken ct = default);


    /// <summary>GET /persons/{id}/library-credits \u2014 role-aware owned work credits for a person.</summary>
    Task<List<PersonLibraryCreditViewModel>> GetPersonLibraryCreditsAsync(Guid personId, CancellationToken ct = default);

    /// <summary>GET /persons/{id}/works \u2014 all collections containing works by this person.</summary>
    Task<List<CollectionViewModel>> GetWorksByPersonAsync(Guid personId, CancellationToken ct = default);

    /// <summary>GET /persons/{id}/aliases — aliases and pseudonyms for a person.</summary>
    Task<PersonAliasResponse?> GetPersonAliasesAsync(Guid personId, CancellationToken ct = default);

}
