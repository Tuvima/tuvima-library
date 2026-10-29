using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IHouseholdKidSettingsRepository"/>: four columns on the household row.</summary>
public sealed class HouseholdKidSettingsRepository(IDatabaseConnection database) : IHouseholdKidSettingsRepository
{
    public Task<HouseholdKidSettings?> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<SettingsRow>(
            """
            SELECT kid_watch_level AS WatchLevel, kid_read_level AS ReadLevel, kid_listen_level AS ListenLevel,
                   kid_show_unrated AS ShowUnrated
            FROM households WHERE id = @householdId LIMIT 1;
            """,
            new { householdId });
        return Task.FromResult(row is null
            ? null
            : new HouseholdKidSettings(row.WatchLevel, row.ReadLevel, row.ListenLevel, row.ShowUnrated));
    }

    public Task<bool> ReplaceAsync(Guid householdId, HouseholdKidSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                """
                UPDATE households
                SET kid_watch_level = @WatchLevel, kid_read_level = @ReadLevel, kid_listen_level = @ListenLevel,
                    kid_show_unrated = @ShowUnrated
                WHERE id = @householdId;
                """,
                new { householdId, settings.WatchLevel, settings.ReadLevel, settings.ListenLevel, settings.ShowUnrated },
                transaction) == 1;
        }, ct);
    }

    private sealed class SettingsRow
    {
        public string WatchLevel { get; set; } = string.Empty;
        public string ReadLevel { get; set; } = string.Empty;
        public string ListenLevel { get; set; } = string.Empty;
        public bool ShowUnrated { get; set; }
    }
}
