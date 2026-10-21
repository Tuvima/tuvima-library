using MediaEngine.Api.Http;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

public sealed class PlayerResourceDeniedException : Exception;

/// <summary>A person who is not a server administrator tried to make a bookmark shared with everybody.</summary>
public sealed class PlayerBookmarkSharingDeniedException : Exception;

/// <summary>Request-bound catalogue access used by the singleton player before selection or persistence.</summary>
public sealed class PlayerCatalogueScope(IHttpContextAccessor http)
{
    private static readonly object AssetsKey = new();
    private HttpContext Context => http.HttpContext ?? throw new PlayerResourceDeniedException();

    public async Task<Guid> RequireProfileAsync(Guid? requestedProfile, CancellationToken ct)
    {
        var context = Context;
        var authority = await context.RequestServices.GetRequiredService<IRequestAuthorityResolver>().ResolveAsync(context, ct);
        if (AuthorityValidity.ValidateHuman(authority) is not null ||
            authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient) ||
            authority.ActiveProfileId is not { } active ||
            requestedProfile is { } requested && requested != active)
        {
            throw new PlayerResourceDeniedException();
        }

        return active;
    }

    /// <summary>
    /// True when the request is a person acting as a server administrator. Only that person may make, or change, a bookmark
    /// shared with everybody (Videos spec D43); the Dashboard hides the switch, and this is the check that counts.
    /// </summary>
    public async Task<bool> IsServerAdministratorAsync(CancellationToken ct)
    {
        var context = Context;
        var authority = await context.RequestServices.GetRequiredService<IRequestAuthorityResolver>().ResolveAsync(context, ct);
        return authority.PrincipalKind == PrincipalKind.Human && authority.IsEffectiveAdministrator;
    }

    internal Task<IReadOnlyList<DisplayWorkRow>> AssetsAsync(CancellationToken ct)
    {
        var context = Context;
        if (context.Items.TryGetValue(AssetsKey, out var existing))
        {
            return (Task<IReadOnlyList<DisplayWorkRow>>)existing!;
        }

        var pending = context.RequestServices.GetRequiredService<AuthorizedDisplayProjectionReadService>()
            .LoadAuthorizedAssetsAsync(ct);
        context.Items[AssetsKey] = pending;
        return pending;
    }

    public async Task<IReadOnlySet<Guid>> AssetIdsAsync(CancellationToken ct) =>
        (await AssetsAsync(ct)).Select(row => row.AssetId).ToHashSet();

    public async Task RequireAssetAsync(Guid assetId, CancellationToken ct, Guid? workId = null)
    {
        if (!(await AssetsAsync(ct)).Any(row => row.AssetId == assetId && (!workId.HasValue || row.WorkId == workId)))
        {
            throw new PlayerResourceDeniedException();
        }
    }

    internal async Task<DisplayWorkRow> RequireAssetContextAsync(Guid assetId, CancellationToken ct)
    {
        var row = (await AssetsAsync(ct)).FirstOrDefault(candidate => candidate.AssetId == assetId);
        return row ?? throw new PlayerResourceDeniedException();
    }

    public async Task<Guid?> FindAssetAsync(Guid workId, Guid? requestedAsset, CancellationToken ct) =>
        (await AssetsAsync(ct)).Where(row => row.WorkId == workId &&
                (!requestedAsset.HasValue || row.AssetId == requestedAsset))
            .OrderBy(row => row.AssetId).Select(row => (Guid?)row.AssetId).FirstOrDefault();

    public async Task<PlayerStateDto> FilterStateAsync(PlayerStateDto state, CancellationToken ct)
    {
        var ids = await AssetIdsAsync(ct);
        var queue = state.Queue.Where(item => item.AssetId.HasValue && ids.Contains(item.AssetId.Value)).ToList();
        var current = queue.FirstOrDefault(item => item.QueueItemId == state.CurrentQueueItemId);
        return state with
        {
            Queue = queue,
            CurrentItem = current,
            CurrentQueueItemId = current?.QueueItemId,
            PlaybackState = current is null ? PlayerPlaybackStates.Stopped : state.PlaybackState,
            PositionSeconds = current is null ? 0 : state.PositionSeconds,
            DurationSeconds = current is null ? null : state.DurationSeconds,
            ProgressPct = current is null ? 0 : state.ProgressPct,
            SourceLabel = queue.Count == 0 ? null : state.SourceLabel,
            AudiobookHistory = state.AudiobookHistory.Where(item => ids.Contains(item.AssetId)).ToList(),
        };
    }
}

internal sealed class PlayerResourceFailureFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (PlayerResourceDeniedException) { return ApiErrors.NotFound("Playback item not found."); }
        catch (PlayerBookmarkSharingDeniedException)
        {
            return ApiErrors.Forbidden("Only a server administrator can share a bookmark with everyone.");
        }
    }
}
