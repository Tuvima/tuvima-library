using Dapper;
using MediaEngine.Domain;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class MediaEditorCommitRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_editor_commit_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _show = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();
    private readonly Guid _source = Guid.NewGuid();
    private readonly Guid _target = Guid.NewGuid();
    private readonly Guid _edition = Guid.NewGuid();
    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _library = Guid.NewGuid();

    public MediaEditorCommitRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id, media_type, work_kind) VALUES(@show, 'TV', 'parent');
            INSERT INTO works(id, media_type, work_kind, parent_work_id)
            VALUES(@season, 'TV', 'parent', @show);
            """, new { show = _show, season = _season });
        connection.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id) VALUES
                (@source, 'TV', 'child', @season),
                (@target, 'TV', 'child', @season);
            """, new { source = _source, target = _target, season = _season });
        connection.Execute("INSERT INTO editions(id, work_id) VALUES(@edition, @source);",
            new { edition = _edition, source = _source });
        connection.Execute("""
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
            VALUES(@asset, @edition, @hash, 'C:/fixture/episode.mkv', @library);
            """, new
        {
            asset = _asset,
            edition = _edition,
            hash = Guid.NewGuid().ToString("N"),
            library = _library.ToString("D")
        });
        connection.Execute("""
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
            VALUES(@source, @key, 'source-r1', @now), (@target, @key, 'target-r1', @now),
                  (@show, @key, 'show-r1', @now);
            """, new
        {
            source = _source,
            target = _target,
            show = _show,
            key = MetadataFieldConstants.IdentityRevision,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
        connection.Execute("""
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value)
            VALUES(@id, @target, @key, 'episode-2'),
                  (@showBridge, @show, @seriesKey, 'series-1');
            """, new
        {
            id = Guid.NewGuid(),
            target = _target,
            key = BridgeIdKeys.TvdbEpisodeId,
            showBridge = Guid.NewGuid(),
            show = _show,
            seriesKey = BridgeIdKeys.TvdbId
        });
    }

    [Fact]
    public async Task CommitMovesOnlySelectedFileAndPersistsRetagIntentAndReplayReceipt()
    {
        using (var setupConnection = _database.CreateConnection())
        {
            setupConnection.Execute("UPDATE works SET work_kind = 'catalog', ownership = 'Unowned', is_catalog_only = 1 WHERE id = @target;",
                    new { target = _target });
        }
        var operation = Move() with { ExpectedTargetWorkKind = "catalog" };
        var first = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(operation);
        var replay = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(operation);

        Assert.Equal(MediaEditorCommitOutcome.Committed, first.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Replayed, replay.Outcome);
        using var connection = _database.CreateConnection();
        Assert.Equal(_target, connection.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = _edition }));
        var state = connection.QuerySingle<(string? Hash, string? Status)>(
            "SELECT writeback_fields_hash AS Hash, writeback_status AS Status FROM media_assets WHERE id = @asset;",
            new { asset = _asset });
        Assert.StartsWith("editor:pending:", state.Hash);
        Assert.Equal("pending", state.Status);
        var ownership = connection.Query<(Guid Id, string Ownership, int IsCatalogOnly, string WorkKind)>(
            "SELECT id AS Id, ownership AS Ownership, is_catalog_only AS IsCatalogOnly, work_kind AS WorkKind FROM works WHERE id IN (@source, @target);",
            new { source = _source, target = _target }).ToDictionary(row => row.Id);
        Assert.Equal("Unowned", ownership[_source].Ownership);
        Assert.Equal(1, ownership[_source].IsCatalogOnly);
        Assert.Equal("Owned", ownership[_target].Ownership);
        Assert.Equal(0, ownership[_target].IsCatalogOnly);
        Assert.Equal("child", ownership[_target].WorkKind);
        Assert.Equal(1, connection.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        var intent = connection.QuerySingle<(Guid AssetId, long Generation, string Status, string Token)>("""
            SELECT asset_id AS AssetId, generation AS Generation, status AS Status,
                   operation_token AS Token
            FROM media_file_write_intents WHERE asset_id=@asset;
            """, new { asset = _asset });
        Assert.Equal(_asset, intent.AssetId);
        Assert.Equal(1, intent.Generation);
        Assert.Equal("pending", intent.Status);
        Assert.Equal(operation.OperationToken, intent.Token);
    }

    [Fact]
    public async Task StaleRevisionOrReusedTokenCannotPartiallyCommit()
    {
        var stale = Move() with { ExpectedSourceIdentityRevision = "old" };
        var first = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(stale);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, first.Outcome);

        var valid = Move();
        var committed = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(valid);
        Assert.Equal(MediaEditorCommitOutcome.Committed, committed.Outcome);
        var reused = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(valid with { TargetTvdbEpisodeId = "another" });
        Assert.Equal(MediaEditorCommitOutcome.Conflict, reused.Outcome);
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        Assert.Equal("episode-2", connection.QuerySingle<string>(
            "SELECT target_tvdb_episode_id FROM media_editor_commits;"));
    }

    [Fact]
    public async Task SingleFileMovePreservesUnselectedSiblingOnSharedSourceWork()
    {
        var siblingEdition = Guid.NewGuid();
        var siblingAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO editions(id, work_id) VALUES(@edition, @work);",
                new { edition = siblingEdition, work = _source });
            connection.Execute("""
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                VALUES(@asset, @edition, @hash, 'C:/fixture/second.mkv');
                """, new { asset = siblingAsset, edition = siblingEdition, hash = Guid.NewGuid().ToString("N") });
        }

        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(Move());
        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = _edition }));
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = siblingEdition }));
        Assert.Equal("Owned", verify.QuerySingle<string>(
            "SELECT ownership FROM works WHERE id = @work;", new { work = _source }));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task WrongEpisodeBridgeOrDifferentShowIsRejected()
    {
        var repository = new MediaEditorCommitRepository(_database);
        var wrongBridge = await repository.CommitVerifiedTvEpisodeMoveAsync(
            Move() with { TargetTvdbEpisodeId = "unreviewed-episode" });
        Assert.Equal(MediaEditorCommitOutcome.Conflict, wrongBridge.Outcome);
        var wrongSeries = await repository.CommitVerifiedTvEpisodeMoveAsync(
            Move() with { ExpectedTargetTvdbSeriesId = "another-series" });
        Assert.Equal(MediaEditorCommitOutcome.Conflict, wrongSeries.Outcome);

        var otherShow = Guid.NewGuid();
        var otherSeason = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works(id, media_type, work_kind) VALUES(@show, 'TV', 'parent');
                INSERT INTO works(id, media_type, work_kind, parent_work_id)
                VALUES(@season, 'TV', 'parent', @show);
                """, new { show = otherShow, season = otherSeason });
            connection.Execute("UPDATE works SET parent_work_id = @season WHERE id = @target;",
                new { season = otherSeason, target = _target });
        }
        var differentShow = await repository.CommitVerifiedTvEpisodeMoveAsync(
            Move() with { ExpectedTargetSeasonWorkId = otherSeason });
        Assert.Equal(MediaEditorCommitOutcome.Conflict, differentShow.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task SpecialsEpisodeMovesToRealSeasonWithoutRetargetingOtherWork()
    {
        var actualSeason = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                UPDATE works SET ordinal = 0, ownership = 'Owned' WHERE id = @specials;
                INSERT INTO works(id, media_type, work_kind, parent_work_id, ordinal, ownership, is_catalog_only)
                VALUES(@actual, 'TV', 'parent', @show, 1, 'Unowned', 1);
                UPDATE works SET parent_work_id = @actual WHERE id = @target;
                """, new { specials = _season, actual = actualSeason, show = _show, target = _target });
        }

        var move = Move() with { ExpectedTargetSeasonWorkId = actualSeason };
        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(move);
        var replay = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(move);

        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Replayed, replay.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = _edition }));
        var seasons = verify.Query<(Guid Id, string Ownership, int IsCatalogOnly)>(
            "SELECT id AS Id, ownership AS Ownership, is_catalog_only AS IsCatalogOnly FROM works WHERE id IN (@source, @target);",
            new { source = _season, target = actualSeason }).ToDictionary(row => row.Id);
        Assert.Equal("Unowned", seasons[_season].Ownership);
        Assert.Equal(1, seasons[_season].IsCatalogOnly);
        Assert.Equal("Owned", seasons[actualSeason].Ownership);
        Assert.Equal(0, seasons[actualSeason].IsCatalogOnly);
    }

    [Fact]
    public async Task CrossSeasonMoveKeepsSourceSeasonOwnedWhenSiblingFileRemains()
    {
        var actualSeason = Guid.NewGuid();
        var siblingWork = Guid.NewGuid();
        var siblingEdition = Guid.NewGuid();
        var siblingAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works(id, media_type, work_kind, parent_work_id, ownership, is_catalog_only)
                VALUES(@actual, 'TV', 'parent', @show, 'Unowned', 1),
                      (@sibling, 'TV', 'child', @sourceSeason, 'Owned', 0);
                UPDATE works SET parent_work_id = @actual WHERE id = @target;
                INSERT INTO editions(id, work_id) VALUES(@siblingEdition, @sibling);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                VALUES(@siblingAsset, @siblingEdition, @hash, 'C:/fixture/sibling.mkv');
                """, new
            {
                actual = actualSeason,
                show = _show,
                sibling = siblingWork,
                sourceSeason = _season,
                target = _target,
                siblingEdition,
                siblingAsset,
                hash = Guid.NewGuid().ToString("N")
            });
        }

        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodeMoveAsync(Move() with { ExpectedTargetSeasonWorkId = actualSeason });
        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal("Owned", verify.QuerySingle<string>(
            "SELECT ownership FROM works WHERE id = @season;", new { season = _season }));
        Assert.Equal(siblingWork, verify.QuerySingle<Guid>("""
            SELECT e.work_id FROM media_assets a JOIN editions e ON e.id = a.edition_id
            WHERE a.id = @asset;
            """, new { asset = siblingAsset }));
        Assert.Null(verify.QuerySingle<string?>(
            "SELECT writeback_status FROM media_assets WHERE id = @asset;", new { asset = siblingAsset }));
    }

    [Fact]
    public void StartupMigratesExistingDatabaseWithoutEditorReceiptTables()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DROP TABLE media_editor_commit_items; DROP TABLE media_editor_commits;");
        }

        _database.RunStartupChecks();

        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='media_editor_commits';"));
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='media_editor_commit_items';"));
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='media_editor_commit_artwork';"));
    }

    [Fact]
    public async Task BatchCanMoveOneEditionWhileUnselectedSiblingEditionKeepsSourceWork()
    {
        var siblingEdition = Guid.NewGuid();
        var siblingAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO editions(id, work_id) VALUES(@edition, @work);",
                new { edition = siblingEdition, work = _source });
            connection.Execute("""
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
                VALUES(@asset, @edition, @hash, 'C:/fixture/unselected.mkv');
                """, new
            {
                asset = siblingAsset,
                edition = siblingEdition,
                hash = Guid.NewGuid().ToString("N")
            });
        }

        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([Move()]);
        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = siblingEdition }));
        Assert.Equal("Owned", verify.QuerySingle<string>(
            "SELECT ownership FROM works WHERE id = @id;", new { id = _source }));
        Assert.Null(verify.QuerySingle<string?>(
            "SELECT writeback_status FROM media_assets WHERE id = @id;", new { id = siblingAsset }));
    }

    [Fact]
    public async Task SharedEditionRequiresAllItsFilesToMoveTogether()
    {
        var secondAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/second-encode.mkv', @library);
                    """, new
            {
                asset = secondAsset,
                edition = _edition,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }

        var repository = new MediaEditorCommitRepository(_database);
        var first = Move();
        var partial = await repository.CommitVerifiedTvEpisodePlanAsync([first]);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, partial.Outcome);
        Assert.Contains("unselected file", partial.Items[0].ConflictReason);

        var second = first with { AssetId = secondAsset };
        var complete = await repository.CommitVerifiedTvEpisodePlanAsync([first, second]);
        var replay = await repository.CommitVerifiedTvEpisodePlanAsync([second, first]);
        var reusedForSubset = await repository.CommitVerifiedTvEpisodePlanAsync([first]);
        Assert.Equal(MediaEditorCommitOutcome.Committed, complete.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Replayed, replay.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, reusedForSubset.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Equal(2, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_items;"));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task OneStaleRowMakesEntireBatchReviewableWithoutPartialSave()
    {
        var otherWork = Guid.NewGuid();
        var otherEdition = Guid.NewGuid();
        var otherAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works(id, media_type, work_kind, parent_work_id)
                VALUES(@work, 'TV', 'child', @season);
                INSERT INTO editions(id, work_id) VALUES(@edition, @work);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                VALUES(@asset, @edition, @hash, 'C:/fixture/other.mkv', @library);
                INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                VALUES(@work, @key, 'other-r1', @now);
                """, new
            {
                work = otherWork,
                season = _season,
                edition = otherEdition,
                asset = otherAsset,
                hash = Guid.NewGuid().ToString("N"),
                key = MetadataFieldConstants.IdentityRevision,
                now = DateTimeOffset.UtcNow.ToString("O"),
                library = _library.ToString("D")
            });
        }
        var first = Move();
        var stale = first with
        {
            AssetId = otherAsset,
            ExpectedEditionId = otherEdition,
            ExpectedSourceWorkId = otherWork,
            ExpectedSourceIdentityRevision = "stale"
        };
        var repository = new MediaEditorCommitRepository(_database);
        var conflicted = await repository.CommitVerifiedTvEpisodePlanAsync([first, stale]);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, conflicted.Outcome);
        Assert.Contains(conflicted.Items, item => item.AssetId == otherAsset
            && item.ConflictReason!.Contains("changed", StringComparison.Ordinal));
        using (var verify = _database.CreateConnection())
        {
            Assert.Equal(_source, verify.QuerySingle<Guid>(
                "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
            Assert.Equal(otherWork, verify.QuerySingle<Guid>(
                "SELECT work_id FROM editions WHERE id = @id;", new { id = otherEdition }));
            Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        }

        var accepted = await repository.CommitVerifiedTvEpisodePlanAsync(
            [first, stale with { ExpectedSourceIdentityRevision = "other-r1" }]);
        Assert.Equal(MediaEditorCommitOutcome.Committed, accepted.Outcome);
        using var after = _database.CreateConnection();
        Assert.Equal(_target, after.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Equal(_target, after.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = otherEdition }));
    }

    [Fact]
    public async Task LateReceiptFailureRollsBackMoveOwnershipAndRetagIntent()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DROP TABLE media_editor_commit_items;");
        }

        await Assert.ThrowsAsync<SqliteException>(() => new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([Move()]));

        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @edition;", new { edition = _edition }));
        Assert.Equal("Owned", verify.QuerySingle<string>(
            "SELECT ownership FROM works WHERE id = @source;", new { source = _source }));
        Assert.Null(verify.QuerySingle<string?>(
            "SELECT writeback_status FROM media_assets WHERE id = @asset;", new { asset = _asset }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task ConcurrentDuplicateSavesCommitOnceAndReplayOnce()
    {
        var operation = Move();
        var repository = new MediaEditorCommitRepository(_database);
        var results = await Task.WhenAll(
            repository.CommitVerifiedTvEpisodePlanAsync([operation]),
            repository.CommitVerifiedTvEpisodePlanAsync([operation]));

        Assert.Contains(results, result => result.Outcome == MediaEditorCommitOutcome.Committed);
        Assert.Contains(results, result => result.Outcome == MediaEditorCommitOutcome.Replayed);
        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_items;"));
    }

    [Fact]
    public async Task CatalogTargetWithAnActualOwnedFileIsRejectedEvenIfItsFlagIsStale()
    {
        var targetEdition = Guid.NewGuid();
        var protectedAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                UPDATE works SET work_kind = 'catalog', ownership = 'Unowned', is_catalog_only = 1
                WHERE id = @target;
                INSERT INTO editions(id, work_id) VALUES(@edition, @target);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                VALUES(@asset, @edition, @hash, 'C:/fixture/protected.mkv', @library);
                """, new
            {
                target = _target,
                edition = targetEdition,
                asset = protectedAsset,
                hash = Guid.NewGuid().ToString("N"),
                library = Guid.NewGuid().ToString("D")
            });
        }

        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([Move() with { ExpectedTargetWorkKind = "catalog" }]);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = targetEdition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task SourceLibraryChangeAfterReviewConflictsInsideTransaction()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id = @library WHERE id = @asset;",
                    new { asset = _asset, library = Guid.NewGuid().ToString("D") });
        }

        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([Move()]);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Null(verify.QuerySingle<string?>(
            "SELECT writeback_status FROM media_assets WHERE id = @id;", new { id = _asset }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task CatalogTargetThatChangedKindAfterReviewConflicts()
    {
        var reviewed = Move() with { ExpectedTargetWorkKind = "catalog" };
        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([reviewed]);

        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task ReviewedEpisodeStillMovesWithSharedEditionAndPreservesPriorArtwork()
    {
        var secondAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@secondAsset, @edition, @hash, 'C:/fixture/second.mkv', @library);
                    """, new
            {
                secondAsset,
                edition = _edition,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }
        var (oldArt, newArt, oldLink) = SeedEpisodeStillArtwork();
        var repository = new MediaEditorCommitRepository(_database);
        var revision = await repository.GetEpisodeStillPreferenceRevisionAsync(_target);
        Assert.StartsWith("v1:", revision);
        var first = Move();
        var second = first with { AssetId = secondAsset };
        var still = new VerifiedEpisodeStillAssignment(_target, newArt, "new-art", revision!,
            [first.AssetId, secondAsset],
            [new(first.AssetId, _library), new(secondAsset, _library)]);

        var committed = await repository.CommitVerifiedTvEpisodePlanAsync([first, second], still);
        var replayed = await repository.CommitVerifiedTvEpisodePlanAsync([second, first],
            still with { ExpectedAffectedAssetIds = [second.AssetId, first.AssetId] });
        Assert.Equal(MediaEditorCommitOutcome.Committed, committed.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Replayed, replayed.Outcome);

        using var verify = _database.CreateConnection();
        var links = verify.Query<(Guid ArtId, int Preferred)>("""
            SELECT artwork_asset_id AS ArtId, is_preferred AS Preferred
            FROM entity_artwork_links WHERE entity_id=@target AND role='Primary' AND context='Episode';
            """, new { target = _target }).ToDictionary(row => row.ArtId);
        Assert.Equal(2, links.Count);
        Assert.Equal(0, links[oldArt].Preferred);
        Assert.Equal(1, links[newArt].Preferred);
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT is_preferred FROM entity_assets WHERE id=@oldLink;", new { oldLink }));
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_assets
            WHERE entity_id=@target AND asset_type='EpisodeStill' AND is_preferred=1;
            """, new { target = _target }));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_artwork;"));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_artwork_writeback;"));
    }

    [Fact]
    public async Task StaleEpisodeStillPreferenceConflictsWithoutMovingFile()
    {
        var (_, newArt, _) = SeedEpisodeStillArtwork();
        var repository = new MediaEditorCommitRepository(_database);
        var oldRevision = await repository.GetEpisodeStillPreferenceRevisionAsync(_target);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    UPDATE entity_artwork_links SET is_preferred=0
                    WHERE entity_id=@target AND role='Primary' AND context='Episode';
                    """, new { target = _target });
        }

        var result = await repository.CommitVerifiedTvEpisodePlanAsync([Move()],
            new VerifiedEpisodeStillAssignment(_target, newArt, "new-art", oldRevision!, [_asset],
                [new(_asset, _library)]));
        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        Assert.Contains("preferred artwork changed", result.Items[0].ConflictReason);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task ExistingTargetSiblingMustBeIncludedInReviewedArtworkImpact()
    {
        var (_, newArt, _) = SeedEpisodeStillArtwork();
        var targetEdition = Guid.NewGuid();
        var unselected = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@targetEdition, @target);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@unselected, @targetEdition, @hash, 'C:/fixture/unselected.mkv', @library);
                    """, new
            {
                targetEdition,
                target = _target,
                unselected,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }
        var repository = new MediaEditorCommitRepository(_database);
        var revision = await repository.GetEpisodeStillPreferenceRevisionAsync(_target);
        var result = await repository.CommitVerifiedTvEpisodePlanAsync([Move()],
            new VerifiedEpisodeStillAssignment(_target, newArt, "new-art", revision!, [_asset],
                [new(_asset, _library)]));

        Assert.Equal(MediaEditorCommitOutcome.Conflict, result.Outcome);
        Assert.Contains("affected file set changed", result.Items[0].ConflictReason);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_artwork;"));

        var reviewed = await repository.CommitVerifiedTvEpisodePlanAsync([Move()],
            new VerifiedEpisodeStillAssignment(_target, newArt, "new-art", revision!,
                [_asset, unselected], [new(_asset, _library), new(unselected, _library)]));
        Assert.Equal(MediaEditorCommitOutcome.Committed, reviewed.Outcome);
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commit_artwork;"));
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT is_preferred FROM entity_artwork_links
            WHERE entity_id=@target AND artwork_asset_id=@newArt;
            """, new { target = _target, newArt }));
        var siblingSync = verify.QuerySingle<(string? Hash, string? Status)>("""
            SELECT writeback_fields_hash AS Hash, writeback_status AS Status
            FROM media_assets WHERE id=@asset;
            """, new { asset = unselected });
        Assert.StartsWith("editor:pending:", siblingSync.Hash);
        Assert.Equal("pending", siblingSync.Status);
    }

    [Fact]
    public async Task ExistingTargetSiblingLibraryChangeOrOrphaningConflicts()
    {
        var (_, newArt, _) = SeedEpisodeStillArtwork();
        var targetEdition = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@targetEdition, @target);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@sibling, @targetEdition, @hash, 'C:/fixture/sibling.mkv', @library);
                    """, new
            {
                targetEdition,
                target = _target,
                sibling,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }
        var repository = new MediaEditorCommitRepository(_database);
        var revision = await repository.GetEpisodeStillPreferenceRevisionAsync(_target);
        VerifiedEpisodeStillAssignment Still() => new(_target, newArt, "new-art", revision!,
            [_asset, sibling], [new(_asset, _library), new(sibling, _library)]);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@changed WHERE id=@sibling;",
                    new { sibling, changed = Guid.NewGuid().ToString("D") });
        }
        var libraryConflict = await repository.CommitVerifiedTvEpisodePlanAsync([Move()], Still());
        Assert.Equal(MediaEditorCommitOutcome.Conflict, libraryConflict.Outcome);
        Assert.Contains("library changed", libraryConflict.Items[0].ConflictReason);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library, is_orphaned=1 WHERE id=@sibling;",
                    new { sibling, library = _library.ToString("D") });
        }
        var orphanConflict = await repository.CommitVerifiedTvEpisodePlanAsync([Move()], Still());
        Assert.Equal(MediaEditorCommitOutcome.Conflict, orphanConflict.Outcome);
        Assert.Contains("cannot safely share", orphanConflict.Items[0].ConflictReason);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task EpisodeStillWriteFailureRollsBackIdentityPreferenceAndReceipt()
    {
        var (oldArt, newArt, _) = SeedEpisodeStillArtwork();
        var repository = new MediaEditorCommitRepository(_database);
        var revision = await repository.GetEpisodeStillPreferenceRevisionAsync(_target);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    CREATE TRIGGER reject_new_episode_still BEFORE INSERT ON entity_artwork_links
                    BEGIN SELECT RAISE(ABORT, 'art write rejected'); END;
                    """);
        }

        await Assert.ThrowsAsync<SqliteException>(() => repository.CommitVerifiedTvEpisodePlanAsync(
            [Move()], new VerifiedEpisodeStillAssignment(_target, newArt, "new-art", revision!, [_asset],
                [new(_asset, _library)])));
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@edition;", new { edition = _edition }));
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT is_preferred FROM entity_artwork_links
            WHERE entity_id=@target AND artwork_asset_id=@oldArt;
            """, new { target = _target, oldArt }));
        Assert.Null(verify.QuerySingle<string?>(
            "SELECT writeback_status FROM media_assets WHERE id=@asset;", new { asset = _asset }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
    }

    [Fact]
    public async Task ReviewedShowPreferenceCommitsWithPairingAndReplaysOnce()
    {
        var art = SeedSharedArtwork();
        var assignment = await ReviewedSharedArtworkAsync(_show, "TvShow", "Background",
            [new(_asset, _library)]);
        var repository = new MediaEditorCommitRepository(_database);
        var move = Move();
        Assert.Null(await repository.TryReplayVerifiedTvEpisodePlanAsync(
            [move], null, assignment));

        var first = await repository.CommitVerifiedTvEpisodePlanAsync([move], null, assignment);
        var replay = await repository.TryReplayVerifiedTvEpisodePlanAsync([move], null, assignment);

        Assert.Equal(MediaEditorCommitOutcome.Committed, first.Outcome);
        Assert.Equal(MediaEditorCommitOutcome.Replayed, replay!.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@id;", new { id = _edition }));
        Assert.Equal(art, verify.QuerySingle<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@show AND role='Background' AND is_preferred=1;
            """, new { show = _show }));
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_preferred_artwork_commits;"));
        Assert.Equal(1, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        Assert.Equal(MediaEditorCommitOutcome.Conflict,
            (await repository.TryReplayVerifiedTvEpisodePlanAsync([move], null,
                assignment with { Role = "Logo" }))!.Outcome);
    }

    [Fact]
    public async Task ShowPreferenceRequiresUnselectedSiblingAndRejectsStaleIdentity()
    {
        SeedSharedArtwork();
        var siblingEdition = Guid.NewGuid();
        var siblingAsset = Guid.NewGuid();
        using (var setup = _database.CreateConnection())
        {
            setup.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@edition, @work);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/sibling.mkv', @library);
                    """, new
            {
                edition = siblingEdition,
                work = _source,
                asset = siblingAsset,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }
        var partial = await ReviewedSharedArtworkAsync(_show, "TvShow", "Primary",
            [new(_asset, _library)]);
        var repository = new MediaEditorCommitRepository(_database);
        var omitted = await repository.CommitVerifiedTvEpisodePlanAsync([Move()], null, partial);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, omitted.Outcome);

        var complete = await ReviewedSharedArtworkAsync(_show, "TvShow", "Primary",
            [new(_asset, _library), new(siblingAsset, _library)]);
        using (var setup = _database.CreateConnection())
        {
            setup.Execute("""
                    INSERT INTO bridge_ids(id, entity_id, id_type, id_value)
                    VALUES(@id, @asset, 'test_identity', 'changed');
                    """, new { id = Guid.NewGuid(), asset = siblingAsset });
        }
        var stale = await repository.CommitVerifiedTvEpisodePlanAsync([Move()], null, complete);
        Assert.Equal(MediaEditorCommitOutcome.Conflict, stale.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@id;", new { id = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_preferred_artwork_commits;"));
    }

    [Fact]
    public async Task StalePreferenceAndLateImpactConflictRollBackTheMove()
    {
        SeedSharedArtwork();
        var reviewed = await ReviewedSharedArtworkAsync(_show, "TvShow", "Logo",
            [new(_asset, _library)]);
        using (var setup = _database.CreateConnection())
        {
            setup.Execute("""
                    INSERT INTO entity_artwork_links
                        (id, entity_id, entity_type, artwork_asset_id, role, context,
                         source_asset_type, is_preferred)
                    VALUES(@id, @show, 'Work', @art, 'Logo', '', 'Logo', 1);
                    """, new
            {
                id = Guid.NewGuid(),
                show = _show,
                art = reviewed.ArtworkAssetId
            });
        }
        var repository = new MediaEditorCommitRepository(_database);
        Assert.Equal(MediaEditorCommitOutcome.Conflict,
            (await repository.CommitVerifiedTvEpisodePlanAsync([Move()], null, reviewed)).Outcome);

        var fresh = await ReviewedSharedArtworkAsync(_show, "TvShow", "Logo",
            [new(_asset, _library)]);
        var lateConflict = fresh with
        {
            ExpectedAffectedAssets =
            [fresh.ExpectedAffectedAssets[0] with { LibraryId = Guid.NewGuid() }]
        };
        Assert.Equal(MediaEditorCommitOutcome.Conflict,
            (await repository.CommitVerifiedTvEpisodePlanAsync([Move()], null, lateConflict)).Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_source, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@id;", new { id = _edition }));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM media_editor_commits;"));
        Assert.Equal(0, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_preferred_artwork_commits;"));
    }

    [Fact]
    public async Task CrossSeasonTargetPreferenceIncludesExistingTargetSiblingOnly()
    {
        var targetSeason = Guid.NewGuid();
        var siblingWork = Guid.NewGuid();
        var siblingEdition = Guid.NewGuid();
        var siblingAsset = Guid.NewGuid();
        using (var setup = _database.CreateConnection())
        {
            setup.Execute("""
                    INSERT INTO works(id, media_type, work_kind, parent_work_id)
                    VALUES(@targetSeason, 'TV', 'parent', @show),
                          (@siblingWork, 'TV', 'child', @targetSeason);
                    UPDATE works SET parent_work_id=@targetSeason WHERE id=@target;
                    INSERT INTO editions(id, work_id) VALUES(@siblingEdition, @siblingWork);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@siblingAsset, @siblingEdition, @hash,
                           'C:/fixture/target-sibling.mkv', @library);
                    """, new
            {
                targetSeason,
                show = _show,
                siblingWork,
                target = _target,
                siblingEdition,
                siblingAsset,
                hash = Guid.NewGuid().ToString("N"),
                library = _library.ToString("D")
            });
        }
        SeedSharedArtwork();
        var reviewed = await ReviewedSharedArtworkAsync(targetSeason, "TvSeason", "Primary",
            [new(_asset, _library), new(siblingAsset, _library)]);
        var result = await new MediaEditorCommitRepository(_database)
            .CommitVerifiedTvEpisodePlanAsync([Move() with
                { ExpectedTargetSeasonWorkId = targetSeason }], null, reviewed);
        Assert.Equal(MediaEditorCommitOutcome.Committed, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(_target, verify.QuerySingle<Guid>(
            "SELECT work_id FROM editions WHERE id=@id;", new { id = _edition }));
        Assert.Contains(siblingAsset.ToString("D"), verify.QuerySingle<string>(
            "SELECT affected_assets_json FROM media_editor_preferred_artwork_commits;"),
            StringComparison.OrdinalIgnoreCase);
        var siblingSync = verify.QuerySingle<(string? Hash, string? Status)>("""
            SELECT writeback_fields_hash AS Hash, writeback_status AS Status
            FROM media_assets WHERE id=@asset;
            """, new { asset = siblingAsset });
        Assert.StartsWith("editor:pending:", siblingSync.Hash);
        Assert.Equal("pending", siblingSync.Status);
    }

    private Guid SeedSharedArtwork()
    {
        var art = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO artwork_assets(id, content_hash, original_path)
            VALUES(@art, 'shared-art', 'C:/fixture/shared-art.jpg');
            """, new { art });
        return art;
    }

    private async Task<VerifiedTvPreferredArtworkAssignment> ReviewedSharedArtworkAsync(
        Guid owner, string scope, string role,
        IReadOnlyList<VerifiedArtworkAssetLibrary> affected)
    {
        using var connection = _database.CreateConnection();
        var art = connection.QuerySingle<Guid>(
            "SELECT id FROM artwork_assets WHERE content_hash='shared-art';");
        var revisions = await new MediaEditorCommitRepository(_database)
            .GetTvArtworkAssetRevisionsAsync(affected.Select(item => item.AssetId).ToArray());
        var ownerRevision = await new MediaEditorCommitRepository(_database)
            .GetTvPreferredArtworkOwnerRevisionAsync(owner, scope, role);
        return new(owner, scope, role, art, "shared-art", ownerRevision!,
            affected.Select(item => new VerifiedTvArtworkAssetReview(item.AssetId,
                item.LibraryId, revisions[item.AssetId])).ToArray());
    }

    private (Guid OldArt, Guid NewArt, Guid OldLink) SeedEpisodeStillArtwork()
    {
        var oldArt = Guid.NewGuid();
        var newArt = Guid.NewGuid();
        var oldLink = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO artwork_assets(id, content_hash, original_path)
            VALUES(@oldArt, 'old-art', 'C:/fixture/old.jpg'),
                  (@newArt, 'new-art', 'C:/fixture/new.jpg');
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred, is_user_override)
            VALUES(@oldLink, @target, 'Work', @oldArt, 'Primary', 'Episode',
                   'EpisodeStill', 1, 1);
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, is_preferred, is_user_override)
            VALUES(@oldLink, @target, 'Work', 'EpisodeStill', 1, 1);
            """, new { oldArt, newArt, oldLink, target = _target });
        return (oldArt, newArt, oldLink);
    }

    private VerifiedTvEpisodeMove Move() => new(
        $"test:{Guid.NewGuid():N}", _asset, _edition, _source, _season,
        _target, _season, _show, _show, "series-1", "series-1", "episode-2",
        "source-r1", "target-r1", "show-r1", "show-r1", "child", _library);

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
