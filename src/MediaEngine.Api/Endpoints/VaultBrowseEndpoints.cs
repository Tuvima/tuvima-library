using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Browsing the Vault (Videos spec D39, D40, D41): recently added, scenes, folders, the unmatched list, and the separate performer,
/// studio and tag lists, with a search that runs only inside Vault mode. Every route here goes through one central gate: anyone whose
/// device session does not have Vault mode on gets exactly what a missing page gets (not found, never forbidden), and every answer,
/// found or not, is private and uncacheable. Real use restarts the idle timer. Search text travels in a request body and neither a
/// title nor a body is ever logged. Nothing here reads or writes the shared People, Tag, Series or artwork records.
/// </summary>
public static class VaultBrowseEndpoints
{
    private const int DefaultTake = 60;
    private const int SearchTake = 24;

    public static IEndpointRouteBuilder MapVaultBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        var vault = app.MapGroup("/api/v1/vault").WithTags("Vault browsing").RequireAuthorization(AuthPolicies.Authenticated);
        vault.AddEndpointFilter(GateAsync);
        vault.WithMetadata(VaultGatedRoute.Instance);

        vault.MapGet("/scenes", async (
            [FromServices] IVaultBrowseRepository browse,
            [FromQuery] string? match, [FromQuery] string? sort, [FromQuery] bool? desc,
            [FromQuery] Guid? performer, [FromQuery] Guid? studio, [FromQuery] Guid? tag, [FromQuery] string? folder,
            [FromQuery] int? skip, [FromQuery] int? take, CancellationToken ct) =>
        {
            var query = new VaultSceneQuery(
                VaultSceneMatchFilter.IsValid(match) ? match! : VaultSceneMatchFilter.All,
                VaultSceneSorts.IsValid(sort) ? sort! : VaultSceneSorts.DateAdded,
                desc ?? (sort is null or VaultSceneSorts.DateAdded or VaultSceneSorts.ReleaseDate),
                performer, studio, tag,
                string.IsNullOrWhiteSpace(folder) ? null : folder,
                Text: null,
                skip ?? 0,
                take ?? DefaultTake);
            return Results.Ok(ToDto(await browse.ListScenesAsync(query, ct)));
        })
        .WithName("ListVaultScenes")
        .WithSummary("Vault scenes, newest first by default: matched and unmatched together, or one kind, or those of a performer, studio, tag or folder.")
        .Produces<VaultSceneListDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapGet("/scenes/{assetId:guid}", async ([FromServices] IVaultBrowseRepository browse, Guid assetId, CancellationToken ct) =>
            await browse.GetSceneAsync(assetId, ct) is { } detail
                ? Results.Ok(ToDto(detail))
                : ApiErrors.NotFound("Not found."))
        .WithName("GetVaultScene")
        .WithSummary("One Vault scene with its description, linked names and how it was matched.")
        .Produces<VaultSceneDetailDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapGet("/folders", async ([FromServices] IVaultBrowseRepository browse, CancellationToken ct) =>
            Results.Ok((await browse.ListFoldersAsync(ct)).Select(f => new VaultFolderDto
            {
                Id = f.Id,
                Name = f.Name,
                SceneCount = f.SceneCount,
                ImageId = f.ImageArtworkId,
            }).ToList()))
        .WithName("ListVaultFolders")
        .WithSummary("The series and sections of the Vault's folder layout, with their scene counts.")
        .Produces<IReadOnlyList<VaultFolderDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapGet("/performers", async (
            [FromServices] IVaultBrowseRepository browse, [FromQuery] string? sort, [FromQuery] bool? desc, CancellationToken ct) =>
        {
            var (order, descending) = NameOrder(sort, desc);
            return Results.Ok((await browse.ListPerformersAsync(order, descending, ct)).Select(ToDto).ToList());
        })
        .WithName("ListVaultPerformers")
        .WithSummary("Performers with at least one owned scene. Never part of the main page.")
        .Produces<IReadOnlyList<VaultNameTileDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapGet("/studios", async (
            [FromServices] IVaultBrowseRepository browse, [FromQuery] string? sort, [FromQuery] bool? desc, CancellationToken ct) =>
        {
            var (order, descending) = NameOrder(sort, desc);
            return Results.Ok((await browse.ListStudiosAsync(order, descending, ct)).Select(ToDto).ToList());
        })
        .WithName("ListVaultStudios")
        .WithSummary("Studios with at least one owned scene. Never part of the main page.")
        .Produces<IReadOnlyList<VaultNameTileDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        vault.MapGet("/tags", async (
            [FromServices] IVaultBrowseRepository browse, [FromQuery] string? sort, [FromQuery] bool? desc, CancellationToken ct) =>
        {
            var (order, descending) = NameOrder(sort, desc);
            return Results.Ok((await browse.ListTagsAsync(order, descending, ct)).Select(ToDto).ToList());
        })
        .WithName("ListVaultTags")
        .WithSummary("Vault tags with at least one owned scene. They are the Vault's own and never join the shared tag lists.")
        .Produces<IReadOnlyList<VaultNameTileDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // POST, because the words typed travel in the body and are never put in an address or a log line.
        vault.MapPost("/search", async ([FromServices] IVaultBrowseRepository browse, VaultSearchRequest request, CancellationToken ct) =>
        {
            var text = request.Text?.Trim() ?? string.Empty;
            if (text.Length < 2)
            {
                return ApiErrors.BadRequest("Type at least two characters.");
            }

            var take = Math.Clamp(request.Take ?? SearchTake, 1, 100);
            var scenes = await browse.ListScenesAsync(new VaultSceneQuery(Text: text, Take: take), ct);
            var names = await browse.SearchNamesAsync(text, take, ct);
            return Results.Ok(new VaultSearchResultDto
            {
                Scenes = scenes.Items.Select(ToDto).ToList(),
                Performers = names.Performers.Select(ToDto).ToList(),
                Studios = names.Studios.Select(ToDto).ToList(),
            });
        })
        .WithName("SearchVault")
        .WithSummary("Searches only what is stored for Vault scenes: file-derived title, description, tags and names, and whatever a lookup brought back.")
        .Produces<VaultSearchResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// The one gate for the Vault's browsing routes. The answer is private and uncacheable whether or not the caller is let in, so
    /// the header never tells the two apart; a locked session, a service app, a paired app and an anonymous caller all get "not found".
    /// A cleared request restarts the unlocked session's idle timer, because browsing is real use.
    /// </summary>
    internal static async ValueTask<object?> GateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // The gate middleware already answered for this request before any query or body was read (see VaultGateMiddleware).
        if (VaultGateMiddleware.HasPassed(context.HttpContext))
        {
            return await next(context);
        }

        return await PassesGateAsync(context.HttpContext) ? await next(context) : ApiErrors.NotFound("Not found.");
    }

    /// <summary>
    /// The one Vault gate: private and uncacheable headers, then Vault mode on for this device session plus the Watch grant. A request
    /// that does not pass is answered "not found" by the caller. Passing restarts the idle timer.
    /// </summary>
    internal static async Task<bool> PassesGateAsync(HttpContext http)
    {
        CatalogueResourceAuthorizationService.ApplyVaultCacheControl(http.Response);
        http.Response.OnStarting(static state =>
        {
            CatalogueResourceAuthorizationService.ApplyVaultCacheControl((HttpResponse)state);
            return Task.CompletedTask;
        }, http.Response);

        var ct = http.RequestAborted;
        var services = http.RequestServices;
        var authority = await services.GetRequiredService<IRequestAuthorityResolver>().ResolveAsync(http, ct);
        var access = services.GetRequiredService<VaultAccessResolver>();
        if (!await access.MayOpenAnySectionAsync(authority, ct)
            || authority.ActiveProfileId is not { } watchingProfile
            || !(await services.GetRequiredService<IProfileAccessRepository>().GetFeatureGrantsAsync(watchingProfile, ct)).Contains(AccountFeatureId.Watch))
        {
            return false;
        }

        if (authority.SessionId is { } sessionId && authority.ActiveProfileId is { } profileId)
        {
            await new NsfwIdleKeeper(services.GetRequiredService<INsfwAccessRepository>(), services.GetRequiredService<TimeProvider>())
                .KeepAwakeAsync(sessionId, profileId, ct);
        }

        return true;
    }

    private static (string Order, bool Descending) NameOrder(string? sort, bool? desc)
    {
        var order = VaultNameSorts.IsValid(sort) ? sort! : VaultNameSorts.Name;
        return (order, desc ?? order == VaultNameSorts.SceneCount);
    }

    private static VaultSceneListDto ToDto(VaultSceneList list) => new()
    {
        Items = list.Items.Select(ToDto).ToList(),
        Total = list.Total,
        Heading = list.Heading,
    };

    private static VaultSceneDto ToDto(VaultSceneRow row) => new()
    {
        Id = row.AssetId,
        WorkId = row.WorkId,
        Title = row.Title,
        Matched = row.Matched,
        FileName = row.FileName,
        Folder = row.FolderName,
        FolderId = row.FolderId,
        DurationSeconds = row.DurationSeconds,
        SizeBytes = row.SizeBytes,
        AddedAt = row.AddedAt,
        ReleaseDate = row.ReleaseDate,
        Studio = row.Studio,
        Tags = row.Tags,
        ImageId = row.ImageArtworkId,
        StillIds = row.StillArtworkIds,
    };

    private static VaultSceneDetailDto ToDto(VaultSceneDetailRow detail) => new()
    {
        Scene = ToDto(detail.Scene),
        Description = detail.Description,
        MatchedBy = detail.MatchedBy,
        Performers = detail.Performers.Select(n => new VaultNameLinkDto { Id = n.Id, Name = n.Name }).ToList(),
        Studios = detail.Studios.Select(n => new VaultNameLinkDto { Id = n.Id, Name = n.Name }).ToList(),
        Tags = detail.Tags.Select(n => new VaultNameLinkDto { Id = n.Id, Name = n.Name }).ToList(),
    };

    private static VaultNameTileDto ToDto(VaultNameTileRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        SceneCount = row.SceneCount,
        ImageId = row.ImageArtworkId,
    };
}
