using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorNavigationReadServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;

    public MediaEditorNavigationReadServiceTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_media_editor_navigation_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetNavigatorAsync_ComicIssue_PrefersIssueTitleOverSeriesTitle(bool assetScoped)
    {
        var seriesId = Guid.NewGuid();
        var issueId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@seriesId, 'Comics', 'parent', 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@issueId, 'Comics', 'child', @seriesId, 2, 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @issueId, 'CBZ');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, 'comic-issue', 'comic.cbz');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@seriesId, 'title', 'Example Series', datetime('now')),
                (@issueId, 'title', 'Example Series', datetime('now')),
                (@assetId, 'title', 'Example Series', datetime('now')),
                (@titleEntityId, 'issue_title', 'Chapter Two', datetime('now')),
                (@assetId, 'issue_number', '2', datetime('now'));
            """, new { seriesId, issueId, editionId, assetId, titleEntityId = assetScoped ? assetId : issueId });
        var service = new MediaEditorNavigationReadService(_database, null!, new HierarchyAlignmentService(_database, null!));
        var navigator = await service.GetNavigatorAsync(issueId, CancellationToken.None);
        Assert.NotNull(navigator);
        var issue = Assert.Single(navigator.Nodes, node => node.NodeKind == "issue");
        Assert.Equal("Chapter Two", issue.Title);
        Assert.Equal("Issue 2", issue.OrdinalLabel);
        Assert.Equal(issueId, issue.EntityId);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ComicSeries_ReturnsLocalRunAndRetailVolumeTargets()
    {
        var issueId = Guid.NewGuid();
        var localRunId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES (@issueId, 'Comics', 'child', 'Owned'),
                       (@localRunId, 'Comics', 'parent', 'Owned');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@localRunId, 'series', 'Local Comic Run', datetime('now'));
                """, new { issueId, localRunId });
        }

        var search = new CapturingSearchService();
        var service = new MediaEditorNavigationReadService(_database, search, new HierarchyAlignmentService(_database, null!));

        var local = await service.GetSuggestionsAsync(issueId, "series", "Local", "local", null, null, CancellationToken.None);
        var retail = await service.GetSuggestionsAsync(issueId, "series", "Retail", "retail", null, null, CancellationToken.None);

        var localSuggestion = Assert.Single(local);
        Assert.Equal(localRunId, localSuggestion.EntityId);
        Assert.Equal("series", localSuggestion.Kind);
        Assert.Equal("Local Comic Run", localSuggestion.Label);

        var retailSuggestion = Assert.Single(retail);
        Assert.Equal("series", retailSuggestion.Kind);
        Assert.Equal("Retail Comic Run", retailSuggestion.Label);
        Assert.Equal(BridgeIdKeys.ComicVineVolumeId, retailSuggestion.ExternalIdKey);
        Assert.Equal("987", retailSuggestion.ExternalIdValue);
        Assert.NotNull(search.RetailRequest);
        Assert.Equal("Comics", search.RetailRequest!.MediaType);
        Assert.Equal("Retail", search.RetailRequest.SearchFields!["series"]);
        Assert.Equal("true", search.RetailRequest.SearchFields["container_search"]);
    }

    [Fact]
    public async Task GetNavigatorAsync_TvHierarchy_MakesOwnedSeasonAndEpisodeSelectable()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES ($seriesId, 'TV', 'parent', 'Owned');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($seasonId, 'TV', 'parent', $seriesId, 1, 'Owned');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($episodeId, 'TV', 'child', $seasonId, 1, 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $episodeId, 'MP4');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'hash', 'show-s01e01.mp4');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    ($seriesId, 'title', 'Example Show', datetime('now')),
                    ($seasonId, 'season_number', '1', datetime('now')),
                    ($assetId, 'episode_number', '1', datetime('now')),
                    ($assetId, 'episode_title', 'Pilot', datetime('now'));
                """;
            command.Parameters.AddWithValue("$seriesId", GuidSql.ToBlob(seriesId));
            command.Parameters.AddWithValue("$seasonId", GuidSql.ToBlob(seasonId));
            command.Parameters.AddWithValue("$episodeId", GuidSql.ToBlob(episodeId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.ExecuteNonQuery();
        }

        var posterId = Guid.NewGuid();
        var stillId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path, is_preferred)
                VALUES (@posterId, @seasonId, 'Work', 'SeasonPoster', 'season.jpg', 1),
                       (@stillId, @episodeId, 'Work', 'EpisodeStill', 'episode.jpg', 1);
                """, new { posterId, seasonId, stillId, episodeId });
        }

        var service = new MediaEditorNavigationReadService(_database, null!, new HierarchyAlignmentService(_database, null!));

        var navigator = await service.GetNavigatorAsync(seriesId, CancellationToken.None);

        Assert.NotNull(navigator);
        Assert.True(navigator.Enabled);

        var season = Assert.Single(navigator.Nodes, node => node.NodeKind == "season");
        Assert.True(season.IsOwned);
        Assert.True(season.CanSelectAsEditorTarget);
        Assert.Equal("1 episode", season.Subtitle);

        var episode = Assert.Single(navigator.Nodes, node => node.NodeKind == "episode");
        Assert.True(episode.IsOwned);
        Assert.True(episode.IsClickable);
        Assert.True(episode.CanSelectAsEditorTarget);
        Assert.Equal(assetId, episode.PrimaryAssetId);
        Assert.Equal("Pilot", episode.Title);
        Assert.Equal($"/stream/artwork/{posterId:D}", season.ArtworkUrl);
        Assert.Equal($"/stream/artwork/{stillId:D}", episode.ArtworkUrl);
        Assert.Equal("wide", episode.ArtworkShape);
        Assert.Null(navigator.Nodes.Single(node => node.IsRoot).ArtworkUrl);
    }

    [Fact]
    public async Task GetNavigatorAsync_MovieSeries_UsesFilmSeriesAndMovieNodes()
    {
        var seriesId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES ($seriesId, 'Movies', 'parent', 'Owned');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($movieId, 'Movies', 'child', $seriesId, 1, 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $movieId, 'MKV');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'movie-hash', 'movie.mkv');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES
                    ($seriesId, 'title', 'The Trilogy', datetime('now')),
                    ($assetId, 'title', 'The First Film', datetime('now'));
                """;
            command.Parameters.AddWithValue("$seriesId", GuidSql.ToBlob(seriesId));
            command.Parameters.AddWithValue("$movieId", GuidSql.ToBlob(movieId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.ExecuteNonQuery();
        }

        var service = new MediaEditorNavigationReadService(_database, null!, new HierarchyAlignmentService(_database, null!));
        var navigator = await service.GetNavigatorAsync(seriesId, CancellationToken.None);

        Assert.NotNull(navigator);
        Assert.True(navigator.Enabled);
        var filmSeries = Assert.Single(navigator.Nodes, node => node.NodeKind == "film_series");
        var movie = Assert.Single(navigator.Nodes, node => node.NodeKind == "movie");
        Assert.Equal("Film Series", filmSeries.Label);
        Assert.Equal("The Trilogy", filmSeries.Title);
        Assert.Equal("Movie", movie.Label);
        Assert.Equal("The First Film", movie.Title);
        Assert.Equal("work", movie.ScopeId);
        Assert.True(movie.CanSelectAsEditorTarget);
    }

    [Fact]
    public async Task GetNavigatorAsync_StandaloneMovie_UsesMovieNode()
    {
        var movieId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES ($movieId, 'Movies', 'standalone', 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $movieId, 'MKV');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'standalone-movie-hash', 'standalone.mkv');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES ($assetId, 'title', 'Standalone Film', datetime('now'));
                """;
            command.Parameters.AddWithValue("$movieId", GuidSql.ToBlob(movieId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.ExecuteNonQuery();
        }

        var service = new MediaEditorNavigationReadService(_database, null!, new HierarchyAlignmentService(_database, null!));
        var navigator = await service.GetNavigatorAsync(movieId, CancellationToken.None);

        Assert.NotNull(navigator);
        Assert.True(navigator.Enabled);
        var movie = Assert.Single(navigator.Nodes);
        Assert.Equal("movie", movie.NodeKind);
        Assert.Equal("Movie", movie.Label);
        Assert.Equal("Standalone Film", movie.Title);
        Assert.True(movie.CanSelectAsEditorTarget);
    }

    [Fact]
    public async Task HierarchyAlignment_BookSeriesPreviewThenApply_MovesOnlyTheWorkAndRetainsTheAsset()
    {
        var previousSeriesId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership, parent_key)
                VALUES ($previousSeriesId, 'Books', 'parent', 'Owned', 'old author|old series');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($bookId, 'Books', 'child', $previousSeriesId, 1, 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $bookId, 'EPUB');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'book-hash', 'books/example.epub');
                """;
            command.Parameters.AddWithValue("$previousSeriesId", GuidSql.ToBlob(previousSeriesId));
            command.Parameters.AddWithValue("$bookId", GuidSql.ToBlob(bookId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.ExecuteNonQuery();
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(
            ScopeId: "volume_issue",
            FieldValues: new Dictionary<string, string?>
            {
                ["series"] = "The New Series",
                ["author"] = "New Author",
                ["series_position"] = "2",
                ["title"] = "Retained Book",
            },
            SelectedTargetIds: null,
            SelectedSuggestions: null);

        var preview = await service.PreviewAsync(bookId, request, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("move_child", preview.Action);
        Assert.True(preview.RequiresNewTarget);
        Assert.True(preview.CanApply);
        Assert.False(preview.Applied);
        Assert.Equal(bookId, preview.SelectedEntityId);
        Assert.Equal(previousSeriesId, preview.TargetRootEntityId);

        var applied = await service.ApplyAsync(bookId, request, CancellationToken.None);

        Assert.NotNull(applied);
        Assert.True(applied.Applied);
        Assert.True(applied.TargetParentEntityId.HasValue);
        Assert.NotEqual(previousSeriesId, applied.TargetParentEntityId.Value);
        Assert.Contains("The New Series", applied.TargetPath, StringComparison.Ordinal);

        using var verification = _database.CreateConnection();
        var retained = verification.QuerySingle<(Guid ParentWorkId, long Ordinal, Guid AssetId, string FilePath)>("""
            SELECT w.parent_work_id AS ParentWorkId,
                   w.ordinal AS Ordinal,
                   ma.id AS AssetId,
                   ma.file_path_root AS FilePath
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id = @bookId;
            """, new { bookId });

        Assert.Equal(applied.TargetParentEntityId.Value, retained.ParentWorkId);
        Assert.Equal(2, retained.Ordinal);
        Assert.Equal(assetId, retained.AssetId);
        Assert.Equal("books/example.epub", retained.FilePath);
    }

    [Fact]
    public async Task HierarchyAlignment_DuplicateOwnedSeriesOrdinal_ReturnsConflictWithoutMovingTheAsset()
    {
        var seriesId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        var conflictingBookId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership, parent_key)
                VALUES ($seriesId, 'Books', 'parent', 'Owned', 'author|series');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($bookId, 'Books', 'child', NULL, 1, 'Owned');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($conflictingBookId, 'Books', 'child', $seriesId, 2, 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $bookId, 'EPUB');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'book-hash', 'books/conflict.epub');
                """;
            command.Parameters.AddWithValue("$seriesId", GuidSql.ToBlob(seriesId));
            command.Parameters.AddWithValue("$bookId", GuidSql.ToBlob(bookId));
            command.Parameters.AddWithValue("$conflictingBookId", GuidSql.ToBlob(conflictingBookId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.ExecuteNonQuery();
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(
            ScopeId: "volume_issue",
            FieldValues: new Dictionary<string, string?>
            {
                ["series"] = "Series",
                ["author"] = "Author",
                ["series_position"] = "2",
            },
            SelectedTargetIds: null,
            SelectedSuggestions: null);

        var preview = await service.PreviewAsync(bookId, request, CancellationToken.None);
        var applied = await service.ApplyAsync(bookId, request, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("conflict", preview.Action);
        Assert.False(preview.CanApply);
        Assert.NotNull(applied);
        Assert.False(applied.Applied);

        using var verification = _database.CreateConnection();
        var retained = verification.QuerySingle<(Guid? ParentWorkId, Guid AssetId, string FilePath)>("""
            SELECT w.parent_work_id AS ParentWorkId,
                   ma.id AS AssetId,
                   ma.file_path_root AS FilePath
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id = @bookId;
            """, new { bookId });

        Assert.Null(retained.ParentWorkId);
        Assert.Equal(assetId, retained.AssetId);
        Assert.Equal("books/conflict.epub", retained.FilePath);
    }

    [Fact]
    public async Task HierarchyAlignment_RetailTvCrossParentMove_RetainsLeafAndWritesContainerIdentityToFinalShow()
    {
        var showAId = Guid.NewGuid();
        var seasonAId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var showBId = Guid.NewGuid();
        var seasonBId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership, external_identifiers)
                VALUES ($showAId, 'TV', 'parent', 'Owned', '{"tmdb_id":"show-a"}');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($seasonAId, 'TV', 'parent', $showAId, 1, 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($episodeId, 'TV', 'child', $seasonAId, 1, 'Owned');

                INSERT INTO works (id, media_type, work_kind, ownership, external_identifiers)
                VALUES ($showBId, 'TV', 'parent', 'Owned', '{"tmdb_id":"show-b-old"}');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($seasonBId, 'TV', 'parent', $showBId, 2, 'Owned');

                INSERT INTO editions (id, work_id, format_label) VALUES ($editionId, $episodeId, 'MP4');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, 'cross-parent-tv-hash', 'tv/show-a/s01e01.mp4');

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    ($showAId, 'show_name', 'Show A', datetime('now')),
                    ($showBId, 'show_name', 'Show B (old)', datetime('now')),
                    ($episodeId, 'identity_provider', 'old-provider', datetime('now'));
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value, provider_id, created_at)
                VALUES ($bridgeAId, $showAId, 'tmdb_id', 'show-a', 'tmdb', datetime('now'));
                """;
            command.Parameters.AddWithValue("$showAId", GuidSql.ToBlob(showAId));
            command.Parameters.AddWithValue("$seasonAId", GuidSql.ToBlob(seasonAId));
            command.Parameters.AddWithValue("$episodeId", GuidSql.ToBlob(episodeId));
            command.Parameters.AddWithValue("$showBId", GuidSql.ToBlob(showBId));
            command.Parameters.AddWithValue("$seasonBId", GuidSql.ToBlob(seasonBId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.Parameters.AddWithValue("$bridgeAId", GuidSql.ToBlob(Guid.NewGuid()));
            command.ExecuteNonQuery();
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(
            ScopeId: "show_episode",
            FieldValues: new Dictionary<string, string?>
            {
                ["show_name"] = "Show B",
                ["season_number"] = "2",
                ["episode_number"] = "4",
                ["episode_title"] = "Moved episode",
            },
            SelectedTargetIds: new Dictionary<string, Guid?>
            {
                ["show"] = showBId,
                ["season"] = seasonBId,
            },
            SelectedSuggestions: null);
        var now = DateTimeOffset.UtcNow;
        var mutation = new HierarchyIdentityMutation(
            [
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "show_name", "Show B", 1, false, now),
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "tmdb_id", "show-b-new", 1, false, now),
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "identity_provider", "tmdb", 1, false, now),
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "identity_provider_item_id", "episode-4", 1, false, now),
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "identity_revision", "revision-4", 1, false, now),
                new HierarchyClaimMutation(episodeId, WellKnownProviders.UserManual, WellKnownProviders.UserManual, "tmdb_episode_id", "episode-4", 1, false, now),
            ],
            [
                new HierarchyCanonicalMutation(episodeId, "show_name", "Show B", WellKnownProviders.UserManual, false, now),
                new HierarchyCanonicalMutation(episodeId, "tmdb_id", "show-b-new", WellKnownProviders.UserManual, false, now),
                new HierarchyCanonicalMutation(episodeId, "identity_provider", "tmdb", WellKnownProviders.UserManual, false, now),
                new HierarchyCanonicalMutation(episodeId, "identity_provider_item_id", "episode-4", WellKnownProviders.UserManual, false, now),
                new HierarchyCanonicalMutation(episodeId, "identity_revision", "revision-4", WellKnownProviders.UserManual, false, now),
                new HierarchyCanonicalMutation(episodeId, "tmdb_episode_id", "episode-4", WellKnownProviders.UserManual, false, now),
            ],
            [
                new HierarchyBridgeIdMutation(episodeId, "tmdb_id", "show-b-new", "tmdb", now),
                new HierarchyBridgeIdMutation(episodeId, "tmdb_episode_id", "episode-4", "tmdb", now),
            ],
            [],
            [new HierarchyExternalIdentifierMutation(
                episodeId,
                [],
                new Dictionary<string, string>
                {
                    ["tmdb_id"] = "show-b-new",
                    ["tmdb_episode_id"] = "episode-4",
                })]);

        var result = await service.ApplyRetailIdentityAsync(episodeId, request, mutation, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Applied);
        Assert.Equal(episodeId, result.SelectedEntityId);
        Assert.Equal(showBId, result.TargetRootEntityId);
        Assert.Equal(seasonBId, result.TargetParentEntityId);
        Assert.Equal("Show A / Season 1 / Episode 1", result.CurrentPath);
        Assert.Equal("Show B / Season 2 / Episode 4", result.TargetPath);

        using var verification = _database.CreateConnection();
        var retained = verification.QuerySingle<(Guid ParentWorkId, long Ordinal, Guid EditionId, Guid AssetId, string FilePath)>("""
            SELECT w.parent_work_id AS ParentWorkId, w.ordinal AS Ordinal, e.id AS EditionId,
                   ma.id AS AssetId, ma.file_path_root AS FilePath
            FROM works w
            JOIN editions e ON e.work_id = w.id
            JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id = @episodeId;
            """, new { episodeId });
        Assert.Equal(seasonBId, retained.ParentWorkId);
        Assert.Equal(4, retained.Ordinal);
        Assert.Equal(editionId, retained.EditionId);
        Assert.Equal(assetId, retained.AssetId);
        Assert.Equal("tv/show-a/s01e01.mp4", retained.FilePath);

        Assert.Equal("Show A", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @showAId AND key = 'show_name';", new { showAId }));
        Assert.Equal("show-a", verification.QuerySingle<string>("SELECT id_value FROM bridge_ids WHERE entity_id = @showAId AND id_type = 'tmdb_id';", new { showAId }));
        Assert.Equal("Show B", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @showBId AND key = 'show_name';", new { showBId }));
        Assert.Equal("show-b-new", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @showBId AND key = 'tmdb_id';", new { showBId }));
        Assert.Equal("show-b-new", verification.QuerySingle<string>("SELECT id_value FROM bridge_ids WHERE entity_id = @showBId AND id_type = 'tmdb_id';", new { showBId }));
        Assert.Equal("tmdb", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @episodeId AND key = 'identity_provider';", new { episodeId }));
        Assert.Equal("episode-4", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @episodeId AND key = 'identity_provider_item_id';", new { episodeId }));
        Assert.Equal("revision-4", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @episodeId AND key = 'identity_revision';", new { episodeId }));
        Assert.Equal("episode-4", verification.QuerySingle<string>("SELECT value FROM canonical_values WHERE entity_id = @episodeId AND key = 'tmdb_episode_id';", new { episodeId }));
        Assert.Equal("episode-4", verification.QuerySingle<string>("SELECT id_value FROM bridge_ids WHERE entity_id = @episodeId AND id_type = 'tmdb_episode_id';", new { episodeId }));
        Assert.Contains("show-a", verification.QuerySingle<string>("SELECT external_identifiers FROM works WHERE id = @showAId;", new { showAId }), StringComparison.Ordinal);
        Assert.Contains("show-b-new", verification.QuerySingle<string>("SELECT external_identifiers FROM works WHERE id = @showBId;", new { showBId }), StringComparison.Ordinal);
        Assert.Contains("episode-4", verification.QuerySingle<string>("SELECT external_identifiers FROM works WHERE id = @episodeId;", new { episodeId }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HierarchyAlignment_RetailIdentityFailure_RollsBackPlacementAndRetainsEditionAssetAndArtwork()
    {
        var bookId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership) VALUES ($bookId, 'Books', 'child', 'Owned');
                INSERT INTO editions (id, work_id, format_label) VALUES ($editionId, $bookId, 'EPUB');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES ($assetId, $editionId, 'rollback-hash', 'books/rollback.epub');
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url, source_provider, owner_scope, is_user_override, created_at)
                VALUES ($artworkId, $bookId, 'Work', 'CoverArt', 'managed://cover', 'user', 'Work', 1, datetime('now'));
                """;
            command.Parameters.AddWithValue("$bookId", GuidSql.ToBlob(bookId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.Parameters.AddWithValue("$artworkId", GuidSql.ToBlob(artworkId));
            command.ExecuteNonQuery();
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(null, new Dictionary<string, string?>
        {
            ["series"] = "Rollback Series", ["author"] = "Author", ["series_position"] = "1",
        }, null, null);
        var mutation = new HierarchyIdentityMutation(
            [new HierarchyClaimMutation(bookId, Guid.NewGuid(), WellKnownProviders.UserManual, "title", "Should fail", 1, false, DateTimeOffset.UtcNow)],
            [], [], []);

        await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyRetailIdentityAsync(bookId, request, mutation, CancellationToken.None));

        using var verification = _database.CreateConnection();
        var retained = verification.QuerySingle<(Guid? ParentWorkId, Guid EditionId, Guid AssetId, string FilePath, Guid ArtworkId)>("""
            SELECT w.parent_work_id AS ParentWorkId, e.id AS EditionId, ma.id AS AssetId,
                   ma.file_path_root AS FilePath, ea.id AS ArtworkId
            FROM works w INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            INNER JOIN entity_assets ea ON ea.entity_id = w.id
            WHERE w.id = @bookId;
            """, new { bookId });
        Assert.Null(retained.ParentWorkId);
        Assert.Equal(editionId, retained.EditionId);
        Assert.Equal(assetId, retained.AssetId);
        Assert.Equal("books/rollback.epub", retained.FilePath);
        Assert.Equal(artworkId, retained.ArtworkId);
        Assert.Equal(0, verification.QuerySingle<int>("SELECT COUNT(*) FROM works WHERE parent_key = 'author|rollback series';"));
        Assert.Equal(0, verification.QuerySingle<int>("SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @bookId AND claim_key = 'title';", new { bookId }));
    }

    [Fact]
    public Task HierarchyAlignment_TrackReparent_RetainsEditionAssetAndDoesNotDuplicateLeaf() =>
        AssertSimpleLeafMoveAsync(
            "Music",
            new Dictionary<string, string?>
            {
                ["album"] = "Target Album",
                ["artist"] = "Target Artist",
                ["track_number"] = "2",
                ["title"] = "Retained Track",
            },
            "album",
            "music/retained-track.flac");

    [Fact]
    public async Task HierarchyAlignment_MovingLastTrack_LeavesFormerAlbumAndArtworkForDeferredCleanup()
    {
        var formerAlbumId = Guid.NewGuid();
        var targetAlbumId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var artworkId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership, parent_key)
                VALUES (@formerAlbumId, 'Music', 'parent', 'Owned', 'original artist|former album'),
                       (@targetAlbumId, 'Music', 'parent', 'Owned', 'target artist|target album');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@trackId, 'Music', 'child', @formerAlbumId, 1, 'Owned');

                INSERT INTO entity_assets
                    (id, entity_id, entity_type, asset_type, image_url, source_provider, owner_scope, is_user_override, created_at)
                VALUES
                    (@artworkId, @formerAlbumId, 'Work', 'CoverArt', 'managed://former-album-cover', 'musicbrainz', 'Work', 0, datetime('now'));
                """, new { formerAlbumId, targetAlbumId, trackId, artworkId });
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var moveRequest = new MembershipPreviewRequest(
                ScopeId: null,
                FieldValues: new Dictionary<string, string?>
                {
                    ["album"] = "Target Album",
                    ["artist"] = "Target Artist",
                    ["track_number"] = "1",
                    ["title"] = "Moved Track",
                },
                SelectedTargetIds: new Dictionary<string, Guid?> { ["album"] = targetAlbumId },
                SelectedSuggestions: null);
        var preview = await service.PreviewAsync(trackId, moveRequest, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.True(preview.SourceParentWillBeEmpty);

        var applied = await service.ApplyAsync(trackId, moveRequest, CancellationToken.None);

        Assert.NotNull(applied);
        Assert.True(applied.Applied);
        Assert.True(applied.SourceParentWillBeEmpty);

        using var verification = _database.CreateConnection();
        Assert.Equal(targetAlbumId, verification.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @trackId;", new { trackId }));
        Assert.Equal(1, verification.QuerySingle<int>("SELECT COUNT(*) FROM works WHERE id = @formerAlbumId;", new { formerAlbumId }));
        Assert.Equal(0, verification.QuerySingle<int>("SELECT COUNT(*) FROM works WHERE parent_work_id = @formerAlbumId;", new { formerAlbumId }));
        Assert.Equal(artworkId, verification.QuerySingle<Guid>("SELECT id FROM entity_assets WHERE entity_id = @formerAlbumId;", new { formerAlbumId }));
    }

    [Fact]
    public async Task HierarchyAlignment_NewTvdbShowMove_KeepsOwnedEpisodeAndMakesSeasonsBrowsable()
    {
        var originalShowId = Guid.NewGuid();
        var originalSeasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var siblingEpisodeId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES (@originalShowId, 'TV', 'parent', 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@originalSeasonId, 'TV', 'parent', @originalShowId, 1, 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@episodeId, 'TV', 'child', @originalSeasonId, 1, 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@siblingEpisodeId, 'TV', 'child', @originalSeasonId, 2, 'Owned');
                INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @episodeId, 'MP4');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, 'tvdb-move', 'tv/original.mp4');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@originalShowId, 'show_name', 'Original Show', datetime('now'));
                """, new { originalShowId, originalSeasonId, episodeId, siblingEpisodeId, editionId, assetId });
        }

        var request = new MediaEngine.Application.ReadModels.MembershipPreviewRequest(
            "show_episode",
            new Dictionary<string, string?>
            {
                ["show_name"] = "Correct Show",
                ["season_number"] = "2",
                ["episode_number"] = "3",
            },
            null,
            new Dictionary<string, MediaEngine.Application.ReadModels.MembershipSuggestionSelection>
            {
                ["show"] = new(null, "retail", false, "show", "Correct Show", null,
                    "tvdb", "127532", BridgeIdKeys.TvdbId, "127532"),
            });
        var mutation = new MediaEngine.Application.ReadModels.HierarchyIdentityMutation([], [], [], []);
        var result = await new HierarchyAlignmentService(_database, null!)
            .ApplyRetailIdentityAsync(episodeId, request, mutation, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Applied);
        Assert.NotEqual(originalShowId, result.TargetRootEntityId);
        using var verification = _database.CreateConnection();
        Assert.Equal(result.TargetParentEntityId,
            verification.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @episodeId;", new { episodeId }));
        Assert.Equal("127532", verification.QuerySingle<string>(
            "SELECT value FROM canonical_values WHERE entity_id = @showId AND key = 'tvdb_id';",
            new { showId = result.TargetRootEntityId }));
        Assert.Equal("127532", verification.QuerySingle<string>(
            "SELECT id_value FROM bridge_ids WHERE entity_id = @showId AND id_type = 'tvdb_id';",
            new { showId = result.TargetRootEntityId }));
        Assert.Equal(assetId, verification.QuerySingle<Guid>("""
            SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @episodeId;
            """, new { episodeId }));
        Assert.Equal(originalShowId, verification.QuerySingle<Guid>(
            "SELECT parent_work_id FROM works WHERE id = @originalSeasonId;", new { originalSeasonId }));
        Assert.Equal(originalSeasonId, verification.QuerySingle<Guid>(
            "SELECT parent_work_id FROM works WHERE id = @siblingEpisodeId;", new { siblingEpisodeId }));
    }

    [Fact]
    public async Task HierarchyAlignment_RejectsSelectedContainerFromAnotherMediaType()
    {
        var originalAlbumId = Guid.NewGuid();
        var invalidShowId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership)
                VALUES (@originalAlbumId, 'Music', 'parent', 'Owned'),
                       (@invalidShowId, 'TV', 'parent', 'Owned');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES (@trackId, 'Music', 'child', @originalAlbumId, 1, 'Owned');
                """, new { originalAlbumId, invalidShowId, trackId });
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(
            null,
            new Dictionary<string, string?>
            {
                ["album"] = "Incorrect Target",
                ["artist"] = "Artist",
                ["track_number"] = "1",
            },
            new Dictionary<string, Guid?> { ["album"] = invalidShowId },
            null);

        var preview = await service.PreviewAsync(trackId, request, CancellationToken.None);
        var applied = await service.ApplyAsync(trackId, request, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("conflict", preview.Action);
        Assert.False(preview.CanApply);
        Assert.NotNull(applied);
        Assert.False(applied.Applied);
        using var verification = _database.CreateConnection();
        Assert.Equal(originalAlbumId, verification.QuerySingle<Guid>("SELECT parent_work_id FROM works WHERE id = @trackId;", new { trackId }));
    }

    [Fact]
    public Task HierarchyAlignment_MovieReparent_RetainsEditionAssetAndDoesNotDuplicateLeaf() =>
        AssertSimpleLeafMoveAsync(
            "Movies",
            new Dictionary<string, string?>
            {
                ["series"] = "Target Film Series",
                ["director"] = "Director",
                ["series_position"] = "2",
                ["title"] = "Retained Movie",
            },
            "series",
            "movies/retained-movie.mkv");

    [Fact]
    public Task HierarchyAlignment_ComicReparent_RetainsEditionAssetAndDoesNotDuplicateLeaf() =>
        AssertSimpleLeafMoveAsync(
            "Comics",
            new Dictionary<string, string?>
            {
                ["series"] = "Target Comic Series",
                ["author"] = "Creator",
                ["series_position"] = "2",
                ["title"] = "Retained Issue",
            },
            "series",
            "comics/retained-issue.cbz");

    private async Task AssertSimpleLeafMoveAsync(
        string mediaType,
        Dictionary<string, string?> fieldValues,
        string targetKey,
        string assetPath)
    {
        var oldParentId = Guid.NewGuid();
        var targetParentId = Guid.NewGuid();
        var leafId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO works (id, media_type, work_kind, ownership, parent_key)
                VALUES ($oldParentId, $mediaType, 'parent', 'Owned', 'old|parent');

                INSERT INTO works (id, media_type, work_kind, ownership, parent_key)
                VALUES ($targetParentId, $mediaType, 'parent', 'Owned', 'target|parent');

                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                VALUES ($leafId, $mediaType, 'child', $oldParentId, 1, 'Owned');

                INSERT INTO editions (id, work_id, format_label)
                VALUES ($editionId, $leafId, 'Test format');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, $assetPath, $assetPath);
                """;
            command.Parameters.AddWithValue("$oldParentId", GuidSql.ToBlob(oldParentId));
            command.Parameters.AddWithValue("$targetParentId", GuidSql.ToBlob(targetParentId));
            command.Parameters.AddWithValue("$leafId", GuidSql.ToBlob(leafId));
            command.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
            command.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
            command.Parameters.AddWithValue("$mediaType", mediaType);
            command.Parameters.AddWithValue("$assetPath", assetPath);
            command.ExecuteNonQuery();
        }

        var service = new HierarchyAlignmentService(_database, null!);
        var request = new MembershipPreviewRequest(
            ScopeId: null,
            FieldValues: fieldValues,
            SelectedTargetIds: new Dictionary<string, Guid?> { [targetKey] = targetParentId },
            SelectedSuggestions: null);

        var preview = await service.PreviewAsync(leafId, request, CancellationToken.None);
        var applied = await service.ApplyAsync(leafId, request, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("move_child", preview.Action);
        Assert.True(preview.CanApply);
        Assert.NotNull(applied);
        Assert.True(applied.Applied);
        Assert.Equal(targetParentId, applied.TargetParentEntityId);

        using var verification = _database.CreateConnection();
        var retained = verification.QuerySingle<(Guid ParentId, Guid EditionId, Guid AssetId, string Path)>("""
            SELECT w.parent_work_id AS ParentId, e.id AS EditionId, ma.id AS AssetId, ma.file_path_root AS Path
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.id = @leafId;
            """, new { leafId });
        Assert.Equal(targetParentId, retained.ParentId);
        Assert.Equal(editionId, retained.EditionId);
        Assert.Equal(assetId, retained.AssetId);
        Assert.Equal(assetPath, retained.Path);
        Assert.Equal(1, verification.QuerySingle<int>("SELECT COUNT(*) FROM works WHERE id = @leafId;", new { leafId }));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class CapturingSearchService : ISearchService
    {
        public SearchRetailRequest? RetailRequest { get; private set; }

        public Task<SearchUniverseResult> SearchUniverseAsync(SearchUniverseRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SearchUniverseResult([], request.Query, request.MediaType));

        public Task<SearchRetailResult> SearchRetailAsync(SearchRetailRequest request, CancellationToken ct = default)
        {
            RetailRequest = request;
            return Task.FromResult(new SearchRetailResult(
                [new RetailCandidate
                {
                    ProviderId = "comicvine",
                    ProviderName = "comicvine",
                    ProviderItemId = "987",
                    Title = "Retail Comic Run",
                    Year = "2024",
                    Author = "Publisher",
                    Confidence = 1,
                }],
                request.Query,
                request.MediaType));
        }
    }
}
