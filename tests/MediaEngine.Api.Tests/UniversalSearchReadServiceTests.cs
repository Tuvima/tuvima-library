using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.TestSupport;
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
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        TestTemp.ReleaseSqlitePool(_dbPath);
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
    public async Task UniverseSearchHit_OpensTheUniversePage()
    {
        var universeId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var oddUniverseId = Guid.NewGuid();
        var oddSeriesId = Guid.NewGuid();
        var work = Guid.NewGuid();
        var oddWork = Guid.NewGuid();
        var disabledUniverse = Guid.NewGuid();
        var disabledSeries = Guid.NewGuid();
        var privateUniverse = Guid.NewGuid();
        var privateSeries = Guid.NewGuid();
        var otherProfile = Guid.NewGuid();
        var disabledWork = Guid.NewGuid();
        var privateWork = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@otherProfile, 'Someone else', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, wikidata_qid, created_at)
                VALUES (@disabledUniverse, 'Cobalt Disabled', 'Universe', 'Q6000', CURRENT_TIMESTAMP),
                       (@privateUniverse, 'Cobalt Private', 'Universe', 'Q6001', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, parent_collection_id, is_enabled, created_at)
                VALUES (@disabledSeries, 'Cobalt Retired', 'Series', @disabledUniverse, 0, CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, parent_collection_id, scope, owner_kind, audience, profile_id, created_at)
                VALUES (@privateSeries, 'Cobalt Secret', 'Series', @privateUniverse, 'user', 'Profile', 'Private', @otherProfile, CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                VALUES (@disabledWork, @disabledSeries, 'Books', 'standalone'),
                       (@privateWork, @privateSeries, 'Books', 'standalone');
                """,
                new { otherProfile, disabledUniverse, disabledSeries, privateUniverse, privateSeries, disabledWork, privateWork });
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (id, display_name, collection_type, wikidata_qid, created_at)
                VALUES (@universeId, 'Arrakis Saga', 'Universe', 'Q4242', CURRENT_TIMESTAMP),
                       (@oddUniverseId, 'Arrakis Imprint', 'Universe', 'not-a-qid', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, parent_collection_id, created_at)
                VALUES (@seriesId, 'Arrakis Novels', 'Series', @universeId, CURRENT_TIMESTAMP),
                       (@oddSeriesId, 'Arrakis Extras', 'Series', @oddUniverseId, CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                VALUES (@work, @seriesId, 'Books', 'standalone'),
                       (@oddWork, @oddSeriesId, 'Books', 'standalone');
                """,
                new { universeId, seriesId, oddUniverseId, oddSeriesId, work, oddWork });
        }

        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow { WorkId = work, AssetId = Guid.NewGuid(), CollectionId = seriesId, MediaType = "Books", Title = "Arrakis One" },
            new DisplayWorkRow { WorkId = oddWork, AssetId = Guid.NewGuid(), CollectionId = oddSeriesId, MediaType = "Books", Title = "Arrakis Two" },
            new DisplayWorkRow { WorkId = disabledWork, AssetId = Guid.NewGuid(), CollectionId = disabledSeries, MediaType = "Books", Title = "Cobalt One" },
            new DisplayWorkRow { WorkId = privateWork, AssetId = Guid.NewGuid(), CollectionId = privateSeries, MediaType = "Books", Title = "Cobalt Two" },
        ]));

        var response = await service.SearchAsync("Arrakis", 20, CancellationToken.None);
        // A Universe whose only Series is switched off, or private to someone else, would open as "not available": it is not offered.
        var cobalt = (await service.SearchAsync("Cobalt", 20, CancellationToken.None)).Sections.SelectMany(section => section.Results).ToList();
        Assert.DoesNotContain(cobalt, result => result.Id == disabledUniverse || result.Id == privateUniverse);

        var results = response.Sections.SelectMany(section => section.Results).ToList();
        var universe = Assert.Single(results, result => result.Id == universeId);
        Assert.Equal("/universe/Q4242", universe.DetailRoute);
        Assert.Equal("Open Universe", universe.PrimaryActionLabel);
        // A Universe without a well-formed QID, and a Series below a Universe, keep the ordinary collection page.
        Assert.Equal($"/details/collection/{oddUniverseId:D}?context=default", Assert.Single(results, result => result.Id == oddUniverseId).DetailRoute);
        Assert.Equal($"/details/collection/{seriesId:D}?context=default", Assert.Single(results, result => result.Id == seriesId).DetailRoute);
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

        var searchIndex = new SearchIndexRepository(_db);
        await searchIndex.RebuildAsync(CancellationToken.None);

        var results = await new CollectionSearchReadService(_db, searchIndex).SearchAsync("Frank Herbert", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(workId, result.WorkId);
        Assert.Equal("Dune", result.Title);
        Assert.Equal("Frank Herbert", result.Author);
        Assert.Equal("1965", result.Year);
        Assert.Equal("4.8", result.Rating);
        Assert.StartsWith("A desert world", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesSearch_ThroughTheSearchIndex_ReturnsTheMatchingWork()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var otherWorkId = Guid.NewGuid();
        var otherEditionId = Guid.NewGuid();
        var otherAssetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@workId, 'Books', 'standalone'), (@otherWorkId, 'Books', 'standalone');
                INSERT INTO editions (id, work_id)
                VALUES (@editionId, @workId), (@otherEditionId, @otherWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, 'dune-index-hash', 'C:/library/books/Dune.epub'),
                       (@otherAssetId, @otherEditionId, 'emma-index-hash', 'C:/library/books/Emma.epub');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', 'Dune', @now),
                       (@otherAssetId, 'title', 'Emma', @now);
                """,
                new
                {
                    workId,
                    editionId,
                    assetId,
                    otherWorkId,
                    otherEditionId,
                    otherAssetId,
                    now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var searchIndex = new SearchIndexRepository(_db);
        await searchIndex.RebuildAsync(CancellationToken.None);

        var results = await new CollectionSearchReadService(_db, searchIndex).SearchAsync("Dune", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(workId, result.WorkId);
        Assert.Equal("Dune", result.Title);
    }

    [Fact]
    public async Task SearchAsync_ForAPairedApp_ShowsAnAllowedMembersCover_NeverTheCollectionsOwnCover()
    {
        var shelf = await SeedMixedCollectionAsync();
        var service = CreateAppService(shelf.Projection);

        var response = await service.SearchAsync("Mixed Shelf", 20, CancellationToken.None);

        var collection = Assert.Single(response.Sections.Single(section => section.Key == "series-collections").Results);
        // A Kids token gets the cover of the first member it may see that has one (the restricted member is first in order and
        // the next allowed one has no cover), not the collection's raw cover and not a route the app door does not serve.
        Assert.Equal(shelf.AllowedCover, collection.ArtworkUrl);
        Assert.DoesNotContain("/collections/", collection.ArtworkUrl, StringComparison.Ordinal);
        // Only the two members the profile may see are counted; the restricted member is neither counted nor used for the tile.
        Assert.Equal("2 items", collection.Subtitle);
    }

    [Fact]
    public async Task SearchAsync_ForAPairedApp_OmitsCollectionArtworkWhenNoAllowedMemberHasACover()
    {
        var shelf = await SeedMixedCollectionAsync();
        var withoutCovers = shelf.Projection.Rows
            .Select(row => new DisplayWorkRow { WorkId = row.WorkId, AssetId = row.AssetId, MediaType = row.MediaType, Title = row.Title })
            .ToArray();
        var service = CreateAppService(new StubDisplayProjection(withoutCovers));

        var response = await service.SearchAsync("Mixed Shelf", 20, CancellationToken.None);

        var collection = Assert.Single(response.Sections.Single(section => section.Key == "series-collections").Results);
        Assert.Null(collection.ArtworkUrl);
    }

    [Fact]
    public async Task SearchAsync_ForASignedInPerson_KeepsTheCollectionsOwnCoverRoute()
    {
        var shelf = await SeedMixedCollectionAsync();
        var service = CreateService(shelf.Projection);

        var response = await service.SearchAsync("Mixed Shelf", 20, CancellationToken.None);

        var collection = Assert.Single(response.Sections.Single(section => section.Key == "series-collections").Results);
        Assert.Equal($"/collections/{shelf.CollectionId:D}/artwork/poster", collection.ArtworkUrl);
    }

    [Fact]
    public async Task SearchAsync_ForAPairedApp_PicksTheFirstAllowedMemberInCollectionOrder()
    {
        var collectionId = Guid.NewGuid();
        // The member that is first in collection order has the higher id, so picking the lowest id would choose the wrong one.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => GuidSql.ToBlob(id), Comparer<byte[]>.Create(
            (left, right) => left.AsSpan().SequenceCompareTo(right))).ToArray();
        var (lowestId, middleId, firstInOrder) = (ids[0], ids[1], ids[2]);
        var unlinked = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (id, display_name, collection_type, created_at)
                VALUES (@collectionId, 'Ordered Shelf', 'Custom', CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                VALUES (@lowestId, NULL, 'Movies', 'standalone'),
                       (@middleId, @collectionId, 'Movies', 'standalone'),
                       (@firstInOrder, NULL, 'Movies', 'standalone'),
                       (@unlinked, @collectionId, 'Movies', 'standalone');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@item1, @collectionId, @lowestId, 5),
                       (@item2, @collectionId, @middleId, 3),
                       (@item3, @collectionId, @firstInOrder, 1);
                """,
                new { collectionId, lowestId, middleId, firstInOrder, unlinked, item1 = Guid.NewGuid(), item2 = Guid.NewGuid(), item3 = Guid.NewGuid() });
        }

        DisplayWorkRow Row(Guid id, string name) => new()
        {
            WorkId = id,
            AssetId = Guid.NewGuid(),
            MediaType = "Movies",
            Title = name,
            CoverUrl = $"/stream/artwork/{name}",
        };
        var service = CreateAppService(new StubDisplayProjection(
        [
            Row(lowestId, "last"), Row(middleId, "second"), Row(firstInOrder, "first"), Row(unlinked, "unordered"),
        ]));

        var response = await service.SearchAsync("Ordered Shelf", 20, CancellationToken.None);

        var collection = Assert.Single(response.Sections.Single(section => section.Key == "series-collections").Results);
        Assert.Equal("/stream/artwork/first", collection.ArtworkUrl);
        // The one reached only through the work's own collection link counts, but sorts after every ordered item.
        Assert.Equal("4 items", collection.Subtitle);
    }

    // One collection with a raw cover and three members: a restricted one (absent from the authorized projection, first in order),
    // an allowed one without a cover, and an allowed one with a cover.
    private async Task<(Guid CollectionId, StubDisplayProjection Projection, string AllowedCover)> SeedMixedCollectionAsync()
    {
        var collectionId = Guid.NewGuid();
        var firstAllowed = Guid.NewGuid();
        var secondAllowed = Guid.NewGuid();
        var restricted = Guid.NewGuid();
        var allowedCover = $"/stream/artwork/{Guid.NewGuid():D}";
        using (var connection = _db.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (id, display_name, collection_type, cover_artwork_path, created_at)
                VALUES (@collectionId, 'Mixed Shelf', 'Custom', 'C:/covers/restricted-poster.jpg', CURRENT_TIMESTAMP);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                VALUES (@firstAllowed, @collectionId, 'Movies', 'standalone'),
                       (@secondAllowed, @collectionId, 'Movies', 'standalone'),
                       (@restricted, @collectionId, 'Movies', 'standalone');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@item1, @collectionId, @restricted, 0),
                       (@item2, @collectionId, @firstAllowed, 1),
                       (@item3, @collectionId, @secondAllowed, 2);
                """,
                new
                {
                    collectionId,
                    firstAllowed,
                    secondAllowed,
                    restricted,
                    item1 = Guid.NewGuid(),
                    item2 = Guid.NewGuid(),
                    item3 = Guid.NewGuid(),
                });
        }

        var projection = new StubDisplayProjection(
        [
            new DisplayWorkRow { WorkId = firstAllowed, AssetId = Guid.NewGuid(), CollectionId = collectionId, MediaType = "Movies", Title = "Allowed One" },
            new DisplayWorkRow { WorkId = secondAllowed, AssetId = Guid.NewGuid(), CollectionId = collectionId, MediaType = "Movies", Title = "Allowed Two", CoverUrl = allowedCover },
        ]);
        return (collectionId, projection, allowedCover);
    }

    [Fact]
    public async Task SearchAsync_ListsAWaitingTitleAsFileMissing_ForItsOwnProfile_WithNoLinkOrArtwork()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Pilot", "Severance", 2022, "TV"));
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Severance", 20, CancellationToken.None);

        var section = Assert.Single(response.Sections);
        Assert.Equal("file-missing", section.Key);
        Assert.Equal("File missing", section.Title);
        Assert.Null(section.SeeAllRoute);
        var row = Assert.Single(section.Results);
        Assert.Equal("file-missing", row.EntityType);
        Assert.Equal("TV", row.MediaType);
        Assert.Equal("Pilot", row.Title);
        Assert.Equal("Severance", row.Subtitle);
        Assert.Equal("2022", row.Year);
        Assert.Equal("File missing", row.MatchReason);
        Assert.Equal(string.Empty, row.DetailRoute);
        Assert.Null(row.ArtworkUrl);
    }

    [Fact]
    public async Task SearchAsync_DoesNotListAnotherProfilesWaitingTitle()
    {
        var other = AddProfile("Theo");
        Wait(other, WaitingTitle("Dune", null, 2021, "Movies"));
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Empty(response.Sections);
    }

    [Fact]
    public async Task SearchAsync_NeverListsAWithheldWaitingTitle()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune", null, 2021, "Videos") with { Withheld = true });
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Empty(response.Sections);
    }

    [Fact]
    public async Task SearchAsync_NeverListsAViewItemWaitingForItsFile()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Holiday photo", null, 2020, "Photos") with { Target = "LocalItem" });
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Holiday", 20, CancellationToken.None);

        Assert.Empty(response.Sections);
    }

    [Fact]
    public async Task SearchAsync_PutsFileMissingLast_NeverAsTheTopResult_AndCountsOnlyTitlesYouCanOpen()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune Part Two", null, 2024, "Movies"));
        var service = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Movies", Title = "Dune" },
        ]));

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Equal(1, response.TotalCount);
        Assert.Equal("movie", response.TopResult?.EntityType);
        Assert.Equal(["watch", "file-missing"], response.Sections.Select(section => section.Key));
        Assert.Equal(1, response.Sections[^1].TotalCount);
    }

    [Fact]
    public async Task SearchAsync_OnlyFileMissingMatches_HasNoTopResultAndNoOpenableTitles()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune", null, 2021, "Movies"));
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Null(response.TopResult);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal("file-missing", Assert.Single(response.Sections).Key);
    }

    [Fact]
    public async Task SearchAsync_StopsListingAFileMissingTitle_OnceItsStateIsReAttached()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune", null, 2021, "Movies"));
        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));
        Assert.Single((await service.SearchAsync("Dune", 20, CancellationToken.None)).Sections);

        var (workId, assetId) = AddMovie("tt1160419", "returned-hash");
        var pass = new WaitingPersonalStateRepository(_db).RunPass(DateTimeOffset.UtcNow.AddMinutes(1), 100, 90);

        // The title is back in the Library, so the ordinary section lists it (it can be opened) and the waiting row is gone.
        Assert.Equal(1, pass.Attached);
        var returned = CreateService(new StubDisplayProjection(
        [
            new DisplayWorkRow { WorkId = workId, AssetId = assetId, MediaType = "Movies", Title = "Dune" },
        ]));
        var response = await returned.SearchAsync("Dune", 20, CancellationToken.None);
        Assert.Equal("watch", Assert.Single(response.Sections).Key);
        Assert.Equal("Dune", Assert.Single(response.Sections[0].Results).Title);
        Assert.DoesNotContain(response.Sections, section => section.Key == "file-missing");
    }

    [Fact]
    public async Task SearchAsync_DoesNotListFileMissingTitles_ToAPairedApp()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune", null, 2021, "Movies"));
        // The paired app's active profile is the one with the waiting row, so only the kind of caller keeps the section out.
        var service = CreateAppService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()), Profile.SeedProfileId);

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Empty(response.Sections);
    }

    [Fact]
    public async Task SearchAsync_DoesNotListFileMissingTitles_ToAnAnonymousCaller()
    {
        Wait(Profile.SeedProfileId, WaitingTitle("Dune", null, 2021, "Movies"));
        var display = new StubDisplayProjection(Array.Empty<DisplayWorkRow>());
        var anonymous = new UniversalSearchReadService(
            _db,
            display,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            TestViewAuthorityResolver.Anonymous,
            SpoilerTestKit.Create(display, _db),
            new ProfileRepository(_db),
            new WaitingPersonalStateRepository(_db));
        var noRequest = new UniversalSearchReadService(
            _db,
            display,
            new NoRequestAccessor(),
            TestViewAuthorityResolver.Human(Profile.SeedProfileId),
            SpoilerTestKit.Create(display, _db),
            new ProfileRepository(_db),
            new WaitingPersonalStateRepository(_db));

        Assert.Empty((await anonymous.SearchAsync("Dune", 20, CancellationToken.None)).Sections);
        Assert.Empty((await noRequest.SearchAsync("Dune", 20, CancellationToken.None)).Sections);
    }

    [Fact]
    public async Task SearchAsync_ListsAtMostTenFileMissingTitles()
    {
        for (var i = 0; i < 12; i++)
        {
            Wait(Profile.SeedProfileId, WaitingTitle("Dune " + i.ToString("00"), null, 2000 + i, "Movies"));
        }

        var service = CreateService(new StubDisplayProjection(Array.Empty<DisplayWorkRow>()));

        var response = await service.SearchAsync("Dune", 20, CancellationToken.None);

        Assert.Equal(10, Assert.Single(response.Sections).Results.Count);
    }

    private static BackupTitle WaitingTitle(string name, string? parent, int year, string mediaType) =>
        BackupTitle.Empty("t1", "MediaAsset") with
        {
            Ids = new Dictionary<string, string> { ["imdb_id"] = "tt1160419" },
            FileHashes = ["original-hash"],
            Edition = "4K Remux",
            Name = name,
            ParentName = parent,
            Year = year,
            MediaType = mediaType,
        };

    private byte[] AddProfile(string name)
    {
        var id = GuidSql.ToBlob(Guid.NewGuid());
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now)",
            new { id, name, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    // Restores one progress entry for the profile on a title the Library does not have yet.
    private void Wait(Guid profileId, BackupTitle title) => Wait(GuidSql.ToBlob(profileId), title);

    private void Wait(byte[] profile, BackupTitle title)
    {
        var stamp = "2026-10-21T10:00:00Z";
        var table = new BackupExportTable(
            "user_states",
            BackupDataKind.PersonalState,
            ["user_id", "asset_id", "progress_pct", "last_accessed"],
            [[BackupValue.FromBlob(profile), BackupValue.FromTitleRef("t1"), BackupValue.FromReal(10), BackupValue.FromText(stamp)]]);
        var export = new BackupExport(
            [table],
            [],
            [title],
            new BackupExportContents(BackupScope.WholeLibrary, 0, 0, 1, 0, 1, 0, 1, 0),
            []);

        new BackupRestoreRepository(_db).Import(export, "backup.zip", null, null, DateTimeOffset.UtcNow);
    }

    private (Guid Work, Guid Asset) AddMovie(string imdb, string hash)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var work = GuidSql.ToBlob(workId);
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        var asset = GuidSql.ToBlob(assetId);
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'Movies')", new { work });
        conn.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, '4K Remux')", new { edition, work });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
            new { asset, edition, hash, path = $"/media/{hash}.mkv" });
        conn.Execute(
            "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @work, 'imdb_id', @imdb)",
            new { id = GuidSql.ToBlob(Guid.NewGuid()), work, imdb });
        return (workId, assetId);
    }

    // A paired Kids TV: a delegated device token on a restricted profile (never a signed-in person).
    private UniversalSearchReadService CreateAppService(IDisplayProjectionReadService display, Guid? activeProfileId = null) => new(
        _db,
        display,
        new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
        new TestViewAuthorityResolver(new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), activeProfileId ?? Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true, ActiveProfileIsRestricted: true)),
        SpoilerTestKit.Create(display, _db),
        new ProfileRepository(_db),
        new WaitingPersonalStateRepository(_db));

    private UniversalSearchReadService CreateService(IDisplayProjectionReadService display)
    {
        var context = new DefaultHttpContext();
        return new UniversalSearchReadService(
            _db,
            display,
            new HttpContextAccessor { HttpContext = context },
            TestViewAuthorityResolver.Human(Profile.SeedProfileId),
            SpoilerTestKit.Create(display, _db),
            new ProfileRepository(_db),
            new WaitingPersonalStateRepository(_db));
    }

    // HttpContextAccessor shares one ambient context across instances, so a test of "no request" needs its own.
    private sealed class NoRequestAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => null; set { } }
    }

    private sealed class StubDisplayProjection : IDisplayProjectionReadService
    {
        private readonly IReadOnlyList<DisplayWorkRow> _rows;

        public IReadOnlyList<DisplayWorkRow> Rows => _rows;

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
