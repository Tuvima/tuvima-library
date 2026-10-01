using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class EditorSuggestionReadServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_suggestions_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public EditorSuggestionReadServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task CustomTagSuggestionsUseSharedMetadataAndExcludeLegacyProfileTags()
    {
        var workId = Guid.NewGuid();
        var canonicalWorkId = Guid.NewGuid();
        var clearedWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, display_overrides_json)
                VALUES (@workId, 'Books', 'standalone', @workOverrides);
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@canonicalWorkId, 'Books', 'standalone');
                INSERT INTO works (id, media_type, work_kind, display_overrides_json)
                VALUES (@clearedWorkId, 'Books', 'standalone', '{"custom_tags":""}');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @assetHash, 'C:/library/tagged-book.epub');
                INSERT INTO persons (id, name, created_at, display_overrides_json)
                VALUES (@personId, 'Tagged Person', CURRENT_TIMESTAMP, @personOverrides);
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, 'Profile', '#112233', 'StandardUser', CURRENT_TIMESTAMP);
                INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at)
                VALUES (@profileId, @workId, '["private work tag"]', 1, CURRENT_TIMESTAMP);
                INSERT INTO profile_person_preferences (profile_id, person_id, local_tags_json, revision, updated_at)
                VALUES (@profileId, @personId, '["private person tag"]', 1, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'custom_tags', 'scalar tag; shared favorite', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@workId, 'custom_tags', 0, 'suppressed work array tag'),
                       (@assetId, 'custom_tags', 0, 'suppressed asset array tag'),
                       (@canonicalWorkId, 'custom_tags', 0, 'array canonical tag'),
                       (@clearedWorkId, 'custom_tags', 0, 'cleared array tag');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'custom_tags', 'suppressed asset scalar tag', CURRENT_TIMESTAMP),
                       (@personId, 'custom_tags', 'suppressed person canonical tag', CURRENT_TIMESTAMP),
                       (@canonicalWorkId, 'custom_tags', 'scalar canonical tag', CURRENT_TIMESTAMP),
                       (@clearedWorkId, 'custom_tags', 'cleared scalar tag', CURRENT_TIMESTAMP);
                """,
                new
                {
                    workId,
                    canonicalWorkId,
                    clearedWorkId,
                    editionId,
                    assetId,
                    personId,
                    profileId,
                    assetHash = Guid.NewGuid().ToString("N"),
                    workOverrides = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["custom_tags"] = "work override tag; shared favorite",
                    }),
                    personOverrides = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["custom_tags"] = "person override tag",
                    }),
                });
        }

        var service = new EditorSuggestionReadService(_db);

        var noProfileValues = service.GetValues("custom_tags", null, 100, CancellationToken.None);
        var profileValues = service.GetValues("tags", profileId, 100, CancellationToken.None);

        Assert.Equal(noProfileValues, profileValues);
        Assert.Contains("array canonical tag", profileValues);
        Assert.Contains("scalar canonical tag", profileValues);
        Assert.Contains("shared favorite", profileValues);
        Assert.Contains("work override tag", profileValues);
        Assert.Contains("person override tag", profileValues);
        Assert.DoesNotContain(profileValues, value => value.StartsWith("suppressed ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profileValues, value => value.StartsWith("cleared ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profileValues, value => value.StartsWith("private ", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
