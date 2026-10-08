using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class MediaEditorPreferredArtworkRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_preferred_art_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _show = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();
    private readonly Guid _episode = Guid.NewGuid();
    private readonly Guid _album = Guid.NewGuid();
    private readonly Guid _track = Guid.NewGuid();
    private readonly Guid _movie = Guid.NewGuid();
    private readonly Guid _tvAsset = Guid.NewGuid();
    private readonly Guid _albumAsset = Guid.NewGuid();
    private readonly Guid _movieAsset = Guid.NewGuid();
    private readonly Guid _tvLibrary = Guid.NewGuid();
    private readonly Guid _albumLibrary = Guid.NewGuid();
    private readonly Guid _movieLibrary = Guid.NewGuid();
    private readonly Guid _artwork = Guid.NewGuid();
    private readonly Guid _oldArtwork = Guid.NewGuid();
    private readonly Guid _oldLink = Guid.NewGuid();

    public MediaEditorPreferredArtworkRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id, media_type, work_kind, parent_work_id) VALUES
                (@show, 'TV', 'parent', NULL),
                (@season, 'TV', 'parent', @show),
                (@episode, 'TV', 'child', @season),
                (@album, 'Music', 'parent', NULL),
                (@track, 'Music', 'child', @album),
                (@movie, 'Movies', 'standalone', NULL);
            INSERT INTO editions(id, work_id) VALUES
                (@tvEdition, @episode), (@albumEdition, @track),
                (@movieEdition, @movie);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id) VALUES
                (@tvAsset, @tvEdition, @tvHash, 'C:/fixture/show.mkv', @tvLibrary),
                (@albumAsset, @albumEdition, @albumHash, 'C:/fixture/song.flac', @albumLibrary),
                (@movieAsset, @movieEdition, @movieHash, 'C:/fixture/movie.mkv', @movieLibrary);
            INSERT INTO artwork_assets(id, content_hash, original_path) VALUES
                (@artwork, 'new-art', 'C:/fixture/new-art.jpg'),
                (@oldArtwork, 'old-art', 'C:/fixture/old-art.jpg');
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred)
            VALUES(@oldLink, @show, 'Work', @oldArtwork, 'Primary', '', 'CoverArt', 1);
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, is_preferred)
            VALUES(@oldLink, @show, 'Work', 'CoverArt', 1);
            """, new
        {
            show = _show,
            season = _season,
            episode = _episode,
            album = _album,
            track = _track,
            movie = _movie,
            tvAsset = _tvAsset,
            albumAsset = _albumAsset,
            movieAsset = _movieAsset,
            tvEdition = Guid.NewGuid(),
            albumEdition = Guid.NewGuid(),
            movieEdition = Guid.NewGuid(),
            tvHash = Guid.NewGuid().ToString("N"),
            albumHash = Guid.NewGuid().ToString("N"),
            movieHash = Guid.NewGuid().ToString("N"),
            tvLibrary = _tvLibrary.ToString("D"),
            albumLibrary = _albumLibrary.ToString("D"),
            movieLibrary = _movieLibrary.ToString("D"),
            artwork = _artwork,
            oldArtwork = _oldArtwork,
            oldLink = _oldLink
        });
    }

    [Theory]
    [InlineData("TvShow", "Primary", "CoverArt", "")]
    [InlineData("TvShow", "Background", "Background", "")]
    [InlineData("TvShow", "Logo", "Logo", "")]
    [InlineData("TvSeason", "Primary", "SeasonPoster", "Season")]
    [InlineData("MusicAlbum", "Primary", "CoverArt", "")]
    [InlineData("Movie", "Primary", "CoverArt", "")]
    public async Task SupportedOwnerRolesCommitAndReplayWithoutRemovingExistingArt(
        string scope, string role, string legacyType, string context)
    {
        var owner = scope switch
        {
            "TvShow" => _show,
            "TvSeason" => _season,
            "Movie" => _movie,
            _ => _album,
        };
        var affected = scope switch
        {
            "MusicAlbum" => new VerifiedArtworkAssetLibrary(_albumAsset, _albumLibrary),
            "Movie" => new VerifiedArtworkAssetLibrary(_movieAsset, _movieLibrary),
            _ => new VerifiedArtworkAssetLibrary(_tvAsset, _tvLibrary),
        };
        var repository = new MediaEditorPreferredArtworkRepository(_database);
        var assignment = new VerifiedPreferredArtworkAssignment(Guid.NewGuid().ToString("D"),
            owner, scope, role, _artwork, "new-art",
            (await repository.GetOwnerRevisionAsync(owner, scope, role))!, [affected]);

        var saved = await repository.CommitVerifiedAsync(assignment);
        var replay = await repository.CommitVerifiedAsync(assignment);

        Assert.Equal(PreferredArtworkCommitOutcome.Committed, saved.Outcome);
        Assert.Equal(PreferredArtworkCommitOutcome.Replayed, replay.Outcome);
        using var connection = _database.CreateConnection();
        Assert.Equal(_artwork, connection.QuerySingle<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@owner AND role=@role AND context=@context AND is_preferred=1;
            """, new { owner, role, context }));
        Assert.Equal(1, connection.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_assets
            WHERE entity_id=@owner AND asset_type=@legacyType AND is_preferred=1;
            """, new { owner, legacyType }));
        Assert.Equal(1, connection.QuerySingle<int>("""
            SELECT COUNT(*) FROM media_editor_preferred_artwork_commits
            WHERE operation_token=@token;
            """, new { token = assignment.OperationToken }));
        if (owner == _show && role == "Primary")
        {
            Assert.Equal(2, connection.QuerySingle<int>("""
                SELECT COUNT(*) FROM entity_artwork_links
                WHERE entity_id=@owner AND role='Primary' AND context='';
                """, new { owner }));
            Assert.Equal(0, connection.QuerySingle<int>("""
                SELECT is_preferred FROM entity_artwork_links WHERE id=@oldLink;
                """, new { oldLink = _oldLink }));
        }
    }

    [Fact]
    public async Task CompleteImpactRejectsNewSiblingAndLibraryChange()
    {
        var repository = new MediaEditorPreferredArtworkRepository(_database);
        var assignment = await ReviewedAsync(_show, "TvShow", "Primary",
            [new(_tvAsset, _tvLibrary)]);
        var sibling = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO editions(id, work_id) VALUES(@edition, @episode);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                VALUES(@sibling, @edition, @hash, 'C:/fixture/sibling.mkv', @library);
                """, new
            {
                edition = Guid.NewGuid(),
                episode = _episode,
                sibling,
                hash = Guid.NewGuid().ToString("N"),
                library = _tvLibrary.ToString("D")
            });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(assignment)).Outcome);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id=@sibling;", new { sibling });
        }
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library WHERE id=@asset;",
                    new { library = Guid.NewGuid().ToString("D"), asset = _tvAsset });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(assignment)).Outcome);
        AssertNoReceipt();
    }

    [Fact]
    public async Task ShowPreferenceRequiresEveryOwnedSeasonFile()
    {
        var secondSeason = Guid.NewGuid();
        var secondEpisode = Guid.NewGuid();
        var secondEdition = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO works(id, media_type, work_kind, parent_work_id)
                    VALUES(@season, 'TV', 'parent', @show),
                          (@episode, 'TV', 'child', @season);
                    INSERT INTO editions(id, work_id) VALUES(@edition, @episode);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/season-2.mkv', @library);
                    """, new
            {
                show = _show,
                season = secondSeason,
                episode = secondEpisode,
                edition = secondEdition,
                asset = secondAsset,
                hash = Guid.NewGuid().ToString("N"),
                library = secondLibrary.ToString("D")
            });
        }
        var repository = new MediaEditorPreferredArtworkRepository(_database);
        var partial = await ReviewedAsync(_show, "TvShow", "Background",
            [new(_tvAsset, _tvLibrary)]);
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(partial)).Outcome);
        var complete = partial with
        {
            ExpectedAffectedAssetLibraries =
            [new(_tvAsset, _tvLibrary), new(secondAsset, secondLibrary)]
        };
        Assert.Equal(PreferredArtworkCommitOutcome.Committed,
            (await repository.CommitVerifiedAsync(complete)).Outcome);
        using var verify = _database.CreateConnection();
        Assert.Contains(secondAsset.ToString("D"), verify.QuerySingle<string>("""
            SELECT affected_assets_json FROM media_editor_preferred_artwork_commits;
            """), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeasonPreferenceDoesNotIncludeAnotherSeasonsFiles()
    {
        var secondSeason = Guid.NewGuid();
        var secondEpisode = Guid.NewGuid();
        var secondEdition = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO works(id, media_type, work_kind, parent_work_id)
                    VALUES(@season, 'TV', 'parent', @show),
                          (@episode, 'TV', 'child', @season);
                    INSERT INTO editions(id, work_id) VALUES(@edition, @episode);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/other-season.mkv', @library);
                    """, new
            {
                show = _show,
                season = secondSeason,
                episode = secondEpisode,
                edition = secondEdition,
                asset = secondAsset,
                hash = Guid.NewGuid().ToString("N"),
                library = _tvLibrary.ToString("D")
            });
        }
        var assignment = await ReviewedAsync(_season, "TvSeason", "Primary",
            [new(_tvAsset, _tvLibrary)]);
        var result = await new MediaEditorPreferredArtworkRepository(_database)
            .CommitVerifiedAsync(assignment);
        Assert.Equal(PreferredArtworkCommitOutcome.Committed, result.Outcome);
        using var verify = _database.CreateConnection();
        Assert.DoesNotContain(secondAsset.ToString("D"), verify.QuerySingle<string>("""
            SELECT affected_assets_json FROM media_editor_preferred_artwork_commits;
            """), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerFileOrOrphanedDescendantBlocksGroupPreference(bool directOwnerFile)
    {
        var directAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            if (directOwnerFile)
            {
                connection.Execute("""
                    INSERT INTO editions(id, work_id) VALUES(@edition, @show);
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/direct-show.mkv', @library);
                    """, new
                {
                    show = _show,
                    edition = Guid.NewGuid(),
                    asset = directAsset,
                    hash = Guid.NewGuid().ToString("N"),
                    library = _tvLibrary.ToString("D")
                });
            }
            else
            {
                connection.Execute("UPDATE media_assets SET is_orphaned=1 WHERE id=@asset;",
                        new { asset = _tvAsset });
            }
        }
        var affected = directOwnerFile
            ? new[] { new VerifiedArtworkAssetLibrary(_tvAsset, _tvLibrary),
                new VerifiedArtworkAssetLibrary(directAsset, _tvLibrary) }
            : [new VerifiedArtworkAssetLibrary(_tvAsset, _tvLibrary)];
        var assignment = await ReviewedAsync(_show, "TvShow", "Primary", affected);
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await new MediaEditorPreferredArtworkRepository(_database).CommitVerifiedAsync(assignment)).Outcome);
        AssertNoReceipt();
    }

    [Fact]
    public async Task StaleOwnerPreferenceVariantAndTokenReuseAreRejected()
    {
        var repository = new MediaEditorPreferredArtworkRepository(_database);
        var assignment = await ReviewedAsync(_show, "TvShow", "Primary",
            [new(_tvAsset, _tvLibrary)]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE entity_artwork_links SET is_user_override=1 WHERE id=@oldLink;",
                    new { oldLink = _oldLink });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(assignment)).Outcome);
        assignment = assignment with
        {
            ExpectedOwnerRevision = (await repository.GetOwnerRevisionAsync(_show, "TvShow", "Primary"))!
        };
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE artwork_assets SET content_hash='changed' WHERE id=@artwork;",
                    new { artwork = _artwork });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(assignment)).Outcome);
        assignment = assignment with { ExpectedVariantContentHash = "changed" };
        Assert.Equal(PreferredArtworkCommitOutcome.Committed,
            (await repository.CommitVerifiedAsync(assignment)).Outcome);
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedAsync(assignment with { Role = "Background" })).Outcome);
    }

    [Fact]
    public async Task OuterTransactionFailureRollsBackPreferenceAndReceipt()
    {
        var assignment = await ReviewedAsync(_season, "TvSeason", "Primary",
            [new(_tvAsset, _tvLibrary)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.ExecuteWriteAsync<int>(
            (connection, transaction, ct) =>
            {
                var result = MediaEditorPreferredArtworkRepository.ApplyVerifiedInTransaction(
                    connection, transaction, assignment, ct);
                Assert.Equal(PreferredArtworkCommitOutcome.Committed, result.Outcome);
                throw new InvalidOperationException("Later editor operation failed");
            }, CancellationToken.None));
        using var verify = _database.CreateConnection();
        Assert.Equal(0, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_artwork_links WHERE entity_id=@season;
            """, new { season = _season }));
        AssertNoReceipt();
    }

    [Fact]
    public async Task UnsupportedEditionAndRoleScopesRemainClosed()
    {
        var repository = new MediaEditorPreferredArtworkRepository(_database);
        Assert.Null(await repository.GetOwnerRevisionAsync(_episode, "Edition", "Primary"));
        Assert.Null(await repository.GetOwnerRevisionAsync(_movie, "Movie", "Logo"));
        var invalid = await repository.CommitVerifiedAsync(new VerifiedPreferredArtworkAssignment(
            Guid.NewGuid().ToString("D"), _episode, "Edition", "Primary", _artwork,
            "new-art", "v1:fake", [new(_tvAsset, _tvLibrary)]));
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict, invalid.Outcome);
        AssertNoReceipt();
    }

    private async Task<VerifiedPreferredArtworkAssignment> ReviewedAsync(Guid owner,
        string scope, string role, IReadOnlyList<VerifiedArtworkAssetLibrary> affected)
    {
        var revision = await new MediaEditorPreferredArtworkRepository(_database)
            .GetOwnerRevisionAsync(owner, scope, role);
        Assert.NotNull(revision);
        return new(Guid.NewGuid().ToString("D"), owner, scope, role, _artwork,
            "new-art", revision, affected);
    }

    private void AssertNoReceipt()
    {
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_preferred_artwork_commits;"));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}
