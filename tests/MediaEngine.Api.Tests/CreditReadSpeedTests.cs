using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A person's credits, the series search and the "add media" search work out credits only for the files they need.
/// These tests pin the cases the candidate-file step has to find (a credit only a provider claim carries, a credit that
/// names the person by Wikidata id, a group credit on an album) and the two things the narrowing could break in series
/// search (the author shown for an audiobook and for a movie), plus the library-sized allow-list of "add media".
/// </summary>
public sealed class CreditReadSpeedTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public CreditReadSpeedTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_credit_read_speed_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A pooled connection can still hold the temp file; the OS temp folder clears it later.
        }
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_FindsCreditsGivenOnlyByAProviderClaim()
    {
        var personId = await AddPersonAsync("Claim Only Author");
        var (workId, assetId) = await AddWorkAsync("Books", "A Claimed Book");
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync("""
                INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@providerId, 'claim-test', '1', 1);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES (@claimId, @assetId, @providerId, 'author', 'Claim Only Author', 1.0, '2026-03-01 00:00:00');
                """,
                new { providerId = Guid.NewGuid(), claimId = Guid.NewGuid(), assetId });
        }

        var credit = Assert.Single(await CreateCreditService().GetLibraryCreditsAsync(personId, CancellationToken.None));

        Assert.Equal(workId, credit.WorkId);
        Assert.Equal("Author", credit.Role);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_FindsCreditsByWikidataIdWhenTheNameDiffers()
    {
        var personId = await AddPersonAsync("K. Reeves", "Q43203");
        var (workId, assetId) = await AddWorkAsync("Movies", "A Matrix Of Names");
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync("""
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                VALUES (@assetId, 'director', 0, 'Keanu Reeves', 'Q43203');
                """,
                new { assetId });
        }

        var credit = Assert.Single(await CreateCreditService().GetLibraryCreditsAsync(personId, CancellationToken.None));

        Assert.Equal(workId, credit.WorkId);
        Assert.Equal("Director", credit.Role);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_FindsGroupCreditsOnAnAlbumThroughItsTracks()
    {
        var groupId = await AddPersonAsync("The Band", isGroup: true);
        var memberId = await AddPersonAsync("Band Member");
        var albumId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync("""
                INSERT INTO person_group_members (group_id, member_id, start_date, end_date) VALUES (@groupId, @memberId, NULL, NULL);
                INSERT INTO works (id, media_type, work_kind) VALUES (@albumId, 'Music', 'parent');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@albumId, 'title', 'Band Album', datetime('now'));
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@albumId, 'artist', 0, 'The Band');
                """,
                new { groupId, memberId, albumId });
            for (var track = 1; track <= 2; track++)
            {
                var trackId = Guid.NewGuid();
                var editionId = Guid.NewGuid();
                await conn.ExecuteAsync("""
                    INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@trackId, @albumId, 'Music', 'child', @track);
                    INSERT INTO editions (id, work_id) VALUES (@editionId, @trackId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                        VALUES (@assetId, @editionId, @hash, @path, 'Normal');
                    """,
                    new
                    {
                        trackId,
                        albumId,
                        track,
                        editionId,
                        assetId = Guid.NewGuid(),
                        hash = $"band-track-{track}",
                        path = $"/library/band/{track:00}.flac",
                    });
            }
        }

        var service = CreateCreditService();
        var groupCredit = Assert.Single(await service.GetLibraryCreditsAsync(groupId, CancellationToken.None));
        var memberCredit = Assert.Single(await service.GetLibraryCreditsAsync(memberId, CancellationToken.None));

        Assert.Equal(albumId, groupCredit.WorkId);
        Assert.Equal(albumId, memberCredit.WorkId);
        Assert.StartsWith("With The Band", memberCredit.Role, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesSearch_ShowsTheNarratorForAnAudiobookWithoutAnAuthor()
    {
        await AddPersonAsync("Quiet Narrator");
        var (_, assetId) = await AddWorkAsync("Audiobooks", "Zephyr Tales");
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@assetId, 'narrator', 0, 'Quiet Narrator');",
                new { assetId });
        }

        var result = Assert.Single(await SearchThroughTheIndexAsync("Zephyr"));

        Assert.Equal("Quiet Narrator", result.Author);
    }

    [Fact]
    public async Task SeriesSearch_ShowsTheCreditedDirectorOfAMovieAndKeepsTheStoredNameForTheRest()
    {
        await AddPersonAsync("Credited Director");
        var (_, movieAsset) = await AddWorkAsync("Movies", "Quokka Movie");
        var (bookWork, _) = await AddWorkAsync("Books", "Quokka Book");
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync("""
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@movieAsset, 'director', 0, 'Credited Director');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@bookWork, 'author', 0, 'Nobody Registered');
                """,
                new { movieAsset, bookWork });
        }

        var results = await SearchThroughTheIndexAsync("Quokka");

        Assert.Equal("Credited Director", Assert.Single(results, r => r.Title == "Quokka Movie").Author);
        Assert.Equal("Nobody Registered", Assert.Single(results, r => r.Title == "Quokka Book").Author);
    }

    [Fact]
    public async Task AddMedia_WithMoreThanThirtyTwoThousandVisibleWorks_ReturnsTheFirstPage()
    {
        var (workId, _) = await AddWorkAsync("Books", "Wombat Book");
        var allowed = Enumerable.Range(0, 33_000).Select(_ => Guid.NewGuid()).ToHashSet();
        allowed.Add(workId);

        var items = await new CollectionMediaLookupReadService(_db).LookupAsync(
            null,
            "Books",
            new HashSet<Guid>(),
            allowed,
            0,
            24,
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Wombat Book", item.Title);
    }

    private async Task<List<MediaEngine.Contracts.Search.SearchResultDto>> SearchThroughTheIndexAsync(string query)
    {
        var index = new SearchIndexRepository(_db);
        await index.RebuildAsync(CancellationToken.None);
        return await new CollectionSearchReadService(_db, index).SearchAsync(query, CancellationToken.None);
    }

    private PersonCreditReadService CreateCreditService() =>
        new(new CanonicalValueArrayRepository(_db), new PersonRepository(_db), _db);

    private async Task<Guid> AddPersonAsync(string name, string? qid = null, bool isGroup = false)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO persons (id, name, wikidata_qid, is_group, created_at) VALUES (@id, @name, @qid, @isGroup, datetime('now'));",
            new { id, name, qid, isGroup = isGroup ? 1 : 0 });
        return id;
    }

    private async Task<(Guid WorkId, Guid AssetId)> AddWorkAsync(string mediaType, string title)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind) VALUES (@workId, @mediaType, 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, datetime('now'));
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, datetime('now'));
            """,
            new
            {
                workId,
                editionId,
                assetId,
                mediaType,
                title,
                hash = $"hash_{assetId:N}",
                path = $"/library/{assetId:N}.bin",
            });
        return (workId, assetId);
    }
}
