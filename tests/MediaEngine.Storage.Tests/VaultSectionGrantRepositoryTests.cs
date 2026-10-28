using Dapper;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The Vault sections each profile may open (Vault sections D28, D39), on real temp SQLite: nothing by default, only real Vault libraries,
/// never a Kids or content-limited profile, and rows that go when the section or the profile does.
/// </summary>
public sealed class VaultSectionGrantRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly VaultSectionGrantRepository _grants;
    private readonly DateTimeOffset _now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public VaultSectionGrantRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_sections_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _grants = new VaultSectionGrantRepository(_database);
    }

    [Fact]
    public async Task AProfile_StartsWithNoSections_AndChoosingRoundTripsAndReplaces()
    {
        var profile = InsertProfile();
        var movies = InsertVaultLibrary();
        var music = InsertVaultLibrary();
        Assert.Empty(await _grants.GetGrantedAsync(profile));

        await _grants.ReplaceGrantedAsync(profile, [movies, music.ToUpperInvariant()], _now, null);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { movies, music }, (await _grants.GetGrantedAsync(profile)).ToHashSet(StringComparer.OrdinalIgnoreCase));

        await _grants.ReplaceGrantedAsync(profile, [movies], _now, null);
        Assert.Equal([movies], (await _grants.GetGrantedAsync(profile)).ToArray());

        await _grants.ReplaceGrantedAsync(profile, [], _now, null);
        Assert.Empty(await _grants.GetGrantedAsync(profile));
    }

    [Fact]
    public async Task ALibraryThatIsNotAVaultSection_IsRefused_AndNothingIsChanged()
    {
        var profile = InsertProfile();
        var movies = InsertVaultLibrary();
        await _grants.ReplaceGrantedAsync(profile, [movies], _now, null);

        await Assert.ThrowsAsync<ArgumentException>(() => _grants.ReplaceGrantedAsync(profile, [movies, Guid.NewGuid().ToString("D")], _now, null));

        Assert.Equal([movies], (await _grants.GetGrantedAsync(profile)).ToArray());
    }

    [Fact]
    public async Task AKidsOrContentLimitedProfile_CanNeverHaveASection_WhateverRouteTheRowTakes()
    {
        var movies = InsertVaultLibrary();
        var kids = InsertProfile("RestrictedProfile");
        var limited = InsertProfile(contentLimit: "PG");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _grants.ReplaceGrantedAsync(kids, [movies], _now, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _grants.ReplaceGrantedAsync(limited, [movies], _now, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _grants.ReplaceGrantedAsync(Guid.NewGuid(), [movies], _now, null));

        // A direct insert is stopped by the data store itself.
        using var connection = _database.CreateConnection();
        Assert.ThrowsAny<Exception>(() => connection.Execute(
            "INSERT INTO vault_section_grants(library_id,profile_id,created_at) VALUES(@movies,@kids,@now);", new { movies, kids, now = _now.ToString("O") }));
    }

    [Fact]
    public async Task AProfileThatBecomesKidsOrLimited_LosesItsSections_AndARemovedSectionOrProfileTakesItsRowsWithIt()
    {
        var movies = InsertVaultLibrary();
        var music = InsertVaultLibrary();
        var becomesKids = InsertProfile();
        var removed = InsertProfile();
        await _grants.ReplaceGrantedAsync(becomesKids, [movies, music], _now, null);
        await _grants.ReplaceGrantedAsync(removed, [movies], _now, null);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET content_limit='PG' WHERE id=@becomesKids;", new { becomesKids });
            connection.Execute("DELETE FROM profiles WHERE id=@removed;", new { removed });
        }

        Assert.Empty(await _grants.GetGrantedAsync(becomesKids));
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_section_grants WHERE profile_id=@removed;", new { removed }));
        }

        var other = InsertProfile();
        await _grants.ReplaceGrantedAsync(other, [movies, music], _now, null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM vault_libraries WHERE library_id=@movies;", new { movies });
        }

        Assert.Equal([music], (await _grants.GetGrantedAsync(other)).ToArray());
    }

    private Guid InsertProfile(string role = "StandardUser", string? contentLimit = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,@name,'#7C4DFF',@role,@contentLimit,@now);",
            new { id, name = $"Person {id:N}", role, contentLimit, now = _now.ToString("O") });
        return id;
    }

    private string InsertVaultLibrary()
    {
        var id = Guid.NewGuid().ToString("D");
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO vault_libraries(library_id,lookup_source,adults_confirmed_at,created_at,updated_at) VALUES(@id,'none',@now,@now,@now);",
            new { id, now = _now.ToString("O") });
        return id;
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
