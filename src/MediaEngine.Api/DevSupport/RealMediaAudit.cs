using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

public static class RealMediaAudit
{
    public static async Task RunAsync(string configDirectory, bool verify)
    {
        var run = RealMediaHarness.Load(configDirectory) ?? throw new InvalidOperationException("No real-media run is configured.");
        RealMediaHarness.ValidateConfiguration(configDirectory, run);
        var baseline = JsonSerializer.Deserialize<List<RealMediaFile>>(await File.ReadAllTextAsync(Path.Combine(run.OutputDirectory, "source-baseline.json")))!;
        string[]? differences = null;
        if (verify)
        {
            differences = RealMediaHarness.Differences(baseline, await RealMediaHarness.SnapshotAsync(run.SourceRoot, true), true);
            RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "audit-source-verification.json"), new { checked_at = DateTimeOffset.UtcNow, passed = differences.Length == 0, differences });
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = run.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var media = Rows(connection, "SELECT hex(id) id, file_path_root path, content_hash hash FROM media_assets");
        var local = Rows(connection, "SELECT s.file_path path, f.content_hash hash FROM local_file_sources s JOIN local_files f ON f.id=s.file_id");
        var identity = Rows(connection, "SELECT state,COUNT(*) count FROM identity_jobs GROUP BY state");
        var operations = Rows(connection, "SELECT status,COUNT(*) count FROM media_operations GROUP BY status");
        var pending = Rows(connection, "SELECT operation_type,stage,status,last_error FROM media_operations WHERE status IN ('pending','queued','leased','running','retry_waiting','failed_retryable','interrupted','blocked')");
        var review = Rows(connection, "SELECT trigger,COUNT(*) count FROM review_queue WHERE status='Pending' GROUP BY trigger");
        var ledger = baseline.Where(f => !f.IsDirectory).Select(file =>
        {
            var full = Path.Combine(run.SourceRoot, file.Path);
            var records = media.Concat(local).ToList();
            var exact = records.FirstOrDefault(r => string.Equals(r["path"]?.ToString(), full, StringComparison.OrdinalIgnoreCase));
            var duplicate = records.FirstOrDefault(r => string.Equals(r["hash"]?.ToString(), file.Sha256, StringComparison.OrdinalIgnoreCase));
            var personal = RealMediaHarness.ViewFolders.Any(f => RealMediaHarness.Contains(Path.Combine(run.SourceRoot, f), full));
            var extension = Path.GetExtension(full).ToLowerInvariant();
            var outcome = exact is not null ? (personal ? "View indexed" : "Catalogue indexed")
                : duplicate is not null ? "Duplicate bytes; original preserved"
                : !personal && extension is ".jpg" or ".jpeg" or ".opf" or ".json" ? "Supporting file; original preserved"
                : "Not indexed; investigate unsupported format, processing, or review";
            return new { file.Path, file.Length, outcome };
        }).ToArray();
        var outside = media.Concat(local).Where(r => !RealMediaHarness.Contains(run.SourceRoot, r["path"]!.ToString()!)).ToArray();
        var report = new
        {
            checked_at = DateTimeOffset.UtcNow,
            source_differences = differences,
            unexpected_assets = outside,
            counts = new { originals = ledger.Length, catalogue = media.Count, view_files = local.Count },
            identity,
            operations,
            pending,
            review,
            subtitles = Rows(connection, "SELECT language,provider,source_format,normalized_format,COUNT(*) count FROM text_tracks WHERE kind='Subtitles' GROUP BY language,provider,source_format,normalized_format"),
            files = ledger,
            playback = "Requires actual player verification; indexing is not playback proof"
        };
        RealMediaHarness.Save(Path.Combine(run.OutputDirectory, "ingestion-report.json"), report);
        await File.WriteAllTextAsync(Path.Combine(run.OutputDirectory, "ingestion-report.md"),
            $"# Real-media ingestion report\n\nChecked: {DateTimeOffset.UtcNow:O}\n\nOriginals: {ledger.Length}. Catalogue assets: {media.Count}. View file references: {local.Count}. Outside-source assets: {outside.Length}.\n\n" +
            (differences is null ? "Source hashes not rechecked in this report.\n" : $"Source differences: {differences.Length}.\n") +
            "\nSee ingestion-report.json for every file, remaining jobs, reviews, and subtitle tracks. Playback must be verified separately.\n");
        Console.WriteLine(JsonSerializer.Serialize(new { catalogue = media.Count, view = local.Count, identity, operations, outside = outside.Length, sourceDifferences = differences?.Length }, RealMediaHarness.Json));
        if (outside.Length > 0 || differences?.Length > 0)
        {
            Environment.ExitCode = 1;
        }
    }

    private static List<Dictionary<string, object?>> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
