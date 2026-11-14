using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// What is being watched and prepared right now, across every account and profile, and the two ways to stop it (playing-now spec D3,
/// D5, D7). Administrators only. Stopping never touches source media: at most it discards a half-built streaming copy, which is a cache.
/// </summary>
internal static class PlayingNowEndpoints
{
    internal static void MapPlayingNow(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/playback/playing-now").WithTags("Playback");

        group.MapGet("", async (
            [FromServices] PlayingNowReadService read,
            CancellationToken ct) => Results.Ok(await read.GetAsync(ct).ConfigureAwait(false)))
            .WithName("GetPlayingNow").WithSummary("Returns everything being watched or prepared right now.")
            .Produces<PlayingNowDto>().RequireAdmin();

        group.MapPost("/{id:guid}/stop", async (
            Guid id,
            [FromServices] PlaybackActivityRegistry registry) =>
            await registry.StopAsync(id).ConfigureAwait(false) ? Results.NoContent() : ApiErrors.NotFound("Not found."))
            .WithName("StopPlayingNowEncode").WithSummary("Stops one encode at once and frees the processor.")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAdmin();

        group.MapPost("/stop-unwatched", async (
            [FromServices] PlaybackActivityRegistry registry,
            IConfigurationLoader configuration) =>
        {
            var pausedFor = TimeSpan.FromMinutes(Math.Max(1, configuration.LoadTranscoding().PausedCountsAsWatchingMinutes));
            return Results.Ok(new StopUnwatchedResultDto { Stopped = await registry.StopUnwatchedAsync(pausedFor).ConfigureAwait(false) });
        }).WithName("StopUnwatchedPlayingNow").WithSummary("Stops every playback encode nobody is watching.")
          .Produces<StopUnwatchedResultDto>().RequireAdmin();
    }
}
