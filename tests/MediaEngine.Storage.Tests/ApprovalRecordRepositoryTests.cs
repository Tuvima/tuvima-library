using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ApprovalRecordRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ApprovalRecordRepository _records;

    public ApprovalRecordRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_approvals_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _records = new ApprovalRecordRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task AddedRecord_ReadsBackWithWhoWhenAndExactlyWhatWasShown()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var approvedAt = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);
        var record = new ApprovalRecord(
            Guid.NewGuid(), ApprovalItemKinds.Plugin, "tuvima.test", "abc123", """{"title":"Test","can_do":["Reads your media files"]}""",
            account, profile, null, "Avery", approvedAt);

        await _records.AddAsync(record);
        var read = Assert.Single(await _records.ListAsync(ApprovalItemKinds.Plugin, "tuvima.test"));

        Assert.Equal(record, read);
    }

    [Fact]
    public async Task ApplicationApprover_IsStoredWithoutAnAccountOrProfile()
    {
        var application = Guid.NewGuid();
        await _records.AddAsync(Record(ApprovalItemKinds.Plugin, "p", "h1", DateTimeOffset.UtcNow, application: application, name: "Home Assistant"));

        var read = Assert.Single(await _records.ListAsync(ApprovalItemKinds.Plugin, "p"));

        Assert.Equal(application, read.ApprovedByApplicationId);
        Assert.Null(read.ApprovedByAccountId);
        Assert.Null(read.ApprovedByProfileId);
        Assert.Equal("Home Assistant", read.ApprovedByName);
    }

    [Fact]
    public async Task ListAsync_ReturnsTheNewestApprovalFirst_AndOnlyForThatItem()
    {
        var start = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
        await _records.AddAsync(Record(ApprovalItemKinds.Plugin, "p", "old", start));
        await _records.AddAsync(Record(ApprovalItemKinds.Plugin, "p", "new", start.AddDays(1)));
        await _records.AddAsync(Record(ApprovalItemKinds.Plugin, "p", "middle", start.AddHours(5)));
        await _records.AddAsync(Record(ApprovalItemKinds.Plugin, "other", "elsewhere", start));
        await _records.AddAsync(Record(ApprovalItemKinds.AiFeature, "p", "otherkind", start));

        var list = await _records.ListAsync(ApprovalItemKinds.Plugin, "p");

        Assert.Equal(["new", "middle", "old"], list.Select(r => r.StatementHash).ToArray());
    }

    [Fact]
    public async Task ListAsync_HonoursTheLimit()
    {
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await _records.AddAsync(Record(ApprovalItemKinds.Webhook, "w", $"h{i}", start.AddMinutes(i)));
        }

        var list = await _records.ListAsync(ApprovalItemKinds.Webhook, "w", limit: 2);

        Assert.Equal(["h4", "h3"], list.Select(r => r.StatementHash).ToArray());
    }

    [Fact]
    public async Task ExistsAsync_IsTrueOnlyForTheSameKindItemAndHash()
    {
        await _records.AddAsync(Record(ApprovalItemKinds.AiFeature, "vibe_tags", "h1", DateTimeOffset.UtcNow));

        Assert.True(await _records.ExistsAsync(ApprovalItemKinds.AiFeature, "vibe_tags", "h1"));
        Assert.False(await _records.ExistsAsync(ApprovalItemKinds.AiFeature, "vibe_tags", "h2"));
        Assert.False(await _records.ExistsAsync(ApprovalItemKinds.AiFeature, "tldr", "h1"));
        Assert.False(await _records.ExistsAsync(ApprovalItemKinds.Plugin, "vibe_tags", "h1"));
    }

    [Fact]
    public void TheRepository_OffersNoWayToChangeOrRemoveARecord()
    {
        string[] banned = ["Update", "Delete", "Remove", "Clear", "Set", "Replace", "Prune", "Purge"];
        var methods = typeof(IApprovalRecordRepository).GetMethods()
            .Concat(typeof(ApprovalRecordRepository).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(method => method.Name)
            .ToArray();

        Assert.NotEmpty(methods);
        Assert.DoesNotContain(methods, name => banned.Any(word => name.StartsWith(word, StringComparison.Ordinal)));
        Assert.Equal(["AddAsync", "ExistsAsync", "ListAsync"], methods.Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FreshStore_HasTheApprovalRecordsTableAndItsItemIndex()
    {
        using var connection = _db.CreateConnection();

        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('approval_records');").ToHashSet();
        var indexes = connection.Query<string>("SELECT name FROM pragma_index_list('approval_records');").ToHashSet();
        var step = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM data_store_upgrades WHERE name = 'approval_records';");

        Assert.Equal(
            new HashSet<string>
            {
                "id", "item_kind", "item_id", "statement_hash", "statement_json", "approved_by_account_id",
                "approved_by_profile_id", "approved_by_application_id", "approved_by_name", "approved_at",
            },
            columns);
        Assert.Contains("ix_approval_records_item", indexes);
        Assert.Equal(1, step);
    }

    private static ApprovalRecord Record(
        string kind,
        string itemId,
        string hash,
        DateTimeOffset approvedAt,
        Guid? application = null,
        string name = "Avery") => new(
            Guid.NewGuid(), kind, itemId, hash, $$"""{"hash":"{{hash}}"}""",
            application is null ? Guid.NewGuid() : null,
            application is null ? Guid.NewGuid() : null,
            application,
            name,
            approvedAt);
}
