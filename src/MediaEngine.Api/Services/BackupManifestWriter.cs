using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services;

internal static class BackupManifestWriter
{
    /// <summary>The data store epoch this build writes (DatabaseEpoch, which is internal to Storage); a test keeps the two equal.</summary>
    internal const string DatabaseEpoch = "guid-blob-v13-profile-held-contract";

    public static void Write(ZipArchiveEntry entry, BackupExportContents contents)
    {
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            schema_version = "3.0",
            kind = BackupArchiveFormat.ContentsKind,
            created_at = DateTimeOffset.UtcNow,
            database_epoch = DatabaseEpoch,
            includes_secrets = false,
            includes_keys = false,
            leaves_out = new[] { "media files", "folders", "artwork", "catalogue", "adult-video catalogue", "secrets" },
            profiles = contents.Profiles,
            configuration_rows = contents.ConfigurationRows,
            personal_rows = contents.PersonalRows,
            correction_rows = contents.CorrectionRows,
            choice_rows = contents.ChoiceRows,
            titles = contents.Titles,
            titles_recognised_by_file_only = contents.TitlesRecognisedByFileOnly,
        });
    }
}
