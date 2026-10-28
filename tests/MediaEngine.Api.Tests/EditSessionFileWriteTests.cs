using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Save changes into the files" in the edit session (universal editor spec D15): a saved session asks the write list for the changed
/// items' files unless that one save said not to, and a session that saved nothing asks for nothing.
/// </summary>
public sealed class EditSessionFileWriteTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-edit-session-files-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _items;
    private readonly SystemActivityRepository _activity;
    private readonly RecordingFileWrites _writes = new();

    public EditSessionFileWriteTests()
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
    public async Task Save_WhenTheRequestDoesNotSayOtherwise_QueuesTheChangedItemsFiles()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(Request(writeIntoFiles: null, DetailsChange("c1", work)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal([work], _writes.Queued);
    }

    [Fact]
    public async Task Save_WhenThisSaveSaysNotToUpdateTheFiles_QueuesNothing()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(Request(writeIntoFiles: false, DetailsChange("c1", work)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Empty(_writes.Queued);
    }

    [Fact]
    public async Task Check_WhenOnlyPreviewing_QueuesNothing()
    {
        var work = SeedWork();

        var result = await CreateService().RunAsync(Request(writeIntoFiles: true, DetailsChange("c1", work), previewOnly: true), NoRefusals);

        Assert.True(result.Valid);
        Assert.Empty(_writes.Queued);
    }

    [Fact]
    public async Task Save_WhenACheckFails_SavesNothingAndQueuesNothing()
    {
        var work = SeedWork();
        var bad = new EditSessionChange
        {
            Id = "bad",
            Kind = EditSessionChangeKinds.Details,
            EntityId = work,
            Fields = [new EditSessionFieldChange { Key = "not_a_field", Value = "x" }],
        };

        var result = await CreateService().RunAsync(Request(writeIntoFiles: null, DetailsChange("c1", work), bad), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Empty(_writes.Queued);
    }

    [Fact]
    public async Task Save_WhenTheChangeIsOnlyAnAlbumMove_DoesNotQueueAFileWrite()
    {
        var work = SeedWork();
        var move = new EditSessionChange { Id = "rel", Kind = EditSessionChangeKinds.Relationship, EntityId = work, TargetId = "album" };

        var result = await CreateService(new NoopHandler(EditSessionChangeKinds.Relationship)).RunAsync(
            Request(writeIntoFiles: null, move), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Empty(_writes.Queued);
    }

    [Fact]
    public async Task Save_WhenTheWriteListFails_StillReportsTheSave()
    {
        var work = SeedWork();
        var failing = new EditSessionFileWrites(new ThrowingQueue(), new MediaAssetRepository(_db), NullLogger<EditSessionFileWrites>.Instance);
        var service = new EditSessionService(
            [new DetailsEditSessionChangeHandler(_items, _activity)],
            NullLogger<EditSessionService>.Instance,
            failing);

        var result = await service.RunAsync(Request(writeIntoFiles: null, DetailsChange("c1", work)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Ok, Assert.Single(result.Changes).Status);
    }

    private EditSessionService CreateService(params IEditSessionChangeHandler[] extra) =>
        new([new DetailsEditSessionChangeHandler(_items, _activity), .. extra], NullLogger<EditSessionService>.Instance, _writes);

    private static EditSessionRequest Request(bool? writeIntoFiles, params EditSessionChange[] changes) =>
        Request(writeIntoFiles, changes[0], previewOnly: false, rest: changes[1..]);

    private static EditSessionRequest Request(bool? writeIntoFiles, EditSessionChange first, bool previewOnly = false, params EditSessionChange[] rest) =>
        new() { PreviewOnly = previewOnly, WriteIntoFiles = writeIntoFiles, Changes = [first, .. rest] };

    private static EditSessionChange DetailsChange(string id, Guid entity) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Details,
        EntityId = entity,
        Fields = [new EditSessionFieldChange { Key = "title", Value = "Orbital" }],
    };

    private Guid SeedWork()
    {
        var workId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Movies', 'standalone');",
            new { workId = GuidSql.ToBlob(workId) });
        return workId;
    }

    private sealed class RecordingFileWrites : IEditSessionFileWrites
    {
        public List<Guid> Queued { get; } = [];

        public Task QueueAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default)
        {
            Queued.AddRange(entityIds);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopHandler(string kind) : IEditSessionChangeHandler
    {
        public string Kind => kind;

        public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default) =>
            Task.FromResult<Func<CancellationToken, Task>?>(null);
    }

    private sealed class ThrowingQueue : MediaEngine.Domain.Contracts.IFileWriteQueue
    {
        public Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default) =>
            throw new InvalidOperationException("The write list is unavailable.");

        public Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default) =>
            throw new InvalidOperationException("The write list is unavailable.");

        public Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default) =>
            throw new InvalidOperationException("The write list is unavailable.");
    }
}
