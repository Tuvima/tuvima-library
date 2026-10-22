using System.Diagnostics;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The paged, filtered read behind the Activity log, against a real temporary SQLite data store.
/// </summary>
public sealed class SystemActivityQueryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly SystemActivityRepository _repo;

    public SystemActivityQueryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_activity_query_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new SystemActivityRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp data store cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp data store cleanup is best effort */ }
    }

    [Fact]
    public async Task Query_FiltersByActionType()
    {
        await Log(SystemActionType.MediaAdded, "Dune");
        await Log(SystemActionType.ReviewItemCreated, "Arrival");
        await Log(SystemActionType.FileHashed, "hash of a file");

        var (items, total) = await _repo.QueryAsync([SystemActionType.MediaAdded, SystemActionType.ReviewItemCreated], null, 0, 50);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.ActionType == SystemActionType.FileHashed);
    }

    [Fact]
    public async Task Query_WithAMediaType_KeepsOnlyEntriesRecordedForThatType_IgnoringCase()
    {
        await Log(SystemActionType.MediaAdded, "Pilot", changesJson: """{"media_type":"TV"}""");
        await Log(SystemActionType.MediaAdded, "Dune", changesJson: """{"media_type":"Movies"}""");
        await Log(SystemActionType.MediaAdded, "No type", changesJson: "not json");
        await Log(SystemActionType.MediaAdded, "Also no type");

        var (items, total) = await _repo.QueryAsync([SystemActionType.MediaAdded], null, 0, 50, "tv");

        Assert.Equal(1, total);
        Assert.Equal("Pilot", Assert.Single(items).CollectionName);
    }

    [Fact]
    public async Task Query_WithNoActionTypes_ReturnsNothing()
    {
        await Log(SystemActionType.MediaAdded, "Dune");

        var (items, total) = await _repo.QueryAsync([], null, 0, 50);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Query_SearchesTitleAndDetailOnly_IgnoringCase()
    {
        await Log(SystemActionType.MediaAdded, "Dune Messiah");
        await Log(SystemActionType.MediaAdded, "Other", detail: "Added from the DUNE shelf");
        await Log(SystemActionType.MediaAdded, "Hidden", changesJson: "{\"source_file\":\"dune.epub\"}");
        await Log(SystemActionType.MediaAdded, "Unrelated");

        var (items, total) = await _repo.QueryAsync([SystemActionType.MediaAdded], "dUnE", 0, 50);

        // The stored change data is never shown, so it never makes a row match.
        Assert.Equal(2, total);
        Assert.DoesNotContain(items, item => item.CollectionName is "Unrelated" or "Hidden");
    }

    [Fact]
    public async Task Query_TreatsPercentUnderscoreAndBackslashInTheSearchAsPlainText()
    {
        await Log(SystemActionType.MediaAdded, "100% Wolf");
        await Log(SystemActionType.MediaAdded, "Wolf_Pack");
        await Log(SystemActionType.MediaAdded, @"C:\Wolf");
        await Log(SystemActionType.MediaAdded, "Wolf Pack");
        await Log(SystemActionType.MediaAdded, "Plain");

        var percent = await _repo.QueryAsync([SystemActionType.MediaAdded], "%", 0, 50);
        var underscore = await _repo.QueryAsync([SystemActionType.MediaAdded], "_", 0, 50);
        var backslash = await _repo.QueryAsync([SystemActionType.MediaAdded], "\\", 0, 50);

        Assert.Equal("100% Wolf", Assert.Single(percent.Items).CollectionName);
        Assert.Equal("Wolf_Pack", Assert.Single(underscore.Items).CollectionName);
        Assert.Equal(@"C:\Wolf", Assert.Single(backslash.Items).CollectionName);
    }

    [Fact]
    public async Task Query_ReturnsNewestFirst_PagesWithoutOverlap_AndReportsTheTotal()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var index = 0; index < 7; index++)
        {
            await Log(SystemActionType.MediaAdded, $"Item {index}", occurredAt: start.AddMinutes(index));
        }

        var first = await _repo.QueryAsync([SystemActionType.MediaAdded], null, 0, 3);
        var second = await _repo.QueryAsync([SystemActionType.MediaAdded], null, 3, 3);
        var last = await _repo.QueryAsync([SystemActionType.MediaAdded], null, 6, 3);

        Assert.Equal(7, first.Total);
        Assert.Equal(["Item 6", "Item 5", "Item 4"], first.Items.Select(item => item.CollectionName));
        Assert.Equal(["Item 3", "Item 2", "Item 1"], second.Items.Select(item => item.CollectionName));
        Assert.Equal(["Item 0"], last.Items.Select(item => item.CollectionName));
    }

    [Fact]
    public async Task Query_BreaksTiesOnTheSameTimeByNewestRowFirst()
    {
        var at = DateTimeOffset.UtcNow;
        await Log(SystemActionType.MediaAdded, "First", occurredAt: at);
        await Log(SystemActionType.MediaAdded, "Second", occurredAt: at);

        var (items, _) = await _repo.QueryAsync([SystemActionType.MediaAdded], null, 0, 10);

        Assert.Equal(["Second", "First"], items.Select(item => item.CollectionName));
    }

    [Fact]
    public async Task Query_With50000Entries_ReturnsPagesQuickly()
    {
        var types = new[]
        {
            SystemActionType.MediaAdded,
            SystemActionType.ReviewItemCreated,
            SystemActionType.MediaFailed,
            SystemActionType.ToolMissing,
            SystemActionType.FileHashed,
        };
        var start = DateTimeOffset.UtcNow.AddDays(-30);
        using (var conn = _db.CreateConnection())
        using (var transaction = conn.BeginTransaction())
        {
            var rows = Enumerable.Range(0, 50_000).Select(index => new
            {
                OccurredAt = start.AddSeconds(index).ToString("O"),
                ActionType = types[index % types.Length],
                CollectionName = index % 1000 == 0 ? $"Needle {index}" : $"Title {index}",
                Detail = $"Detail {index}",
                ChangesJson = $"{{\"n\":{index}}}",
            });
            conn.Execute("""
                INSERT INTO system_activity (occurred_at, action_type, collection_name, detail, changes_json)
                VALUES (@OccurredAt, @ActionType, @CollectionName, @Detail, @ChangesJson);
                """, rows, transaction);
            transaction.Commit();
        }

        var shown = new[]
        {
            SystemActionType.MediaAdded,
            SystemActionType.ReviewItemCreated,
            SystemActionType.MediaFailed,
            SystemActionType.ToolMissing,
        };

        var timer = Stopwatch.StartNew();
        var firstPage = await _repo.QueryAsync(shown, null, 0, 50);
        var firstElapsed = timer.Elapsed;

        timer.Restart();
        var deepPage = await _repo.QueryAsync(shown, null, 25_000, 50);
        var deepElapsed = timer.Elapsed;

        timer.Restart();
        var searched = await _repo.QueryAsync(shown, "Needle", 0, 50);
        var searchElapsed = timer.Elapsed;

        Assert.Equal(40_000, firstPage.Total);
        Assert.Equal(50, firstPage.Items.Count);
        Assert.Equal(50, deepPage.Items.Count);
        Assert.Equal(50, searched.Total);
        Assert.True(firstElapsed < TimeSpan.FromSeconds(1), $"The first page took {firstElapsed.TotalMilliseconds:0} ms.");
        Assert.True(deepElapsed < TimeSpan.FromSeconds(1), $"The page at offset 25,000 took {deepElapsed.TotalMilliseconds:0} ms.");
        Assert.True(searchElapsed < TimeSpan.FromSeconds(1), $"The search took {searchElapsed.TotalMilliseconds:0} ms.");
    }

    private Task Log(
        string actionType,
        string? name,
        string? detail = null,
        string? changesJson = null,
        DateTimeOffset? occurredAt = null) =>
        _repo.LogAsync(new SystemActivityEntry
        {
            ActionType = actionType,
            CollectionName = name,
            Detail = detail,
            ChangesJson = changesJson,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        });
}
