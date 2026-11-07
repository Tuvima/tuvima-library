using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Editing;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A Series is read-only (universal editor v5 D7, acceptance A10): a Series of books, audiobooks, comics or films is the root of a family
/// whose members are the real items, and the edit session refuses every field or picture write to it while a member saves as usual.
/// The family's structure comes from a hand-written navigation stand-in; the data store and the save are real.
/// </summary>
public sealed class SeriesReadOnlyEditSessionTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-series-readonly-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _book = Guid.NewGuid();
    private readonly EditSessionService _service;

    public SeriesReadOnlyEditSessionTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        SeedWork(_series, "Books", "parent");
        SeedWork(_book, "Books", "standalone");
        var items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        var activity = new SystemActivityRepository(_db);
        var guard = new EditorSeriesGuard(new SeriesOfBooksNavigation(_series, _book));
        var details = new DetailsEditSessionChangeHandler(items, activity, seriesGuard: guard);
        _service = new EditSessionService([details], NullLogger<EditSessionService>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task ASeriesOfBooks_RefusesATitleWrite_WithAPlainReason_AndSavesNothing()
    {
        var result = await _service.RunAsync(Request(_series, ("title", "Dune books")), NoRefusals);

        Assert.False(result.Valid);
        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionReasons.SeriesIsReadOnly, Assert.Single(result.Changes).Reason);
        Assert.Equal(0, OverrideCount(_series));
    }

    [Fact]
    public async Task ABookInTheSeries_StillSavesItsOwnTitle()
    {
        var result = await _service.RunAsync(Request(_book, ("title", "Dune (my copy)")), NoRefusals);

        Assert.True(result.Valid);
        Assert.True(result.DataSaved);
        Assert.Equal(1, OverrideCount(_book));
    }

    [Fact]
    public async Task ASeriesWithNothingUnderIt_IsNotTreatedAsASeries()
    {
        var alone = Guid.NewGuid();
        SeedWork(alone, "Books", "standalone");

        var result = await _service.RunAsync(Request(alone, ("title", "A lone book")), NoRefusals);

        Assert.True(result.DataSaved);
    }

    private static EditSessionRequest Request(Guid entity, params (string Key, string Value)[] fields) => new()
    {
        PreviewOnly = false,
        Changes =
        [
            new EditSessionChange
            {
                Id = "d1",
                Kind = EditSessionChangeKinds.Details,
                EntityId = entity,
                Fields = fields.Select(field => new EditSessionFieldChange { Key = field.Key, Value = field.Value }).ToList(),
            },
        ],
    };

    private void SeedWork(Guid id, string mediaType, string kind)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind) VALUES (@id, @mediaType, @kind);",
            new { id = GuidSql.ToBlob(id), mediaType, kind });
    }

    private long OverrideCount(Guid id)
    {
        using var connection = _db.CreateConnection();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM works WHERE id = @id AND display_overrides_json IS NOT NULL AND display_overrides_json NOT IN ('', '{}');",
            new { id = GuidSql.ToBlob(id) });
    }

    // The family structure the editor header reads: the Series is a root with the book under it. A book answers as a leaf with no members.
    private sealed class SeriesOfBooksNavigation(Guid series, Guid book) : IMediaEditorNavigationReadService
    {
        public Task<MediaEditorNavigatorEnvelope?> GetNavigatorAsync(Guid entityId, CancellationToken ct, bool structureOnly = false)
        {
            if (entityId == series)
            {
                var root = Node(series, null, "Dune Novels", isRoot: true, isLeaf: false);
                return Task.FromResult<MediaEditorNavigatorEnvelope?>(new MediaEditorNavigatorEnvelope(
                    true, "Books", series, series, "Series", "Dune Novels", null, [root]));
            }

            if (entityId == book)
            {
                var root = Node(series, null, "Dune Novels", isRoot: true, isLeaf: false);
                return Task.FromResult<MediaEditorNavigatorEnvelope?>(new MediaEditorNavigatorEnvelope(
                    true, "Books", series, book, "Series", "Dune Novels", null, [root, Node(book, series, "Dune", isRoot: false, isLeaf: true)]));
            }

            return Task.FromResult<MediaEditorNavigatorEnvelope?>(null);
        }

        private static MediaEditorNavigatorNodeEnvelope Node(Guid id, Guid? parent, string title, bool isRoot, bool isLeaf) => new(
            id, parent, id, string.Empty, isLeaf ? "book" : "series", string.Empty, title, null, null, parent is null ? 0 : 1, isRoot, isLeaf,
            true, null, null, [], true, true, false, 0);
    }
}
