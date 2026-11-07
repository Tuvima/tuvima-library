using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Engine side of the editor's Bookmarks tab (universal editor v5 D37, screens Q and Q2): the two lists of a title, the overview of a
/// show, season or album, and the title's and the show's Export and Import compare. Saving is not here: add, remove, rename, change time,
/// import and start over are a change of the editor's staged save (<c>/library/edit-session</c>, kind <c>bookmarks</c>), so one Save is all or
/// nothing across title fields and bookmarks. Every action is for administrators only, and a title in the Vault is refused; the Dashboard hides
/// the tab, and these checks are the ones that count.
/// </summary>
public static class EditorBookmarkEndpoints
{
    public static IEndpointRouteBuilder MapEditorBookmarkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/editor-bookmarks")
            .WithTags("Editor bookmarks")
            .AddEndpointFilter(new PlayerResourceFailureFilter());

        group.MapGet("/titles/{workId:guid}", async (Guid workId, EditorBookmarksService bookmarks, CancellationToken ct) =>
            Results.Ok(await bookmarks.ReadAsync(workId, ct)))
        .WithName("GetEditorBookmarks")
        .WithSummary("A title's Everyone's bookmarks and every profile's bookmarks beside them, for the editor's Bookmarks tab. Administrators only; never a Vault title.")
        .Produces<EditorBookmarksDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/titles/{workId:guid}/compare", async (
            Guid workId, EditorBookmarkCompareRequestDto request, EditorBookmarksService bookmarks, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await bookmarks.CompareAsync(workId, request, ct));
            }
            catch (BookmarkPackInvalidException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("CompareEditorBookmarkFile")
        .WithSummary("Reads a bookmark file and lays each bookmark beside the nearest one in the title's Everyone's list. Changes nothing.")
        .Produces<BookmarkSetCompareDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/titles/{workId:guid}/export", async (Guid workId, EditorBookmarksService bookmarks, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await bookmarks.ExportAsync(workId, ct));
            }
            catch (BookmarkPackInvalidException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("ExportEditorBookmarks")
        .WithSummary("Saves a title's Everyone's bookmarks as a bookmark file (never anyone's personal ones).")
        .Produces<BookmarkPackFileDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapGet("/families/{workId:guid}", async (Guid workId, EditorBookmarkOverviewService overview, CancellationToken ct) =>
            Results.Ok(await overview.OverviewAsync(workId, ct)))
        .WithName("GetEditorBookmarkOverview")
        .WithSummary("One row per episode or track of a show, season or album with how many bookmarks Everyone sees on it, grouped by season.")
        .Produces<EditorBookmarkOverviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/families/{workId:guid}/export", async (Guid workId, EditorBookmarkOverviewService overview, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await overview.ExportAsync(workId, ct));
            }
            catch (BookmarkPackInvalidException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("ExportEditorShowBookmarks")
        .WithSummary("Saves the Everyone's bookmarks of a whole show as one file, each episode named by season and episode number.")
        .Produces<BookmarkPackFileDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/families/{workId:guid}/compare", async (
            Guid workId, EditorBookmarkCompareRequestDto request, EditorBookmarkOverviewService overview, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await overview.CompareAsync(workId, request, ct));
            }
            catch (BookmarkPackInvalidException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("CompareEditorShowBookmarkFile")
        .WithSummary("Reads a show bookmark file and sets each episode's bookmarks beside the show's, matched by season and episode number. Changes nothing.")
        .Produces<EditorBookmarkShowCompareDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return app;
    }
}
