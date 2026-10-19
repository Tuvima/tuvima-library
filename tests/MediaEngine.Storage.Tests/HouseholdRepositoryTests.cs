using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

public sealed class HouseholdRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly HouseholdRepository _households;
    private readonly DateTimeOffset _now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    public HouseholdRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_household_repo_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _households = new HouseholdRepository(_database);
    }

    [Fact]
    public async Task ANewHousehold_IsEmpty_RenamesAndListsByName_WithItsCounts()
    {
        var lake = await _households.CreateAsync("  Lake house ", _now);
        var attic = await _households.CreateAsync("Attic", _now);
        InsertProfile(lake.Id);
        InsertProfile(lake.Id);

        Assert.Equal("Lake house", lake.Name);
        Assert.True(await _households.RenameAsync(lake.Id, "Cabin"));
        Assert.False(await _households.RenameAsync(Guid.NewGuid(), "Nowhere"));

        var summaries = await _households.ListSummariesAsync();
        Assert.Equal(["Attic", "Cabin"], summaries.Select(summary => summary.Household.Name));
        Assert.True(summaries[0].IsEmpty);
        Assert.Equal(2, summaries[1].ProfileCount);
        Assert.Equal(0, summaries[1].AccountCount);
        Assert.Equal(attic.Id, (await _households.GetSummaryAsync(attic.Id))!.Household.Id);
        Assert.Null(await _households.GetSummaryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AnEmptyHousehold_IsRemoved_AndOneWithAPersonIsLeftExactlyAsItWas()
    {
        var empty = await _households.CreateAsync("Empty", _now);
        var occupied = await _households.CreateAsync("Occupied", _now);
        var personId = InsertProfile(occupied.Id);

        Assert.Equal(HouseholdDeleteOutcome.Deleted, await _households.DeleteWhenEmptyAsync(empty.Id));
        Assert.Equal(HouseholdDeleteOutcome.NotEmpty, await _households.DeleteWhenEmptyAsync(occupied.Id));
        Assert.Equal(HouseholdDeleteOutcome.NotFound, await _households.DeleteWhenEmptyAsync(empty.Id));

        Assert.Null(await _households.GetByIdAsync(empty.Id));
        Assert.NotNull(await _households.GetByIdAsync(occupied.Id));
        Assert.Equal(personId, Assert.Single(await _households.ListProfilesAsync(occupied.Id)).Id);
    }

    [Fact]
    public async Task AHouseholdWithAnEmptySharedLibraryRow_IsKeptUntilTheDataStoreLetsTheRowGo()
    {
        var shared = await new ViewSharedLibraryRepository(_database).GetAsync();

        Assert.Equal(HouseholdDeleteOutcome.HoldsSharedLibrary, await _households.DeleteWhenEmptyAsync(shared.HouseholdId));

        Assert.NotNull(await _households.GetByIdAsync(shared.HouseholdId));
    }

    [Fact]
    public async Task AHouseholdWhoseSharedLibraryStillHoldsPhotos_IsKeptWithEverythingInIt()
    {
        var shared = await new ViewSharedLibraryRepository(_database).GetAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@id, 'shared', @library, 'image', 'beach.jpg', 'image/jpeg', @now, @now);
                """, new { id = Guid.NewGuid(), library = shared.LibraryId, now = _now.ToString("O") });
        }

        Assert.Equal(HouseholdDeleteOutcome.HoldsSharedItems, await _households.DeleteWhenEmptyAsync(shared.HouseholdId));

        Assert.NotNull(await _households.GetByIdAsync(shared.HouseholdId));
        using var check = _database.CreateConnection();
        Assert.Equal(1, check.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM view_shared_library WHERE household_id = @id;", new { id = shared.HouseholdId }));
    }

    [Fact]
    public async Task TheAuditLookup_FindsTheSameEventForTheSamePersonAndSubjectInsideTheWindowOnly()
    {
        var accounts = new AccountRepository(_database);
        var profileId = Guid.NewGuid();
        await accounts.WriteAuthorizationAuditAsync(new AuthorizationAuditEvent(
            "activity.viewed_others", _now, null, null, null, "household", "h-1", new Dictionary<string, string?>()));

        Assert.True(await accounts.HasAuthorizationAuditSinceAsync("activity.viewed_others", null, "household", "h-1", _now.AddMinutes(-1)));
        Assert.False(await accounts.HasAuthorizationAuditSinceAsync("activity.viewed_others", null, "household", "h-1", _now.AddMinutes(1)));
        Assert.False(await accounts.HasAuthorizationAuditSinceAsync("taste.viewed_others", null, "household", "h-1", _now.AddMinutes(-1)));
        Assert.False(await accounts.HasAuthorizationAuditSinceAsync("activity.viewed_others", null, "household", "h-2", _now.AddMinutes(-1)));
        Assert.False(await accounts.HasAuthorizationAuditSinceAsync("activity.viewed_others", profileId, "household", "h-1", _now.AddMinutes(-1)));
    }

    public void Dispose()
    {
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private Guid InsertProfile(Guid householdId)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, role, created_at, household_id)
            VALUES (@id, 'Someone', 'StandardUser', @now, @householdId);
            """, new { id, householdId, now = _now.ToString("O") });
        return id;
    }
}
