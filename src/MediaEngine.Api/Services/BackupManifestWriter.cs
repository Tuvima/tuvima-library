using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services;

internal static class BackupManifestWriter
{
    /// <summary>The data store epoch this build writes (DatabaseEpoch, which is internal to Storage); a test keeps the two equal.</summary>
    internal const string DatabaseEpoch = "guid-blob-v13-profile-held-contract";

    /// <summary>
    /// The manifest of a user file: kind <c>user</c>, and <c>scope</c> <c>profile</c> (one profile with the sign-in that opens it) or
    /// <c>household</c> (every profile of a household), with how many profiles and sign-ins it holds and the export counts. The file
    /// carries what is needed to sign in, so the manifest says so. It names no person and no email: what the file holds is read from its
    /// data. <c>two_step_left_out</c> is true when the file holds no two-step secret for someone it holds a sign-in for, because someone
    /// else made it; they set two-step up again.
    /// </summary>
    public static void WriteUser(ZipArchiveEntry entry, BackupExportContents contents, string scope, int signInCount, bool twoStepLeftOut)
    {
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            schema_version = "3.0",
            kind = UserExportScope.Kind,
            scope,
            created_at = DateTimeOffset.UtcNow,
            database_epoch = DatabaseEpoch,
            includes_secrets = false,
            includes_sign_in = signInCount > 0,
            includes_keys = false,
            leaves_out = new[] { "roles", "library access", "devices", "sessions", "server settings", "media files", "folders", "artwork", "catalogue" },
            profile_count = contents.Profiles,
            sign_in_count = signInCount,
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
