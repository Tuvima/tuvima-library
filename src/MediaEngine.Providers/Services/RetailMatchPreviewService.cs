using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Read-only editor boundary over the interactive retail candidate path.
/// </summary>
public sealed class RetailMatchPreviewService(SearchService searchService)
{
    /// <summary>The providers that fit a media type, each with whether it is switched on and whether it still needs a key.</summary>
    public IReadOnlyList<RetailProviderInfo> ListProviders(MediaType mediaType) => searchService.ListRetailProviders(mediaType);

    public Task<SearchRetailResult> SearchAsync(
        SearchRetailRequest request,
        CancellationToken ct = default) =>
        searchService.SearchRetailAsync(request, ct);
}
