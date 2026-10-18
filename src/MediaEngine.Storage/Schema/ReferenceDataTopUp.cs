using MediaEngine.Domain;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Schema;

/// <summary>
/// Adds the built-in rows whose master copy is in the app's code (decision D12 of the data store upgrade-steps spec),
/// so a new provider or setup step reaches an existing data store after an update without an upgrade step.
/// </summary>
/// <remarks>
/// Runs at every startup, after the upgrade steps, in one transaction. It only ever adds missing rows with
/// <c>INSERT OR IGNORE</c>: no structure statements, no UPDATE, no DELETE, and no tables other than
/// <c>metadata_providers</c>, <c>onboarding_workflows</c>, <c>onboarding_steps</c> and <c>profiles</c>
/// (the default Owner profile). Built-in data written in SQL belongs in an upgrade step instead.
/// </remarks>
internal sealed class ReferenceDataTopUp
{
    public void Run(SqliteConnection conn)
    {
        DatabaseConnection.ExecuteStartupTransaction(conn, transaction =>
        {
            SeedOnboardingWorkflow(conn, transaction);
            SeedMetadataProviders(conn, transaction);
            SeedDefaultProfile(conn, transaction);
        });
    }

    private static void SeedOnboardingWorkflow(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO onboarding_workflows
                (workflow_version, state, current_step, revision, created_at, updated_at)
            VALUES
                (1, 'in_progress', 'server', 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            INSERT OR IGNORE INTO onboarding_steps (workflow_version, step_key, status, updated_at)
            VALUES
                (1, 'server', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'preflight', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'administrator', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'media-locations', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'providers', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                (1, 'readiness', 'not_started', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
    }

    private static void SeedMetadataProviders(SqliteConnection conn, SqliteTransaction transaction)
    {
        ReadOnlySpan<(Guid Id, string Name, string Version)> providers =
        [
            (WellKnownProviders.LocalProcessor, "local_processor", "1.0"),
            (WellKnownProviders.LibraryScanner, "library_scanner", "1.0"),
            (WellKnownProviders.AppleApi, "apple_api", "2.0"),
            (WellKnownProviders.Wikidata, "wikidata", "1.0"),
            (WellKnownProviders.Wikipedia, "wikipedia", "1.0"),
            (WellKnownProviders.OpenLibrary, "open_library", "1.0"),
            (WellKnownProviders.MusicBrainz, "musicbrainz", "1.0"),
            (WellKnownProviders.Tmdb, "tmdb", "1.0"),
            (WellKnownProviders.Tvdb, "tvdb", "1.0"),
            (WellKnownProviders.ComicVine, "comicvine", "1.0"),
            (WellKnownProviders.Lrclib, "lrclib", "1.0"),
            (WellKnownProviders.OpenSubtitles, "opensubtitles", "1.0"),
            (WellKnownProviders.Subdl, "subdl", "2.0"),
            (WellKnownProviders.UserManual, "user_manual", "1.0"),
            (WellKnownProviders.AiProvider, "ai_provider", "1.0"),
            (WellKnownProviders.Pseudonym, "pseudonym", "1.0"),
        ];

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@id, @name, @version, 1);
            """;

        var pId = cmd.Parameters.Add("@id", SqliteType.Blob);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pVersion = cmd.Parameters.Add("@version", SqliteType.Text);

        foreach (var (id, name, version) in providers)
        {
            pId.Value = GuidSql.ToBlob(id);
            pName.Value = name;
            pVersion.Value = version;
            cmd.ExecuteNonQuery();
        }
    }

    private static void SeedDefaultProfile(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR IGNORE INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, @color, @role, @created);
            """;
        cmd.Parameters.Add("@id", SqliteType.Blob).Value =
            GuidSql.ToBlob(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        cmd.Parameters.AddWithValue("@name", "Owner");
        cmd.Parameters.AddWithValue("@color", "#7C4DFF");
        cmd.Parameters.AddWithValue("@role", "Administrator");
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
