using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>What people removed from, or added to, their own taste. Real temp SQLite.</summary>
public sealed class TasteOverrideRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-taste-overrides-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly TasteOverrideRepository _repository;
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();

    public TasteOverrideRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new TasteOverrideRepository(_database);
        AddProfile(_first, "First");
        AddProfile(_second, "Second");
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ASetValue_IsListedForItsPersonOnly()
    {
        await _repository.SetAsync(_first, TasteGroups.Genre, "sci-fi", TasteOverrideActions.Removed, At);
        await _repository.SetAsync(_second, TasteGroups.Mood, "cozy", TasteOverrideActions.Added, At);

        var first = await _repository.ListAsync([_first]);
        var everyone = await _repository.ListAsync(null);

        var change = Assert.Single(first);
        Assert.Equal(new TasteOverride(_first, TasteGroups.Genre, "sci-fi", TasteOverrideActions.Removed), change);
        Assert.Equal(2, everyone.Count);
    }

    [Fact]
    public async Task SettingTheSameValueAgain_ReplacesTheEarlierChange()
    {
        await _repository.SetAsync(_first, TasteGroups.Era, "1990s", TasteOverrideActions.Removed, At);
        await _repository.SetAsync(_first, TasteGroups.Era, "1990s", TasteOverrideActions.Added, At.AddMinutes(1));

        var change = Assert.Single(await _repository.ListAsync([_first]));

        Assert.Equal(TasteOverrideActions.Added, change.Action);
    }

    [Fact]
    public async Task AskingForNoProfiles_ReturnsNothing()
    {
        await _repository.SetAsync(_first, TasteGroups.Genre, "drama", TasteOverrideActions.Added, At);

        Assert.Empty(await _repository.ListAsync([]));
    }

    [Fact]
    public async Task DeletingAProfile_RemovesItsChanges()
    {
        await _repository.SetAsync(_first, TasteGroups.Genre, "drama", TasteOverrideActions.Added, At);
        await _repository.SetAsync(_second, TasteGroups.Genre, "drama", TasteOverrideActions.Added, At);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @id;", new { id = _first });
        }

        var left = Assert.Single(await _repository.ListAsync(null));
        Assert.Equal(_second, left.ProfileId);
    }

    [Fact]
    public async Task AnUnknownGroupOrAction_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _repository.SetAsync(_first, "flavour", "sweet", TasteOverrideActions.Added, At));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _repository.SetAsync(_first, TasteGroups.Genre, "drama", "starred", At));
    }

    [Fact]
    public async Task KnownGenres_AreTheLibrarysMostCommonGenresInLowerCase()
    {
        var work = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES
                  (@work, 'genre', 0, 'Drama'), (@work, 'genre', 1, 'Comedy'),
                  (@other, 'genre', 0, 'drama'), (@other, 'author', 0, 'Someone');
                """, new { work, other });
        }

        var genres = await _repository.ListKnownGenresAsync(10);
        var limited = await _repository.ListKnownGenresAsync(1);

        Assert.Equal(["drama", "comedy"], genres);
        Assert.Equal(["drama"], limited);
    }

    private void AddProfile(Guid id, string name)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now);",
            new { id, name, now = At.ToString("O") });
    }
}
