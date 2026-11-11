using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static class ServerFolderEndpoints
{
    public static IEndpointRouteBuilder MapServerFolderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/server-folders")
            .WithTags("Settings", "Server Folders")
            .RequireAdmin();

        group.MapGet("/roots", (ServerFolderBrowserService service) =>
            Results.Ok(service.GetStorageLocations()))
        .WithName("GetServerFolderRoots")
        .WithSummary("Lists only administrator-approved server/container folder roots.")
        .Produces<IReadOnlyList<ServerStorageLocationDto>>();

        group.MapPost("/browse", async (
            [FromBody] BrowseServerFoldersRequest request,
            ServerFolderBrowserService service,
            HttpContext http) =>
        {
            try
            {
                return Results.Ok(await service.BrowseAsync(request, await http.HiddenVaultFolderRootsAsync(http.RequestAborted), http.RequestAborted));
            }
            catch (ServerFolderAccessException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        })
        .WithName("BrowseServerFolders")
        .WithSummary("Lists directories beneath one approved server/container root.")
        .Produces<BrowseServerFoldersResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/validate", async (
            [FromBody] ValidateServerFolderRequest request,
            ServerFolderBrowserService service,
            HttpContext http) =>
        {
            try
            {
                return Results.Ok(await service.ValidateAsync(request, await http.HiddenVaultFolderRootsAsync(http.RequestAborted), http.RequestAborted));
            }
            catch (ServerFolderAccessException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        })
        .WithName("ValidateServerFolder")
        .WithSummary("Validates a folder and source-policy constraints from the server/container perspective.")
        .Produces<ServerFolderValidationResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}
