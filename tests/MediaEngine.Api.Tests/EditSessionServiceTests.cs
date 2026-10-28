using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The edit session's check-then-apply order (universal editor spec D13): real details saves against a temp SQLite data
/// store, with hand-written handlers standing in for the kinds that are not wired yet.
/// </summary>
public sealed class EditSessionServiceTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-edit-session-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _items;
    private readonly SystemActivityRepository _activity;

    public EditSessionServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        _activity = new SystemActivityRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task Preview_WhenEveryChangeIsFine_ReportsOkAndWritesNothing()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(
            Request(previewOnly: true, DetailsChange("c1", work, ("title", "Orbital"))), NoRefusals);

        Assert.True(result.Valid);
        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
        Assert.Empty(await OverridesOfAsync(work));
    }

    [Fact]
    public async Task Apply_WhenAMixedListIsFine_SavesTheDetailsAndThenRunsTheFileMove()
    {
        var work = SeedWork();
        var other = SeedWork();
        var order = new List<string>();
        var service = CreateService(new RecordingHandler(EditSessionChangeKinds.FileMove, order));

        var result = await service.RunAsync(
            Request(
                previewOnly: false,
                FileMoveChange("move", work),
                DetailsChange("c1", work, ("title", "Orbital"), ("year", "2025")),
                DetailsChange("c2", other, ("title", "Smile Empty Soul"))),
            NoRefusals);

        Assert.True(result.DataSaved);
        Assert.All(result.Changes, change => Assert.Equal(EditSessionChangeStatuses.Ok, change.Status));
        Assert.Equal("Orbital", (await OverridesOfAsync(work))["title"]);
        Assert.Equal("2025", (await OverridesOfAsync(work))["year"]);
        Assert.Equal("Smile Empty Soul", (await OverridesOfAsync(other))["title"]);
        Assert.Equal(["apply:move"], order);
    }

    [Fact]
    public async Task Apply_WhenOneCheckFails_SavesNothingAndSaysWhichChangeFailed()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(
            Request(
                previewOnly: false,
                DetailsChange("good", work, ("title", "Orbital")),
                DetailsChange("bad", work, ("not_a_field", "x"))),
            NoRefusals);

        Assert.False(result.Valid);
        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.NotApplied, result.Changes[0].Status);
        Assert.Equal(EditSessionChangeStatuses.Invalid, result.Changes[1].Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Changes[1].Reason));
        Assert.Empty(await OverridesOfAsync(work));
    }

    [Fact]
    public async Task Apply_WhenALaterDataChangeFailsWhileWriting_UndoesTheEarlierOnesAndNeverRunsTheMove()
    {
        var work = SeedWork();
        await _items.SaveDisplayOverridesAsync(work, new Dictionary<string, string> { ["title"] = "Before" });
        var order = new List<string>();
        var service = CreateService(
            new RecordingHandler(EditSessionChangeKinds.FileMove, order),
            new FailingHandler(EditSessionChangeKinds.Relationship));

        var result = await service.RunAsync(
            Request(
                previewOnly: false,
                DetailsChange("c1", work, ("title", "After")),
                new EditSessionChange { Id = "rel", Kind = EditSessionChangeKinds.Relationship, EntityId = work, TargetId = "album" },
                FileMoveChange("move", work)),
            NoRefusals);

        Assert.True(result.Valid);
        Assert.False(result.DataSaved);
        Assert.Equal("Before", (await OverridesOfAsync(work))["title"]);
        Assert.Equal(EditSessionChangeStatuses.NotApplied, result.Changes.Single(change => change.Id == "c1").Status);
        Assert.Equal(EditSessionChangeStatuses.Failed, result.Changes.Single(change => change.Id == "rel").Status);
        Assert.Equal(EditSessionChangeStatuses.NotApplied, result.Changes.Single(change => change.Id == "move").Status);
        Assert.Empty(order);
    }

    [Fact]
    public async Task Apply_WhenAFileMoveFails_KeepsTheDataSavedAndNamesTheMove()
    {
        var work = SeedWork();
        var service = CreateService(new FailingHandler(EditSessionChangeKinds.FileMove));

        var result = await service.RunAsync(
            Request(previewOnly: false, DetailsChange("c1", work, ("title", "Orbital")), FileMoveChange("move", work)),
            NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, result.Changes.Single(change => change.Id == "c1").Status);
        var move = result.Changes.Single(change => change.Id == "move");
        Assert.Equal(EditSessionChangeStatuses.Failed, move.Status);
        Assert.Equal("The file could not be moved.", move.Reason);
        Assert.Equal("Orbital", (await OverridesOfAsync(work))["title"]);
    }

    [Fact]
    public async Task Check_WhenTheCallerMayNotEditAnItem_RefusesItsChangesWithTheGivenReason()
    {
        var work = SeedWork();
        var refusals = new Dictionary<Guid, string> { [work] = "You can't edit this item." };

        var result = await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("c1", work, ("title", "x"))), refusals);

        Assert.False(result.Valid);
        Assert.Equal("You can't edit this item.", Assert.Single(result.Changes).Reason);
        Assert.Empty(await OverridesOfAsync(work));
    }

    [Fact]
    public async Task Check_RefusesDuplicateIdsUnknownKindsMissingItemsAndKindsThatAreNotWiredYet()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(
            Request(
                previewOnly: true,
                DetailsChange("dup", work, ("title", "a")),
                DetailsChange("dup", work, ("year", "2020")),
                new EditSessionChange { Id = "k", Kind = "delete", EntityId = work },
                DetailsChange("gone", Guid.NewGuid(), ("title", "a")),
                new EditSessionChange { Id = "art", Kind = EditSessionChangeKinds.Artwork, EntityId = work, Slot = "CoverArt" }),
            NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(
            [
                EditSessionChangeStatuses.Ok,
                EditSessionChangeStatuses.Invalid,
                EditSessionChangeStatuses.Invalid,
                EditSessionChangeStatuses.Invalid,
                EditSessionChangeStatuses.Invalid,
            ],
            result.Changes.Select(change => change.Status).ToList());
        Assert.Equal(EditSessionReasons.DuplicateId, result.Changes[1].Reason);
        Assert.Equal(EditSessionReasons.UnknownKind, result.Changes[2].Reason);
        Assert.Equal(EditSessionReasons.Gone, result.Changes[3].Reason);
        Assert.Equal(EditSessionReasons.NotAvailableYet, result.Changes[4].Reason);
    }

    [Fact]
    public async Task Check_RefusesTheSameFieldChangedTwiceForOneItem()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(
            Request(previewOnly: true, DetailsChange("c1", work, ("title", "a"), ("Title", "b"))), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionReasons.DuplicateField("Title"), Assert.Single(result.Changes).Reason);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private EditSessionService CreateService(params IEditSessionChangeHandler[] extra) =>
        new(
            [new DetailsEditSessionChangeHandler(_items, _activity), .. extra],
            NullLogger<EditSessionService>.Instance);

    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) =>
        new() { PreviewOnly = previewOnly, Changes = [.. changes] };

    private static EditSessionChange DetailsChange(string id, Guid entity, params (string Key, string Value)[] fields) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Details,
        EntityId = entity,
        Fields = fields.Select(field => new EditSessionFieldChange { Key = field.Key, Value = field.Value }).ToList(),
    };

    private static EditSessionChange FileMoveChange(string id, Guid entity) =>
        new() { Id = id, Kind = EditSessionChangeKinds.FileMove, EntityId = entity, Token = "token" };

    private async Task<Dictionary<string, string>> OverridesOfAsync(Guid workId) =>
        new((await _items.LoadDisplayOverridesAsync(workId)).Values, StringComparer.OrdinalIgnoreCase);

    private Guid SeedWork()
    {
        var workId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Movies', 'standalone');",
            new { workId = GuidSql.ToBlob(workId) });
        return workId;
    }

    /// <summary>Hand-written stand-in for a kind that is not wired to the real write path yet.</summary>
    private sealed class RecordingHandler(string kind, List<string> order) : IEditSessionChangeHandler
    {
        public string Kind => kind;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
        {
            order.Add($"apply:{change.Id}");
            return Task.FromResult<Func<CancellationToken, Task>?>(null);
        }
    }

    private sealed class FailingHandler(string kind) : IEditSessionChangeHandler
    {
        public string Kind => kind;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                kind == EditSessionChangeKinds.FileMove ? "The file could not be moved." : "The album could not be changed.");
    }
}
