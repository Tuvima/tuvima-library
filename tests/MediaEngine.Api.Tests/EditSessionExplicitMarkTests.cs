using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Explicit mark in the edit session: it rides on a details change, only a video can carry it, nothing is written by a check, a save
/// writes the same user-locked claim and winning value the old Explicit action wrote (so what hides it from Kids profiles is unchanged),
/// and an undo puts the previous mark back.
/// </summary>
public sealed class EditSessionExplicitMarkTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-explicit-mark-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _items;
    private readonly CanonicalValueRepository _canonical;
    private readonly DetailsEditSessionChangeHandler _handler;

    public EditSessionExplicitMarkTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        _canonical = new CanonicalValueRepository(_db);
        _handler = new DetailsEditSessionChangeHandler(_items, new SystemActivityRepository(_db), null, new MetadataClaimRepository(_db), _canonical);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task Check_RefusesTheMarkOnABook_AndOnAValueThatIsNotOnOrOff()
    {
        var book = SeedWork("Books");
        var video = SeedWork("Videos");

        Assert.NotNull(await _handler.CheckAsync(Change(book, "true"), CancellationToken.None));
        Assert.NotNull(await _handler.CheckAsync(Change(video, "maybe"), CancellationToken.None));
        Assert.Null(await _handler.CheckAsync(Change(video, "true"), CancellationToken.None));
        Assert.False(await IsExplicitAsync(video));
    }

    [Fact]
    public async Task Save_MarksTheVideoExplicit_AsAUserLockedWinningValue_AndUndoRestoresIt()
    {
        var video = SeedWork("Videos");
        var service = new EditSessionService([_handler], NullLogger<EditSessionService>.Instance);

        var preview = await service.RunAsync(Request(true, Change(video, "true")), NoRefusals);
        Assert.True(preview.Valid);
        Assert.False(await IsExplicitAsync(video));

        var saved = await service.RunAsync(Request(false, Change(video, "true")), NoRefusals);
        Assert.True(saved.DataSaved);
        Assert.True(await IsExplicitAsync(video));
        using (var conn = _db.CreateConnection())
        {
            var locked = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM metadata_claims WHERE claim_key = 'explicit' AND is_user_locked = 1 AND claim_value = 'true';");
            Assert.Equal(1, locked);
        }

        var undo = await _handler.ApplyAsync(Change(video, "false"), CancellationToken.None);
        Assert.False(await IsExplicitAsync(video));
        await undo!(CancellationToken.None);
        Assert.True(await IsExplicitAsync(video));
    }

    private async Task<bool> IsExplicitAsync(Guid workId) =>
        (await _canonical.GetByEntityAsync(workId)).Any(v =>
            string.Equals(v.Key, MetadataFieldConstants.Explicit, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static EditSessionRequest Request(bool previewOnly, EditSessionChange change) =>
        new() { PreviewOnly = previewOnly, Changes = [change] };

    private static EditSessionChange Change(Guid entity, string value) => new()
    {
        Id = "c1",
        Kind = EditSessionChangeKinds.Details,
        EntityId = entity,
        Fields = [new EditSessionFieldChange { Key = "explicit", Value = value }],
    };

    private Guid SeedWork(string mediaType)
    {
        var workId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind) VALUES (@workId, @mediaType, 'standalone');",
            new { workId = GuidSql.ToBlob(workId), mediaType });
        return workId;
    }
}
