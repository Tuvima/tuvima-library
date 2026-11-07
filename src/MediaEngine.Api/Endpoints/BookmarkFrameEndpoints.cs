using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The still frame of a video bookmark (Unified player v2 D21a), served by exactly the rule of the bookmark list: a profile that may
/// not see the bookmark, or may not play its file (Vault, Kid limit), gets 404, as does a frame that is not made yet or a bookmark that
/// does not exist, so nothing is revealed. The address the list hands out carries a <c>v</c> query value that only busts caches; the
/// route ignores it.
/// </summary>
public static class BookmarkFrameEndpoints
{
    public static IEndpointRouteBuilder MapBookmarkFrameEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/player")
            .WithTags("Player")
            .AddEndpointFilter(new PlayerResourceFailureFilter());

        group.MapGet("/bookmarks/{bookmarkId:guid}/frame", async (
            Guid bookmarkId,
            Guid? profileId,
            HttpContext context,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            var identity = PlayerEndpoints.Bind(user, profileId, null, null);
            var frame = await player.GetBookmarkFramePathAsync(identity.ProfileId, bookmarkId, ct);
            if (frame is null)
            {
                // A frame that is not made yet, a bookmark that is gone and one the profile may not see all look the same.
                return ApiErrors.NotFound("No frame is available for this bookmark.");
            }

            // A frame of a Vault video is never kept by a browser or device cache, like everything else about a Vault file.
            context.Response.Headers.CacheControl = frame.InVault ? "private, no-store" : "private, max-age=3600";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(frame.Path, "image/jpeg");
        })
        .WithName("GetBookmarkFrame")
        .WithSummary("The still frame taken at a video bookmark's time. Shown only to a profile that may see the bookmark; otherwise not found.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        return app;
    }
}
