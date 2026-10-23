using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services;

internal static class BackupManifestWriter
{
    /// <summary>The data store epoch this build writes (DatabaseEpoch, which is internal to Storage); a test keeps the two equal.</summary>
    internal const string DatabaseEpoch = "guid-blob-v12-profile-held-access";

    /// <summary>
    /// The manifest of a user export: kind and scope <c>user</c>, the person's email and how many profiles they have (for the import
    /// preview), and the export counts. The file carries what is needed to sign in, so the manifest says so. <c>two_step_left_out</c> is
    /// true when an administrator made the file for someone else, so it holds no two-step secret and the person sets two-step up again.
    /// </summary>
    public static void WriteUser(ZipArchiveEntry entry, BackupExportContents contents, string email, bool twoStepLeftOut)
    {
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            schema_version = "3.0",
            kind = UserExportScope.Kind,
            scope = UserExportScope.Kind,
            created_at = DateTimeOffset.UtcNow,
            database_epoch = DatabaseEpoch,
            includes_secrets = false,
            includes_sign_in = true,
            includes_keys = false,
            leaves_out = new[] { "other users", "access rights", "devices", "sessions", "server settings", "media files", "folders", "artwork", "catalogue" },
            user_email = email,
            profile_count = contents.Profiles,
            two_step_left_out = twoStepLeftOut,
            configuration_rows = contents.ConfigurationRows,
            personal_rows = contents.PersonalRows,
            titles = contents.Titles,
            titles_recognised_by_file_only = contents.TitlesRecognisedByFileOnly,
        });
    }

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
