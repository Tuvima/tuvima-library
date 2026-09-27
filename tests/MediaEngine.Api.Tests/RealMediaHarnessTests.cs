using MediaEngine.Api.DevSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class RealMediaHarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "real-harness-tests-" + Guid.NewGuid());
    public RealMediaHarnessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task VerificationDetectsBytesEvenWhenSizeAndTimestampArePreserved()
    {
        var path = Path.Combine(_root, "original.mkv");
        await File.WriteAllTextAsync(path, "original");
        var before = await RealMediaHarness.SnapshotAsync(_root, true);
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "modified");
        File.SetLastWriteTimeUtc(path, stamp);
        var after = await RealMediaHarness.SnapshotAsync(_root, true);
        Assert.Equal(new[] { "original.mkv" }, RealMediaHarness.Differences(before, after, true));
    }

    [Fact]
    public async Task VerificationDetectsRenameAdditionAndEmptyDirectoryRemoval()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "original.epub"), "content");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var before = await RealMediaHarness.SnapshotAsync(_root, true);
        File.Move(Path.Combine(_root, "original.epub"), Path.Combine(_root, "renamed.epub"));
        Directory.Delete(Path.Combine(_root, "empty"));
        var diffs = RealMediaHarness.Differences(before, await RealMediaHarness.SnapshotAsync(_root, true), true);
        Assert.Contains("original.epub", diffs);
        Assert.Contains("renamed.epub", diffs);
        Assert.Contains("empty", diffs);
    }

    [Fact]
    public void OutputCannotOverlapProtectedSourceInEitherDirection()
    {
        Assert.Throws<InvalidOperationException>(() => RealMediaHarness.RequireSeparate(_root, _root));
        Assert.Throws<InvalidOperationException>(() => RealMediaHarness.RequireSeparate(_root, Path.Combine(_root, "cache")));
        Assert.Throws<InvalidOperationException>(() => RealMediaHarness.RequireSeparate(_root, Path.GetDirectoryName(_root)!));
        RealMediaHarness.RequireSeparate(_root, _root + "-other");
    }

    [Fact]
    public void ResetClearsViewContentAndSourcesButPreservesAccountsAndStorageReservations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE accounts(id INTEGER); INSERT INTO accounts VALUES(1); CREATE TABLE view_storage_labels(id INTEGER); INSERT INTO view_storage_labels VALUES(1); CREATE TABLE view_sources(id INTEGER); INSERT INTO view_sources VALUES(1); CREATE TABLE local_items(id INTEGER); INSERT INTO local_items VALUES(1); CREATE VIRTUAL TABLE local_item_search USING fts5(title); INSERT INTO local_item_search VALUES('old synthetic'); CREATE TABLE profile_sequence_preferences(id INTEGER); INSERT INTO profile_sequence_preferences VALUES(1);";
        cmd.ExecuteNonQuery();
        RealMediaHarness.ResetDatabase(connection);
        foreach (var table in new[] { "view_sources", "local_items", "local_item_search", "profile_sequence_preferences" })
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        cmd.CommandText = "SELECT COUNT(*) FROM accounts";
        Assert.Equal(1L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM view_storage_labels";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void ReadOnlyFolderHealthDoesNotCreateProbeFilesOrChangeDirectoryTimestamp()
    {
        var before = Directory.GetLastWriteTimeUtc(_root);
        var health = MediaEngine.Api.Services.FolderHealthService.ProbePath(_root);
        Assert.True(health.IsAccessible);
        Assert.True(health.HasRead);
        Assert.False(health.HasWrite);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        Assert.Equal(before, Directory.GetLastWriteTimeUtc(_root));
    }

    [Fact]
    public void RealModeRejectsAnOldConfigurationEvenWithAValidHash()
    {
        Assert.Throws<InvalidOperationException>(() => RealMediaHarness.ValidateSources(
            new MediaEngine.Domain.Configuration.LibrariesConfiguration(), _root));
    }

    [Fact]
    public void RejectedCleanupCannotDeleteAnOriginalOutsideStaging()
    {
        var staging = Path.Combine(_root, ".data", "staging", "rejected");
        Assert.False(MediaEngine.Api.Services.RejectedFileCleanupService.IsRejectedStagingPath(Path.Combine(_root, "movie.mkv"), staging));
        Assert.False(MediaEngine.Api.Services.RejectedFileCleanupService.IsRejectedStagingPath(staging + "-other/movie.mkv", staging));
        Assert.True(MediaEngine.Api.Services.RejectedFileCleanupService.IsRejectedStagingPath(Path.Combine(staging, "movie.mkv"), staging));
    }

    [Fact]
    public void ResetSupportsTheActualDatabaseSchema()
    {
        using var database = new MediaEngine.Storage.DatabaseConnection(Path.Combine(_root, "library.db"));
        database.InitializeSchema();
        RealMediaHarness.ResetDatabase(database.Open());
        database.RunStartupChecks();
        SqliteConnection.ClearPool(database.Open());
    }

    [Fact]
    public void ResetRollsBackWhenPreservedRecordsWouldDangle()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE media(id INTEGER PRIMARY KEY); INSERT INTO media VALUES(1); CREATE TABLE accounts(id INTEGER REFERENCES media(id)); INSERT INTO accounts VALUES(1);";
        cmd.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => RealMediaHarness.ResetDatabase(connection));
        cmd.CommandText = "SELECT COUNT(*) FROM media";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
