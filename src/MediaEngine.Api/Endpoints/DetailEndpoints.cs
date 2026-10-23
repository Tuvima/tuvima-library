using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
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
            string? sequenceGroup,
            int? sequenceOffset,
            int? sequenceLimit,
            string? sequenceCurrent,
            bool? sequenceOnly,
            HttpContext httpContext,
            DetailComposerService composer,
            ContributorReadiness readiness,
            DetailUnmatchedStateService unmatchedState,
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
                authority.ActiveProfileId, actionAuthorization, authorizedAssetIds, authorizedWorks,
                SequenceWindowRequest.FromQuery(sequenceGroup, sequenceOffset, sequenceLimit, sequenceCurrent, sequenceOnly),
                IsWorkDetail(parsedType) ? CreateUniverseLinkResolver(httpContext, authority.ActiveProfileId) : null);
            var compositionMs = timing.Elapsed.TotalMilliseconds - projectionMs;
            if (detail is not null)
            {
                await readiness.ApplyAsync(detail, ct);
                // Single-item pages only (authorizedAssetIds is set for them): an administrator sees the item's Unmatched state and actions.
                await unmatchedState.ApplyAsync(detail, authorizedAssetIds, actionAuthorization, ct);
            }
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DetailPerformance")
                .LogInformation("Detail {Type}: projection {ProjectionMs:F0} ms, composition {CompositionMs:F0} ms, total {TotalMs:F0} ms",
                    parsedType, projectionMs, compositionMs, timing.Elapsed.TotalMilliseconds);
            return detail is null
                ? ApiErrors.NotFound($"No detail page found for {entityType} '{id}'.")
                : Results.Ok(detail);
        })
        .WithName("GetDetailPage")
        .WithSummary("Returns the unified detail-page model for media and related entities.")
        .Produces<DetailPageViewModel>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .WithMetadata(new CatalogueEntityAccessMetadata(ApplicationPermissionIds.LibraryRead.Value));

        // The owned episodes after a given episode file, in the show's watch order, for a native app's Up Next.
        // Hidden libraries and the profile's content limit are applied by the projection; unknown and not-allowed both read as 404.
        group.MapGet("/show/{id:guid}/next-episodes", async (
            Guid id,
            Guid? after,
            int? count,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (after is not { } afterAssetId || afterAssetId == Guid.Empty)
            {
                return ApiErrors.BadRequest("The 'after' episode file is required.");
            }

            var next = NextEpisodesPlanner.Plan(id, afterAssetId, NextEpisodesPlanner.ClampCount(count), await display.LoadWorksIncludingUnmatchedAsync(ct));
            return next is null
                ? ApiErrors.NotFound($"No episodes found after '{afterAssetId}' in show '{id}'.")
                : Results.Ok(next);
        })
        .WithName("GetShowNextEpisodes")
        .WithSummary("Returns up to 10 owned episodes that follow an episode file in a show's watch order.")
        .Produces<NextEpisodesDto>(StatusCodes.Status200OK)
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

    // The "Part of the ... Universe" link is read through the profile-aware Universe read of this request.
    // Without that read registered (a trimmed host) there is simply no link.
    private static UniverseLinkResolver? CreateUniverseLinkResolver(HttpContext httpContext, Guid? profileId)
    {
        var universes = httpContext.RequestServices.GetService<UniverseClientReadService>();
        var profiles = httpContext.RequestServices.GetService<IProfileRepository>();
        if (universes is null)
        {
            return null;
        }

        return async (universeQid, token) =>
        {
            var profile = profileId is { } id && profiles is not null
                ? await profiles.GetByIdAsync(id, token)
                : null;
            return await universes.GetUniverseLinkAsync(universeQid, profile, token);
        };
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
