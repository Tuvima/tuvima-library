using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>The retry queue for plugins that send things to an outside service, against a real temporary data store.</summary>
public sealed class PluginOutboxRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 5, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly PluginOutboxRepository _repo;
    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _ben = Guid.NewGuid();

    public PluginOutboxRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_outbox_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new PluginOutboxRepository(_db);
        AddProfile(_ana, "Ana");
        AddProfile(_ben, "Ben");
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp data store cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp data store cleanup is best effort */ }
    }

    [Fact]
    public async Task ANewItem_IsDueNow_AndComesBackOldestFirst()
    {
        await _repo.EnqueueAsync(Row("p.one", _ana, "{\"n\":1}", Now), 100);
        await _repo.EnqueueAsync(Row("p.one", _ben, "{\"n\":2}", Now.AddSeconds(1)), 100);
        await _repo.EnqueueAsync(Row("p.two", _ana, "{\"n\":3}", Now), 100);

        var due = await _repo.ListDueAsync("p.one", Now.AddSeconds(5), 10);

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], due.Select(row => row.PayloadJson));
        Assert.Equal(["p.one", "p.two"], await _repo.ListPluginsWithDueItemsAsync(Now.AddSeconds(5)));
    }

    [Fact]
    public async Task APausedItem_IsNotDueUntilItsTimeComes()
    {
        var row = Row("p.one", _ana, "{}", Now);
        await _repo.EnqueueAsync(row, 100);

        await _repo.RescheduleAsync(row.Id, 1, Now.AddMinutes(1), "x");

        Assert.Empty(await _repo.ListDueAsync("p.one", Now.AddSeconds(30), 10));
        var due = Assert.Single(await _repo.ListDueAsync("p.one", Now.AddMinutes(1), 10));
        Assert.Equal(1, due.Attempts);
    }

    [Fact]
    public async Task PastTheCap_TheOldestItemsAreDropped_AndOtherPluginsAreUntouched()
    {
        await _repo.EnqueueAsync(Row("p.two", _ana, "{\"keep\":true}", Now), 2);
        for (var i = 1; i <= 3; i++)
        {
            await _repo.EnqueueAsync(Row("p.one", _ana, $"{{\"n\":{i}}}", Now.AddSeconds(i)), 2);
        }

        var left = await _repo.ListDueAsync("p.one", Now.AddMinutes(1), 10);

        Assert.Equal(["{\"n\":2}", "{\"n\":3}"], left.Select(row => row.PayloadJson));
        Assert.Equal(1, await _repo.CountAsync("p.two"));
    }

    [Fact]
    public async Task Completing_RemovesTheItem()
    {
        var row = Row("p.one", _ana, "{}", Now);
        await _repo.EnqueueAsync(row, 100);

        await _repo.CompleteAsync(row.Id);

        Assert.Equal(0, await _repo.CountAsync("p.one"));
    }

    [Fact]
    public async Task ADeletedProfile_TakesItsQueuedItemsWithIt_AndUnlinkingClearsOnlyThatProfile()
    {
        await _repo.EnqueueAsync(Row("p.one", _ana, "{}", Now), 100);
        await _repo.EnqueueAsync(Row("p.one", _ben, "{}", Now), 100);

        Assert.Equal(1, await _repo.DeleteForProfileAsync("p.one", _ana));
        Assert.Equal(1, await _repo.CountAsync("p.one"));

        using (var connection = _db.CreateConnection())
        {
            connection.Execute("PRAGMA foreign_keys = ON;");
            connection.Execute("DELETE FROM profiles WHERE id = @id;", new { id = GuidSql.ToBlob(_ben) });
        }

        Assert.Equal(0, await _repo.CountAsync("p.one"));
    }

    [Fact]
    public async Task RemovingAPluginsData_ClearsItsQueue()
    {
        await _repo.EnqueueAsync(Row("p.one", _ana, "{}", Now), 100);
        await _repo.EnqueueAsync(Row("p.two", _ana, "{}", Now), 100);

        await new PluginDataRepository(_db).RemoveAsync("p.one", includeOutput: false);

        Assert.Equal(0, await _repo.CountAsync("p.one"));
        Assert.Equal(1, await _repo.CountAsync("p.two"));
    }

    private static PluginOutboxRow Row(string plugin, Guid profile, string payload, DateTimeOffset at) =>
        new(Guid.NewGuid(), plugin, profile, payload, at, 0);

    private void AddProfile(Guid id, string name)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id = GuidSql.ToBlob(id), name, now = Now.ToString("O") });
    }
}
