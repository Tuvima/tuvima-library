using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>The plugin record and event-cursor tables added by upgrade step 0021, over a real temporary SQLite file.</summary>
public sealed class PluginHostRepositoriesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PluginRecordLimits Roomy = new(MaxValueBytes: 1024, MaxRecords: 100, MaxTotalBytes: 100_000);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly PluginRecordRepository _records;
    private readonly PluginEventCursorRepository _cursors;

    public PluginHostRepositoriesTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_host_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _records = new PluginRecordRepository(_db);
        _cursors = new PluginEventCursorRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task APutRecord_ReadsBackExactlyAsStored_WithItsSizeAndTime()
    {
        var result = await _records.PutAsync("p1", "watched", "dune", """{"title":"Dune é"}""", Now, Roomy);

        var stored = await _records.GetAsync("p1", "watched", "dune");

        Assert.Equal(PluginRecordWriteResult.Saved, result);
        Assert.Equal(new StoredPluginRecord("p1", "watched", "dune", """{"title":"Dune é"}""", 19, Now), stored);
    }

    [Fact]
    public async Task RecordsAreScopedToThePluginThatWroteThem()
    {
        await _records.PutAsync("p1", "c", "k", "1", Now, Roomy);
        await _records.PutAsync("p2", "c", "k", "2", Now, Roomy);

        Assert.Equal("1", (await _records.GetAsync("p1", "c", "k"))!.Json);
        Assert.Equal("2", (await _records.GetAsync("p2", "c", "k"))!.Json);
        Assert.Single(await _records.ListAsync("p1", "c", null, 10));
        Assert.False(await _records.DeleteAsync("p3", "c", "k"));
        Assert.True(await _records.DeleteAsync("p1", "c", "k"));
        Assert.Equal("2", (await _records.GetAsync("p2", "c", "k"))!.Json);
    }

    [Fact]
    public async Task APutOverALimit_ChangesNothing_AndNamesTheLimitThatStoppedIt()
    {
        var limits = new PluginRecordLimits(MaxValueBytes: 10, MaxRecords: 2, MaxTotalBytes: 12);
        Assert.Equal(PluginRecordWriteResult.Saved, await _records.PutAsync("p1", "c", "a", "123456", Now, limits));
        Assert.Equal(PluginRecordWriteResult.Saved, await _records.PutAsync("p1", "c", "b", "123456", Now, limits));

        Assert.Equal(PluginRecordWriteResult.ValueTooLarge, await _records.PutAsync("p1", "c", "x", "12345678901", Now, limits));
        Assert.Equal(PluginRecordWriteResult.TooManyRecords, await _records.PutAsync("p1", "c", "x", "1", Now, limits));
        Assert.Equal(PluginRecordWriteResult.OutOfSpace, await _records.PutAsync("p1", "c", "b", "1234567", Now, limits));
        Assert.Equal(PluginRecordWriteResult.Saved, await _records.PutAsync("p2", "c", "x", "123456", Now, limits));

        Assert.Equal(new PluginRecordUsage(2, 12), await _records.GetUsageAsync("p1"));
        Assert.Equal("123456", (await _records.GetAsync("p1", "c", "b"))!.Json);
    }

    [Fact]
    public async Task ListPrefix_IsLiteral_OrderedByKey_AndLimited()
    {
        foreach (var key in new[] { "b_2", "b%1", "a1", "bX" })
        {
            await _records.PutAsync("p1", "c", key, "{}", Now, Roomy);
        }

        Assert.Equal(["a1", "b%1", "bX", "b_2"], (await _records.ListAsync("p1", "c", null, 10)).Select(r => r.Key));
        Assert.Equal(["b%1"], (await _records.ListAsync("p1", "c", "b%", 10)).Select(r => r.Key));
        Assert.Equal(["b_2"], (await _records.ListAsync("p1", "c", "b_", 10)).Select(r => r.Key));
        Assert.Equal(["a1", "b%1"], (await _records.ListAsync("p1", "c", null, 2)).Select(r => r.Key));
    }

    [Fact]
    public async Task EventCursors_AreSetReplacedReadAndDeletedPerPlugin()
    {
        Assert.Null(await _cursors.GetAsync("p1"));

        await _cursors.SetAsync("p1", 10, Now);
        await _cursors.SetAsync("p1", 25, Now);
        await _cursors.SetAsync("p2", 3, Now);

        Assert.Equal(25, await _cursors.GetAsync("p1"));
        Assert.Equal(3, await _cursors.GetAsync("p2"));
        await _cursors.DeleteAsync("p1");
        Assert.Null(await _cursors.GetAsync("p1"));
        Assert.Equal(3, await _cursors.GetAsync("p2"));
    }

    [Fact]
    public async Task RecordsAndCursors_SurviveReopeningTheDatabaseFile()
    {
        await _records.PutAsync("p1", "c", "k", """{"v":1}""", Now, Roomy);
        await _cursors.SetAsync("p1", 42, Now);
        _db.Dispose();

        using var reopened = new DatabaseConnection(_dbPath);
        reopened.InitializeSchema();
        reopened.RunStartupChecks();

        Assert.Equal("""{"v":1}""", (await new PluginRecordRepository(reopened).GetAsync("p1", "c", "k"))!.Json);
        Assert.Equal(42, await new PluginEventCursorRepository(reopened).GetAsync("p1"));
    }
}
