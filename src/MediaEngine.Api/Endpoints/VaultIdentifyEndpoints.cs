using System.Globalization;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.Vault;
using MediaEngine.Storage;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Identify for a Vault video (Videos spec D38), how a Vault video was matched (D30) and the quiet Vault activity list (D32).
/// A Vault video is absent for anyone without an unlocked Vault mode session: the item routes go through the same catalogue access
/// filter every other action on a file uses (not found, never forbidden), and the responses are private and uncacheable. Search
/// text travels in a request body, and neither a title nor a body is ever logged. What is applied is read again from the server by
/// record id, never taken from the request.
/// </summary>
public static class VaultIdentifyEndpoints
{
    public static IEndpointRouteBuilder MapVaultIdentifyEndpoints(this IEndpointRouteBuilder app)
    {
        var items = app.MapGroup("/api/v1/vault/items").WithTags("Vault");

        // Every answer from these routes is private and uncacheable, including the "not found" a locked session gets and the one an id
        // that does not exist gets, so the header never tells the two apart. This filter runs before the access filter on each route.
        // The pre-binding gate answers a caller whose Vault mode is off "not found" before any body or id is read (a malformed body
        // would otherwise answer 400 and show that the route exists).
        items.WithMetadata(VaultGatedRoute.Instance);
        items.AddEndpointFilter(static async (context, next) =>
        {
            CatalogueResourceAuthorizationService.ApplyVaultCacheControl(context.HttpContext.Response);
            return await next(context);
        });

        items.MapPost("/{assetId:guid}/identify/search", async (
            Guid assetId, VaultIdentifySearchRequest request, VaultIdentifyService identify, CancellationToken ct) =>
        {
            var list = await identify.SearchAsync(assetId, request.Text, ct);
            return list.Status == VaultIdentifyStatus.Unavailable ? ApiErrors.NotFound("Not found.") : Results.Ok(ToDto(list));
        })
        .WithName("SearchVaultIdentifyCandidates")
        .WithSummary("Records that might be this Vault video, found by the typed text, the name patterns' title or the product code.")
        .Produces<VaultCandidateListDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataWrite);

        items.MapPost("/{assetId:guid}/identify/preview", async (
            Guid assetId, VaultIdentifyPreviewRequest request, VaultIdentifyService identify, CancellationToken ct) =>
        {
            var preview = await identify.PreviewAsync(assetId, request.ServerId, request.RecordId, ct);
            return preview.Status switch
            {
                VaultIdentifyStatus.Unavailable => ApiErrors.NotFound("Not found."),
                VaultIdentifyStatus.NotFound => ApiErrors.NotFound("That record could not be read from the server."),
                _ => Results.Ok(ToDto(preview)),
            };
        })
        .WithName("PreviewVaultIdentifyCandidate")
        .WithSummary("The current values beside the values a chosen record would put there, with a tick per field.")
        .Produces<VaultIdentifyPreviewDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataWrite);

        items.MapPost("/{assetId:guid}/identify/apply", async (
            Guid assetId, VaultIdentifyApplyRequest request, VaultIdentifyService identify, CancellationToken ct) =>
        {
            if (request.Fields is null || request.Fields.Count == 0 || request.Fields.Any(f => !VaultFieldNames.IsValid(f)))
            {
                return ApiErrors.BadRequest("Tick at least one field to apply.");
            }

            var result = await identify.ApplyAsync(assetId, request.ServerId, request.RecordId, request.Fields, ct);
            return result.Status switch
            {
                VaultIdentifyStatus.Unavailable => ApiErrors.NotFound("Not found."),
                VaultIdentifyStatus.NotFound => ApiErrors.NotFound("That record could not be read from the server."),
                _ => Results.Ok(new VaultIdentifyApplyResultDto { Status = result.Status, Applied = result.Applied, Refused = result.Refused }),
            };
        })
        .WithName("ApplyVaultIdentifyCandidate")
        .WithSummary("Applies exactly the fields that were ticked from a chosen record, and records where each came from.")
        .Produces<VaultIdentifyApplyResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataWrite);

        items.MapGet("/{assetId:guid}/match", async (
            Guid assetId, IVaultMatchRepository matches, CancellationToken ct) =>
        {
            if (await matches.GetFileFactsAsync(assetId, ct) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            var match = await matches.GetMatchAsync(assetId, ct);
            var fields = await matches.GetFieldSourcesAsync(assetId, ct);
            return Results.Ok(new VaultMatchDto
            {
                Matched = match is not null,
                ServerId = match?.ServerId,
                RecordId = match?.RecordId,
                MatchedBy = match?.MatchedBy,
                AppliedVia = match?.AppliedVia,
                MatchedAt = match?.MatchedAt,
                Fields = fields.Select(f => new VaultFieldSourceDto
                {
                    Field = f.Field,
                    ServerId = f.ServerId,
                    RecordId = f.RecordId,
                    MatchedBy = f.MatchedBy,
                    AppliedVia = f.AppliedVia,
                    Language = f.Language,
                    AppliedAt = f.AppliedAt,
                }).ToList(),
            });
        })
        .WithName("GetVaultMatch")
        .WithSummary("How a Vault video was matched, if it was: the server, the record, the signal, and where each field came from.")
        .Produces<VaultMatchDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataRead);

        // The quiet Vault activity list: the only place Vault ingestion shows. An administrator with Vault mode on sees it; everyone
        // else gets exactly what a missing page gets.
        app.MapGet("/api/v1/vault/activity", async (
            HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access, IVaultActivityRepository activity, CancellationToken ct) =>
        {
            CatalogueResourceAuthorizationService.ApplyVaultCacheControl(http.Response);
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            var rows = await activity.GetRecentAsync(100, ct);
            return Results.Ok(rows.Select(r => new VaultActivityItemDto
            {
                Id = r.Id,
                AssetId = r.AssetId,
                Kind = r.Kind,
                FileName = r.FileName,
                Detail = r.Detail,
                At = r.At,
            }).ToList());
        })
        .WithMetadata(VaultGatedRoute.Instance)
        .WithName("ListVaultActivity")
        .WithSummary("The quiet Vault activity list: what ingestion did with each Vault file. Only inside Vault mode, only for an administrator.")
        .Produces<IReadOnlyList<VaultActivityItemDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorInPerson();

        // Saved Vault bookmarks that came back with a restored backup and still wait for their video. A count only, never a title.
        app.MapGet("/api/v1/vault/restored-bookmarks", async (
            HttpContext http, IRequestAuthorityResolver resolver, VaultAccessResolver access, [Microsoft.AspNetCore.Mvc.FromServices] VaultBookmarkRelinkRepository relink, CancellationToken ct) =>
        {
            CatalogueResourceAuthorizationService.ApplyVaultCacheControl(http.Response);
            var authority = await resolver.ResolveAsync(http, ct);
            if (!authority.IsEffectiveAdministrator || !await access.IsUnlockedAsync(authority, ct))
            {
                return ApiErrors.NotFound("Not found.");
            }

            var waiting = await relink.CountWaitingAsync(ct);
            return Results.Ok(new VaultRestoredBookmarksDto
            {
                Waiting = waiting,
                Message = waiting == 0
                    ? string.Empty
                    : $"{waiting} saved bookmark{(waiting == 1 ? " is" : "s are")} still waiting for a matching video. They are re-linked when a scan of the Vault folders finds the video; any that never find one cannot be restored.",
            });
        })
        .WithMetadata(VaultGatedRoute.Instance)
        .WithName("GetVaultRestoredBookmarks")
        .WithSummary("How many saved Vault bookmarks from a restored backup still wait for their video. Only inside Vault mode, only for an administrator.")
        .Produces<VaultRestoredBookmarksDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorInPerson();

        return app;
    }

    private static VaultCandidateListDto ToDto(VaultCandidateList list) => new()
    {
        Status = list.Status,
        SearchedFor = list.SearchedFor,
        ProductCode = list.ProductCode,
        Candidates = list.Candidates.Select(c => new VaultCandidateDto
        {
            ServerId = c.ServerId,
            RecordId = c.RecordId,
            Title = c.Title,
            Studio = c.Studio,
            ReleaseDate = c.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ProductCode = c.ProductCode,
            DurationSeconds = c.DurationSeconds,
            Performers = c.Performers,
            LengthAgrees = c.LengthAgrees,
        }).ToList(),
    };

    private static VaultIdentifyPreviewDto ToDto(VaultIdentifyPreview preview) => new()
    {
        Status = preview.Status,
        ServerId = preview.ServerId,
        RecordId = preview.RecordId,
        RecordTitle = preview.RecordTitle,
        ProductCode = preview.ProductCode,
        DurationSeconds = preview.DurationSeconds,
        LengthAgrees = preview.LengthAgrees,
        Fields = preview.Fields.Select(f => new VaultFieldChoiceDto
        {
            Field = f.Field,
            Current = f.Current,
            Proposed = f.Proposed,
            Changes = f.Changes,
            PersonsOwn = f.PersonsOwn,
            Ticked = f.Ticked,
        }).ToList(),
    };
}
