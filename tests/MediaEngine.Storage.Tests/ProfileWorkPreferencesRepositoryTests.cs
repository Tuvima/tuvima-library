using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileWorkPreferencesRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ProfileWorkPreferencesRepository _repository;

    public ProfileWorkPreferencesRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_work_preferences_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new ProfileWorkPreferencesRepository(_database);
    }

    [Fact]
    public async Task SaveAsync_AtomicallyStoresLocalPreferencesAndDisplayOverrides()
    {
        var profileId = SeedProfile("Owner");
        var otherProfileId = SeedProfile("Guest");
        var workId = SeedWork();

        var result = await _repository.SaveAsync(new EditorPreferencesSaveCommand(
            profileId,
            workId,
            ExpectedRevision: 0,
            new Dictionary<string, string>
            {
                ["title"] = "My title",
                ["description"] = "My description",
                ["genre"] = "Science Fiction, Neo Noir",
            },
            LocalTags: ["Noir", "Favorite", "noir"]));

        Assert.True(result.WorkExists);
        Assert.True(result.ProfileExists);
        Assert.False(result.Conflict);
        Assert.Equal(1, result.Preferences.Revision);
        Assert.Equal(["Favorite", "Noir"], result.Preferences.LocalTags);
        Assert.Equal("My title", result.DisplayOverrides["title"]);
        Assert.Equal("Science Fiction, Neo Noir", result.DisplayOverrides["genre"]);

        var stored = await _repository.GetAsync(profileId, workId);
        Assert.Equal(result.Preferences.ProfileId, stored.ProfileId);
        Assert.Equal(result.Preferences.WorkId, stored.WorkId);
        Assert.Equal(result.Preferences.LocalTags, stored.LocalTags);
        Assert.Equal(result.Preferences.Revision, stored.Revision);

        var otherProfile = await _repository.GetAsync(otherProfileId, workId);
        Assert.Equal(0, otherProfile.Revision);
        Assert.Empty(otherProfile.LocalTags);

        using var connection = _database.CreateConnection();
        var displayJson = connection.ExecuteScalar<string>(
            "SELECT display_overrides_json FROM works WHERE id = @workId;",
            new { workId });
        Assert.NotNull(displayJson);
        var displayOverrides = JsonSerializer.Deserialize<Dictionary<string, string>>(displayJson!);
        Assert.Equal("My description", displayOverrides!["description"]);
        Assert.Equal("Science Fiction, Neo Noir", displayOverrides["genre"]);
    }

    [Fact]
    public async Task SaveAsync_RejectsStaleRevisionWithoutPartiallyUpdatingTheWork()
    {
        var profileId = SeedProfile("Owner");
        var workId = SeedWork();
        var first = await _repository.SaveAsync(new EditorPreferencesSaveCommand(
            profileId, workId, 0,
            new Dictionary<string, string> { ["title"] = "First title" },
            ["First"]));

        var stale = await _repository.SaveAsync(new EditorPreferencesSaveCommand(
            profileId, workId, 0,
            new Dictionary<string, string> { ["title"] = "Stale title" },
            ["Stale"]));

        Assert.Equal(1, first.Preferences.Revision);
        Assert.True(stale.Conflict);
        Assert.Equal(1, stale.Preferences.Revision);
        Assert.Equal(["First"], stale.Preferences.LocalTags);
        Assert.Equal("First title", stale.DisplayOverrides["title"]);

        var stored = await _repository.GetAsync(profileId, workId);
        Assert.Equal(["First"], stored.LocalTags);
    }

    [Fact]
    public async Task SaveAsync_MissingProfileDoesNotChangeDisplayOverrides()
    {
        var workId = SeedWork("{\"title\":\"Original title\"}");

        var result = await _repository.SaveAsync(new EditorPreferencesSaveCommand(
            Guid.NewGuid(), workId, 0,
            new Dictionary<string, string> { ["title"] = "Uncommitted title" },
            []));

        Assert.True(result.WorkExists);
        Assert.False(result.ProfileExists);

        using var connection = _database.CreateConnection();
        var displayJson = connection.ExecuteScalar<string>(
            "SELECT display_overrides_json FROM works WHERE id = @workId;",
            new { workId });
        Assert.Equal("{\"title\":\"Original title\"}", displayJson);
    }

    [Fact]
    public void FreshSchema_DoesNotRetainPersonalNotesStorage()
    {
        using var connection = _database.CreateConnection();
        var columns = connection.Query<string>(
                """
                SELECT p.name
                FROM sqlite_master AS m
                JOIN pragma_table_info(m.name) AS p
                WHERE m.type = 'table'
                  AND m.name IN ('profile_work_preferences', 'profile_person_preferences');
                """)
            .ToList();

        Assert.DoesNotContain("personal_notes", columns, StringComparer.OrdinalIgnoreCase);
    }

    private Guid SeedProfile(string displayName)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at) VALUES (@id, @displayName, 'Administrator', @createdAt);",
            new { id, displayName, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private Guid SeedWork(string? displayOverridesJson = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, display_overrides_json) VALUES (@id, 'Movies', @displayOverridesJson);",
            new { id, displayOverridesJson });
        return id;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
