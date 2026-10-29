namespace MediaEngine.Domain.Contracts;

/// <summary>
/// What a household's Kid's profiles may see (library access D9, D10): a maximum level for each area on the G / PG / PG-13 / R ladder, and
/// whether items with no rating are shown. Defaults: PG for all three, unrated shown.
/// </summary>
public sealed record HouseholdKidSettings(string WatchLevel, string ReadLevel, string ListenLevel, bool ShowUnrated)
{
    /// <summary>PG for Watch, Read and Listen, unrated items shown.</summary>
    public static readonly HouseholdKidSettings Default = new("PG", "PG", "PG", true);

    /// <summary>The most protective settings (G everywhere, unrated hidden), used when a profile cannot be found.</summary>
    public static readonly HouseholdKidSettings Strictest = new("G", "G", "G", false);
}

/// <summary>
/// A profile's content limit as the Engine enforces it. A profile that is not a Kid's profile has no limit (<see cref="IsKid"/> is false); a
/// Kid's profile follows the household's levels in <see cref="Settings"/>.
/// </summary>
public sealed record ProfileContentLimit(bool IsKid, HouseholdKidSettings Settings)
{
    /// <summary>No limit: nothing is filtered and no rating needs to be read.</summary>
    public static readonly ProfileContentLimit Unrestricted = new(false, HouseholdKidSettings.Default);

    /// <summary>The most protective limit (G, unrated hidden), used when a profile cannot be found.</summary>
    public static readonly ProfileContentLimit Strictest = new(true, HouseholdKidSettings.Strictest);

    public bool IsUnrestricted => !IsKid;
}

/// <summary>Reads the content limit of one profile (a single lookup of the profile and its household).</summary>
public interface IProfileContentLimitRepository
{
    /// <summary>
    /// The profile's limit. A profile that cannot be found gets <see cref="ProfileContentLimit.Strictest"/>: a
    /// session that lost its profile (or acts for a deleted one) must never see more than a child would.
    /// </summary>
    Task<ProfileContentLimit> GetAsync(Guid profileId, CancellationToken ct = default);
}

/// <summary>Reads and replaces a household's Kid settings.</summary>
public interface IHouseholdKidSettingsRepository
{
    /// <summary>The household's settings; <see langword="null"/> when there is no such household.</summary>
    Task<HouseholdKidSettings?> GetAsync(Guid householdId, CancellationToken ct = default);

    /// <summary>Replaces all four settings. Returns false when there is no such household.</summary>
    Task<bool> ReplaceAsync(Guid householdId, HouseholdKidSettings settings, CancellationToken ct = default);
}
