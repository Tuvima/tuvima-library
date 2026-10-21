namespace MediaEngine.Contracts.Vault;

/// <summary>How the Vault lists its items (Videos spec D24): shelves like Watch, a grid of covers, a details list or a timeline.</summary>
public enum VaultLayout
{
    ShelfRows,
    Grid,
    DetailsList,
    Timeline,
}

public enum VaultGroupBy
{
    Folder,
    Tag,
    DateAdded,
}

public enum VaultSort
{
    RecentlyAdded,
    Title,
    Year,
    RecentlyPlayed,
}

/// <summary>What covers look like. Blur is the suggested default.</summary>
public enum VaultCoverMode
{
    Show,
    Blur,
    Hide,
}

/// <summary>
/// One profile's Vault viewing options (D24), remembered on this device only. <see cref="ShowQuickCheck"/> is the optional
/// SFW/NSFW check in the top menu (D23b), off until the person turns it on.
/// </summary>
public sealed record VaultViewPreferences(
    VaultLayout Layout = VaultLayout.ShelfRows,
    VaultGroupBy GroupBy = VaultGroupBy.Folder,
    VaultSort Sort = VaultSort.RecentlyAdded,
    VaultCoverMode Covers = VaultCoverMode.Blur,
    bool TitlesHiddenUntilPointed = false,
    bool ShowQuickCheck = false)
{
    public static VaultViewPreferences Default { get; } = new();
}
