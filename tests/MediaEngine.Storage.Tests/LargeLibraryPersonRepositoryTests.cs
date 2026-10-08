using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class LargeLibraryPersonRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public LargeLibraryPersonRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_people_batch_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ListPagedAsync_ReturnsBoundedPersonPage()
    {
        SeedPeople(120);
        var repo = new PersonRepository(_db);

        var page = await repo.ListPagedAsync(role: null, offset: 50, limit: 10);

        Assert.Equal(10, page.Count);
        Assert.Equal("Person 050", page[0].Name);
        Assert.Equal("Person 059", page[^1].Name);
    }

    [Fact]
    public async Task CatalogPage_UsesCanonicalOwnedCreditsForPeopleAndRoles()
    {
        SeedWorksAssetsAndPeople(3);
        var repo = new PersonRepository(_db);

        var page = await repo.ListCatalogPagedAsync(
            search: "Contributor 001",
            role: "Author",
            offset: 0,
            limit: 10,
            lane: "Read",
            sort: "count");
        var roleCounts = await repo.GetCatalogRoleCountsAsync();

        var person = Assert.Single(page);
        Assert.Equal("Contributor 001", person.Name);
        Assert.Equal(["Author"], person.Roles);
        Assert.Equal(3, roleCounts["Author"]);
    }

    [Fact]
    public async Task GetByMediaAssetsAsync_LoadsPeopleForManyAssetsInOneCall()
    {
        var assetIds = SeedWorksAssetsAndPeople(40);
        var repo = new PersonRepository(_db);

        var requestedIds = assetIds
            .Concat(Enumerable.Range(0, 1_100).Select(_ => Guid.NewGuid()))
            .ToList();
        var people = await repo.GetByMediaAssetsAsync(requestedIds);

        Assert.Equal(40, people.Count);
        Assert.Contains(people, person => person.Name == "Contributor 000");
        Assert.Contains(people, person => person.Name == "Contributor 039");
    }

    [Fact]
    public async Task CreateAsync_RollsBackPersonWhenRoleInsertFails()
    {
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                CREATE TRIGGER fail_person_role_insert
                BEFORE INSERT ON person_roles
                BEGIN
                    SELECT RAISE(ABORT, 'forced role failure');
                END;
                """);
        }

        var repo = new PersonRepository(_db);
        var person = new Person
        {
            Name = "Transactional Person",
            Roles = ["Author"],
        };

        await Assert.ThrowsAsync<SqliteException>(() => repo.CreateAsync(person));

        using var verify = _db.CreateConnection();
        var personCount = verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM persons WHERE id = @id;",
            new { id = person.Id });
        var roleCount = verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM person_roles WHERE person_id = @id;",
            new { id = person.Id });

        Assert.Equal(0, personCount);
        Assert.Equal(0, roleCount);
    }

    [Fact]
    public async Task GetCharacterPerformersAsync_UsesTypedBlobIdsAndReturnsAllCreditsInOneRead()
    {
        var firstCharacterId = Guid.NewGuid();
        var secondCharacterId = Guid.NewGuid();
        var performerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO fictional_entities
                        (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES
                        ($firstCharacterId, 'QCHAR1', 'First Character', 'Character', $now),
                        ($secondCharacterId, 'QCHAR2', 'Second Character', 'Character', $now);
                    INSERT INTO persons
                        (id, name, headshot_url, local_headshot_path, created_at)
                    VALUES
                        ($performerId, 'Sample Performer', 'https://example.test/headshot.jpg', 'people/sample.jpg', $now);
                    INSERT INTO character_performer_links
                        (person_id, fictional_entity_id, work_qid)
                    VALUES
                        ($performerId, $firstCharacterId, 'QWORK1'),
                        ($performerId, $secondCharacterId, 'QWORK2');
                    """;
                AddGuid(cmd, "$firstCharacterId", firstCharacterId);
                AddGuid(cmd, "$secondCharacterId", secondCharacterId);
                AddGuid(cmd, "$performerId", performerId);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
        }

        var repo = new PersonRepository(_db);
        var requestedIds = Enumerable.Range(0, 1_100)
            .Select(_ => Guid.NewGuid())
            .Append(firstCharacterId)
            .Append(secondCharacterId)
            .Append(firstCharacterId)
            .Append(Guid.Empty)
            .ToList();
        var credits = await repo.GetCharacterPerformersAsync(requestedIds);

        Assert.Equal(2, credits.Count);
        Assert.All(credits, credit => Assert.Equal(performerId, credit.PersonId));
        Assert.Contains(credits, credit => credit.FictionalEntityId == firstCharacterId && credit.WorkQid == "QWORK1");
        Assert.Contains(credits, credit => credit.FictionalEntityId == secondCharacterId && credit.WorkQid == "QWORK2");
        Assert.All(credits, credit => Assert.Equal("Sample Performer", credit.PerformerName));
        Assert.All(credits, credit => Assert.Equal("people/sample.jpg", credit.LocalHeadshotPath));
    }

    [Fact]
    public async Task GetPresenceBatchAsync_ChunksLargeIdSets()
    {
        var repo = new PersonRepository(_db);
        var personIds = Enumerable.Range(0, 1_100)
            .Select(_ => Guid.NewGuid())
            .ToList();

        var presence = await repo.GetPresenceBatchAsync(personIds);

        Assert.Empty(presence);
    }

    [Fact]
    public async Task GetPresenceBatchAsync_CountsTopLevelAlbumsAndShowsInsteadOfTracksAndEpisodes()
    {
        var personId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var firstTrackId = Guid.NewGuid();
        var secondTrackId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var firstEpisodeId = Guid.NewGuid();
        var secondEpisodeId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO persons (id, name, wikidata_qid, created_at)
                        VALUES ($personId, 'Primary Contributor', 'QPERSON', $now);
    
                    INSERT INTO works (id, media_type, work_kind) VALUES
                        ($albumId, 'Music', 'parent'),
                        ($showId, 'TV', 'parent'),
                        ($bookId, 'Books', 'standalone');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal) VALUES
                        ($firstTrackId, 'Music', 'child', $albumId, 1),
                        ($secondTrackId, 'Music', 'child', $albumId, 2),
                        ($firstEpisodeId, 'TV', 'child', $showId, 1),
                        ($secondEpisodeId, 'TV', 'child', $showId, 2);
    
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid) VALUES
                        ($albumId, 'artist', 0, 'Primary Contributor', 'QPERSON'),
                        ($showId, 'director', 0, 'Primary Contributor', 'QPERSON'),
                        ($bookId, 'author', 0, 'Primary Contributor', 'QPERSON');
    
                    INSERT INTO editions (id, work_id) VALUES
                        ($firstTrackEditionId, $firstTrackId),
                        ($secondTrackEditionId, $secondTrackId),
                        ($firstEpisodeEditionId, $firstEpisodeId),
                        ($secondEpisodeEditionId, $secondEpisodeId),
                        ($bookEditionId, $bookId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES
                        ($firstTrackAssetId, $firstTrackEditionId, 'presence-track-1', 'C:/music/track-1.flac'),
                        ($secondTrackAssetId, $secondTrackEditionId, 'presence-track-2', 'C:/music/track-2.flac'),
                        ($firstEpisodeAssetId, $firstEpisodeEditionId, 'presence-episode-1', 'C:/tv/episode-1.mkv'),
                        ($secondEpisodeAssetId, $secondEpisodeEditionId, 'presence-episode-2', 'C:/tv/episode-2.mkv'),
                        ($bookAssetId, $bookEditionId, 'presence-book', 'C:/books/book.epub');
                    """;

                AddGuid(cmd, "$personId", personId);
                AddGuid(cmd, "$albumId", albumId);
                AddGuid(cmd, "$firstTrackId", firstTrackId);
                AddGuid(cmd, "$secondTrackId", secondTrackId);
                AddGuid(cmd, "$showId", showId);
                AddGuid(cmd, "$firstEpisodeId", firstEpisodeId);
                AddGuid(cmd, "$secondEpisodeId", secondEpisodeId);
                AddGuid(cmd, "$bookId", bookId);
                foreach (var parameterName in new[]
                         {
                             "$firstTrackEditionId", "$secondTrackEditionId", "$firstEpisodeEditionId",
                             "$secondEpisodeEditionId", "$bookEditionId", "$firstTrackAssetId",
                             "$secondTrackAssetId", "$firstEpisodeAssetId", "$secondEpisodeAssetId", "$bookAssetId",
                         })
                {
                    AddGuid(cmd, parameterName, Guid.NewGuid());
                }

                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
        }

        var presence = await new PersonRepository(_db).GetPresenceBatchAsync([personId]);
        var counts = Assert.Single(presence).Value;

        Assert.Equal(1, counts["Music"]);
        Assert.Equal(1, counts["TV"]);
        Assert.Equal(1, counts["Books"]);
    }

    private void SeedPeople(int count)
    {
        using var conn = _db.CreateConnection();
        for (var i = 0; i < count; i++)
        {
            var personId = Guid.NewGuid();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, created_at) VALUES ($id, $name, $createdAt);
                INSERT INTO person_roles (person_id, role) VALUES ($id, 'Author');
                """;
            cmd.Parameters.AddWithValue("$id", GuidSql.ToBlob(personId));
            cmd.Parameters.AddWithValue("$name", $"Person {i:000}");
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<Guid> SeedWorksAssetsAndPeople(int count)
    {
        using var conn = _db.CreateConnection();
        var assetIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var personId = Guid.NewGuid();
            assetIds.Add(assetId);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO works (id, media_type) VALUES ($workId, 'Books');
                INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, $hash, $path);
                INSERT INTO persons (id, name, created_at) VALUES ($personId, $name, $createdAt);
                INSERT INTO person_roles (person_id, role) VALUES ($personId, 'Author');
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                    VALUES ($assetId, $personId, 'Author');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'author', 0, $name);
                """;
            cmd.Parameters.AddWithValue("$workId", GuidSql.ToBlob(workId));
            cmd.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            cmd.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            cmd.Parameters.AddWithValue("$personId", GuidSql.ToBlob(personId));
            cmd.Parameters.AddWithValue("$hash", $"hash-{i:000}");
            cmd.Parameters.AddWithValue("$path", $"C:/library/file-{i:000}.epub");
            cmd.Parameters.AddWithValue("$name", $"Contributor {i:000}");
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return assetIds;
    }

    private static void AddGuid(SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = GuidSql.ToBlob(value);
}
