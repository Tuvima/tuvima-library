using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class UniversalSearchReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public UniversalSearchReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_universal_search_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SearchAsync_ReturnsRankedPeopleAndPlaylistsFromOneQuery()
    {
        var personId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO persons (id, name, biography, occupation, created_at)
                    VALUES ($personId, 'Aurora Drift', 'Electronic musician and composer.', 'Musician', $createdAt);
    
                    INSERT INTO person_roles (person_id, role)
                    VALUES ($personId, 'Artist');
    
                    INSERT INTO collections (id, display_name, collection_type, description, profile_id, created_at)
                    VALUES ($playlistId, 'Aurora Drift Favorites', 'Playlist', 'A saved listening queue.', $profileId, $createdAt);
                    INSERT INTO works (id, collection_id, media_type, work_kind)
                    VALUES ($workId, $playlistId, 'Music', 'standalone');
                    INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'aurora-search', 'C:/music/aurora.flac');
                    INSERT INTO person_media_links (media_asset_id, person_id, role)
                    VALUES ($assetId, $personId, 'Artist');
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'artist', 0, 'Aurora Drift');
                    INSERT INTO collection_items (id, collection_id, work_id)
                    VALUES ($itemId, $playlistId, $workId);
                    """;
                cmd.Parameters.AddWithValue("$personId", GuidSql.ToBlob(personId));
                cmd.Parameters.AddWithValue("$playlistId", GuidSql.ToBlob(playlistId));
                cmd.Parameters.AddWithValue("$profileId", GuidSql.ToBlob(Profile.SeedProfileId));
                cmd.Parameters.AddWithValue("$workId", GuidSql.ToBlob(workId));
                cmd.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
                cmd.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
                cmd.Parameters.AddWithValue("$itemId", GuidSql.ToBlob(Guid.NewGuid()));
                cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow
            {
                WorkId = workId,
                AssetId = assetId,
                CollectionId = playlistId,
                MediaType = "Music",
                Title = "Aurora Drift Song",
                Artist = "Aurora Drift",
            },
        ]));

        var response = await service.SearchAsync("Aurora Drift", 20, CancellationToken.None);

        Assert.Equal(3, response.TotalCount);
        Assert.NotNull(response.TopResult);
        Assert.Equal("person", response.TopResult.EntityType);
        Assert.Equal($"/details/person/{personId:D}", response.TopResult.DetailRoute);
        Assert.Contains(response.Sections, section => section.Key == "people");
        var groupSection = Assert.Single(response.Sections, section => section.Key == "series-collections");
        var playlist = Assert.Single(groupSection.Results);
        Assert.Equal("playlist", playlist.EntityType);
        Assert.Equal($"/listen/music/playlists/{playlistId:D}", playlist.DetailRoute);
    }

    [Fact]
    public async Task SearchAsync_NormalizesOwnedMediaWithoutHydratingDetailModels()
    {
        var workId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var works = new[]
        {
            new SearchResultDto
            {
                WorkId = workId,
                CollectionId = collectionId,
                Title = "Midnight Run",
                Author = "Martin Brest",
                MediaType = "Movies",
                CollectionDisplayName = "Midnight Run",
                Year = "1988",
                Rating = "7.5",
                Description = "A reluctant cross-country journey.",
                CoverUrl = $"/stream/artwork/{Guid.NewGuid():D}",
            },
        };
        var service = CreateService(new StubDisplayProjection(works));

        var response = await service.SearchAsync("Midnight", 12, CancellationToken.None);

        var result = Assert.Single(response.Sections.Single(section => section.Key == "watch").Results);
        Assert.Equal("movie", result.EntityType);
        Assert.Equal("Movie", result.MediaType);
        Assert.Equal("Watch", result.PrimaryActionLabel);
        Assert.Equal($"/details/work/{workId:D}?context=watch", result.DetailRoute);
        Assert.Equal("1988", result.Year);
        Assert.Contains("7.5", result.Facts);
    }

    [Fact]
    public async Task SearchAsync_DoesNotRepeatTheTitleAsCreatorOrSubtitle()
    {
        var workId = Guid.NewGuid();
        var service = CreateService(new StubDisplayProjection(
        [
            new SearchResultDto
            {
                WorkId = workId,
                Title = "Dune",
                Author = "Dune",
                MediaType = "Audiobooks",
                CollectionDisplayName = "Dune",
            },
        ]));

        var response = await service.SearchAsync("Dune", 12, CancellationToken.None);

        Assert.NotNull(response.TopResult);
        Assert.Null(response.TopResult.Creator);
        Assert.Null(response.TopResult.Subtitle);
        Assert.DoesNotContain("Dune", response.TopResult.Facts);
        Assert.Equal($"/details/work/{workId:D}?context=listen", response.TopResult.DetailRoute);
    }

    [Fact]
    public async Task SearchAsync_FindsVisibleCanonicalNetworkPublisherCountryAndFranchiseValues()
    {
        var linkedWorkId = Guid.NewGuid();
        var titleWorkId = Guid.NewGuid();
        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow
            {
                WorkId = linkedWorkId,
                AssetId = Guid.NewGuid(),
                MediaType = "Movies",
                Title = "A Quiet Film",
                SearchNetwork = "Northstar Network",
                SearchPublisher = "Cedar House Press",
                CountryOfOrigin = "New Zealand",
                Franchise = "The Far Meridian",
                Description = "An independent movie from the southern hemisphere.",
            },
            new DisplayWorkRow
            {
                WorkId = titleWorkId,
                AssetId = Guid.NewGuid(),
                MediaType = "Movies",
                Title = "Northstar Network",
            },
        ]));

        var network = await service.SearchAsync("Northstar Network", 20, CancellationToken.None);
        var publisher = await service.SearchAsync("Cedar House Press", 20, CancellationToken.None);
        var country = await service.SearchAsync("New Zealand", 20, CancellationToken.None);
        var franchise = await service.SearchAsync("Far Meridian", 20, CancellationToken.None);

        Assert.Equal(titleWorkId, network.TopResult?.Id);
        var networkMatch = Assert.Single(network.Sections.Single(section => section.Key == "watch").Results,
            result => result.Id == linkedWorkId);
        Assert.Equal("Matched network", networkMatch.MatchReason);
        Assert.Equal("An independent movie from the southern hemisphere.", networkMatch.Description);
        var publisherMatch = Assert.Single(publisher.Sections.Single(section => section.Key == "watch").Results);
        Assert.Equal(linkedWorkId, publisherMatch.Id);
        Assert.Equal("Matched publisher", publisherMatch.MatchReason);
        var countryMatch = Assert.Single(country.Sections.Single(section => section.Key == "watch").Results);
        Assert.Equal(linkedWorkId, countryMatch.Id);
        Assert.Equal("Matched country of origin", countryMatch.MatchReason);
        var franchiseMatch = Assert.Single(franchise.Sections.Single(section => section.Key == "watch").Results);
        Assert.Equal(linkedWorkId, franchiseMatch.Id);
        Assert.Equal("Matched franchise", franchiseMatch.MatchReason);
    }

    [Theory]
    [InlineData("TV", "watch", "/watch/tv?q=Northstar%20Network")]
    [InlineData("Comics", "books", "/read/comics?q=Northstar%20Network")]
    public async Task SearchAsync_MetadataSeeAllPreservesTheMatchingMediaScope(
        string mediaType, string sectionKey, string expectedRoute)
    {
        var service = CreateService(new StubDisplayProjection(new[]
        {
            new DisplayWorkRow
            {
                WorkId = Guid.NewGuid(),
                AssetId = Guid.NewGuid(),
                MediaType = mediaType,
                Title = "Pilot",
                SearchNetwork = "Northstar Network",
            },
        }));

        var response = await service.SearchAsync("Northstar Network", 20, CancellationToken.None);

        Assert.Equal(expectedRoute, Assert.Single(response.Sections,
            section => section.Key == sectionKey).SeeAllRoute);
    }

    [Fact]
    public async Task SearchAsync_DoesNotExposeMatchingMetadataFromWorkOutsideVisibleProjection()
    {
        var hiddenWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@hiddenWorkId, 'TV', 'standalone', 'pending');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @hiddenWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @hash, 'C:/hidden/shows/hidden.mp4');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@hiddenWorkId, 'network', 'Hidden Network', CURRENT_TIMESTAMP);
                """,
                new { hiddenWorkId, editionId, assetId, hash = Guid.NewGuid().ToString("N") });
        }
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Hidden Network", 20, CancellationToken.None);

        Assert.Empty(response.Sections);
        Assert.DoesNotContain(response.Sections.SelectMany(section => section.Results),
            result => result.Id == hiddenWorkId);
    }

    [Fact]
    public async Task SearchAsync_MusicTrackUsesDirectSongPlaybackRoute()
    {
        var workId = Guid.NewGuid();
        var service = CreateService(new StubDisplayProjection(
        [
            new SearchResultDto
            {
                WorkId = workId,
                CollectionId = Guid.NewGuid(),
                Title = "Heroes",
                Author = "David Bowie",
                MediaType = "Music",
            },
        ]));

        var response = await service.SearchAsync("Heroes", 12, CancellationToken.None);

        Assert.Equal($"/listen/music?browse=songs&track={workId:D}", response.TopResult?.DetailRoute);
    }

    [Fact]
    public async Task SearchAsync_FiltersPeopleAndCollectionCountsByAuthorizedProjection()
    {
        var allowedPerson = Guid.NewGuid();
        var deniedPerson = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var allowedWork = Guid.NewGuid();
        var deniedWork = Guid.NewGuid();
        var allowedEdition = Guid.NewGuid();
        var deniedEdition = Guid.NewGuid();
        var allowedAsset = Guid.NewGuid();
        var deniedAsset = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@allowedPerson, 'Shared Name Allowed', CURRENT_TIMESTAMP),
                       (@deniedPerson, 'Shared Name Hidden', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, created_at)
                VALUES (@collectionId, 'Shared Name Shelf', 'Custom', CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                VALUES (@allowedWork, @collectionId, 'Books', 'standalone'),
                       (@deniedWork, @collectionId, 'Books', 'standalone');
                INSERT INTO editions (id, work_id)
                VALUES (@allowedEdition, @allowedWork), (@deniedEdition, @deniedWork);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@allowedAsset, @allowedEdition, @allowedHash, 'C:/books/allowed.epub'),
                       (@deniedAsset, @deniedEdition, @deniedHash, 'C:/books/hidden.epub');
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                VALUES (@allowedAsset, @allowedPerson, 'Author'),
                       (@deniedAsset, @deniedPerson, 'Author');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@allowedWork, 'author', 0, 'Shared Name Allowed'),
                       (@deniedWork, 'author', 0, 'Shared Name Hidden');
                INSERT INTO collection_items (id, collection_id, work_id)
                VALUES (@allowedItem, @collectionId, @allowedWork),
                       (@deniedItem, @collectionId, @deniedWork);
                """,
                new
                {
                    allowedPerson,
                    deniedPerson,
                    collectionId,
                    allowedWork,
                    deniedWork,
                    allowedEdition,
                    deniedEdition,
                    allowedAsset,
                    deniedAsset,
                    allowedHash = Guid.NewGuid().ToString("N"),
                    deniedHash = Guid.NewGuid().ToString("N"),
                    allowedItem = Guid.NewGuid(),
                    deniedItem = Guid.NewGuid(),
                });
        }
        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow
            {
                WorkId = allowedWork,
                AssetId = allowedAsset,
                CollectionId = collectionId,
                MediaType = "Books",
                Title = "Shared Name Book",
            },
        ]));

        var response = await service.SearchAsync("Shared Name", 20, CancellationToken.None);

        var people = response.Sections.Single(section => section.Key == "people").Results;
        Assert.Equal(allowedPerson, Assert.Single(people).Id);
        var collection = Assert.Single(response.Sections
            .Single(section => section.Key == "series-collections").Results);
        Assert.Equal("1 item", collection.Subtitle);
        Assert.DoesNotContain(response.Sections.SelectMany(section => section.Results),
            result => result.Id == deniedPerson);
    }

    [Fact]
    public async Task SearchAsync_CharacterNameReturnsTheOwnedWorksCanonicalPerformer()
    {
        var personId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
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

        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow
            {
                WorkId = workId,
                AssetId = assetId,
                MediaType = "Movies",
                Title = "The Matrix",
            },
        ]));

        var response = await service.SearchAsync("Neo", 20, CancellationToken.None);

        var result = Assert.Single(response.Sections.Single(section => section.Key == "people").Results);
        Assert.Equal(personId, result.Id);
        Assert.Equal("Keanu Reeves", result.Title);
        Assert.Equal("Plays Neo", result.Subtitle);
        Assert.Equal("Matched character portrayal", result.MatchReason);
    }

    [Fact]
    public async Task OwnedWorkSearch_MaterializesCanonicalFactsAndNormalizedCreatorFromGuidBlobStorage()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO works (id, media_type, work_kind)
                    VALUES ($workId, 'Books', 'standalone');
    
                    INSERT INTO editions (id, work_id, format_label)
                    VALUES ($editionId, $workId, 'EPUB');
    
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'dune-search-hash', 'C:/library/books/Dune.epub');
    
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES
                        ($assetId, 'title', 'Dune', $now),
                        ($assetId, 'original_publication_year', '1965', $now),
                        ($assetId, 'description', 'A desert world and a dangerous inheritance.', $now),
                        ($assetId, 'rating', '4.8', $now);
    
                    INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Frank Herbert', $now);
    
                    INSERT INTO person_media_links (media_asset_id, person_id, role)
                    VALUES ($assetId, $personId, 'Author');
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'author', 0, 'Frank Herbert');
                    """;
                cmd.Parameters.AddWithValue("$workId", GuidSql.ToBlob(workId));
                cmd.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
                cmd.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
                cmd.Parameters.AddWithValue("$personId", GuidSql.ToBlob(personId));
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        var results = await new CollectionSearchReadService(_db).SearchAsync("Frank Herbert", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(workId, result.WorkId);
        Assert.Equal("Dune", result.Title);
        Assert.Equal("Frank Herbert", result.Author);
        Assert.Equal("1965", result.Year);
        Assert.Equal("4.8", result.Rating);
        Assert.StartsWith("A desert world", result.Description, StringComparison.Ordinal);
    }

    private UniversalSearchReadService CreateService(IDisplayProjectionReadService display)
    {
        var context = new DefaultHttpContext();
        return new UniversalSearchReadService(
            _db,
            display,
            new HttpContextAccessor { HttpContext = context },
            TestViewAuthorityResolver.Human(Profile.SeedProfileId));
    }

    private sealed class StubDisplayProjection : IDisplayProjectionReadService
    {
        private readonly IReadOnlyList<DisplayWorkRow> _rows;

        public StubDisplayProjection(IReadOnlyList<DisplayWorkRow> rows) => _rows = rows;

        public StubDisplayProjection(IReadOnlyList<SearchResultDto> rows) =>
            _rows = rows.Select(row => new DisplayWorkRow
            {
                WorkId = row.WorkId,
                AssetId = Guid.NewGuid(),
                CollectionId = row.CollectionId,
                Title = row.Title,
                Author = row.Author,
                MediaType = row.MediaType,
                CollectionTitle = row.CollectionDisplayName,
                Series = row.Series,
                SeriesPosition = row.SeriesPosition,
                ShowName = row.ShowName,
                SeasonNumber = row.SeasonNumber,
                EpisodeNumber = row.EpisodeNumber,
                CoverUrl = row.CoverUrl,
                Year = row.Year,
                Description = row.Description,
                Rating = row.Rating,
            }).ToList();

        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) =>
            Task.FromResult(_rows);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);

        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
            Guid? profileId,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
