using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// The editor's Bookmarks tab on the data store (universal editor v5 D37, WP15): until an administrator changes a title's list, Everyone's
// bookmarks are every profile's bookmarks pooled; the first change makes the list a definitive set (the bookmarks flagged for Everyone);
// "Start over from profiles" pools again; and the staged operations are written in one transaction that can be put back exactly.
public sealed class EditorBookmarkRepositoryTests : IDisposable
{
    private const double SameSpot = 3;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_editor_bookmarks_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly EditorBookmarkRepository _editor;
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _ada = Guid.NewGuid();
    private readonly Guid _ben = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();

    public EditorBookmarkRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _bookmarks = new AudiobookBookmarkRepository(_database);
        _editor = new EditorBookmarkRepository(_database);
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).ToString("O");
        var editionId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',@now);
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@ada,'Ada','#7C4DFF','StandardUser',@now);
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@ben,'Ben','#7C4DFF','StandardUser',@now);
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, 'hash-editor-bookmarks', 'C:/videos/film.mkv', CURRENT_TIMESTAMP, @libraryId);
            """,
            new { admin = _admin, ada = _ada, ben = _ben, workId = _workId, editionId, assetId = _assetId, now, libraryId = Guid.NewGuid().ToString("D") });
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private Task<AudiobookBookmarkDto> SaveAsync(Guid profile, double seconds, string? note = null) =>
        _bookmarks.CreateAsync(profile, _workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = _assetId,
            PositionSeconds = seconds,
            DurationSeconds = 7200,
            Note = note,
        });

    private static double SameSpotFor(string kind) => SameSpot;

    private static EditorBookmarkOp Op(string kind, Guid? bookmark = null, string? name = null, double? seconds = null, IReadOnlyList<EditorBookmarkImportItem>? items = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Kind = kind, BookmarkId = bookmark, Name = name, PositionSeconds = seconds, Items = items ?? [] };

    private Task<EditorBookmarkCommitOutcome> CommitAsync(params EditorBookmarkOp[] ops) =>
        _editor.CommitAsync(_workId, _admin, _assetId, ops, SameSpotFor, null);

    private async Task<IReadOnlyList<EditorBookmarkRow>> EveryoneAsync() => await _editor.GetEveryoneAsync(_workId, null, SameSpotFor);

    [Fact]
    public async Task ATitleNobodyHasCurated_PoolsEveryProfilesBookmarks_AndListsTwoAtTheSameSpotOnce()
    {
        await SaveAsync(_ada, 750, "The reveal");
        await SaveAsync(_ben, 751);
        await SaveAsync(_ben, 2400, "The chase");

        var everyone = await EveryoneAsync();

        Assert.Equal(2, everyone.Count);
        Assert.Equal("The reveal", everyone[0].Name);
        Assert.Equal("The chase", everyone[1].Name);
        Assert.Empty(await _editor.GetDefinitiveAsync([_workId]));
    }

    [Fact]
    public async Task TheFirstChange_MakesTheSetDefinitive_AndABookmarkMadeLaterByAProfileIsNotInEveryones()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        var chase = await SaveAsync(_ben, 2400, "The chase");

        await CommitAsync(Op(EditorBookmarkOpKinds.Add, opening.Id));
        var later = await SaveAsync(_ben, 5000, "Made later");

        var everyone = await EveryoneAsync();
        Assert.Equal([opening.Id, chase.Id], everyone.Select(row => row.Id));
        Assert.DoesNotContain(everyone, row => row.Id == later.Id);
        Assert.Contains(_workId, await _editor.GetDefinitiveAsync([_workId]));
    }

    [Fact]
    public async Task TheFirstChange_TurnsThePoolIntoTheSet_WithoutCopyingOrDeletingAnything()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        var chase = await SaveAsync(_ben, 2400, "The chase");

        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.Remove, chase.Id));

        Assert.Empty(outcome.DeletedIds);
        var all = await _editor.GetByWorksAsync([_workId], null);
        Assert.Equal(2, all.Count);
        Assert.Empty(await EveryoneAsync().ContinueWith(task => task.Result.Where(row => row.Id == chase.Id)));
        Assert.Contains(await EveryoneAsync(), row => row.Id == opening.Id);
    }

    [Fact]
    public async Task AddingToEveryones_MergesTheSameSpot_SoABookmarkInEveryonesStepsOutAndAPersonalOneIsReplaced()
    {
        var standing = await SaveAsync(_ada, 724, "Opening");
        var elsewhere = await SaveAsync(_ben, 2400, "The chase");
        await CommitAsync(Op(EditorBookmarkOpKinds.Remove, elsewhere.Id));
        var chosen = await SaveAsync(_ben, 726, "Cold open");
        var personal = await SaveAsync(_ada, 727);
        var far = await SaveAsync(_ada, 3000, "Far away");

        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.Add, chosen.Id));

        var replaced = Assert.Single(outcome.DeletedIds);
        Assert.Equal(personal.Id, replaced.Id);
        var left = await _editor.GetByWorksAsync([_workId], null);
        Assert.Contains(left, row => row.Id == standing.Id && !row.IsGlobal);
        Assert.Contains(left, row => row.Id == far.Id);
        Assert.DoesNotContain(left, row => row.Id == personal.Id);
        Assert.Equal(chosen.Id, Assert.Single(await EveryoneAsync()).Id);
    }

    [Fact]
    public async Task RemovingFromEveryones_LeavesTheBookmarkWithItsOwner()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        await CommitAsync(Op(EditorBookmarkOpKinds.Add, opening.Id));

        await CommitAsync(Op(EditorBookmarkOpKinds.Remove, opening.Id));

        Assert.Empty(await EveryoneAsync());
        var mine = await _bookmarks.GetByWorkAsync(_ada, _workId);
        Assert.Equal(opening.Id, Assert.Single(mine).Id);
        Assert.False(Assert.Single(mine).IsGlobal);
    }

    [Fact]
    public async Task Renaming_ChangesTheNameEveryoneSees_ButOnlyForABookmarkInEveryones()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        var chase = await SaveAsync(_ben, 2400, "The chase");
        await CommitAsync(Op(EditorBookmarkOpKinds.Remove, chase.Id));

        await CommitAsync(Op(EditorBookmarkOpKinds.Rename, opening.Id, name: "  Cold open  "));
        var refused = await Assert.ThrowsAsync<EditorBookmarkCommitException>(() => CommitAsync(Op(EditorBookmarkOpKinds.Rename, chase.Id, name: "Nope")));

        Assert.Equal("Cold open", Assert.Single(await EveryoneAsync()).Name);
        Assert.Contains("not in Everyone's", refused.Message);
    }

    [Fact]
    public async Task ChangingTheTime_MovesTheBookmark_AndSaysWhichPictureToMakeAgain()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        await CommitAsync(Op(EditorBookmarkOpKinds.Add, opening.Id));

        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.ChangeTime, opening.Id, seconds: 1800));

        var moved = Assert.Single(outcome.TimeChanged);
        Assert.Equal(opening.Id, moved.Id);
        Assert.Equal(1800, moved.PositionSeconds);
        Assert.Equal(1800, Assert.Single(await EveryoneAsync()).PositionSeconds);
    }

    [Fact]
    public async Task ChangingTheTimeToAPlaceOutsideTheFile_IsRefused_AndNothingIsWritten()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");
        var chase = await SaveAsync(_ben, 2400, "The chase");

        await Assert.ThrowsAsync<EditorBookmarkCommitException>(() => CommitAsync(
            Op(EditorBookmarkOpKinds.Add, opening.Id),
            Op(EditorBookmarkOpKinds.ChangeTime, opening.Id, seconds: 99999)));

        Assert.Empty(await _editor.GetDefinitiveAsync([_workId]));
        var all = await _editor.GetByWorksAsync([_workId], null);
        Assert.Equal(2, all.Count);
        Assert.All(all, row => Assert.False(row.IsGlobal));
        Assert.Contains(all, row => row.Id == chase.Id);
    }

    [Fact]
    public async Task AnImport_ThatNamesABookmarkItReplaces_TakesItOutOfEveryones_ButItStaysWithItsOwner()
    {
        var chase = await SaveAsync(_ben, 2400, "The chase");
        await CommitAsync(Op(EditorBookmarkOpKinds.Add, chase.Id));

        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.Import, items:
        [
            new EditorBookmarkImportItem { PositionSeconds = 100, Name = "Titles" },
            new EditorBookmarkImportItem { PositionSeconds = 2410, Name = "The big chase", ReplacesBookmarkId = chase.Id },
        ]));

        Assert.Equal(2, outcome.AddedIds.Count);
        var everyone = await EveryoneAsync();
        Assert.Equal(["Titles", "The big chase"], everyone.Select(row => row.Name));
        Assert.DoesNotContain(everyone, row => row.Id == chase.Id);
        Assert.Contains(await _bookmarks.GetByWorkAsync(_ben, _workId), row => row.Id == chase.Id && !row.IsGlobal);
    }

    [Fact]
    public async Task AnImport_AtTheSameSpotAsABookmarkInEveryones_MergesIt_WithoutBeingToldToReplaceIt()
    {
        // Within three seconds is the same spot: the imported bookmark takes the place of the one already in Everyone's, which stays with its
        // owner, and of another profile's personal one, which is deleted.
        var standing = await SaveAsync(_ada, 724, "Opening");
        await CommitAsync(Op(EditorBookmarkOpKinds.Add, standing.Id));
        var personal = await SaveAsync(_ben, 729, "Mine");

        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.Import, items: [new EditorBookmarkImportItem { PositionSeconds = 726, Name = "Cold open" }]));

        var everyone = await EveryoneAsync();
        Assert.Equal(["Cold open"], everyone.Select(row => row.Name));
        Assert.Contains(await _bookmarks.GetByWorkAsync(_ada, _workId), row => row.Id == standing.Id && !row.IsGlobal);
        Assert.DoesNotContain(await _bookmarks.GetByWorkAsync(_ben, _workId), row => row.Id == personal.Id);
        Assert.Contains(outcome.DeletedIds, deleted => deleted.Id == personal.Id);
    }

    [Fact]
    public async Task AnImport_JustPastTheSameSpot_KeepsBothBookmarks()
    {
        var standing = await SaveAsync(_ada, 724, "Opening");
        await CommitAsync(Op(EditorBookmarkOpKinds.Add, standing.Id));

        await CommitAsync(Op(EditorBookmarkOpKinds.Import, items: [new EditorBookmarkImportItem { PositionSeconds = 727.5, Name = "Later" }]));

        Assert.Equal(["Opening", "Later"], (await EveryoneAsync()).Select(row => row.Name));
    }

    [Fact]
    public async Task AnImportedBookFromAFile_KeepsItsLocationAndChapter_AndAnotherAtTheSameLocationIsMerged()
    {
        const string place = "epubcfi(/6/8!/4/2/6/1:0)";
        var outcome = await CommitAsync(Op(EditorBookmarkOpKinds.Import, items:
        [
            new EditorBookmarkImportItem { PositionSeconds = 0, Locator = place, ChapterIndex = 3, Name = "The turn", ChapterTitle = "Chapter 4" },
            new EditorBookmarkImportItem { PositionSeconds = 0, Locator = place, ChapterIndex = 3, Name = "The same turn" },
        ]));

        var everyone = await EveryoneAsync();
        var kept = Assert.Single(everyone);
        Assert.Equal("The same turn", kept.Name);
        Assert.Equal(BookmarkPooling.TextKind, kept.PositionKind);
        Assert.Equal(place, kept.Locator);
        Assert.Equal(3, kept.ChapterIndex);
        // A bookmark in a book has no picture to make.
        Assert.Empty(outcome.AddedIds);
    }

    [Fact]
    public async Task AnImportedBookmark_WithALocationTheReaderCannotRead_RefusesTheWholeSave()
    {
        await Assert.ThrowsAsync<EditorBookmarkCommitException>(() => CommitAsync(Op(EditorBookmarkOpKinds.Import, items:
        [
            new EditorBookmarkImportItem { PositionSeconds = 0, Locator = "not a location", Name = "Broken" },
        ])));

        Assert.Empty(await _editor.GetDefinitiveAsync([_workId]));
    }

    [Fact]
    public async Task StartingOver_ClearsTheDefinitiveSet_SoEveryoneIsPooledFromTheProfilesAgain()
    {
        await SaveAsync(_ada, 724, "Opening");
        var chase = await SaveAsync(_ben, 2400, "The chase");
        await CommitAsync(Op(EditorBookmarkOpKinds.Remove, chase.Id));
        Assert.Single(await EveryoneAsync());

        await CommitAsync(Op(EditorBookmarkOpKinds.StartOver));

        Assert.Empty(await _editor.GetDefinitiveAsync([_workId]));
        Assert.Equal(2, (await EveryoneAsync()).Count);
    }

    [Fact]
    public async Task PuttingBackASave_RestoresTheBookmarksTheCommitReplacedAndAdded_AndTheDefinitiveFlag()
    {
        var ada = await SaveAsync(_ada, 724, "Opening");
        var ben = await SaveAsync(_ben, 726);
        var before = await _editor.GetByWorksAsync([_workId], null);

        var outcome = await CommitAsync(
            Op(EditorBookmarkOpKinds.Add, ada.Id),
            Op(EditorBookmarkOpKinds.Import, items: [new EditorBookmarkImportItem { PositionSeconds = 4000, Name = "Added" }]));
        await _editor.RestoreAsync(outcome.Snapshot, [.. outcome.AddedIds.Select(added => added.Id)]);

        var after = await _editor.GetByWorksAsync([_workId], null);
        Assert.Equal(before.Select(row => (row.Id, row.IsGlobal, row.PositionSeconds)).Order(), after.Select(row => (row.Id, row.IsGlobal, row.PositionSeconds)).Order());
        Assert.Contains(after, row => row.Id == ben.Id);
        Assert.Empty(await _editor.GetDefinitiveAsync([_workId]));
    }

    [Fact]
    public async Task ABookmarkOnAFileTheCallerCannotSee_IsNeitherListedNorChanged()
    {
        var opening = await SaveAsync(_ada, 724, "Opening");

        var listed = await _editor.GetByWorksAsync([_workId], new HashSet<Guid>());
        var refused = await Assert.ThrowsAsync<EditorBookmarkCommitException>(() =>
            _editor.CommitAsync(_workId, _admin, _assetId, [Op(EditorBookmarkOpKinds.Add, opening.Id)], SameSpotFor, new HashSet<Guid>()));

        Assert.Empty(listed);
        Assert.Contains("no longer there", refused.Message);
    }
}
