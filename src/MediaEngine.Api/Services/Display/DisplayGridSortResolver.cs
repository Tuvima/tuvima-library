using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>Picks the sort a View all grid opens with when the caller did not ask for one.</summary>
internal static class DisplayGridSortResolver
{
    /// <summary>
    /// Returns the requested sort when there is one. Otherwise returns the profile's remembered sort for the grid's lane
    /// (null when the grid is not remembered, there is no profile, or nothing is saved, so the grid uses its own default).
    /// </summary>
    public static async Task<string?> ResolveAsync(
        string lane,
        string shelfKey,
        string? requestedSort,
        Guid? profileId,
        IProfileGridPreferenceRepository preferences,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedSort))
        {
            return requestedSort;
        }

        if (profileId is not { } activeProfile || DisplayGridCatalog.Find(lane, shelfKey)?.RememberKey is not { } rememberKey)
        {
            return null;
        }

        var remembered = await preferences.GetSortsAsync(activeProfile, ct).ConfigureAwait(false);
        return remembered.GetValueOrDefault(rememberKey);
    }
}
