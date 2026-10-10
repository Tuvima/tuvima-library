using System.Collections.Concurrent;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// What one request's profile may see: its content limit turned into a yes/no for a rating. A profile with no limit
/// (and any request that has no profile, such as a service application) gets <see cref="Unrestricted"/>, which never
/// reads a rating.
/// </summary>
internal sealed class ContentLimitFilter(ProfileContentLimit limit)
{
    public static readonly ContentLimitFilter Unrestricted = new(ProfileContentLimit.Unrestricted);

    public bool IsUnrestricted => limit.IsUnrestricted;

    public bool Allows(string? rating) => ProfileContentLimits.Allows(limit.Limit, limit.AllowUnrated, rating);
}

/// <summary>
/// Resolves the content limit of the profile a request acts as. The limit is read live (one primary-key lookup per
/// profile per request), so a parent's change applies on the child's very next call. It sits beside library and
/// feature access: every list, search, detail page and player resolves its catalogue through the same checks.
/// </summary>
internal sealed class ContentLimitPolicy(IProfileContentLimitRepository limits)
{
    private readonly ConcurrentDictionary<Guid, Task<ContentLimitFilter>> _byProfile = new();

    public Task<ContentLimitFilter> ForAsync(RequestAuthority authority) =>
        authority.PrincipalKind is PrincipalKind.Human or PrincipalKind.DelegatedUserClient &&
        authority.ActiveProfileId is { } profileId && profileId != Guid.Empty
            ? _byProfile.GetOrAdd(profileId, id => LoadAsync(id))
            : Task.FromResult(ContentLimitFilter.Unrestricted);

    // Not tied to one caller's cancellation: the answer is cached for the rest of the request.
    private async Task<ContentLimitFilter> LoadAsync(Guid profileId)
    {
        var limit = await limits.GetAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        return limit.IsUnrestricted ? ContentLimitFilter.Unrestricted : new ContentLimitFilter(limit);
    }
}

/// <summary>The SQL that finds an item's rating the same way the display projection does: the work, then its show or album, then the file.</summary>
internal static class ContentRatingSql
{
    /// <summary>
    /// A scalar expression for the rating of a work, given the SQL for its own id, its root work's id (the show for an
    /// episode, the work itself otherwise) and its media asset's id. Only built for profiles that have a limit.
    /// </summary>
    public static string Expression(string work, string root, string asset)
    {
        static string One(string id) =>
            $"(SELECT NULLIF(TRIM(value), '') FROM canonical_values WHERE entity_id = {id} AND key IN ('content_rating', 'certification') LIMIT 1)";
        return $"COALESCE({One(work)}, {One(root)}, {One(asset)})";
    }
}
