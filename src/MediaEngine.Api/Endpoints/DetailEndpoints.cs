using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

public static class DetailEndpoints
{
    public static IEndpointRouteBuilder MapDetailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/details")
            .WithTags("Details");

        group.MapGet("/{entityType}/{id:guid}", async (
            string entityType,
            Guid id,
            string? context,
            string? containerId,
            HttpContext httpContext,
            DetailComposerService composer,
            ContributorReadiness readiness,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (!DetailComposerService.TryParseEntityType(entityType, out var parsedType))
            {
                return ApiErrors.BadRequest($"Unsupported detail entity type '{entityType}'.");
            }

            var timing = System.Diagnostics.Stopwatch.StartNew();
            var presentationContext = DetailComposerService.ParseContext(context);
            var resolver = httpContext.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
            var authority = await resolver.ResolveAsync(httpContext, ct);
            var authorizedWorks = IsWorkDetail(parsedType) || parsedType is DetailEntityType.Person
                ? await display.LoadDetailWorksAsync(id, ct)
                : await display.LoadWorksAsync(ct);
            var projectionMs = timing.Elapsed.TotalMilliseconds;
            IReadOnlyList<Guid>? authorizedAssetIds = null;
            if (IsWorkDetail(parsedType))
            {
                var resources = httpContext.RequestServices
                    .GetRequiredService<CatalogueResourceAuthorizationService>();
                authorizedAssetIds = await resources.GetAuthorizedAssetIdsForWorkAsync(
                        httpContext,
                        id,
                        authority.ActiveProfileId,
                        ApplicationPermissionIds.LibraryRead,
                        ct);
                if (authorizedAssetIds.Count == 0)
                {
                    return ApiErrors.NotFound($"No detail page found for {entityType} '{id}'.");
                }
            }
            var actionAuthorization = await DetailActionAuthorizationPolicy.ResolveAsync(
                authority,
                httpContext.RequestServices.GetRequiredService<IAccountAccessDecisionService>(),
                httpContext.RequestServices.GetRequiredService<IAuthorizationEvaluator>(),
                ct);
            var detail = await composer.BuildAuthorizedAsync(
                parsedType, id, presentationContext, ct, containerId,
                authority.ActiveProfileId, actionAuthorization, authorizedAssetIds, authorizedWorks);
            var compositionMs = timing.Elapsed.TotalMilliseconds - projectionMs;
            if (detail is not null)
            {
                await readiness.ApplyAsync(detail, ct);
            }
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DetailPerformance")
                .LogInformation("Detail {Type}: projection {ProjectionMs:F0} ms, composition {CompositionMs:F0} ms, total {TotalMs:F0} ms",
                    parsedType, projectionMs, compositionMs, timing.Elapsed.TotalMilliseconds);
            return detail is null
                ? ApiErrors.NotFound($"No detail page found for {entityType} '{id}'.")
                : Results.Ok(detail);
        })
        .WithName("GetDetailPage")
        .WithSummary("Returns the unified Tuvima detail-page model for media and related entities.")
        .Produces<DetailPageViewModel>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .WithMetadata(new CatalogueEntityAccessMetadata(ApplicationPermissionIds.LibraryRead.Value));

        group.MapPut("/{entityType}/{id:guid}/sequence-default", async (
            string entityType,
            Guid id,
            SetDefaultSequenceRequest request,
            DetailComposerService composer,
            ICanonicalValueRepository canonicalValues,
            CancellationToken ct) =>
        {
            if (!DetailComposerService.TryParseEntityType(entityType, out var parsedType))
            {
                return ApiErrors.BadRequest($"Unsupported detail entity type '{entityType}'.");
            }

            var containerId = NormalizeContainerId(request.ContainerId);
            if (string.IsNullOrWhiteSpace(containerId))
            {
                return ApiErrors.BadRequest("A valid sequence container is required.");
            }

            var detail = await composer.BuildAsync(parsedType, id, DetailPresentationContext.Default, ct);
            var matchingContainer = detail?.SequencePlacement?.AvailableContainers.FirstOrDefault(option =>
                ContainerMatches(option, containerId));
            if (matchingContainer is null)
            {
                return ApiErrors.BadRequest("The selected container is not valid for this item.");
            }

            var now = DateTimeOffset.UtcNow;
            await canonicalValues.UpsertBatchAsync(
            [
                new CanonicalValue
                {
                    EntityId = id,
                    Key = "default_sequence_container_id",
                    Value = containerId,
                    LastScoredAt = now,
                    WinningProviderId = WellKnownProviders.UserManual,
                },
                new CanonicalValue
                {
                    EntityId = id,
                    Key = "default_sequence_container_label",
                    Value = string.IsNullOrWhiteSpace(request.ContainerTitle)
                        ? matchingContainer.ContainerTitle
                        : request.ContainerTitle.Trim(),
                    LastScoredAt = now,
                    WinningProviderId = WellKnownProviders.UserManual,
                },
            ], ct);

            return Results.NoContent();
        })
        .WithName("SetDetailDefaultSequence")
        .WithSummary("Sets the default same-media sequence container for a work.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "id");

        return app;
    }

    private static bool IsWorkDetail(DetailEntityType type) => type is
        DetailEntityType.Work or DetailEntityType.Movie or DetailEntityType.TvEpisode or
        DetailEntityType.Book or DetailEntityType.Audiobook or DetailEntityType.ComicIssue;

    private static string? NormalizeContainerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var slashIndex = trimmed.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            trimmed = trimmed[(slashIndex + 1)..];
        }

        return trimmed.Length > 1 && trimmed[0] is 'Q' && trimmed.Skip(1).All(char.IsDigit)
            ? trimmed
            : trimmed;
    }

    private static bool ContainerMatches(SequenceContainerOptionViewModel option, string containerId)
    {
        var normalized = NormalizeContainerId(containerId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return string.Equals(NormalizeContainerId(option.ContainerId), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeContainerId(option.SourceContainerId), normalized, StringComparison.OrdinalIgnoreCase)
            || option.EquivalentContainerIds.Any(alias =>
                string.Equals(NormalizeContainerId(alias), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
