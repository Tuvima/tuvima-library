using System.Collections.Concurrent;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// What one request's profile may see: its content limit turned into a yes/no for a rating, and whether the Vault is open.
/// A profile with no limit (and a service application, which acts for no person) gets <see cref="Unrestricted"/>, which never
/// reads a rating. A person's request that has no usable profile gets the strictest limit instead (fail closed).
/// The Vault is separate from the limit: a filter without an unlocked NSFW session hides every Vault library, whoever asks.
/// </summary>
internal sealed class ContentLimitFilter(
    ProfileContentLimit limit,
    IReadOnlySet<string>? videoLibrariesOk = null,
    VaultVisibility? vault = null)
{
    public static readonly ContentLimitFilter Unrestricted = new(ProfileContentLimit.Unrestricted);

    private readonly ProfileContentLimit _limit = limit;
    private readonly IReadOnlySet<string>? _videoLibrariesOk = videoLibrariesOk;

    /// <summary>What this request may know about the Vault. A filter built without it knows of no Vault library.</summary>
    public VaultVisibility Vault { get; } = vault ?? VaultVisibility.NoVault;

    public bool IsUnrestricted => _limit.IsUnrestricted;

    /// <summary>True when nothing needs filtering at all: no content limit and no Vault library to keep out of view.</summary>
    public bool NeedsNoFiltering => _limit.IsUnrestricted && !Vault.HasHiddenLibraries;

    public bool Allows(string? rating) => ProfileContentLimits.Allows(_limit.Limit, _limit.AllowUnrated, rating);

    /// <summary>
    /// Saved videos have no ratings, so a limited profile sees Videos only from a library an administrator marked "OK for
    /// limited profiles". Anything that is not a video is not affected.
    /// </summary>
    public bool AllowsVideo(string? libraryId, string mediaType) =>
        _limit.IsUnrestricted
        || DisplayMediaRules.NormalizeDisplayKind(mediaType) != "Video"
        || (libraryId is not null && _videoLibrariesOk?.Contains(libraryId) == true);

    /// <summary>True when an item in this library must be treated as absent for this request (a Vault library without an unlocked NSFW session).</summary>
    public bool HidesLibrary(string? libraryId) => Vault.HidesLibrary(libraryId);

    /// <summary>True when the library is a Vault library, whether or not it is open to this request.</summary>
    public bool IsVaultLibrary(string? libraryId) => Vault.IsVaultLibrary(libraryId);

    /// <summary>The same limit with the Vault state of a request added.</summary>
    public ContentLimitFilter WithVault(VaultVisibility vault) => new(_limit, _videoLibrariesOk, vault);
}

/// <summary>
/// Resolves the content limit of the profile a request acts as, and what that request may know about the Vault. The limit is
/// read live (one primary-key lookup per profile per request), so a parent's change applies on the child's very next call.
/// It sits beside library and feature access: every list, search, detail page and player resolves its catalogue through the same checks.
/// </summary>
internal sealed class ContentLimitPolicy(
    IProfileContentLimitRepository limits,
    VaultAccessResolver vaultAccess,
    IConfigurationLoader? configuration = null)
{
    private readonly ConcurrentDictionary<(Guid Profile, Guid Session), Task<ContentLimitFilter>> _byProfile = new();

    private static readonly ContentLimitFilter Strictest = new(ProfileContentLimit.Strictest);

    public Task<ContentLimitFilter> ForAsync(RequestAuthority authority) =>
        _byProfile.GetOrAdd(
            (authority.PrincipalKind is PrincipalKind.Human or PrincipalKind.DelegatedUserClient ? authority.ActiveProfileId ?? Guid.Empty : Guid.Empty, authority.SessionId ?? Guid.Empty),
            _ => ResolveAsync(authority));

    private async Task<ContentLimitFilter> ResolveAsync(RequestAuthority authority)
    {
        var limit = await LimitForAsync(authority).ConfigureAwait(false);

        // Not tied to one caller's cancellation: the answer is cached for the rest of the request.
        var vault = await vaultAccess.ResolveAsync(authority, CancellationToken.None).ConfigureAwait(false);
        return limit.WithVault(vault);
    }

    private Task<ContentLimitFilter> LimitForAsync(RequestAuthority authority)
    {
        if (authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient))
        {
            return Task.FromResult(ContentLimitFilter.Unrestricted);
        }

        return authority.ActiveProfileId is { } profileId && profileId != Guid.Empty
            ? LoadAsync(profileId)
            : Task.FromResult(Strictest);
    }

    private async Task<ContentLimitFilter> LoadAsync(Guid profileId)
    {
        var limit = await limits.GetAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        if (limit.IsUnrestricted)
        {
            return ContentLimitFilter.Unrestricted;
        }

        var videoLibrariesOk = configuration?.LoadLibraries().Libraries
            .Where(library => library.OkForLimitedProfiles)
            .Select(library => library.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ContentLimitFilter(limit, videoLibrariesOk);
    }
}

/// <summary>The SQL that finds an item's rating the same way the display projection does: the work, then its show or album, then the file.</summary>
internal static class ContentRatingSql
{
    /// <summary>
    /// A scalar expression for the rating of a work, given the SQL for its own id, its root work's id (the show for an
    /// episode, the work itself otherwise) and its media asset's id. Only built for profiles that have a limit.
    /// When an item carries both a rating and a certification, both come back ("PG|R") and the higher rank counts.
    /// Each lookup is an index seek on (entity_id, key), and the later levels only run when the earlier ones are empty.
    /// </summary>
    public static string Expression(string work, string root, string asset)
    {
        static string One(string id) =>
            $"(SELECT GROUP_CONCAT(NULLIF(TRIM(value), ''), '|') FROM canonical_values WHERE entity_id = {id} AND key IN ('content_rating', 'certification'))";
        return WithExplicit(work, root, asset, $"COALESCE({One(work)}, {One(root)}, {One(asset)})");
    }

    /// <summary>
    /// An item or series a person marked Explicit counts as adults-only, so no limit below "Everything" lets it through.
    /// Wraps a rating expression so every reader that feeds a content-limit check agrees on that.
    /// </summary>
    public static string WithExplicit(string work, string root, string asset, string rating)
    {
        static string Marked(string id) =>
            $"EXISTS (SELECT 1 FROM canonical_values WHERE entity_id = {id} AND key = 'explicit' AND value = 'true')";
        return $"CASE WHEN {Marked(work)} OR {Marked(root)} OR {Marked(asset)} THEN 'NC-17' ELSE {rating} END";
    }
}
