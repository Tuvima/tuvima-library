using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Storage.Tests;

public sealed class ViewIntakeRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ViewIntakeRepository _repository;
    private readonly DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public ViewIntakeRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_intake_repo_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _repository = new ViewIntakeRepository(_database);
    }

    [Fact]
    public async Task AddingTheSamePathTwice_ReturnsTheRowAlreadyThere()
    {
        var first = await _repository.AddAsync(Row("/import/Photos/a.jpg"));
        var second = await _repository.AddAsync(Row("/IMPORT/photos/A.JPG"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _repository.CountWaitingAsync(null));
    }

    [Fact]
    public async Task APathThatWasFiled_CanBeTrackedAgainWhenANewFileArrivesThere()
    {
        var first = await _repository.AddAsync(Row("/import/Photos/a.jpg"));
        await _repository.UpdateAsync(first.Id, ViewIntakeStates.Filed, filedPath: "/library/2024/a.jpg");

        var again = await _repository.AddAsync(Row("/import/Photos/a.jpg"));

        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public async Task CountingWaitingFiles_CountsOnlyWaitingRowsOfThatKind()
    {
        await _repository.AddAsync(Row("/import/Photos/a.jpg"));
        await _repository.AddAsync(Row("/import/Photos/b.jpg"));
        await _repository.AddAsync(Row("/import/Personal videos/c.mp4", kind: ViewContentKinds.PersonalVideos));
        var filed = await _repository.AddAsync(Row("/import/Photos/d.jpg"));
        await _repository.UpdateAsync(filed.Id, ViewIntakeStates.Filed);

        Assert.Equal(3, await _repository.CountWaitingAsync(null));
        Assert.Equal(2, await _repository.CountWaitingAsync(ViewContentKinds.Photos));
        Assert.Equal(1, await _repository.CountWaitingAsync(ViewContentKinds.PersonalVideos));
    }

    [Fact]
    public async Task UpdatingARow_KeepsTheFieldsLeftOut_AndStoresTheReasonAndExpiry()
    {
        var row = await _repository.AddAsync(Row("/import/Photos/a.jpg"));

        await _repository.UpdateAsync(row.Id, ViewIntakeStates.Rejected, currentPath: "/import/.data/rejected/Photos/a.jpg",
            reasonCode: ViewIntakeReasons.Damaged, reasonText: "damaged or unreadable", expiresAt: _now.AddDays(30));
        var stored = (await _repository.GetAsync(row.Id))!;

        Assert.Equal(ViewIntakeStates.Rejected, stored.State);
        Assert.Equal("/import/.data/rejected/Photos/a.jpg", stored.CurrentPath);
        Assert.Equal("a.jpg", stored.OriginalName);
        Assert.Equal(_now.AddDays(30), stored.ExpiresAt);
        Assert.Equal(ViewIntakeReasons.Damaged, stored.ReasonCode);
    }

    [Fact]
    public async Task ExpiredRejectedRows_AreFoundOnlyOncePastTheirExpiry()
    {
        var soon = await _repository.AddAsync(Row("/r/a.jpg"));
        var later = await _repository.AddAsync(Row("/r/b.jpg"));
        await _repository.UpdateAsync(soon.Id, ViewIntakeStates.Rejected, expiresAt: _now.AddDays(1));
        await _repository.UpdateAsync(later.Id, ViewIntakeStates.Rejected, expiresAt: _now.AddDays(30));

        var expired = await _repository.GetExpiredAsync(_now.AddDays(2), 10);

        Assert.Equal(soon.Id, Assert.Single(expired).Id);
    }

    [Fact]
    public async Task AGroupIsReadBackTogether()
    {
        await _repository.AddAsync(Row("/import/Photos/x.nef", group: "x"));
        await _repository.AddAsync(Row("/import/Photos/x.jpg", group: "x"));
        await _repository.AddAsync(Row("/import/Photos/y.jpg", group: "y"));

        Assert.Equal(2, (await _repository.GetGroupAsync("x")).Count);
    }

    [Fact]
    public async Task ADismissedDuplicateGroup_StaysDismissedUntilItGrows()
    {
        var library = Guid.NewGuid();
        await _repository.UpsertDuplicateGroupAsync(library, "personal", "abc", 2, _now);
        var group = Assert.Single(await _repository.GetDuplicateGroupsAsync(null, library, includeDismissed: false));
        await _repository.DismissDuplicateGroupAsync(group.Id, Guid.NewGuid(), _now);

        await _repository.UpsertDuplicateGroupAsync(library, "personal", "abc", 2, _now.AddDays(1));
        Assert.Empty(await _repository.GetDuplicateGroupsAsync(null, library, includeDismissed: false));

        await _repository.UpsertDuplicateGroupAsync(library, "personal", "abc", 3, _now.AddDays(2));
        Assert.Equal(3, Assert.Single(await _repository.GetDuplicateGroupsAsync(null, library, includeDismissed: false)).CopyCount);
    }

    [Fact]
    public async Task GroupsTheLatestScanDidNotSee_AreRemoved()
    {
        var library = Guid.NewGuid();
        await _repository.UpsertDuplicateGroupAsync(library, "personal", "old", 2, _now);
        await _repository.UpsertDuplicateGroupAsync(library, "personal", "new", 2, _now.AddDays(1));

        var removed = await _repository.RemoveUnseenDuplicateGroupsAsync("personal", _now.AddDays(1));

        Assert.Equal(1, removed);
        Assert.Equal("new", Assert.Single(await _repository.GetDuplicateGroupsAsync(null, library, includeDismissed: true)).ContentHash);
    }

    private ViewIntakeFile Row(string path, string kind = ViewContentKinds.Photos, string? group = null) => new(
        Guid.NewGuid(), kind, "personal", null, null, Path.GetFileName(path), path, null, null, group, null, 10, null,
        ViewIntakeStates.Waiting, null, null, _now, _now, null);

    public void Dispose()
    {
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
