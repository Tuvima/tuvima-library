using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// What a person needs to know about the Vault before restoring: the catalogue is never in a backup, so Vault libraries come back empty
/// until their folders are scanned, and saved Vault bookmarks are re-linked to their videos as the scan finds them. Counts only.
/// </summary>
internal static class VaultRestoreNote
{
    public static string For(string stagedDatabase)
    {
        using var connection = new SqliteConnection($"Data Source={stagedDatabase};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var hasVault = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'vault_libraries';") == 1
            && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_libraries;") > 0;
        if (!hasVault)
        {
            return string.Empty;
        }

        var waiting = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'vault_pending_bookmarks';") == 1
            ? connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_pending_bookmarks;")
            : 0;
        return " A backup never holds the Vault catalogue: Vault libraries come back empty and fill in again as their folders are scanned."
            + (waiting > 0
                ? $" {waiting} saved Vault bookmark{(waiting == 1 ? " is" : "s are")} kept and will be re-linked to the matching videos as the scan finds them; any that find no matching video stay waiting, and the Vault settings say how many."
                : string.Empty);
    }
}
