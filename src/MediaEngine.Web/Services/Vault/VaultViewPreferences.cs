namespace MediaEngine.Web.Services.Vault;

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

/// <summary>The plain words the Vault options use.</summary>
public static class VaultViewLabels
{
    public static string For(VaultLayout value) => value switch
    {
        VaultLayout.ShelfRows => "Shelf rows",
        VaultLayout.Grid => "Grid of covers",
        VaultLayout.DetailsList => "Details list",
        _ => "Timeline by date added",
    };

    public static string For(VaultGroupBy value) => value switch
    {
        VaultGroupBy.Folder => "Series or folder",
        VaultGroupBy.Tag => "Tag",
        _ => "Date added",
    };

    public static string For(VaultSort value) => value switch
    {
        VaultSort.RecentlyAdded => "Recently added",
        VaultSort.Title => "Title",
        VaultSort.Year => "Year",
        _ => "Recently played",
    };

    public static string For(VaultCoverMode value) => value switch
    {
        VaultCoverMode.Show => "Show",
        VaultCoverMode.Blur => "Blur until pointed at",
        _ => "Hide",
    };
}
