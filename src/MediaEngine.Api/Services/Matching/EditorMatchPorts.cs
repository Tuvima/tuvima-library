using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Decides the editor kind of an item (the same decision the editor's header makes), so the Match tab never trusts a kind sent by the Dashboard.</summary>
public interface IEditorMatchKindResolver
{
    Task<string?> ResolveAsync(Guid entityId, CancellationToken ct);
}

/// <summary>The header's own kind decision, shared with the Match tab.</summary>
public sealed class HeaderEditorMatchKindResolver(MediaEngine.Api.Services.EditSession.EditorHeaderReadService header) : IEditorMatchKindResolver
{
    public Task<string?> ResolveAsync(Guid entityId, CancellationToken ct) => header.ResolveKindAsync(entityId, ct);
}

/// <summary>The providers that fit a kind of item, with whether each is switched on and whether it still needs a key.</summary>
public interface IEditorMatchProviderCatalog
{
    IReadOnlyList<RetailProviderInfo> List(string mediaType);
}

/// <summary>What a Match search got back, or why it was refused.</summary>
/// <param name="Response">The candidates and the providers that did not answer; null when the search was refused.</param>
/// <param name="Refusal">The plain reason it was refused (the item is gone, its library never looks anything up); null when it ran.</param>
public sealed record EditorMatchSearchOutcome(ItemCanonicalSearchResponseDto? Response, string? Refusal);

/// <summary>The one place the Match tab asks the retail providers, so a test can stand in for them.</summary>
public interface IEditorMatchCandidateSearch
{
    Task<EditorMatchSearchOutcome> SearchAsync(Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct);
}

/// <summary>The providers of the Library's retail search, as the Match tab lists them.</summary>
public sealed class RetailEditorMatchProviderCatalog(RetailMatchPreviewService preview) : IEditorMatchProviderCatalog
{
    public IReadOnlyList<RetailProviderInfo> List(string mediaType) => preview.ListProviders(MediaTypeParser.Parse(mediaType));
}

/// <summary>Runs the Library's canonical retail search (the same one the older canonical-search connection point runs).</summary>
public sealed class CanonicalEditorMatchCandidateSearch(
    RetailMatchPreviewService retail,
    WikidataMatchPreviewService wikidata,
    IItemCanonicalRepository itemCanonicalData,
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    IExternalLookupGuard lookupGuard) : IEditorMatchCandidateSearch
{
    public async Task<EditorMatchSearchOutcome> SearchAsync(Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct)
    {
        var result = await ItemCanonicalEndpoints.SearchCanonicalCandidatesAsync(
            entityId, request, retail, wikidata, itemCanonicalData, httpFactory, cache, lookupGuard, ct).ConfigureAwait(false);
        return result switch
        {
            Ok<ItemCanonicalSearchResponseDto> { Value: { } response } => new EditorMatchSearchOutcome(response, null),
            ProblemHttpResult problem => new EditorMatchSearchOutcome(null, problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title),
            _ => new EditorMatchSearchOutcome(null, "The search could not be run."),
        };
    }
}
