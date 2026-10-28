using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>Accounts a person linked to a plugin for their own profile, against a real temporary data store.</summary>
public sealed class PluginProfileConnectionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly PluginProfileConnectionRepository _repo;

    public PluginProfileConnectionRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_profile_connections_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new PluginProfileConnectionRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp data store cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp data store cleanup is best effort */ }
    }

    [Fact]
    public async Task EachProfile_SeesOnlyItsOwnLink_AndLinkingAgainReplacesTheValue()
    {
        var ana = Profile("Ana");
        var ben = Profile("Ben");

        await _repo.UpsertAsync("tuvima.listenbrainz", ana, "token", "cipher-1", Now);
        await _repo.UpsertAsync("tuvima.listenbrainz", ana, "token", "cipher-2", Now.AddMinutes(1));

        Assert.Equal("cipher-2", await _repo.GetCiphertextAsync("tuvima.listenbrainz", ana, "token"));
        Assert.Null(await _repo.GetCiphertextAsync("tuvima.listenbrainz", ben, "token"));
        Assert.Empty(await _repo.ListAsync("tuvima.listenbrainz", ben));
        var row = Assert.Single(await _repo.ListAsync("tuvima.listenbrainz", ana));
        Assert.Equal("token", row.CredentialName);
        Assert.Equal(Now.AddMinutes(1), row.LinkedAt);
    }

    [Fact]
    public async Task Unlinking_RemovesOnlyThatProfilesLink()
    {
        var ana = Profile("Ana");
        var ben = Profile("Ben");
        await _repo.UpsertAsync("p.one", ana, "token", "a", Now);
        await _repo.UpsertAsync("p.one", ben, "token", "b", Now);

        Assert.True(await _repo.DeleteAsync("p.one", ana, "token"));
        Assert.False(await _repo.DeleteAsync("p.one", ana, "token"));

        Assert.Null(await _repo.GetCiphertextAsync("p.one", ana, "token"));
        Assert.Equal("b", await _repo.GetCiphertextAsync("p.one", ben, "token"));
        Assert.Equal([ben], await _repo.ListProfilesAsync("p.one"));
    }

    [Fact]
    public async Task RemovingAProfile_RemovesItsLinks()
    {
        var ana = Profile("Ana");
        await _repo.UpsertAsync("p.one", ana, "token", "a", Now);

        using (var connection = _db.CreateConnection())
        {
            connection.Execute("PRAGMA foreign_keys = ON;");
            connection.Execute("DELETE FROM profiles WHERE id = @id;", new { id = GuidSql.ToBlob(ana) });
        }

        Assert.Empty(await _repo.ListProfilesAsync("p.one"));
    }

    [Fact]
    public async Task RemovingAPluginsData_RemovesEveryProfilesLinks()
    {
        var ana = Profile("Ana");
        var ben = Profile("Ben");
        await _repo.UpsertAsync("p.one", ana, "token", "a", Now);
        await _repo.UpsertAsync("p.one", ben, "token", "b", Now);
        await _repo.UpsertAsync("p.two", ana, "token", "c", Now);

        await new PluginDataRepository(_db).RemoveAsync("p.one", includeOutput: false);

        Assert.Empty(await _repo.ListProfilesAsync("p.one"));
        Assert.Equal("c", await _repo.GetCiphertextAsync("p.two", ana, "token"));
    }

    private Guid Profile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id = GuidSql.ToBlob(id), name, now = Now.ToString("O") });
        return id;
    }
}
