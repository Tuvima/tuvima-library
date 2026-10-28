using Dapper;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The profiles chosen for each Selected-profiles library (Library pages D9, WP6), on real temp SQLite: choosing, replacing, the authorization
/// version, Vault refusal, a new profile starting without any choice, and a deleted profile taking its choices with it.
/// </summary>
public sealed class LibraryProfileGrantRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly LibraryProfileGrantRepository _grants;
    private readonly DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public LibraryProfileGrantRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_library_grants_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _grants = new LibraryProfileGrantRepository(_database);
    }

    [Fact]
    public async Task ALibrary_StartsWithNoProfilesChosen_AndAProfileStartsWithNoLibraries()
    {
        var profile = InsertProfile();

        Assert.Empty(await _grants.GetGrantedProfilesAsync(Guid.NewGuid()));
        Assert.Empty(await _grants.GetGrantedLibrariesAsync(profile));
    }

    [Fact]
    public async Task ChoosingProfiles_RoundTrips_ReplacesThePreviousChoice_AndReadsBackBothWays()
    {
        var library = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = InsertProfile();
        var second = InsertProfile();

        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { first, second }, _now, null);
        await _grants.ReplaceGrantedProfilesAsync(other, new HashSet<Guid> { first }, _now, null);
        Assert.Equal(new HashSet<Guid> { first, second }, (await _grants.GetGrantedProfilesAsync(library)).ToHashSet());
        Assert.Equal(new HashSet<Guid> { library, other }, (await _grants.GetGrantedLibrariesAsync(first)).ToHashSet());

        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { second }, _now, null);
        Assert.Equal([second], (await _grants.GetGrantedProfilesAsync(library)).ToArray());
        Assert.Equal([other], (await _grants.GetGrantedLibrariesAsync(first)).ToArray());
    }

    [Fact]
    public async Task OnlyTheProfilesThatGainedOrLostTheLibrary_HaveTheirAuthorizationVersionRaised()
    {
        var library = Guid.NewGuid();
        var kept = InsertProfile();
        var dropped = InsertProfile();
        var gained = InsertProfile();
        var untouched = InsertProfile();
        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { kept, dropped }, _now, null);
        var before = new[] { kept, dropped, gained, untouched }.ToDictionary(id => id, Version);

        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { kept, gained }, _now, null);

        Assert.Equal(before[kept], Version(kept));
        Assert.Equal(before[dropped] + 1, Version(dropped));
        Assert.Equal(before[gained] + 1, Version(gained));
        Assert.Equal(before[untouched], Version(untouched));
    }

    [Fact]
    public async Task ASaveThatChangesNothing_DoesNotRaiseAnyVersion()
    {
        var library = Guid.NewGuid();
        var profile = InsertProfile();
        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { profile }, _now, null);
        var before = Version(profile);

        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { profile }, _now, null);

        Assert.Equal(before, Version(profile));
    }

    [Fact]
    public async Task AnUnknownProfile_IsRefused_AndNothingIsChanged()
    {
        var library = Guid.NewGuid();
        var known = InsertProfile();
        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { known }, _now, null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { Guid.NewGuid() }, _now, null));

        Assert.Equal([known], (await _grants.GetGrantedProfilesAsync(library)).ToArray());
    }

    [Fact]
    public async Task AVaultLibrary_IsNeverAcceptedAsAGrant()
    {
        var vault = Guid.NewGuid();
        var profile = InsertProfile();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = vault.ToString("D"), now = _now.ToString("O") });
        }

        await Assert.ThrowsAsync<ArgumentException>(() => _grants.ReplaceGrantedProfilesAsync(vault, new HashSet<Guid> { profile }, _now, null));

        Assert.Empty(await _grants.GetGrantedProfilesAsync(vault));
    }

    [Fact]
    public async Task ClearingALibrary_RemovesEveryChoice_AndRaisesTheVersionOfEachProfileThatHadIt()
    {
        var library = Guid.NewGuid();
        var chosen = InsertProfile();
        var other = InsertProfile();
        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { chosen }, _now, null);
        var before = (Chosen: Version(chosen), Other: Version(other));

        await _grants.ClearLibraryAsync(library);

        Assert.Empty(await _grants.GetGrantedProfilesAsync(library));
        Assert.Equal(before.Chosen + 1, Version(chosen));
        Assert.Equal(before.Other, Version(other));
    }

    [Fact]
    public async Task DeletingAProfile_RemovesItsChoices_ButNotAnotherProfilesOnTheSameLibrary()
    {
        var library = Guid.NewGuid();
        var leaving = InsertProfile();
        var staying = InsertProfile();
        await _grants.ReplaceGrantedProfilesAsync(library, new HashSet<Guid> { leaving, staying }, _now, null);

        await new AccountRepository(_database).DeleteManagedProfileAsync(leaving);

        Assert.Equal([staying], (await _grants.GetGrantedProfilesAsync(library)).ToArray());
        Assert.Empty(await _grants.GetGrantedLibrariesAsync(leaving));
    }

    [Fact]
    public async Task TheChoicesAreBackupSettings_AndTheirTableIsClassified()
    {
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_profile_grants';"));
        Assert.NotNull(MediaEngine.Storage.Backup.BackupTableClassification.Find("library_profile_grants"));
        await Task.CompletedTask;
    }

    private Guid InsertProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id, name = $"Person {id:N}", now = _now.ToString("O") });
        return id;
    }

    private long Version(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT authorization_version FROM profiles WHERE id=@profileId;", new { profileId });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
