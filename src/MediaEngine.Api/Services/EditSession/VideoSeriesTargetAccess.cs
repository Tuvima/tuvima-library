using MediaEngine.Api.Security;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Whether the caller may put a video in the series or section a series change points at (universal editor v6, D24). A series a video would
/// join is checked like any other item the caller edits, the same check the list of series a video could join is made with: a series the
/// caller may not write to, cannot see, or that does not exist all look exactly alike, so a made-up id reveals nothing and cannot move a
/// file into another series' folder. The preview of a series move and the save of a series change both ask here.
/// </summary>
internal static class VideoSeriesTargetAccess
{
    /// <summary>True when the choice names no existing series (a new series, or on its own), or names one the caller may change.</summary>
    public static async Task<bool> MayUseAsync(
        HttpContext http, CatalogueResourceAuthorizationService resources, string? targetId, string? token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(resources);
        if (EditSessionVideo.IsNoSeries(token) || EditSessionVideo.TryParseNewSeries(token, out _) || string.IsNullOrWhiteSpace(targetId))
        {
            return true;
        }

        return Guid.TryParse(targetId, out var series)
               && await resources.EvaluateAnyEntityAsync(http, series, ApplicationPermissionIds.MetadataWrite, ct).ConfigureAwait(false)
               == CatalogueResourceAccess.Allowed;
    }

    /// <summary>
    /// The session's refusals with one more for every video whose series change points at a series the caller may not use. The refusal is the
    /// "no longer available" line a missing series gets, and it is keyed by the video, so nothing in the save is written.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> AddRefusalsAsync(
        HttpContext http,
        CatalogueResourceAuthorizationService resources,
        IReadOnlyList<EditSessionChange> changes,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(refusals);
        Dictionary<Guid, string>? merged = null;
        foreach (var change in changes.Where(change => change.Kind == EditSessionChangeKinds.VideoSeries))
        {
            if (refusals.ContainsKey(change.EntityId) || merged?.ContainsKey(change.EntityId) == true)
            {
                continue;
            }

            if (!await MayUseAsync(http, resources, change.TargetId, change.Token, ct).ConfigureAwait(false))
            {
                merged ??= new Dictionary<Guid, string>(refusals);
                merged[change.EntityId] = EditSessionReasons.SeriesGone;
            }
        }

        return merged ?? refusals;
    }
}
