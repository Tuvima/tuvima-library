using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A data store built at the step before "view_shared_history_survives_item_removal", holding contribution and transfer history
/// that points at personal items, upgrades through that step: every row and every link comes through with identical values, and the
/// item link no longer blocks removing an item (it is cleared instead, so the history row stays).
/// </summary>
public sealed class ViewSharedHistoryUpgradeTests : IDisposable
{
    private const string StepName = "view_shared_history_survives_item_removal";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_shared_history_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    private readonly Guid _profile = Guid.NewGuid();
    private readonly Guid _space = Guid.NewGuid();
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _itemA = Guid.NewGuid();
    private readonly Guid _itemB = Guid.NewGuid();

    public ViewSharedHistoryUpgradeTests()
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
    public void ContributionAndTransferHistory_ComesThroughTheUpgradeWithIdenticalValuesAndLinks()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());
        SeedHistory();
        var itemsBefore = Rows("""
            SELECT hex(id), hex(contribution_id), hex(item_id), hex(original_profile_id), original_profile_name, position, operation,
                   source_manifest_json, execution_state, error, updated_at
              FROM view_shared_contribution_items ORDER BY position;
            """);
        var transfersBefore = Rows("""
            SELECT hex(id), hex(item_id), hex(contribution_item_id), operation, state, source_manifest_json,
                   destination_manifest_json, error, created_at, updated_at, completed_at
              FROM view_shared_transfers ORDER BY created_at;
            """);
        Assert.Equal(2, itemsBefore.Count);
        Assert.Equal(2, transfersBefore.Count);

        var result = _runner.Run(_connection, all, Options());

        Assert.Contains(result.Applied, applied => applied.Number == step.Number);
        Assert.Equal(itemsBefore, Rows("""
            SELECT hex(id), hex(contribution_id), hex(item_id), hex(original_profile_id), original_profile_name, position, operation,
                   source_manifest_json, execution_state, error, updated_at
              FROM view_shared_contribution_items ORDER BY position;
            """));
        Assert.Equal(transfersBefore, Rows("""
            SELECT hex(id), hex(item_id), hex(contribution_item_id), operation, state, source_manifest_json,
                   destination_manifest_json, error, created_at, updated_at, completed_at
              FROM view_shared_transfers ORDER BY created_at;
            """));
        // The transfer -> contribution link survived the rebuild (dropping the old table must not have cleared it).
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM view_shared_transfers WHERE contribution_item_id IS NOT NULL;"));
        Assert.Empty(Query<string>("SELECT \"table\" FROM pragma_foreign_key_check;"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM view_shared_contributions WHERE status = 'accepted';"));
        Assert.Contains("idx_view_shared_contribution_items_batch", Query<string>("SELECT name FROM sqlite_master WHERE type = 'index';"));
    }

    [Fact]
    public void AfterTheUpgrade_RemovingAnItemKeepsItsHistoryRowsWithNoItemLink()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());
        SeedHistory();

        // Before the step the link blocks removing the item.
        Assert.Throws<SqliteException>(() => Execute($"DELETE FROM local_items WHERE id = {Blob(_itemA)};"));

        _runner.Run(_connection, all, Options());
        Execute($"DELETE FROM local_items WHERE id = {Blob(_itemA)};");

        Assert.Equal(0, Scalar<long>($"SELECT COUNT(*) FROM local_items WHERE id = {Blob(_itemA)};"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM view_shared_contribution_items WHERE item_id IS NULL AND execution_state = 'completed';"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM view_shared_transfers WHERE item_id IS NULL AND state = 'completed';"));
        // The link between the two history rows is untouched by removing the item.
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM view_shared_transfers WHERE contribution_item_id IS NOT NULL;"));
        // The other item's history is untouched.
        Assert.Equal(1, Scalar<long>($"SELECT COUNT(*) FROM view_shared_contribution_items WHERE item_id = {Blob(_itemB)};"));
    }

    private void SeedHistory()
    {
        var contribution = Guid.NewGuid();
        var contributionItemA = Guid.NewGuid();
        var contributionItemB = Guid.NewGuid();
        Execute($$"""
            INSERT INTO profiles (id, display_name, created_at) VALUES ({{Blob(_profile)}}, 'Person', '2026-10-01T00:00:00Z');
            INSERT INTO view_personal_spaces (id, owner_profile_id, library_id, created_at, updated_at)
                VALUES ({{Blob(_space)}}, {{Blob(_profile)}}, {{Blob(_library)}}, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO local_items (id, scope_kind, personal_space_id, owner_profile_id, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES ({{Blob(_itemA)}}, 'personal', {{Blob(_space)}}, {{Blob(_profile)}}, {{Blob(_library)}}, 'image', 'a.jpg', 'image/jpeg', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z'),
                       ({{Blob(_itemB)}}, 'personal', {{Blob(_space)}}, {{Blob(_profile)}}, {{Blob(_library)}}, 'image', 'b.jpg', 'image/jpeg', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
            INSERT INTO view_shared_contributions (id, submitted_by_profile_id, submitted_by_name, status, destination_kind, idempotency_key, submitted_at, updated_at)
                VALUES ({{Blob(contribution)}}, {{Blob(_profile)}}, 'Person', 'accepted', 'timeline', 'key-1', '2026-10-02T00:00:00Z', '2026-10-02T00:00:00Z');
            INSERT INTO view_shared_contribution_items
                (id, contribution_id, item_id, original_profile_id, original_profile_name, position, operation, source_manifest_json, execution_state, error, updated_at)
                VALUES ({{Blob(contributionItemA)}}, {{Blob(contribution)}}, {{Blob(_itemA)}}, {{Blob(_profile)}}, 'Person', 0, 'copy', '{"files":["a"]}', 'completed', NULL, '2026-10-03T00:00:00Z'),
                       ({{Blob(contributionItemB)}}, {{Blob(contribution)}}, {{Blob(_itemB)}}, {{Blob(_profile)}}, 'Person', 1, 'move', '[]', 'needs_attention', 'It could not be copied.', '2026-10-04T00:00:00Z');
            INSERT INTO view_shared_transfers
                (id, item_id, contribution_item_id, operation, state, source_manifest_json, destination_manifest_json, error, created_at, updated_at, completed_at)
                VALUES ({{Blob(Guid.NewGuid())}}, {{Blob(_itemA)}}, {{Blob(contributionItemA)}}, 'copy', 'completed', '{"files":["a"]}', '{"files":["b"]}', NULL, '2026-10-02T01:00:00Z', '2026-10-03T00:00:00Z', '2026-10-03T00:00:00Z'),
                       ({{Blob(Guid.NewGuid())}}, {{Blob(_itemB)}}, NULL, 'move', 'failed', '[]', NULL, 'Stopped.', '2026-10-02T02:00:00Z', '2026-10-04T00:00:00Z', NULL);
            """);
    }

    private static string Blob(Guid id) => $"X'{Convert.ToHexString(id.ToByteArray())}'";

    private static DataStoreUpgradeOptions Options()
        => new() { Clock = TimeProvider.System, EngineVersion = "test" };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql)
    {
        var rows = new List<T>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((T)reader.GetValue(0));
        }

        return rows;
    }

    /// <summary>Every column of every row as text, so two reads can be compared for identical values.</summary>
    private List<string> Rows(string sql)
    {
        var rows = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }
}
