using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Unmatched;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The files the Engine could not match (Library setup spec D34, D40): a library's Unmatched list and count, Retry now, Keep as is, Keep all
/// and Look up again. Looking at and managing them is an administrator's job, like the rest of Libraries in Settings. A Vault library is
/// never listed or acted on here.
/// </summary>
public static class UnmatchedEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapUnmatchedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/unmatched")
            .WithTags("Library", "Unmatched")
            .RequireEffectiveAdministrator();

        group.MapGet("/counts", async (
            HttpContext http,
            IRequestAuthorityResolver authorities,
            [FromServices] UnmatchedCountService counts,
            CancellationToken ct) =>
        {
            var scope = UnmatchedScope.For(await authorities.ResolveAsync(http, ct));
            return Results.Ok(new UnmatchedCountsDto { Counts = new Dictionary<string, int>(await counts.CountsAsync(scope, ct)) });
        })
        .WithName("GetUnmatchedCounts")
        .WithSummary("Each library's Unmatched count (kept-as-is files excluded), for the libraries the caller may see.")
        .Produces<UnmatchedCountsDto>();

        group.MapGet("/libraries/{libraryId}", async (
            string libraryId,
            bool? includeKept,
            int? skip,
            int? take,
            IUnmatchedAssetRepository unmatched,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            CancellationToken ct) =>
        {
            if (!await IsListedLibraryAsync(libraryId, configuration, vault, ct))
            {
                return ApiErrors.NotFound($"Library '{libraryId}' was not found.");
            }

            var offset = Math.Max(0, skip ?? 0);
            var size = Math.Clamp(take ?? 50, 1, MaxPageSize);
            var page = await unmatched.ListByLibraryAsync(libraryId, includeKept ?? false, offset, size, ct);
            return Results.Ok(new UnmatchedPageDto
            {
                Items = page.Items.Select(ToDto).ToList(),
                Total = page.Total,
                Skip = offset,
                Take = size,
            });
        })
        .WithName("ListUnmatchedItems")
        .WithSummary("One library's Unmatched files, newest first, a page at a time. Kept-as-is files are left out unless include_kept is set.")
        .Produces<UnmatchedPageDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/libraries/{libraryId}/retry", async (
            string libraryId,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] UnmatchedAssetService service,
            CancellationToken ct) =>
            await IsListedLibraryAsync(libraryId, configuration, vault, ct)
                ? Results.Ok(new UnmatchedActionResultDto { Affected = await service.RetryLibraryNowAsync(libraryId, ct) })
                : ApiErrors.NotFound($"Library '{libraryId}' was not found."))
        .WithName("RetryUnmatchedLibrary")
        .WithSummary("Retry now for a whole library: makes every Unmatched file in it that is not kept due now and returns at once; the files are looked up in the background.")
        .Produces<UnmatchedActionResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/libraries/{libraryId}/keep", async (
            string libraryId,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] UnmatchedAssetService service,
            CancellationToken ct) =>
            await IsListedLibraryAsync(libraryId, configuration, vault, ct)
                ? Results.Ok(new UnmatchedActionResultDto { Affected = await service.KeepAllAsIsAsync(libraryId, ct) })
                : ApiErrors.NotFound($"Library '{libraryId}' was not found."))
        .WithName("KeepAllUnmatchedAsTheyAre")
        .WithSummary("Keep all Unmatched as they are, for one library: stops their retries and clears them from the Unmatched count and filter.")
        .Produces<UnmatchedActionResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/items/{assetId:guid}/retry", (
            Guid assetId,
            IUnmatchedAssetRepository unmatched,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] UnmatchedAssetService service,
            CancellationToken ct) =>
            ActOnItemAsync(assetId, unmatched, configuration, vault, service.RetryNowAsync, ct))
        .WithName("RetryUnmatchedItem")
        .WithSummary("Retry now for one Unmatched file that is not kept. A kept file is brought back with Look up again.")
        .Produces<UnmatchedActionResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/items/{assetId:guid}/keep", (
            Guid assetId,
            IUnmatchedAssetRepository unmatched,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] UnmatchedAssetService service,
            CancellationToken ct) =>
            ActOnItemAsync(assetId, unmatched, configuration, vault, service.KeepAsIsAsync, ct))
        .WithName("KeepUnmatchedItemAsItIs")
        .WithSummary("Keep as is: stops the file's retries and clears it from the Unmatched count and filter.")
        .Produces<UnmatchedActionResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/items/{assetId:guid}/look-up-again", (
            Guid assetId,
            IUnmatchedAssetRepository unmatched,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] UnmatchedAssetService service,
            CancellationToken ct) =>
            ActOnItemAsync(assetId, unmatched, configuration, vault, service.LookUpAgainAsync, ct))
        .WithName("LookUpUnmatchedItemAgain")
        .WithSummary("Look up again: brings a kept file back (clears the kept mark) and looks it up now.")
        .Produces<UnmatchedActionResultDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ActOnItemAsync(
        Guid assetId,
        IUnmatchedAssetRepository unmatched,
        IConfigurationLoader configuration,
        IVaultLibraryRepository vault,
        Func<Guid, CancellationToken, Task<bool>> action,
        CancellationToken ct)
    {
        var item = await unmatched.GetAsync(assetId, ct);
        if (item is null || !await IsListedLibraryAsync(item.LibraryId, configuration, vault, ct))
        {
            return ApiErrors.NotFound($"File '{assetId}' is not Unmatched.");
        }

        var done = await action(assetId, ct);
        return Results.Ok(new UnmatchedActionResultDto { Affected = done ? 1 : 0 });
    }

    // A library the Libraries screens list: configured, and not a Vault library (the Vault has its own routes and is never in a shared list).
    internal static async Task<bool> IsListedLibraryAsync(
        string libraryId, IConfigurationLoader configuration, IVaultLibraryRepository vault, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(libraryId)
            || !configuration.LoadLibraries().Libraries.Any(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !(await vault.GetIdsAsync(ct)).Contains(libraryId);
    }

    private static UnmatchedItemDto ToDto(UnmatchedAssetListItem item) => new()
    {
        AssetId = item.Asset.AssetId,
        LibraryId = item.Asset.LibraryId,
        Title = item.Title,
        FileName = Path.GetFileName(item.FilePath),
        Reason = item.Asset.Reason,
        FirstUnmatchedAt = item.Asset.FirstUnmatchedAt,
        LastTriedAt = item.Asset.LastTriedAt,
        NextRetryAt = item.Asset.NextRetryAt,
        KeptAsIs = item.Asset.KeptAsIs,
    };
}
