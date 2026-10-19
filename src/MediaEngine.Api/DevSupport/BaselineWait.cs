using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// The "is ingestion finished" SQL, shared by the integration harness and the offline baseline wait so both
/// judge a pipeline the same way.
/// </summary>
public static class IngestionSettleQueries
{
    /// <summary>Identity jobs still moving through Stage 1, 2 or 3 (a universe-enriching job counts until its Stage 3 stamp exists).</summary>
    public const string ActiveJobsThroughStage3 = """
        SELECT COUNT(*)
        FROM identity_jobs j
        LEFT JOIN canonical_values stage3_enhanced
               ON stage3_enhanced.entity_id = j.entity_id
              AND stage3_enhanced.key = 'stage3_enhanced_at'
        WHERE j.state NOT IN (
            'Ready',
            'ReadyWithoutUniverse',
            'Failed',
            'RetailNoMatch',
            'QidNoMatch',
            'QidNeedsReview'
        )
        AND (
            j.state <> 'UniverseEnriching'
            OR stage3_enhanced.entity_id IS NULL
        );
        """;

    /// <summary>Identity jobs still moving through Stage 1 or 2.</summary>
    public const string ActiveJobsThroughStage2 = """
        SELECT COUNT(*)
        FROM identity_jobs
        WHERE state NOT IN (
            'Ready',
            'ReadyWithoutUniverse',
            'Failed',
            'RetailNoMatch',
            'QidNoMatch',
            'QidNeedsReview',
            'UniverseEnriching'
        );
        """;
}

/// <summary>One look at how far ingestion has got. Two equal snapshots mean nothing moved in between.</summary>
public sealed record IngestionSnapshot(
    int Assets,
    int ViewFiles,
    int Works,
    int ResolvedWorks,
    int PendingWorks,
    int Claims,
    int Derived,
    int ActiveJobs,
    int ActiveOperations,
    int ReviewItems,
    string JobStates);

/// <summary>
/// The stable-snapshot rule: settled once the same snapshot has been seen <see cref="RequiredIdenticalSnapshots"/>
/// times in a row with no active identity job or operation, and either every expected asset is registered or
/// nothing has moved for <see cref="ExpectationGiveUpSnapshots"/> snapshots (files the Engine will never register,
/// such as duplicates, would otherwise wait forever).
/// </summary>
public sealed class IngestionSettleTracker(int expectedAssets)
{
    public const int RequiredIdenticalSnapshots = 3;
    public const int ExpectationGiveUpSnapshots = 20;

    private IngestionSnapshot? _last;
    private int _identical;

    public int IdenticalSnapshots => _identical;

    /// <summary>Records a snapshot and says whether ingestion has settled.</summary>
    public bool Observe(IngestionSnapshot snapshot)
    {
        _identical = _last is not null && _last == snapshot ? _identical + 1 : 1;
        _last = snapshot;

        var idle = snapshot.ActiveJobs == 0 && snapshot.ActiveOperations == 0;
        var registered = snapshot.Assets + snapshot.ViewFiles > 0
            && (snapshot.Assets >= expectedAssets || _identical >= ExpectationGiveUpSnapshots);
        return idle && registered && _identical >= RequiredIdenticalSnapshots;
    }
}

/// <summary>
/// Offline command <c>--wait-baseline &lt;config-dir&gt; [--budget-minutes N] [--poll-seconds N] [--pid N]</c>:
/// polls the run's data store read-only until ingestion settles. Exit code 0 settled, 2 out of time,
/// 3 the Engine process (when a pid is given) exited first.
/// </summary>
public static class BaselineWait
{
    public const int ExitSettled = 0;
    public const int ExitTimedOut = 2;
    public const int ExitEngineStopped = 3;

    private static readonly string[] ActiveOperationStatuses =
        ["pending", "queued", "leased", "running", "retry_waiting", "failed_retryable", "interrupted"];

    public static async Task<int> RunAsync(string configDirectory, string[] options)
    {
        var budget = TimeSpan.FromMinutes(ReadNumber(options, "--budget-minutes", 180));
        var poll = TimeSpan.FromSeconds(ReadNumber(options, "--poll-seconds", 15));
        var pid = (int)ReadNumber(options, "--pid", 0);
        var databasePath = Path.GetFullPath(MediaEngine.Storage.TuvimaDataPathResolver.ResolveDatabasePath(configDirectory, null, null));
        var tracker = new IngestionSettleTracker(ExpectedAssets(configDirectory));
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget)
        {
            if (pid > 0 && !IsRunning(pid))
            {
                Console.WriteLine($"The Engine process {pid} exited before ingestion settled.");
                return ExitEngineStopped;
            }

            if (File.Exists(databasePath))
            {
                IngestionSnapshot? snapshot = null;
                try
                {
                    snapshot = ReadSnapshot(databasePath);
                }
                catch (SqliteException ex)
                {
                    // The Engine may hold a write lock or still be creating tables; the next poll tries again.
                    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] snapshot unavailable ({ex.SqliteErrorCode}); retrying.");
                }

                if (snapshot is not null)
                {
                    var settled = tracker.Observe(snapshot);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[{clock.Elapsed:hh\\:mm\\:ss}] assets={snapshot.Assets} view={snapshot.ViewFiles} works={snapshot.Works} resolved={snapshot.ResolvedWorks} pending={snapshot.PendingWorks} claims={snapshot.Claims} derived={snapshot.Derived} jobs={snapshot.ActiveJobs} ops={snapshot.ActiveOperations} review={snapshot.ReviewItems} identical={tracker.IdenticalSnapshots}/{IngestionSettleTracker.RequiredIdenticalSnapshots} states=[{snapshot.JobStates}]"));
                    if (settled)
                    {
                        Console.WriteLine($"Ingestion settled after {clock.Elapsed:hh\\:mm\\:ss}.");
                        return ExitSettled;
                    }
                }
            }

            await Task.Delay(poll).ConfigureAwait(false);
        }

        Console.WriteLine($"Ingestion did not settle within {budget.TotalMinutes:0} minutes.");
        return ExitTimedOut;
    }

    public static IngestionSnapshot ReadSnapshot(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        var statuses = string.Join(",", ActiveOperationStatuses.Select(status => $"'{status}'"));
        return new IngestionSnapshot(
            Assets: Scalar(connection, "SELECT COUNT(*) FROM media_assets"),
            ViewFiles: Scalar(connection, "SELECT COUNT(*) FROM local_files"),
            Works: Scalar(connection, "SELECT COUNT(*) FROM works"),
            ResolvedWorks: Scalar(connection, """
                SELECT COUNT(DISTINCT w.id) FROM works w
                LEFT JOIN editions e ON e.work_id = w.id
                LEFT JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN review_queue rq ON rq.entity_id = ma.id AND rq.status = 'Pending'
                WHERE w.wikidata_qid IS NOT NULL OR rq.id IS NOT NULL OR w.curator_state IS NOT NULL
                """),
            PendingWorks: Scalar(connection, """
                SELECT COUNT(DISTINCT w.id) FROM works w
                LEFT JOIN editions e ON e.work_id = w.id
                LEFT JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN review_queue rq ON rq.entity_id = ma.id AND rq.status = 'Pending'
                WHERE w.wikidata_qid IS NULL AND rq.id IS NULL AND w.curator_state IS NULL
                """),
            Claims: Scalar(connection, "SELECT COUNT(*) FROM metadata_claims"),
            // Work that carries on after the identity jobs are done: canonical values, artwork, people and their links.
            Derived: Scalar(connection, """
                SELECT (SELECT COUNT(*) FROM canonical_values) + (SELECT COUNT(*) FROM canonical_value_arrays)
                     + (SELECT COUNT(*) FROM artwork_assets) + (SELECT COUNT(*) FROM persons) + (SELECT COUNT(*) FROM person_media_links)
                """),
            ActiveJobs: Scalar(connection, IngestionSettleQueries.ActiveJobsThroughStage3),
            ActiveOperations: Scalar(connection, $"SELECT COUNT(*) FROM media_operations WHERE status IN ({statuses})"),
            ReviewItems: Scalar(connection, "SELECT COUNT(*) FROM review_queue"),
            JobStates: JobStates(connection));
    }

    public static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout = 5000";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string JobStates(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM identity_jobs GROUP BY state ORDER BY state";
        using var reader = command.ExecuteReader();
        var parts = new List<string>();
        while (reader.Read())
        {
            parts.Add($"{reader.GetString(0)}:{reader.GetInt32(1)}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Catalogue files in the sample folders whose extension some media type claims.</summary>
    private static int ExpectedAssets(string configDirectory)
    {
        var info = BaselineRunInfo.Load(configDirectory);
        var mediaTypes = Path.Combine(configDirectory, "media_types.json");
        if (info is null || !File.Exists(mediaTypes))
        {
            return 0;
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mediaTypes));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("types", out var types))
        {
            foreach (var type in types.EnumerateArray())
            {
                if (type.TryGetProperty("extensions", out var list))
                {
                    foreach (var extension in list.EnumerateArray())
                    {
                        extensions.Add(extension.GetString() ?? string.Empty);
                    }
                }
            }
        }

        return info.CatalogueFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(info.SampleRoot, folder), "*", SearchOption.AllDirectories))
            .Count(file => extensions.Contains(Path.GetExtension(file)));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById reports an unknown pid this way: the process is gone.
            return false;
        }
    }

    private static double ReadNumber(string[] options, string name, double fallback)
    {
        var index = Array.IndexOf(options, name);
        return index >= 0 && index + 1 < options.Length
            && double.TryParse(options[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}
