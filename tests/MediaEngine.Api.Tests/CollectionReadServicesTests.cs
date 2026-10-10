using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class CollectionReadServicesTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly CollectionBrowseReadService _browse;
    private readonly CollectionCatalogReadService _catalog;
    private readonly CollectionMediaLookupReadService _lookup;

    public CollectionReadServicesTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_reads_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var collectionRepository = new CollectionRepository(_database);
        _browse = new CollectionBrowseReadService(
            collectionRepository,
            _database,
            NullLogger<CollectionBrowseReadService>.Instance);
        _lookup = new CollectionMediaLookupReadService(_database);
        _catalog = new CollectionCatalogReadService(
            collectionRepository,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            _lookup,
            _database,
            NullLogger<CollectionCatalogReadService>.Instance);
    }

    [Fact]
    public async Task BrowseReads_ResolveHierarchyAssetsPaletteAndMusicDetail()
    {
        var seeded = await SeedMusicHierarchyAsync();

        var root = await _browse.GetRootWorkIdAsync(seeded.TrackWorkId, CancellationToken.None);
        var assets = await _browse.GetPrimaryAssetIdsAsync([seeded.TrackWorkId], CancellationToken.None);
        var palette = await _browse.GetAssetPaletteAsync(seeded.AlbumWorkId, CancellationToken.None);
        var artistRows = await _browse.GetArtistWorksAsync("The Artist", CancellationToken.None);
        var detailRows = await _browse.GetSystemViewDetailWorksAsync(
            "album",
            "The Album",
            "Music",
            "The Artist",
            CancellationToken.None);

        Assert.Equal(seeded.AlbumWorkId, root);
        Assert.Equal(seeded.AssetId, assets[seeded.TrackWorkId]);
        Assert.Equal("#112233", palette?.PrimaryHex);
        var artistRow = Assert.Single(artistRows);
        Assert.Equal(seeded.TrackWorkId, artistRow.WorkId);
        Assert.Equal("The Album", artistRow.Album);
        var detailRow = Assert.Single(detailRows);
        Assert.Equal(seeded.AlbumWorkId, detailRow.RootWorkId);
        Assert.Equal("Track One", detailRow.Title);
    }

    [Fact]
    public async Task MetadataLookup_IsSetBasedAndPreservesRequestedOrder()
    {
        var first = await SeedMusicHierarchyAsync("First Track", "hash-first");
        var second = await SeedMusicHierarchyAsync("Second Track", "hash-second");

        var results = await _lookup.ResolveMetadataAsync(
            [second.TrackWorkId, first.TrackWorkId],
            CancellationToken.None);

        Assert.Collection(
            results,
            item =>
            {
                Assert.Equal(second.TrackWorkId, item.EntityId);
                Assert.Equal("Second Track", item.Title);
                Assert.Equal("The Artist", item.Creator);
                Assert.Equal($"/stream/{second.AssetId:D}/cover", item.CoverUrl);
            },
            item =>
            {
                Assert.Equal(first.TrackWorkId, item.EntityId);
                Assert.Equal("First Track", item.Title);
            });
    }

    [Fact]
    public async Task MetadataLookup_PreservesEvaluatorSortOrderForResolvedCollections()
    {
        var first = await SeedMusicHierarchyAsync("First Track", "hash-sorted-first");
        var second = await SeedMusicHierarchyAsync("Second Track", "hash-sorted-second");

        // CollectionRuleEvaluator supplies work IDs in the persisted collection sort.
        // The metadata lookup must not silently re-sort that result by title.
        var results = await _lookup.ResolveMetadataAsync(
            [second.TrackWorkId, first.TrackWorkId],
            CancellationToken.None);

        Assert.Equal([second.TrackWorkId, first.TrackWorkId], results.Select(item => item.EntityId));
    }

    [Fact]
    public async Task MediaTypeCounts_AreCalculatedForEveryMatchedRootWork()
    {
        var movieId = Guid.NewGuid();
        var comicOneId = Guid.NewGuid();
        var comicTwoId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind) VALUES
                    (@MovieId, 'Movies', 'standalone'),
                    (@ComicOneId, 'Comics', 'standalone'),
                    (@ComicTwoId, 'Comics', 'standalone');
                """,
                new { MovieId = movieId, ComicOneId = comicOneId, ComicTwoId = comicTwoId });
        }

        var counts = await _lookup.CountMediaTypesAsync(
            [movieId, comicOneId, comicTwoId],
            CancellationToken.None);

        Assert.Equal(1, counts["Movies"]);
        Assert.Equal(2, counts["Comics"]);
    }

    [Fact]
    public async Task CollectionCatalog_SkipsUnsupportedAndDisabledManagedCollections()
    {
        var supportedId = Guid.NewGuid();
        var unsupportedId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, is_enabled)
                VALUES (
                    @SupportedId, 'Supported collection', 'Custom', 'library', 'materialized', 1);
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, is_enabled, rule_json)
                VALUES (
                    @UnsupportedId, 'Unsupported collection', 'Custom', 'library', 'query', 1,
                    '{"field":"genre","operator":"equals","value":"Drama"}');
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, is_enabled)
                VALUES (
                    @DisabledId, 'Disabled collection', 'Custom', 'library', 'materialized', 0);
                """,
                new { SupportedId = supportedId, UnsupportedId = unsupportedId, DisabledId = disabledId });
        }

        var managed = await _catalog.GetManagedAsync(null, CancellationToken.None);
        var entry = Assert.Single(managed);
        Assert.Equal(supportedId, entry.Id);
    }

    [Fact]
    public async Task CollectionCatalog_AppliesAllowedWorksBeforeCountsAndArtwork()
    {
        var collectionId = Guid.NewGuid();
        var allowedWorkId = Guid.NewGuid();
        var hiddenWorkId = Guid.NewGuid();
        var allowedAssetId = Guid.NewGuid();
        var hiddenAssetId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections
                    (id, display_name, collection_type, scope, resolution, is_enabled)
                VALUES (@collectionId, 'Scoped collection', 'Custom', 'library', 'materialized', 1);
                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                VALUES (@allowedWorkId, 'Books', 'standalone', 'Owned', 'Accepted'),
                       (@hiddenWorkId, 'Movies', 'standalone', 'Owned', 'Accepted');
                INSERT INTO editions (id, work_id)
                VALUES (@allowedEditionId, @allowedWorkId), (@hiddenEditionId, @hiddenWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@allowedAssetId, @allowedEditionId, @allowedHash, 'C:/library/allowed.epub'),
                       (@hiddenAssetId, @hiddenEditionId, @hiddenHash, 'C:/library/hidden.mkv');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@hiddenItemId, @collectionId, @hiddenWorkId, 0),
                       (@allowedItemId, @collectionId, @allowedWorkId, 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@allowedAssetId, 'title', 'Allowed book', CURRENT_TIMESTAMP),
                       (@allowedAssetId, 'cover_url', '/allowed.jpg', CURRENT_TIMESTAMP),
                       (@hiddenAssetId, 'title', 'Hidden movie', CURRENT_TIMESTAMP),
                       (@hiddenAssetId, 'cover_url', '/hidden.jpg', CURRENT_TIMESTAMP);
                """,
                new
                {
                    collectionId,
                    allowedWorkId,
                    hiddenWorkId,
                    allowedAssetId,
                    hiddenAssetId,
                    allowedEditionId = Guid.NewGuid(),
                    hiddenEditionId = Guid.NewGuid(),
                    allowedHash = Guid.NewGuid().ToString("N"),
                    hiddenHash = Guid.NewGuid().ToString("N"),
                    allowedItemId = Guid.NewGuid(),
                    hiddenItemId = Guid.NewGuid(),
                });
        }

        var entry = Assert.Single(await _catalog.GetCatalogAsync(
            null,
            CancellationToken.None,
            new HashSet<Guid> { allowedWorkId }));

        Assert.Equal(1, entry.ItemCount);
        Assert.Equal(1, entry.ReadCount);
        Assert.Equal(0, entry.WatchCount);
        Assert.Equal(allowedWorkId, Assert.Single(entry.ArtworkItems).WorkId);
        Assert.DoesNotContain(entry.ArtworkItems, item => item.WorkId == hiddenWorkId);

        var items = await _catalog.GetItemsAsync(
            collectionId,
            null,
            1,
            CancellationToken.None,
            new HashSet<Guid> { allowedWorkId });
        Assert.Equal(allowedWorkId, Assert.Single(items.Items).WorkId);

        var managed = Assert.Single(await _catalog.GetManagedAsync(
            null,
            CancellationToken.None,
            new HashSet<Guid> { allowedWorkId }));
        Assert.Equal(1, managed.ItemCount);
    }

    [Fact]
    public async Task QueryCollection_UsesItsPersistedSortForActualItems()
    {
        var collectionId = Guid.NewGuid();
        var alphaId = Guid.NewGuid();
        var zuluId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var definition = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }]);
        var ruleJson = JsonSerializer.Serialize(definition);

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, is_enabled,
                    rule_json, sort_field, sort_direction)
                VALUES (
                    @CollectionId, 'Sorted movies', 'Custom', 'library', 'query', 1,
                    @RuleJson, 'title', 'desc');

                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                VALUES
                    (@AlphaId, 'Movies', 'standalone', 'Owned', 'Accepted'),
                    (@ZuluId, 'Movies', 'standalone', 'Owned', 'Accepted');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    (@AlphaId, 'title', 'Alpha', @Now),
                    (@ZuluId, 'title', 'Zulu', @Now);
                """,
                new { CollectionId = collectionId, AlphaId = alphaId, ZuluId = zuluId, RuleJson = ruleJson, Now = now });

            foreach (var workId in new[] { alphaId, zuluId })
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO editions (id, work_id) VALUES (@EditionId, @WorkId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
                    """,
                    new
                    {
                        EditionId = Guid.NewGuid(),
                        WorkId = workId,
                        AssetId = Guid.NewGuid(),
                        ContentHash = $"sort-{workId:N}",
                        FilePath = $"C:/library/{workId:N}.mkv",
                    });
            }
        }

        var result = await _catalog.GetItemsAsync(collectionId, null, 20, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(["Zulu", "Alpha"], result.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task QueryCollectionCatalogRetainsAuthorizedTvShowTileAndArtwork()
    {
        var collectionId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var definition = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "TV" }]);
        var ruleJson = JsonSerializer.Serialize(definition);

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections
                    (id, display_name, collection_type, scope, resolution, is_enabled, rule_json)
                VALUES
                    (@collectionId, 'Smart television', 'Custom', 'library', 'query', 1, @ruleJson);

                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@showId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, curator_state)
                VALUES (@seasonId, 'TV', 'parent', @showId, 'accepted'),
                       (@episodeId, 'TV', 'child', @seasonId, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeId);
                INSERT INTO media_assets
                    (id, edition_id, content_hash, file_path_root, status, is_orphaned)
                VALUES
                    (@assetId, @editionId, @contentHash, @filePath, 'Normal', 0);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@showId, 'title', 'The Smart Show', CURRENT_TIMESTAMP);
                """,
                new
                {
                    collectionId,
                    showId,
                    seasonId,
                    episodeId,
                    editionId,
                    assetId,
                    ruleJson,
                    contentHash = Guid.NewGuid().ToString("N"),
                    filePath = $"C:/library/{assetId:N}.mkv",
                });
        }

        var entry = Assert.Single(await _catalog.GetCatalogAsync(
            null,
            CancellationToken.None,
            new HashSet<Guid> { episodeId }));

        Assert.Equal(collectionId, entry.Id);
        Assert.Equal(1, entry.ItemCount);
        var artwork = Assert.Single(entry.ArtworkItems);
        Assert.Equal(showId, artwork.WorkId);
        Assert.Equal($"/stream/{assetId:D}/cover", artwork.CoverUrl);
    }

    [Fact]
    public async Task CollectionItems_ResolveGuidBlobMembershipAndManagedArtwork()
    {
        var seeded = await SeedMusicHierarchyAsync();
        var collectionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var results = await _lookup.ResolveItemsAsync(
            collectionId,
            [new CollectionItem
            {
                Id = itemId,
                CollectionId = collectionId,
                WorkId = seeded.TrackWorkId,
                SortOrder = 3,
            }],
            CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(itemId, item.Id);
        Assert.Equal(seeded.AlbumWorkId, item.WorkId);
        Assert.Equal("The Album", item.Title);
        Assert.Equal("The Artist", item.Creator);
        Assert.Equal("Music", item.MediaType);
        Assert.Equal($"/stream/{seeded.AssetId:D}/cover", item.CoverUrl);
        Assert.Equal(3, item.SortOrder);
    }

    [Fact]
    public async Task CollectionItems_CollapseTvEpisodesToShowPosterAndShowRoute()
    {
        var collectionId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var posterAssetId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind) VALUES (@ShowId, 'TV', 'parent');
                INSERT INTO works (id, parent_work_id, media_type, work_kind) VALUES (@SeasonId, @ShowId, 'TV', 'parent');
                INSERT INTO works (id, parent_work_id, media_type, work_kind) VALUES (@EpisodeId, @SeasonId, 'TV', 'child');
                INSERT INTO editions (id, work_id) VALUES (@EditionId, @EpisodeId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@AssetId, @EditionId, 'collection-tv-poster-test', 'C:/library/Test Show/S01E01.mkv');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    (@ShowId, 'title', 'Test Show', @Now),
                    (@ShowId, 'poster_url', '/art/test-show-poster.jpg', @Now),
                    (@EpisodeId, 'episode_still_url', '/art/test-show-s01e01.jpg', @Now);
                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, aspect_class,
                    local_image_path, is_preferred, created_at)
                VALUES (
                    @PosterAssetId, @ShowId, 'Work', 'SeasonPoster', 'Portrait',
                    'C:/managed-art/test-show-poster.jpg', 1, @Now);
                """,
                new { ShowId = showId, SeasonId = seasonId, EpisodeId = episodeId, EditionId = editionId, AssetId = assetId, PosterAssetId = posterAssetId, Now = now });
        }

        var result = Assert.Single(await _lookup.ResolveItemsAsync(
            collectionId,
            [new CollectionItem { Id = itemId, CollectionId = collectionId, WorkId = episodeId, SortOrder = 1 }],
            CancellationToken.None));

        Assert.Equal(showId, result.WorkId);
        Assert.Equal("Test Show", result.Title);
        Assert.Equal($"/stream/artwork/{posterAssetId:D}", result.CoverUrl);
        Assert.Equal($"/details/tvshow/{showId:D}?context=watch", result.DetailRoute);
        Assert.DoesNotContain("s01e01", result.CoverUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemViewGroups_ReturnOrderedArtworkPreviewsAndNullableSqliteDimensions()
    {
        var first = await SeedBookSeriesMemberAsync("First Book", "1", 1649, "book-series-first", 1997);
        var second = await SeedBookSeriesMemberAsync("Second Book", "2", 1800, "book-series-second", 2024);

        var groups = await _browse.GetSystemViewGroupsAsync("Books", "series", CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal("The Test Series", group.DisplayName);
        Assert.Equal(2, group.WorkCount);
        Assert.Equal(1997, group.EarliestYear);
        Assert.Equal(2024, group.LatestYear);
        Assert.Contains(group.CoverWidthPx, new int?[] { 1649, 1800 });
        Assert.Collection(
            group.PreviewItems,
            item =>
            {
                Assert.Equal(first.WorkId, item.WorkId);
                Assert.Equal($"/stream/{first.AssetId:D}/cover", item.ImageUrl);
                Assert.Equal("1", item.Position);
            },
            item =>
            {
                Assert.Equal(second.WorkId, item.WorkId);
                Assert.Equal("2", item.Position);
            });
    }

    [Fact]
    public async Task SeriesOverrides_PreserveMembershipAndRestoreOrderedAutomaticArtwork()
    {
        var first = await SeedBookSeriesMemberAsync("First Book", "1", 1649, "override-first", 1997);
        var second = await SeedBookSeriesMemberAsync("Second Book", "2", 1800, "override-second", 2024);
        var rootId = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, display_overrides_json)
            VALUES (@rootId, 'Books', 'parent', '{"title":"My shelf title","description":"My shelf description"}');
            UPDATE works SET parent_work_id = @rootId, work_kind = 'child' WHERE id IN (@firstId, @secondId);
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path, is_preferred, is_user_override)
            VALUES (@artworkId, @rootId, 'Work', 'CoverArt', 'custom-cover.jpg', 1, 1);
            """, new { rootId, artworkId, firstId = first.WorkId, secondId = second.WorkId });

        var group = Assert.Single(await _browse.GetSystemViewGroupsAsync("Books", "series", CancellationToken.None));
        Assert.Equal(rootId, group.RootWorkId);
        Assert.Equal("My shelf title", group.DisplayName);
        Assert.Equal("My shelf description", group.Description);
        Assert.Equal($"/stream/artwork/{artworkId:D}", Assert.Single(group.PreviewItems).ImageUrl);
        Assert.Equal(group.PreviewItems[0].ImageUrl, group.CoverUrl);
        var routeId = SystemViewGroupIdentity.CreateId(group, "Books", "series");
        var members = await _browse.GetSystemViewDetailWorksAsync(
            "series", group.DisplayName, "Books", null, CancellationToken.None, rootId);
        Assert.Equal(2, members.Count);
        Assert.Equal(new[] { first.WorkId, second.WorkId }, members.Select(member => member.WorkId));

        await connection.ExecuteAsync("DELETE FROM entity_assets WHERE id = @artworkId; UPDATE works SET display_overrides_json = '{}' WHERE id = @rootId;", new { artworkId, rootId });
        var restored = Assert.Single(await _browse.GetSystemViewGroupsAsync("Books", "series", CancellationToken.None));
        Assert.Equal(routeId, SystemViewGroupIdentity.CreateId(restored, "Books", "series"));
        Assert.Equal("The Test Series", restored.DisplayName);
        Assert.Equal(new[] { first.WorkId, second.WorkId }, restored.PreviewItems.Select(item => item.WorkId));
    }

    [Fact]
    public async Task SystemViewGroups_MusicAlbumsExposeTheTrackLevelArtist()
    {
        var seeded = await SeedMusicHierarchyAsync();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM canonical_value_arrays
                WHERE key IN ('album_artist', 'artist') AND entity_id IN (@AlbumWorkId, @AssetId);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@TrackWorkId, 'author', 0, 'The Artist');
                """,
                new
                {
                    seeded.AlbumWorkId,
                    seeded.TrackWorkId,
                    seeded.AssetId,
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var groups = await _browse.GetSystemViewGroupsAsync("Music", "album", CancellationToken.None);
        var detailRows = await _browse.GetSystemViewDetailWorksAsync(
            "album",
            "The Album",
            "Music",
            "The Artist",
            CancellationToken.None);

        var group = Assert.Single(groups);
        var detail = Assert.Single(detailRows);
        Assert.Equal("The Album", group.DisplayName);
        Assert.Equal("The Artist", group.Creator);
        Assert.Equal("The Artist", detail.Author);
    }

    [Fact]
    public async Task MusicAlbumGroups_SameNamedRoots_ResolveFromEveryMemberRootId()
    {
        var first = await SeedMusicHierarchyAsync("Track One", "hash-sn-1");
        var second = await SeedMusicHierarchyAsync("Track Two", "hash-sn-2");
        var other = await SeedMusicHierarchyAsync("Elsewhere", "hash-sn-3");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                UPDATE canonical_values SET value = 'Another Album'
                WHERE key IN ('album', 'title') AND entity_id IN (@AlbumWorkId, @AssetId) AND value = 'The Album';
                """,
                new { other.AlbumWorkId, other.AssetId });
        }

        var groups = await _browse.GetSystemViewGroupsAsync("Music", "album", CancellationToken.None);
        var merged = Assert.Single(groups, group => group.DisplayName == "The Album");
        Assert.Equal(2, merged.WorkCount);

        var viaFirst = await _browse.GetMusicAlbumGroupForRootAsync(first.AlbumWorkId, CancellationToken.None);
        var viaSecond = await _browse.GetMusicAlbumGroupForRootAsync(second.AlbumWorkId, CancellationToken.None);
        var viaOther = await _browse.GetMusicAlbumGroupForRootAsync(other.AlbumWorkId, CancellationToken.None);

        Assert.Equal("The Album", viaFirst?.DisplayName);
        Assert.Equal("The Album", viaSecond?.DisplayName);
        Assert.Equal(viaFirst?.RootWorkId, viaSecond?.RootWorkId);
        Assert.Equal("Another Album", viaOther?.DisplayName);
        Assert.Null(await _browse.GetMusicAlbumGroupForRootAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await _browse.GetMusicAlbumGroupForRootAsync(first.TrackWorkId, CancellationToken.None));
    }

    [Fact]
    public async Task MusicAlbumGroups_SameNamedAlbumsByDifferentArtists_AreSeparateGroupsThatResolveTheirOwnDetail()
    {
        var queen = await SeedMusicHierarchyAsync("Under Pressure", "hash-ga-queen", artist: "Queen");
        var abba = await SeedMusicHierarchyAsync("Dancing Queen", "hash-ga-abba", artist: "ABBA");
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                UPDATE canonical_values SET value = 'Greatest Hits'
                WHERE key IN ('album', 'title') AND entity_id IN (@QueenAlbum, @QueenAsset, @AbbaAlbum, @AbbaAsset)
                  AND value = 'The Album';
                """,
                new
                {
                    QueenAlbum = queen.AlbumWorkId,
                    QueenAsset = queen.AssetId,
                    AbbaAlbum = abba.AlbumWorkId,
                    AbbaAsset = abba.AssetId,
                });
        }

        var groups = await _browse.GetSystemViewGroupsAsync("Music", "album", CancellationToken.None);

        Assert.Equal(2, groups.Count(group => group.DisplayName == "Greatest Hits"));
        var queenGroup = Assert.Single(groups, group => group.Creator == "Queen");
        var abbaGroup = Assert.Single(groups, group => group.Creator == "ABBA");
        Assert.Equal(queen.AlbumWorkId, queenGroup.RootWorkId);
        Assert.Equal(abba.AlbumWorkId, abbaGroup.RootWorkId);
        Assert.Equal(1, queenGroup.WorkCount);
        Assert.Equal(1, abbaGroup.WorkCount);
        Assert.Equal(queen.TrackWorkId, Assert.Single(queenGroup.PreviewItems).WorkId);
        Assert.Equal(abba.TrackWorkId, Assert.Single(abbaGroup.PreviewItems).WorkId);
        Assert.NotEqual(
            SystemViewGroupIdentity.CreateId(queenGroup, "Music", "album"),
            SystemViewGroupIdentity.CreateId(abbaGroup, "Music", "album"));

        // Each root resolves its own group, and each group's detail lists only its own tracks.
        var viaQueen = await _browse.GetMusicAlbumGroupForRootAsync(queen.AlbumWorkId, CancellationToken.None);
        var viaAbba = await _browse.GetMusicAlbumGroupForRootAsync(abba.AlbumWorkId, CancellationToken.None);
        Assert.Equal("Queen", viaQueen?.Creator);
        Assert.Equal("ABBA", viaAbba?.Creator);
        var queenTracks = await _browse.GetSystemViewDetailWorksAsync(
            "album", viaQueen!.DisplayName, "Music", viaQueen.Creator, CancellationToken.None);
        var abbaTracks = await _browse.GetSystemViewDetailWorksAsync(
            "album", viaAbba!.DisplayName, "Music", viaAbba.Creator, CancellationToken.None);
        Assert.Equal(queen.TrackWorkId, Assert.Single(queenTracks).WorkId);
        Assert.Equal(abba.TrackWorkId, Assert.Single(abbaTracks).WorkId);
    }

    [Fact]
    public async Task MusicAlbumGroups_SameArtistSplitRoots_StayOneGroup_EvenWhenArtistCaseDiffers()
    {
        var first = await SeedMusicHierarchyAsync("Track One", "hash-sa-1", artist: "The Artist");
        var second = await SeedMusicHierarchyAsync("Track Two", "hash-sa-2", artist: "the artist");

        var groups = await _browse.GetSystemViewGroupsAsync("Music", "album", CancellationToken.None);

        var merged = Assert.Single(groups);
        Assert.Equal(2, merged.WorkCount);
        Assert.Equal(2, merged.PreviewItems.Count);
        var viaFirst = await _browse.GetMusicAlbumGroupForRootAsync(first.AlbumWorkId, CancellationToken.None);
        var viaSecond = await _browse.GetMusicAlbumGroupForRootAsync(second.AlbumWorkId, CancellationToken.None);
        Assert.Equal(merged.RootWorkId, viaFirst?.RootWorkId);
        Assert.Equal(merged.RootWorkId, viaSecond?.RootWorkId);
    }

    [Fact]
    public async Task MusicAlbumDetail_SameNamedRoots_BothRootIdsOpenADetailPage()
    {
        var first = await SeedMusicHierarchyAsync("Track One", "hash-sd-1");
        var second = await SeedMusicHierarchyAsync("Track Two", "hash-sd-2");
        var composer = new DetailComposerService(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            null!,
            new DetailRecommendationService(_database),
            collectionBrowse: _browse);

        foreach (var rootId in new[] { first.AlbumWorkId, second.AlbumWorkId })
        {
            var detail = await composer.BuildAsync(DetailEntityType.MusicAlbum, rootId, DetailPresentationContext.Listen);

            Assert.NotNull(detail);
            Assert.Equal("The Album", detail.Title);
            Assert.Equal(DetailEntityType.MusicAlbum, detail.EntityType);
        }
    }

    [Fact]
    public async Task MusicAlbumGrouping_StopsAtImmediateAlbumParent()
    {
        var seeded = await SeedMusicHierarchyAsync();
        var catalogueWorkId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@CatalogueWorkId, 'Music', 'parent');
                UPDATE works
                SET parent_work_id = @CatalogueWorkId
                WHERE id = @AlbumWorkId;
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    (@CatalogueWorkId, 'title', 'Artist Catalogue', @Now),
                    (@CatalogueWorkId, 'album', 'Artist Catalogue', @Now);
                """,
                new
                {
                    CatalogueWorkId = catalogueWorkId,
                    seeded.AlbumWorkId,
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var groups = await _browse.GetSystemViewGroupsAsync("Music", "album", CancellationToken.None);
        var group = Assert.Single(groups);
        var detailRows = await _browse.GetSystemViewDetailWorksAsync(
            "album",
            "The Album",
            "Music",
            "The Artist",
            CancellationToken.None);
        var projected = Assert.Single(await new DisplayWorkProjectionReader(_database).LoadAsync(CancellationToken.None));

        Assert.Equal("The Album", group.DisplayName);
        Assert.Equal(seeded.AlbumWorkId, group.RootWorkId);
        Assert.Equal(seeded.AlbumWorkId, Assert.Single(detailRows).RootWorkId);
        Assert.Equal(seeded.AlbumWorkId, projected.RootWorkId);
    }

    [Theory]
    [InlineData("Books", "author", "Author", "Author", "Ursula K. Le Guin")]
    [InlineData("Comics", "author", "Author", "Creator", "Marjane Satrapi")]
    [InlineData("Movies", "director", "Director", "Director", "Denis Villeneuve")]
    [InlineData("Music", "artist", "Performer", "Artist", "Nina Simone")]
    [InlineData("Audiobooks", "narrator", "Narrator", "Narrator", "Bahni Turpin")]
    public async Task SystemViewGroups_ResolvePersonIdentityForEveryLane(
        string mediaType,
        string groupField,
        string role,
        string displayRole,
        string personName)
    {
        var seeded = await SeedCreditedWorkAsync(mediaType, groupField, role, personName);

        var groups = await _browse.GetSystemViewGroupsAsync(mediaType, groupField, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(personName, group.DisplayName);
        Assert.Equal(personName, group.Creator);
        Assert.Equal(seeded.WorkId, Assert.Single(group.PreviewItems).WorkId);
        Assert.Equal(seeded.PersonId, group.PersonId);
        Assert.Equal($"/persons/{seeded.PersonId:D}/headshot", group.PersonPhotoUrl);
        Assert.Contains(displayRole, group.PersonRoles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemViewGroups_PreferTheEnrichedArtistWhenDuplicateNamesExist()
    {
        var seeded = await SeedCreditedWorkAsync("Music", "artist", "Author", "Hans Zimmer");
        var enrichedPersonId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                UPDATE persons
                SET wikidata_qid = 'Q-WRONG',
                    headshot_url = NULL,
                    local_headshot_path = NULL,
                    biography = NULL,
                    enriched_at = NULL
                WHERE id = @StubPersonId;

                INSERT INTO persons (
                    id,
                    name,
                    wikidata_qid,
                    biography,
                    occupation,
                    local_headshot_path,
                    created_at,
                    enriched_at)
                VALUES (
                    @EnrichedPersonId,
                    'Hans Zimmer',
                    'Q-CANONICAL',
                    'Canonical composer biography',
                    'composer',
                    'C:\assets\people\hans-zimmer.jpg',
                    @Now,
                    @Now);

                INSERT INTO person_roles (person_id, role)
                VALUES (@EnrichedPersonId, 'Performer');
                """,
                new
                {
                    StubPersonId = seeded.PersonId,
                    EnrichedPersonId = enrichedPersonId,
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }

        var group = Assert.Single(await _browse.GetSystemViewGroupsAsync(
            "Music",
            "artist",
            CancellationToken.None));

        Assert.Equal(enrichedPersonId, group.PersonId);
        Assert.Equal($"/persons/{enrichedPersonId:D}/headshot", group.PersonPhotoUrl);
        Assert.Contains("Artist", group.PersonRoles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Performer", group.PersonRoles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TvNetworkAndTimelineGroups_CountEachShowOnceAndUseTheShowPremiereYear()
    {
        var showWorkId = Guid.NewGuid();
        var seasonWorkId = Guid.NewGuid();
        var firstEpisodeId = Guid.NewGuid();
        var secondEpisodeId = Guid.NewGuid();
        var firstEditionId = Guid.NewGuid();
        var secondEditionId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();
        var showPosterAssetId = Guid.NewGuid();
        var seasonPosterAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@ShowWorkId, 'TV', 'parent');
                INSERT INTO works (id, media_type, work_kind, parent_work_id)
                VALUES (@SeasonWorkId, 'TV', 'parent', @ShowWorkId);
                INSERT INTO works (id, media_type, work_kind, parent_work_id)
                VALUES
                    (@FirstEpisodeId, 'TV', 'child', @SeasonWorkId),
                    (@SecondEpisodeId, 'TV', 'child', @SeasonWorkId);

                INSERT INTO editions (id, work_id)
                VALUES
                    (@FirstEditionId, @FirstEpisodeId),
                    (@SecondEditionId, @SecondEpisodeId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES
                    (@FirstAssetId, @FirstEditionId, 'tv-network-first', 'C:/library/Test Show/S01E01.mkv'),
                    (@SecondAssetId, @SecondEditionId, 'tv-network-second', 'C:/library/Test Show/S01E02.mkv');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    (@ShowWorkId, 'title', 'Test Show', @Now),
                    (@ShowWorkId, 'network', 'HBO', @Now),
                    (@ShowWorkId, 'release_year', '2020', @Now),
                    (@FirstAssetId, 'show_name', 'Test Show', @Now),
                    (@FirstAssetId, 'network', 'HBO', @Now),
                    (@FirstAssetId, 'air_date', '2020-01-05', @Now),
                    (@FirstAssetId, 'season_number', '1', @Now),
                    (@SecondAssetId, 'show_name', 'Test Show', @Now),
                    (@SecondAssetId, 'network', 'HBO', @Now),
                    (@SecondAssetId, 'air_date', '2023-06-12', @Now),
                    (@SecondAssetId, 'season_number', '1', @Now);

                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, aspect_class,
                    local_image_path, is_preferred, created_at)
                VALUES
                    (@ShowPosterAssetId, @ShowWorkId, 'Work', 'CoverArt', 'Portrait',
                     'C:/managed-art/test-show/poster.jpg', 1, @Now),
                    (@SeasonPosterAssetId, @SeasonWorkId, 'Work', 'SeasonPoster', 'Portrait',
                     'C:/managed-art/test-show/season-01-poster.jpg', 1, @Now);
                """,
                new
                {
                    ShowWorkId = showWorkId,
                    SeasonWorkId = seasonWorkId,
                    FirstEpisodeId = firstEpisodeId,
                    SecondEpisodeId = secondEpisodeId,
                    FirstEditionId = firstEditionId,
                    SecondEditionId = secondEditionId,
                    FirstAssetId = firstAssetId,
                    SecondAssetId = secondAssetId,
                    ShowPosterAssetId = showPosterAssetId,
                    SeasonPosterAssetId = seasonPosterAssetId,
                    Now = now,
                });
        }

        var network = Assert.Single(await _browse.GetSystemViewGroupsAsync(
            "TV",
            "network",
            CancellationToken.None));
        var timeline = Assert.Single(await _browse.GetSystemViewGroupsAsync(
            "TV",
            "show_name",
            CancellationToken.None));

        Assert.Equal("HBO", network.DisplayName);
        Assert.Equal(1, network.WorkCount);
        Assert.Null(network.SeasonCount);
        Assert.Equal(showWorkId, Assert.Single(network.PreviewItems).WorkId);

        Assert.Equal("Test Show", timeline.DisplayName);
        Assert.Equal(showWorkId, timeline.RootWorkId);
        Assert.Equal(1, timeline.WorkCount);
        Assert.Equal(1, timeline.SeasonCount);
        Assert.Equal(2020, timeline.EarliestYear);
        Assert.Equal(2020, timeline.LatestYear);
        Assert.Equal("2020", timeline.Year);
        Assert.Equal($"/stream/artwork/{showPosterAssetId:D}", timeline.CoverUrl);
        Assert.DoesNotContain(seasonPosterAssetId.ToString("D"), timeline.CoverUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(showWorkId, Assert.Single(timeline.PreviewItems).WorkId);
    }

    [Fact]
    public async Task CollectionCatalog_GroupsCrossMediaChildrenThroughTheirSharedParentUniverse()
    {
        var universeId = Guid.NewGuid();
        var fixtures = new[]
        {
            (CollectionId: Guid.NewGuid(), WorkId: Guid.NewGuid(), MediaType: "Books", Title: "Leviathan Wakes"),
            (CollectionId: Guid.NewGuid(), WorkId: Guid.NewGuid(), MediaType: "Audiobooks", Title: "Leviathan Wakes"),
            (CollectionId: Guid.NewGuid(), WorkId: Guid.NewGuid(), MediaType: "TV", Title: "The Expanse"),
        };
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, wikidata_qid)
                VALUES (
                    @UniverseId, 'The Expanse', 'Universe', 'library', 'query', 'Q19610143');
                """,
                new { UniverseId = universeId });

            foreach (var fixture in fixtures)
            {
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                await connection.ExecuteAsync(
                    """
                    INSERT INTO collections (
                        id, parent_collection_id, display_name, collection_type, scope, resolution)
                    VALUES (
                        @CollectionId, @UniverseId, 'The Expanse', 'ContentGroup', 'library', 'query');
                    INSERT INTO works (
                        id, collection_id, media_type, work_kind, ownership, curator_state)
                    VALUES (
                        @WorkId, @CollectionId, @MediaType, 'standalone', 'Owned', 'Accepted');
                    INSERT INTO editions (id, work_id)
                    VALUES (@EditionId, @WorkId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@WorkId, 'title', @Title, @Now);
                    """,
                    new
                    {
                        fixture.CollectionId,
                        UniverseId = universeId,
                        fixture.WorkId,
                        fixture.MediaType,
                        fixture.Title,
                        EditionId = editionId,
                        AssetId = assetId,
                        ContentHash = $"cross-media-{assetId:N}",
                        FilePath = $"C:/library/{assetId:N}.media",
                        Now = now,
                    });
            }
        }

        var entry = Assert.Single(await _catalog.GetCatalogAsync(null, CancellationToken.None));
        var items = await _catalog.GetItemsAsync(entry.Id, null, 20, CancellationToken.None);

        Assert.Equal("The Expanse", entry.Name);
        Assert.Equal(3, entry.ItemCount);
        Assert.Equal(1, entry.BookCount);
        Assert.Equal(1, entry.AudiobookCount);
        Assert.Equal(1, entry.TvCount);
        Assert.True(items.Found);
        Assert.False(items.Forbidden);
        Assert.Equal(3, items.Items.Count);
        Assert.Contains(items.Items, item => item.MediaType == "Books");
        Assert.Contains(items.Items, item => item.MediaType == "Audiobooks");
        Assert.Contains(items.Items, item => item.MediaType == "TV");
    }

    [Fact]
    public async Task CollectionCatalog_IncludesSingleSeriesGroupWithMultipleOwnedTitlesAcrossLanes()
    {
        var collectionId = Guid.NewGuid();
        var fixtures = new[]
        {
            (WorkId: Guid.NewGuid(), MediaType: "Books", Title: "Harry Potter and the Philosopher's Stone"),
            (WorkId: Guid.NewGuid(), MediaType: "Audiobooks", Title: "Harry Potter and the Philosopher's Stone"),
            (WorkId: Guid.NewGuid(), MediaType: "Books", Title: "Harry Potter and the Chamber of Secrets"),
        };
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, wikidata_qid, group_by_field)
                VALUES (
                    @CollectionId, 'Harry Potter', 'ContentGroup', 'library', 'materialized', 'Q8337', 'series');
                INSERT INTO collection_relationships (
                    id, collection_id, rel_type, rel_qid, rel_label, confidence)
                VALUES (
                    @RelationshipId, @CollectionId, 'franchise', 'Q30739117', 'Wizarding World', 1.0);
                """,
                new { CollectionId = collectionId, RelationshipId = Guid.NewGuid() });

            foreach (var fixture in fixtures)
            {
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                await connection.ExecuteAsync(
                    """
                    INSERT INTO works (
                        id, collection_id, media_type, work_kind, ownership, curator_state)
                    VALUES (
                        @WorkId, @CollectionId, @MediaType, 'standalone', 'Owned', 'Accepted');
                    INSERT INTO editions (id, work_id)
                    VALUES (@EditionId, @WorkId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@WorkId, 'title', @Title, @Now);
                    """,
                    new
                    {
                        fixture.WorkId,
                        CollectionId = collectionId,
                        fixture.MediaType,
                        fixture.Title,
                        EditionId = editionId,
                        AssetId = assetId,
                        ContentHash = $"harry-potter-{assetId:N}",
                        FilePath = $"C:/library/{assetId:N}.media",
                        Now = now,
                    });
            }
        }

        var entry = Assert.Single(await _catalog.GetCatalogAsync(null, CancellationToken.None));

        Assert.Equal("Harry Potter", entry.Name);
        Assert.Equal("CrossMedia", entry.PrimaryLane);
        Assert.Equal(2, entry.BookCount);
        Assert.Equal(1, entry.AudiobookCount);
    }

    [Fact]
    public async Task CollectionCatalog_ResolvesEpisodesAndComicIssuesToCoverLedSeriesEntries()
    {
        var universeId = Guid.NewGuid();
        var comicCollectionId = Guid.NewGuid();
        var tvCollectionId = Guid.NewGuid();
        var comicRootId = Guid.NewGuid();
        var issueId = Guid.NewGuid();
        var issueEditionId = Guid.NewGuid();
        var issueAssetId = Guid.NewGuid();
        var showRootId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var episodeEditionId = Guid.NewGuid();
        var episodeAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, wikidata_qid)
                VALUES (
                    @UniverseId, 'Shared Story World', 'Universe', 'library', 'materialized', 'QUNIVERSE');
                INSERT INTO collections (
                    id, parent_collection_id, display_name, collection_type, scope, resolution, group_by_field)
                VALUES (
                    @ComicCollectionId, @UniverseId, 'Batman', 'ContentGroup', 'library', 'materialized', 'series');
                INSERT INTO collections (
                    id, parent_collection_id, display_name, collection_type, scope, resolution, group_by_field)
                VALUES (
                    @TvCollectionId, @UniverseId, 'The Expanse', 'ContentGroup', 'library', 'materialized', 'show_name');

                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                    VALUES (@ComicRootId, 'Comics', 'parent', 'Owned', 'Accepted');
                INSERT INTO works (
                    id, parent_work_id, collection_id, media_type, work_kind, ownership, curator_state)
                    VALUES (
                    @IssueId, @ComicRootId, @ComicCollectionId, 'Comics', 'child', 'Owned', 'Accepted');
                INSERT INTO editions (id, work_id)
                    VALUES (@IssueEditionId, @IssueId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@IssueAssetId, @IssueEditionId, 'batman-404', 'C:/library/Batman 404.cbz');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                    VALUES (@IssueItemId, @ComicCollectionId, @IssueId, 404);

                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                    VALUES (@ShowRootId, 'TV', 'parent', 'Owned', 'Accepted');
                INSERT INTO works (
                    id, parent_work_id, media_type, work_kind, ownership, curator_state)
                    VALUES (@SeasonId, @ShowRootId, 'TV', 'parent', 'Owned', 'Accepted');
                INSERT INTO works (
                    id, parent_work_id, collection_id, media_type, work_kind, ownership, curator_state)
                    VALUES (
                    @EpisodeId, @SeasonId, @TvCollectionId, 'TV', 'child', 'Owned', 'Accepted');
                INSERT INTO editions (id, work_id)
                    VALUES (@EpisodeEditionId, @EpisodeId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@EpisodeAssetId, @EpisodeEditionId, 'expanse-s01e01', 'C:/library/The Expanse/S01E01.mkv');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                    VALUES (@EpisodeItemId, @TvCollectionId, @EpisodeId, 1);

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@ComicRootId, 'title', 'Batman', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@ComicRootId, 'cover_url', '/stream/artwork/batman-cover', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@IssueId, 'issue_title', 'Batman - Issue 404', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@ShowRootId, 'title', 'The Expanse', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@ShowRootId, 'cover_url', '/stream/artwork/expanse-cover', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@EpisodeId, 'episode_title', 'Dulcinea', @Now);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@EpisodeId, 'episode_still_url', '/stream/artwork/expanse-still', @Now);
                """,
                new
                {
                    UniverseId = universeId,
                    ComicCollectionId = comicCollectionId,
                    TvCollectionId = tvCollectionId,
                    ComicRootId = comicRootId,
                    IssueId = issueId,
                    IssueEditionId = issueEditionId,
                    IssueAssetId = issueAssetId,
                    IssueItemId = Guid.NewGuid(),
                    ShowRootId = showRootId,
                    SeasonId = seasonId,
                    EpisodeId = episodeId,
                    EpisodeEditionId = episodeEditionId,
                    EpisodeAssetId = episodeAssetId,
                    EpisodeItemId = Guid.NewGuid(),
                    Now = now,
                });
        }

        var result = await _catalog.GetItemsAsync(universeId, null, 20, CancellationToken.None);

        Assert.True(result.Found);
        Assert.False(result.Forbidden);
        Assert.Equal(2, result.Items.Count);

        var comic = Assert.Single(result.Items, item => item.MediaType == "Comics");
        Assert.Equal(comicRootId, comic.WorkId);
        Assert.Equal("Batman", comic.Title);
        Assert.Equal("/stream/artwork/batman-cover", comic.CoverUrl);
        Assert.Equal($"/details/comicseries/{comicCollectionId:D}?context=comics", comic.DetailRoute);

        var show = Assert.Single(result.Items, item => item.MediaType == "TV");
        Assert.Equal(showRootId, show.WorkId);
        Assert.Equal("The Expanse", show.Title);
        Assert.Equal("/stream/artwork/expanse-cover", show.CoverUrl);
        Assert.Equal($"/details/tvshow/{showRootId:D}?context=watch", show.DetailRoute);
    }

    [Fact]
    public async Task CollectionCatalog_ExpandsBookAndMovieSeriesIntoIndependentOwnedWorks()
    {
        var universeId = Guid.NewGuid();
        var bookCollectionId = Guid.NewGuid();
        var movieCollectionId = Guid.NewGuid();
        var bookRootId = Guid.NewGuid();
        var movieRootId = Guid.NewGuid();
        var firstBookId = Guid.NewGuid();
        var secondBookId = Guid.NewGuid();
        var firstMovieId = Guid.NewGuid();
        var secondMovieId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, wikidata_qid)
                VALUES (
                    @UniverseId, 'Shared Story World', 'Universe', 'library', 'materialized', 'QUNIVERSE');
                INSERT INTO collections (
                    id, parent_collection_id, display_name, collection_type, scope, resolution, group_by_field)
                VALUES (
                    @BookCollectionId, @UniverseId, 'Novel Sequence', 'ContentGroup', 'library', 'materialized', 'series');
                INSERT INTO collections (
                    id, parent_collection_id, display_name, collection_type, scope, resolution, group_by_field)
                VALUES (
                    @MovieCollectionId, @UniverseId, 'Film Sequence', 'ContentGroup', 'library', 'materialized', 'series');

                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                VALUES
                    (@BookRootId, 'Books', 'parent', 'Owned', 'Accepted'),
                    (@MovieRootId, 'Movies', 'parent', 'Owned', 'Accepted');
                INSERT INTO works (
                    id, parent_work_id, collection_id, media_type, work_kind, ownership, curator_state, ordinal)
                VALUES
                    (@FirstBookId, @BookRootId, @BookCollectionId, 'Books', 'child', 'Owned', 'Accepted', 1),
                    (@SecondBookId, @BookRootId, @BookCollectionId, 'Books', 'child', 'Owned', 'Accepted', 2),
                    (@FirstMovieId, @MovieRootId, @MovieCollectionId, 'Movies', 'child', 'Owned', 'Accepted', 1),
                    (@SecondMovieId, @MovieRootId, @MovieCollectionId, 'Movies', 'child', 'Owned', 'Accepted', 2);

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    (@BookRootId, 'title', 'Novel Sequence', @Now),
                    (@MovieRootId, 'title', 'Film Sequence', @Now),
                    (@FirstBookId, 'title', 'First Novel', @Now),
                    (@SecondBookId, 'title', 'Second Novel', @Now),
                    (@FirstMovieId, 'title', 'First Film', @Now),
                    (@SecondMovieId, 'title', 'Second Film', @Now);
                """,
                new
                {
                    UniverseId = universeId,
                    BookCollectionId = bookCollectionId,
                    MovieCollectionId = movieCollectionId,
                    BookRootId = bookRootId,
                    MovieRootId = movieRootId,
                    FirstBookId = firstBookId,
                    SecondBookId = secondBookId,
                    FirstMovieId = firstMovieId,
                    SecondMovieId = secondMovieId,
                    Now = now,
                });

            foreach (var (workId, collectionId, extension) in new[]
                     {
                         (firstBookId, bookCollectionId, ".epub"),
                         (secondBookId, bookCollectionId, ".epub"),
                         (firstMovieId, movieCollectionId, ".mkv"),
                         (secondMovieId, movieCollectionId, ".mkv"),
                     })
            {
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                await connection.ExecuteAsync(
                    """
                    INSERT INTO editions (id, work_id)
                    VALUES (@EditionId, @WorkId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
                    INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                    VALUES (@ItemId, @CollectionId, @WorkId, 0);
                    """,
                    new
                    {
                        EditionId = editionId,
                        WorkId = workId,
                        AssetId = assetId,
                        ContentHash = $"independent-{assetId:N}",
                        FilePath = $"C:/library/{workId:N}{extension}",
                        ItemId = Guid.NewGuid(),
                        CollectionId = collectionId,
                    });
            }
        }

        var result = await _catalog.GetItemsAsync(universeId, null, 20, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(4, result.Items.Count);
        Assert.DoesNotContain(result.Items, item => item.WorkId == bookRootId || item.WorkId == movieRootId);
        Assert.Collection(
            result.Items.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            item => Assert.Equal($"/details/work/{firstMovieId:D}?context=watch", item.DetailRoute),
            item => Assert.Equal($"/details/work/{firstBookId:D}?context=read", item.DetailRoute),
            item => Assert.Equal($"/details/work/{secondMovieId:D}?context=watch", item.DetailRoute),
            item => Assert.Equal($"/details/work/{secondBookId:D}?context=read", item.DetailRoute));
    }

    [Fact]
    public async Task CollectionCatalog_UsesOwnedAudiobookIdentityInsteadOfItsSeriesParent()
    {
        var universeId = Guid.NewGuid();
        var audioCollectionId = Guid.NewGuid();
        var audioSeriesRootId = Guid.NewGuid();
        var ownedAudiobookWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO collections (
                    id, display_name, collection_type, scope, resolution, wikidata_qid)
                VALUES (
                    @UniverseId, 'Shared Story World', 'Universe', 'library', 'materialized', 'QUNIVERSE');
                INSERT INTO collections (
                    id, parent_collection_id, display_name, collection_type, scope, resolution, group_by_field)
                VALUES (
                    @AudioCollectionId, @UniverseId, 'Audio Series', 'ContentGroup', 'library', 'materialized', 'series');
                INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
                VALUES (@AudioSeriesRootId, 'Audiobooks', 'parent', 'Owned', 'Accepted');
                INSERT INTO works (
                    id, parent_work_id, collection_id, media_type, work_kind, ownership, curator_state)
                VALUES (
                    @OwnedAudiobookWorkId, @AudioSeriesRootId, @AudioCollectionId, 'Audiobooks', 'child', 'Owned', 'Accepted');
                INSERT INTO editions (id, work_id)
                VALUES (@EditionId, @OwnedAudiobookWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@AssetId, @EditionId, 'audio-independent', 'C:/library/Leviathan Wakes.m4b');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@ItemId, @AudioCollectionId, @OwnedAudiobookWorkId, 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    (@AudioSeriesRootId, 'title', 'The Expanse', @Now),
                    (@AssetId, 'title', 'Leviathan Wakes', @Now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@AssetId, 'author', 0, 'James S. A. Corey');
                """,
                new
                {
                    UniverseId = universeId,
                    AudioCollectionId = audioCollectionId,
                    AudioSeriesRootId = audioSeriesRootId,
                    OwnedAudiobookWorkId = ownedAudiobookWorkId,
                    EditionId = editionId,
                    AssetId = assetId,
                    ItemId = Guid.NewGuid(),
                    Now = now,
                });
        }

        var item = Assert.Single(
            (await _catalog.GetItemsAsync(universeId, null, 20, CancellationToken.None)).Items);

        Assert.Equal(ownedAudiobookWorkId, item.WorkId);
        Assert.Equal("Leviathan Wakes", item.Title);
        Assert.Equal("James S. A. Corey", item.Creator);
        Assert.Equal($"/details/work/{ownedAudiobookWorkId:D}?context=listen", item.DetailRoute);
    }

    [Fact]
    public async Task FieldValues_UseCurrentWorkSchemaAndIncludeArrayBackedMetadata()
    {
        var workId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@WorkId, 'FieldValueTest', 'standalone');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@WorkId, 'genre', 'Scalar Genre', @LastScoredAt);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@WorkId, 'genre', 0, 'Array Genre');
                """,
                new { WorkId = workId, LastScoredAt = DateTimeOffset.UtcNow.ToString("O") });
        }

        var mediaTypes = await _browse.GetFieldValuesAsync("media_type", null, 500, CancellationToken.None);
        var genres = await _browse.GetFieldValuesAsync("genre", null, 500, CancellationToken.None);

        Assert.Contains("FieldValueTest", mediaTypes);
        Assert.Contains("Scalar Genre", genres);
        Assert.Contains("Array Genre", genres);
    }

    [Fact]
    public async Task EntityFieldValues_CanResolvePersistedQidBackToDisplayLabel()
    {
        var workId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@WorkId, 'Movies', 'standalone');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                VALUES (@WorkId, 'original_language', 0, 'English', 'Q1860');
                """,
                new { WorkId = workId });
        }

        var values = await _browse.GetEntityFieldValuesAsync(
            "original_language",
            "Q1860",
            20,
            CancellationToken.None);

        var english = Assert.Single(values);
        Assert.Equal("Q1860", english.Value);
        Assert.Equal("English", english.Label);
    }

    [Fact]
    public async Task BrowseReads_ObserveCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _browse.GetFieldValuesAsync("artist", null, 20, cancellation.Token));
    }

    private async Task<SeededMusic> SeedMusicHierarchyAsync(
        string title = "Track One",
        string? contentHash = null,
        string artist = "The Artist")
    {
        var albumWorkId = Guid.NewGuid();
        var trackWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@AlbumWorkId, 'Music', 'parent');
            INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@TrackWorkId, 'Music', 'child', @AlbumWorkId);
            INSERT INTO editions (id, work_id, format_label) VALUES (@EditionId, @TrackWorkId, 'Digital');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@AlbumWorkId, 'album', 'The Album', @Now),
                (@AlbumWorkId, 'title', 'The Album', @Now),
                (@AssetId, 'title', @Title, @Now),
                (@AssetId, 'album', 'The Album', @Now),
                (@AssetId, 'track_number', '1', @Now),
                (@AssetId, 'year', '2026', @Now);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES
                (@AlbumWorkId, 'album_artist', 0, @Artist),
                (@AssetId, 'artist', 0, @Artist);
            INSERT INTO entity_assets (
                id, entity_id, entity_type, asset_type, aspect_class,
                primary_hex, secondary_hex, accent_hex, created_at)
            VALUES (
                @ArtworkId, @AlbumWorkId, 'Work', 'CoverArt', 'Square',
                '#112233', '#445566', '#778899', @Now);
            """,
            new
            {
                AlbumWorkId = albumWorkId,
                TrackWorkId = trackWorkId,
                EditionId = editionId,
                AssetId = assetId,
                ArtworkId = Guid.NewGuid(),
                ContentHash = contentHash ?? $"hash-{assetId:N}",
                FilePath = $"C:/library/{assetId:N}.flac",
                Title = title,
                Artist = artist,
                Now = now,
            });

        return new SeededMusic(albumWorkId, trackWorkId, assetId);
    }

    private async Task<SeededBook> SeedBookSeriesMemberAsync(
        string title,
        string position,
        int coverWidth,
        string contentHash,
        int year)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@WorkId, 'Books', 'standalone');
            INSERT INTO editions (id, work_id, format_label) VALUES (@EditionId, @WorkId, 'EPUB');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@AssetId, 'title', @Title, @Now),
                (@AssetId, 'series', 'The Test Series', @Now),
                (@AssetId, 'series_index', @Position, @Now),
                (@AssetId, 'release_year', @Year, @Now),
                (@AssetId, 'cover_width_px', @CoverWidth, @Now),
                (@AssetId, 'cover_height_px', '2400', @Now);
            """,
            new
            {
                WorkId = workId,
                EditionId = editionId,
                AssetId = assetId,
                ContentHash = contentHash,
                FilePath = $"C:/library/{assetId:N}.epub",
                Title = title,
                Position = position,
                Year = year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CoverWidth = coverWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Now = now,
            });

        return new SeededBook(workId, assetId);
    }

    private async Task<SeededBook> SeedCreditedWorkAsync(
        string mediaType,
        string creditKey,
        string role,
        string personName)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var supplementaryPersonId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@WorkId, @MediaType, 'standalone');
            INSERT INTO editions (id, work_id, format_label) VALUES (@EditionId, @WorkId, 'Digital');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@AssetId, @EditionId, @ContentHash, @FilePath);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@AssetId, 'title', @Title, @Now),
                (@AssetId, 'year', '2026', @Now);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@WorkId, @CreditKey, 0, @PersonName);
            INSERT INTO persons (id, name, headshot_url, created_at)
            VALUES
                (@PersonId, @PersonName, 'https://images.example.test/person.jpg', @Now),
                (@SupplementaryPersonId, @SupplementaryPersonName, NULL, @Now);
            INSERT INTO person_roles (person_id, role) VALUES
                (@PersonId, @Role),
                (@SupplementaryPersonId, @Role);
            INSERT INTO person_media_links (media_asset_id, person_id, role)
            VALUES
                (@AssetId, @PersonId, @Role),
                (@AssetId, @SupplementaryPersonId, @Role);
            """,
            new
            {
                WorkId = workId,
                MediaType = mediaType,
                EditionId = editionId,
                AssetId = assetId,
                ContentHash = $"credit-{assetId:N}",
                FilePath = $"C:/library/{assetId:N}.media",
                Title = $"A {role} Credit",
                PersonId = personId,
                PersonName = personName,
                SupplementaryPersonId = supplementaryPersonId,
                SupplementaryPersonName = $"Supplementary {role}",
                CreditKey = creditKey,
                Role = role,
                Now = now,
            });

        return new SeededBook(workId, assetId, personId);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    private sealed record SeededMusic(Guid AlbumWorkId, Guid TrackWorkId, Guid AssetId);
    private sealed record SeededBook(Guid WorkId, Guid AssetId, Guid? PersonId = null);
}
