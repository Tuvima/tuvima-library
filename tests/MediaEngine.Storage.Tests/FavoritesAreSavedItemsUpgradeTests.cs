using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A data store built at the step before "favorites_are_saved_items", holding Love, Like and Dislike reactions, upgrades through that
/// step once: every Love becomes a saved item and leaves no reaction, Like and Dislike stay as they were, and Love can no longer be stored.
/// </summary>
public sealed class FavoritesAreSavedItemsUpgradeTests : IDisposable
{
    private const string StepName = "favorites_are_saved_items";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_favorites_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    public FavoritesAreSavedItemsUpgradeTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
    }

    public void Dispose()
    {
        _connection.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public void ALovedSong_BecomesASavedItemDatedWhenItWasLoved_AndLeavesNoReaction()
    {
        var profile = Guid.NewGuid();
        var loved = Guid.NewGuid();
        var step = UpgradeUpToButNotIncludingTheStep();
        InsertProfile(profile);
        Execute($"""
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES ({Blob(profile)}, 'Song', {Blob(loved)}, 'Love', '2026-10-01T08:00:00Z');
            """);

        var result = _runner.Run(_connection, DataStoreUpgradeCatalog.ForProduct(), Options());

        Assert.Contains(result.Applied, applied => applied.Number == step.Number);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM profile_reactions;"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM profile_saved_items WHERE profile_id = {Blob(profile)} AND entity_kind = 'Song' AND entity_id = {Blob(loved)} AND saved_at = '2026-10-01T08:00:00Z';"));
    }

    [Fact]
    public void ALovedTitleAlreadyOnTheList_KeepsItsOriginalSavedRow()
    {
        var profile = Guid.NewGuid();
        var movie = Guid.NewGuid();
        UpgradeUpToButNotIncludingTheStep();
        InsertProfile(profile);
        Execute($"""
            INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at, position)
            VALUES ({Blob(profile)}, 'Movie', {Blob(movie)}, '2026-09-01T00:00:00Z', 3);
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES ({Blob(profile)}, 'Movie', {Blob(movie)}, 'Love', '2026-10-01T00:00:00Z');
            """);

        _runner.Run(_connection, DataStoreUpgradeCatalog.ForProduct(), Options());

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM profile_saved_items;"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM profile_saved_items WHERE saved_at = '2026-09-01T00:00:00Z' AND position = 3;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM profile_reactions;"));
    }

    [Fact]
    public void LikeAndDislikeRows_AreLeftUntouched_AndNothingIsSaved()
    {
        var profile = Guid.NewGuid();
        var liked = Guid.NewGuid();
        var disliked = Guid.NewGuid();
        UpgradeUpToButNotIncludingTheStep();
        InsertProfile(profile);
        Execute($"""
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES ({Blob(profile)}, 'Movie', {Blob(liked)}, 'Like', '2026-10-02T00:00:00Z'),
                   ({Blob(profile)}, 'Song', {Blob(disliked)}, 'Dislike', '2026-10-03T00:00:00Z');
            """);

        _runner.Run(_connection, DataStoreUpgradeCatalog.ForProduct(), Options());

        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM profile_reactions;"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM profile_reactions WHERE entity_id = {Blob(liked)} AND reaction = 'Like' AND updated_at = '2026-10-02T00:00:00Z';"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM profile_reactions WHERE entity_id = {Blob(disliked)} AND reaction = 'Dislike' AND updated_at = '2026-10-03T00:00:00Z';"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM profile_saved_items;"));
    }

    [Fact]
    public void AfterTheUpgrade_StoringLove_FailsTheCheck_AndLikeAndDislikeStillWork()
    {
        var profile = Guid.NewGuid();
        _runner.Run(_connection, DataStoreUpgradeCatalog.ForProduct(), Options());
        InsertProfile(profile);

        Assert.Throws<SqliteException>(() => Execute($"""
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES ({Blob(profile)}, 'Song', {Blob(Guid.NewGuid())}, 'Love', '2026-10-01T00:00:00Z');
            """));
        Execute($"""
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES ({Blob(profile)}, 'Song', {Blob(Guid.NewGuid())}, 'Like', '2026-10-01T00:00:00Z'),
                   ({Blob(profile)}, 'Song', {Blob(Guid.NewGuid())}, 'Dislike', '2026-10-01T00:00:00Z');
            """);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_profile_reactions_recent';"));
    }

    private DataStoreUpgradeStep UpgradeUpToButNotIncludingTheStep()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());
        return step;
    }

    private void InsertProfile(Guid id)
        => Execute($"INSERT INTO profiles (id, display_name, role, created_at) VALUES ({Blob(id)}, 'Test', 'Administrator', '2026-01-01T00:00:00Z');");

    private static string Blob(Guid id) => $"X'{Convert.ToHexString(id.ToByteArray())}'";

    private static DataStoreUpgradeOptions Options()
        => new() { Clock = TimeProvider.System, EngineVersion = "test" };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
