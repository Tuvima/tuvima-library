namespace MediaEngine.Domain.Contracts;

/// <summary>A profile's content limit as the Engine enforces it. <see cref="Limit"/> is <see langword="null"/> for Everything.</summary>
public sealed record ProfileContentLimit(string? Limit, bool AllowUnrated)
{
    /// <summary>No limit: nothing is filtered and no rating needs to be read.</summary>
    public static readonly ProfileContentLimit Unrestricted = new(null, false);

    /// <summary>The most protective limit (G, unrated hidden), used when a profile cannot be found.</summary>
    public static readonly ProfileContentLimit Strictest = new("G", false);

    public bool IsUnrestricted => Limit is null;
}

/// <summary>Reads the content limit of one profile (a single primary-key lookup).</summary>
public interface IProfileContentLimitRepository
{
    /// <summary>
    /// The profile's limit. A profile that cannot be found gets <see cref="ProfileContentLimit.Strictest"/>: a
    /// session that lost its profile (or acts for a deleted one) must never see more than a child would.
    /// </summary>
    Task<ProfileContentLimit> GetAsync(Guid profileId, CancellationToken ct = default);
}
