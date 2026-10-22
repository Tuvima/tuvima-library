using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Unmatched;

/// <summary>Which libraries a caller may see Unmatched files and counts for. Scope is decided before anything is counted.</summary>
public sealed class UnmatchedScope
{
    private readonly IReadOnlySet<string> _libraryIds;

    private UnmatchedScope(bool all, IReadOnlySet<string> libraryIds)
    {
        AllLibraries = all;
        _libraryIds = libraryIds;
    }

    /// <summary>Every library.</summary>
    public static UnmatchedScope All { get; } = new(true, new HashSet<string>());

    /// <summary>No library: the caller sees no Unmatched file and no count.</summary>
    public static UnmatchedScope None { get; } = new(false, new HashSet<string>());

    public bool AllLibraries { get; }

    /// <summary>One named set of libraries (matched without regard to case).</summary>
    public static UnmatchedScope Of(IEnumerable<string> libraryIds) =>
        new(false, new HashSet<string>(libraryIds, StringComparer.OrdinalIgnoreCase));

    public bool Allows(string libraryId) => AllLibraries || _libraryIds.Contains(libraryId);

    /// <summary>
    /// The scope of a request. Looking at and managing Unmatched files is an administrator's job (it sits with Libraries in Settings), so
    /// an effective administrator sees every library and everyone else, including a signed-out caller, sees none.
    /// </summary>
    public static UnmatchedScope For(RequestAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return AuthorityValidity.Validate(authority) is null && authority.IsEffectiveAdministrator ? All : None;
    }
}

/// <summary>Reads each library's Unmatched count (kept-as-is files excluded) for the libraries a caller may see.</summary>
public sealed class UnmatchedCountService(IUnmatchedAssetRepository unmatched)
{
    public async Task<IReadOnlyDictionary<string, int>> CountsAsync(UnmatchedScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var counts = await unmatched.CountByLibraryAsync(ct).ConfigureAwait(false);
        return scope.AllLibraries
            ? counts
            : counts.Where(count => scope.Allows(count.Key)).ToDictionary(count => count.Key, count => count.Value, StringComparer.OrdinalIgnoreCase);
    }
}
