using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Storage.Tests;

public sealed class HouseholdLibraryAccessRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly HouseholdRepository _households;
    private readonly HouseholdLibraryAccessRepository _repository;
    private readonly DateTimeOffset _now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public HouseholdLibraryAccessRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_household_libraries_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _households = new HouseholdRepository(_database);
        _repository = new HouseholdLibraryAccessRepository(_database);
    }

    [Fact]
    public async Task ANewHousehold_HidesNothing_AndASavedListIsReadBackForItsProfilesOnly()
    {
        var hidden = Guid.NewGuid();
        var household = await _households.CreateAsync("Household 2", _now);
        var other = await _households.CreateAsync("Household 1", _now);
        var inside = InsertProfile(household.Id);
        var outside = InsertProfile(other.Id);
        Assert.Empty(await _repository.GetHiddenLibrariesAsync(household.Id));

        Assert.True(await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { hidden }, _now, inside));

        Assert.Equal([hidden], (await _repository.GetHiddenLibrariesAsync(household.Id)).ToArray());
        Assert.Equal([hidden], (await _repository.GetHiddenLibrariesForProfileAsync(inside)).ToArray());
        Assert.Empty(await _repository.GetHiddenLibrariesForProfileAsync(outside));
        Assert.Empty(await _repository.GetHiddenLibrariesAsync(other.Id));
    }

    [Fact]
    public async Task ASavedList_ReplacesTheOldOne_AndAnEmptyListShowsEverythingAgain()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var household = await _households.CreateAsync("Replacing", _now);

        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { first }, _now, null);
        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { second }, _now, null);
        Assert.Equal([second], (await _repository.GetHiddenLibrariesAsync(household.Id)).ToArray());

        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid>(), _now, null);
        Assert.Empty(await _repository.GetHiddenLibrariesAsync(household.Id));
    }

    [Fact]
    public async Task AVaultLibrary_IsRefusedAndNothingIsSaved()
    {
        var vault = Guid.NewGuid();
        var ordinary = Guid.NewGuid();
        var household = await _households.CreateAsync("Vault refusal", _now);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { ordinary }, _now, null);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { ordinary, vault }, _now, null));

        Assert.Equal([ordinary], (await _repository.GetHiddenLibrariesAsync(household.Id)).ToArray());
    }

    [Fact]
    public async Task AnUnknownHousehold_IsNotFound()
    {
        Assert.False(await _repository.ReplaceHiddenLibrariesAsync(Guid.NewGuid(), new HashSet<Guid> { Guid.NewGuid() }, _now, null));
    }

    [Fact]
    public async Task ARealChange_RaisesTheAuthorizationVersionOfEveryProfileInTheHousehold_ButTheSameListAgainDoesNot()
    {
        var library = Guid.NewGuid();
        var household = await _households.CreateAsync("Versions", _now);
        var other = await _households.CreateAsync("Untouched", _now);
        var first = InsertProfile(household.Id);
        var second = InsertProfile(household.Id);
        var outside = InsertProfile(other.Id);
        var outsideBefore = VersionOf(outside);

        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { library }, _now, null);
        var afterFirstSave = (VersionOf(first), VersionOf(second));
        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { library }, _now, null);

        Assert.Equal(afterFirstSave, (VersionOf(first), VersionOf(second)));
        Assert.Equal(outsideBefore, VersionOf(outside));
        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid>(), _now, null);
        Assert.True(VersionOf(first) > afterFirstSave.Item1);
        Assert.True(VersionOf(second) > afterFirstSave.Item2);
    }

    [Fact]
    public async Task RemovingAHousehold_RemovesItsList()
    {
        var household = await _households.CreateAsync("Going", _now);
        await _repository.ReplaceHiddenLibrariesAsync(household.Id, new HashSet<Guid> { Guid.NewGuid() }, _now, null);

        Assert.Equal(HouseholdDeleteOutcome.Deleted, await _households.DeleteWhenEmptyAsync(household.Id));

        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM household_library_hidden WHERE household_id=@id;", new { id = household.Id }));
    }

    [Fact]
    public void TheHouseholdList_IsConfiguration_ForBackup_AndTheRetiredGrantsTableIsGone()
    {
        Assert.Equal(BackupDataKind.Configuration, BackupTableClassification.Find("household_library_hidden")?.Kind);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='library_profile_grants';"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='household_library_hidden';"));
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private long VersionOf(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT authorization_version FROM profiles WHERE id=@id;", new { id = profileId });
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
