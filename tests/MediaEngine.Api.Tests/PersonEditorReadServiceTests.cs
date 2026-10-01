using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Persons;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Xunit;

namespace MediaEngine.Api.Tests;

public sealed class PersonEditorReadServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_person_editor_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public PersonEditorReadServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task SharedTagsAreVisibleAcrossProfilesAndRevisionConflictsAcrossEditors()
    {
        var personId = Guid.NewGuid();
        var firstProfileId = Guid.NewGuid();
        var secondProfileId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'Shared Person', CURRENT_TIMESTAMP);
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@firstProfileId, 'First', '#112233', 'StandardUser', CURRENT_TIMESTAMP),
                       (@secondProfileId, 'Second', '#445566', 'StandardUser', CURRENT_TIMESTAMP);
                INSERT INTO profile_person_preferences (profile_id, person_id, local_tags_json, revision, updated_at)
                VALUES (@firstProfileId, @personId, '["private first"]', 4, CURRENT_TIMESTAMP),
                       (@secondProfileId, @personId, '["private second"]', 9, CURRENT_TIMESTAMP);
                """,
                new { personId, firstProfileId, secondProfileId });
        }

        var service = new PersonEditorReadService(_db, new PersonRepository(_db));
        var firstState = Assert.IsType<MediaEngine.Contracts.Persons.PersonEditorStateResponse>(
            await service.GetAsync(personId, firstProfileId, CancellationToken.None));
        var secondState = Assert.IsType<MediaEngine.Contracts.Persons.PersonEditorStateResponse>(
            await service.GetAsync(personId, secondProfileId, CancellationToken.None));
        Assert.Empty(firstState.LocalTags);
        Assert.Equal(firstState.Revision, secondState.Revision);

        var saved = await service.SaveAsync(personId, new PersonEditorSaveRequest
        {
            ProfileId = firstProfileId,
            ExpectedRevision = firstState.Revision,
            LocalTags = ["  quiet favorite ", "library pick", "quiet favorite"],
        }, CancellationToken.None);

        Assert.True(saved.Saved);
        var visibleFromSecondProfile = await service.GetAsync(personId, secondProfileId, CancellationToken.None);
        Assert.NotNull(visibleFromSecondProfile);
        Assert.Equal(["quiet favorite", "library pick"], visibleFromSecondProfile.LocalTags);
        Assert.Equal(saved.Revision, visibleFromSecondProfile.Revision);

        var staleWrite = await service.SaveAsync(personId, new PersonEditorSaveRequest
        {
            ProfileId = secondProfileId,
            ExpectedRevision = secondState.Revision,
            LocalTags = ["stale replacement"],
        }, CancellationToken.None);
        Assert.False(staleWrite.Saved);
        Assert.Equal(saved.Revision, staleWrite.Revision);

        var clearState = await service.GetAsync(personId, secondProfileId, CancellationToken.None);
        var cleared = await service.SaveAsync(personId, new PersonEditorSaveRequest
        {
            ProfileId = secondProfileId,
            ExpectedRevision = clearState!.Revision,
            LocalTags = [],
        }, CancellationToken.None);
        Assert.True(cleared.Saved);
        Assert.Empty((await service.GetAsync(personId, null, CancellationToken.None))!.LocalTags);

        using var verify = _db.CreateConnection();
        var sharedJson = await verify.QuerySingleAsync<string>(
            "SELECT display_overrides_json FROM persons WHERE id=@personId;",
            new { personId });
        var storedOverrides = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sharedJson)!;
        Assert.True(storedOverrides.TryGetValue("custom_tags", out var emptySentinel));
        Assert.Equal(string.Empty, emptySentinel);
        var legacyRows = (await verify.QueryAsync<(Guid ProfileId, string LocalTagsJson)>("""
            SELECT profile_id AS ProfileId, local_tags_json AS LocalTagsJson
            FROM profile_person_preferences WHERE person_id=@personId ORDER BY profile_id;
            """, new { personId })).ToList();
        Assert.Equal(2, legacyRows.Count);
        Assert.Contains(legacyRows, row => row.LocalTagsJson == "[\"private first\"]");
        Assert.Contains(legacyRows, row => row.LocalTagsJson == "[\"private second\"]");
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
