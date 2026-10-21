using MediaEngine.Contracts.Vault;

namespace MediaEngine.Web.Services.Vault;

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
        VaultSort.Duration => "Length",
        _ => "Recently played",
    };

    public static string For(VaultCoverMode value) => value switch
    {
        VaultCoverMode.Show => "Show",
        VaultCoverMode.Blur => "Blur until pointed at",
        _ => "Hide",
    };
}
