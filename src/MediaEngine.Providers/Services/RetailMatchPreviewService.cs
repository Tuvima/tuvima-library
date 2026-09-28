using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Read-only editor boundary over the interactive retail candidate path.
/// </summary>
public sealed class RetailMatchPreviewService(SearchService searchService)
{
    public Task<SearchRetailResult> SearchAsync(
        SearchRetailRequest request,
        CancellationToken ct = default) =>
        searchService.SearchRetailAsync(request, ct);
}
