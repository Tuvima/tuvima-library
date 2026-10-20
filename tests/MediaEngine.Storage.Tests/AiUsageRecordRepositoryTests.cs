using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class AiUsageRecordRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly AiUsageRecordRepository _records;

    public AiUsageRecordRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ai_usage_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _records = new AiUsageRecordRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task AddedRecord_ReadsBackExactlyAsStored()
    {
        var record = Record(AiCallerKind.Plugin, "tuvima.test", Now, outcome: AiRequestStatus.TimedOut, wait: 1200, duration: 3400, message: "Waited too long.");

        await _records.AddAsync(record);
        var read = Assert.Single(await _records.ListRecentAsync(10));

        Assert.Equal(record, read);
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsNewestFirst_PagesAndFiltersByCaller()
    {
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-3)));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-2)));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "b", Now.AddMinutes(-1)));
        await _records.AddAsync(Record(AiCallerKind.Ingestion, "a", Now));

        var all = await _records.ListRecentAsync(10);
        var secondPage = await _records.ListRecentAsync(2, skip: 2);
        var onlyPluginA = await _records.ListRecentAsync(10, kind: AiCallerKind.Plugin, callerId: "a");

        Assert.Equal(new[] { Now, Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-3) }, all.Select(r => r.StartedAt));
        Assert.Equal(new[] { Now.AddMinutes(-2), Now.AddMinutes(-3) }, secondPage.Select(r => r.StartedAt));
        Assert.Equal(2, onlyPluginA.Count);
        Assert.All(onlyPluginA, r => Assert.Equal(("a", AiCallerKind.Plugin), (r.CallerId, r.CallerKind)));
    }

    [Fact]
    public async Task GetTotalsSinceAsync_CountsOnlyTheRollingHour_ForThatCaller()
    {
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-90), duration: 5_000));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-59), duration: 2_000));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-10), duration: 3_000));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "b", Now.AddMinutes(-10), duration: 7_000));
        await _records.AddAsync(Record(AiCallerKind.LibraryBackground, "a", Now.AddMinutes(-10), duration: 9_000));

        var totals = await _records.GetTotalsSinceAsync(AiCallerKind.Plugin, "a", Now.AddHours(-1));

        Assert.Equal(new AiUsageTotals(2, 5_000), totals);
    }

    [Fact]
    public async Task GetTotalsSinceAsync_DoesNotCountRefusedOrUnavailableRequests()
    {
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-5), duration: 1_000));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-4), outcome: AiRequestStatus.RefusedOverBudget));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-3), outcome: AiRequestStatus.Unavailable));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-2), outcome: AiRequestStatus.Failed, duration: 500));

        var totals = await _records.GetTotalsSinceAsync(AiCallerKind.Plugin, "a", Now.AddHours(-1));

        Assert.Equal(new AiUsageTotals(2, 1_500), totals);
    }

    [Fact]
    public async Task GetTotalsSinceAsync_ForACallerWithNoRecords_IsZero()
    {
        var totals = await _records.GetTotalsSinceAsync(AiCallerKind.Plugin, "nobody", Now.AddHours(-1));

        Assert.Equal(new AiUsageTotals(0, 0), totals);
    }

    [Fact]
    public async Task SummariseSinceAsync_GroupsByCallerAndAbility_BusiestFirst()
    {
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-5), AiAbility.Classify, duration: 100));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-4), AiAbility.Classify, outcome: AiRequestStatus.Failed, duration: 200));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-3), AiAbility.Classify, duration: 300));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddMinutes(-2), AiAbility.ReadTextFromImage, engine: AiEngine.Tesseract));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddDays(-2), AiAbility.Classify));

        var summary = await _records.SummariseSinceAsync(Now.AddDays(-1));

        Assert.Equal(2, summary.Count);
        var classify = summary[0];
        Assert.Equal((AiCallerKind.Plugin, "a", AiAbility.Classify, 3, 2, 600L), (classify.CallerKind, classify.CallerId, classify.Ability, classify.Requests, classify.Succeeded, classify.BusyMs));
        Assert.Equal(Now.AddMinutes(-3), classify.LastUsedAt);
        Assert.Equal(AiAbility.ReadTextFromImage, summary[1].Ability);
    }

    [Fact]
    public async Task PruneAsync_DeletesOnlyRecordsOlderThanNinetyDays()
    {
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddDays(-91)));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now.AddDays(-89)));
        await _records.AddAsync(Record(AiCallerKind.Plugin, "a", Now));

        var deleted = await _records.PruneAsync(Now);

        Assert.Equal(1, deleted);
        Assert.Equal(2, (await _records.ListRecentAsync(10)).Count);
    }

    private static AiUsageRecord Record(
        AiCallerKind kind,
        string callerId,
        DateTimeOffset startedAt,
        AiAbility ability = AiAbility.WriteTextQuick,
        AiRequestStatus outcome = AiRequestStatus.Succeeded,
        AiEngine engine = AiEngine.TextModel,
        int wait = 0,
        int duration = 0,
        string? message = null) =>
        new(Guid.NewGuid(), startedAt, kind, callerId, ability, engine, wait, duration, outcome, message);
}
