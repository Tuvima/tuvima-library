using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Universe appearance API endpoint — "Also appears in": where else a character, place, organisation or person from a
/// Universe shows up across the Library, grouped by format.
///
/// <list type="bullet">
/// <item><c>GET /universe/{qid}/also-appears-in?entity={entityQid}</c> — the Works that share a Wikidata item</item>
/// <item><c>GET /universe/{qid}/also-appears-in?person={personId}</c> — the Works a person is credited on</item>
/// </list>
/// </summary>
public static class UniverseAppearanceEndpoints
{
    public static IEndpointRouteBuilder MapUniverseAppearanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/")
                       .WithTags("Universe Graph");

        group.MapGet("/universe/{qid}/also-appears-in", GetAlsoAppearsInAsync)
            .Produces<EntityAppearancesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
            .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        return app;
    }

    internal static async Task<IResult> GetAlsoAppearsInAsync(
        string qid,
        string? entity,
        Guid? person,
        HttpContext http,
        IProfileRepository profiles,
        EntityAppearanceReadService appearances,
        CancellationToken ct)
    {
        var entityQid = string.IsNullOrWhiteSpace(entity) ? null : entity.Trim();
        if ((entityQid is null) == (person is null))
        {
            return ApiErrors.BadRequest("Give exactly one of 'entity' (a Wikidata QID) or 'person' (a person id).");
        }

        if (entityQid is not null)
        {
            var profile = await CollectionClientEndpoints.ResolveActiveProfileAsync(http, profiles, ct);
            var byEntity = await appearances.GetForEntityAsync(qid, entityQid, profile, ct);
            return byEntity is null
                ? ApiErrors.NotFound($"Entity '{entityQid}' not found.")
                : Results.Ok(byEntity);
        }

        var byPerson = await appearances.GetForPersonAsync(qid, person!.Value, ct);
        return byPerson is null
            ? ApiErrors.NotFound($"Person '{person.Value}' not found.")
            : Results.Ok(byPerson);
    }
}
