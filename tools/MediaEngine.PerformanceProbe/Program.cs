using System.Diagnostics;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    throw new ArgumentException("Supply the catalogue database path. This probe opens it read-only.");
}
DapperConfiguration.Configure();
using var database = new ReadOnlyDatabase(Path.GetFullPath(args[0]));
using var connection = database.CreateConnection();
var work = connection.QueryFirst<Guid>("SELECT id FROM works WHERE media_type = 'Audiobooks' AND work_kind != 'parent' LIMIT 1");
var person = connection.QueryFirst<Guid>("SELECT p.id FROM persons p JOIN primary_person_media_credits c ON c.person_name = p.name COLLATE NOCASE LIMIT 1");
var reader = new DisplayWorkProjectionReader(database);
var results = new List<object>();
foreach (var (label, id) in new (string, Guid?)[] { ("audiobook", work), ("person", person), ("catalogue", null) })
{
    var samples = new List<double>();
    var count = 0;
    for (var i = 0; i < 6; i++)
    {
        var timer = Stopwatch.StartNew();
        var rows = await reader.LoadAsync(default, detailId: id);
        samples.Add(Math.Round(timer.Elapsed.TotalMilliseconds, 1));
        count = rows.Count;
    }
    results.Add(new { scope = label, rows = count, first_ms = samples[0], warm_ms = samples.Skip(1), warm_max_ms = samples.Skip(1).Max() });
}
Console.WriteLine(JsonSerializer.Serialize(new { measured_at = DateTimeOffset.UtcNow, note = "Read projection only; not end-to-end page latency", results }, new JsonSerializerOptions { WriteIndented = true }));

sealed class ReadOnlyDatabase(string path) : IDatabaseConnection
{
    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }
    public SqliteConnection Open() => CreateConnection();
    public void Dispose() { }
    public void InitializeSchema() => throw new NotSupportedException();
    public void RunStartupChecks() => throw new NotSupportedException();
    public Task AcquireWriteLockAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public void ReleaseWriteLock() => throw new NotSupportedException();
    public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default) => throw new NotSupportedException();
}
