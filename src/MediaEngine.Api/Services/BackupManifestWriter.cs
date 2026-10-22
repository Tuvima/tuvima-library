using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Api.Services.Vault;

namespace MediaEngine.Api.Services;

internal static class BackupManifestWriter
{
    public static void Write(ZipArchiveEntry entry, VaultBackupScrubResult vault)
    {
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            schema_version = "1.0",
            created_at = DateTimeOffset.UtcNow,
            database_epoch = "guid-blob-v6-shared-library-contributions",
            includes_secrets = false,
            note = "Provider secrets are intentionally excluded. Re-enter credentials after restore if needed.",
            vault_catalogue_excluded = true,
            vault_libraries_kept = vault.VaultLibraries,
            vault_bookmarks_kept = vault.BookmarksKept,
            vault_bookmark_packs_dropped = vault.BookmarkPacksDropped,
            vault_note = "The Vault catalogue is never part of a backup. Vault libraries reappear empty and are rebuilt by a rescan; saved Vault bookmarks are kept as the title they belong to and re-linked as the rescan finds each video.",
        });
    }
}
