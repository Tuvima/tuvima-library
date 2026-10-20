using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ArtworkLibraryReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _database;

    public ArtworkLibraryReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_artwork_library_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_dbPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        TestTemp.ReleaseSqlitePool(_dbPath);
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PeopleBrowse_CharacterNameReturnsItsOwnedWorkPerformer()
    {
        var personId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'Keanu Reeves', CURRENT_TIMESTAMP);
                INSERT INTO person_roles (person_id, role)
                VALUES (@personId, 'Actor');
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@workId, 'Movies', 'standalone');
                INSERT INTO editions (id, work_id)
                VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @contentHash, 'C:/movies/matrix.mkv');
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                VALUES (@assetId, @personId, 'Actor');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@workId, 'cast_member', 0, 'Keanu Reeves');
                INSERT INTO fictional_entities (
                    id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
                VALUES (@characterId, 'Q92740', 'Neo', 'Character', 'Q83495', CURRENT_TIMESTAMP);
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                VALUES (@personId, @characterId, 'Q83495');
                """,
                new
                {
                    personId,
                    characterId,
                    workId,
                    editionId,
                    assetId,
                    contentHash = Guid.NewGuid().ToString("N"),
                });
        }

        var service = new ArtworkLibraryReadService(
            _database,
            new DisplayWorkProjectionReader(_database),
            new DisplayCardBuilder());

        var page = await service.BrowseAsync(
            "people", null, "Neo", null, null, null, null, 0, 48, CancellationToken.None);

        var person = Assert.Single(page.Items);
        Assert.Equal(personId, person.EntityId);
        Assert.Equal("Keanu Reeves", person.DisplayTitle);
    }

    [Fact]
    public async Task ManagedArtworkSummary_CountsTheVariantsStoredForAShowOrAlbum()
    {
        var showId = Guid.NewGuid();
        var otherWorkId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url)
            VALUES (@coverId, @showId, 'Work', 'CoverArt', 'https://example.test/cover.jpg'),
                   (@backgroundId, @showId, 'Work', 'Background', 'https://example.test/background.jpg'),
                   (@otherId, @otherWorkId, 'Work', 'CoverArt', 'https://example.test/other.jpg');
            """,
            new
            {
                coverId = Guid.NewGuid(),
                backgroundId = Guid.NewGuid(),
                otherId = Guid.NewGuid(),
                showId,
                otherWorkId,
            });

        var summaries = ArtworkLibraryReadService.LoadManagedAssets(connection, [showId], CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(showId, summary.Key);
        Assert.Equal(2, summary.Value.VariantCount);
        Assert.NotNull(summary.Value.PreferredCoverId);
    }

    [Fact]
    public async Task CollectionArtworkSummary_ReadsTheSeriesOwnCoverBackgroundAndLogo()
    {
        var seriesId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO collections (id, display_name, collection_type, cover_artwork_path, background_artwork_path, logo_artwork_path)
            VALUES (@seriesId, 'Dune Novels', 'Series', 'covers/dune.jpg', 'backgrounds/dune.jpg', NULL);
            """,
            new { seriesId });

        var summaries = ArtworkLibraryReadService.LoadCollectionArtwork(connection, [seriesId], CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(seriesId, summary.Key);
        Assert.True(summary.Value.HasCover);
        Assert.True(summary.Value.HasBackground);
        Assert.False(summary.Value.HasLogo);
        Assert.Equal(2, summary.Value.VariantCount);
    }
}
