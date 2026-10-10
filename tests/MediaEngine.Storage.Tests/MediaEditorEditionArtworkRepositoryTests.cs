using Dapper;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class MediaEditorEditionArtworkRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_edition_art_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _firstEdition = Guid.NewGuid();
    private readonly Guid _secondEdition = Guid.NewGuid();
    private readonly Guid _firstAsset = Guid.NewGuid();
    private readonly Guid _secondAsset = Guid.NewGuid();
    private readonly Guid _firstLibrary = Guid.NewGuid();
    private readonly Guid _secondLibrary = Guid.NewGuid();
    private readonly Guid _workArtwork = Guid.NewGuid();
    private readonly Guid _editionArtwork = Guid.NewGuid();

    public MediaEditorEditionArtworkRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Theory]
    [InlineData("Books")]
    [InlineData("Audiobooks")]
    [InlineData("Movies")]
    [InlineData("Comics")]
    [InlineData("Music")]
    public async Task ExactEditionCoverOverridesWorkOnlyForItsOwnAndFutureAssets(string mediaType)
    {
        Seed(mediaType);
        var repository = new MediaEditorEditionArtworkRepository(_database);
        var beforeFirst = await repository.GetEffectiveAssetCoverAsync(_firstAsset);
        var beforeSecond = await repository.GetEffectiveAssetCoverAsync(_secondAsset);
        Assert.Equal(_workArtwork, beforeFirst!.Variant!.ArtworkAssetId);
        Assert.Equal("Work", beforeFirst.SourceEntityType);
        Assert.Equal(_workArtwork, beforeSecond!.Variant!.ArtworkAssetId);

        var releaseId = mediaType == "Music" ? Guid.NewGuid().ToString("D") : null;
        if (releaseId is not null)
        {
            using (var connection = _database.CreateConnection())
            {
                connection.Execute("""
                            INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                            VALUES(@edition, 'musicbrainz_release_id', @releaseId, @now);
                            """, new
                {
                    edition = _firstEdition,
                    releaseId,
                    now = DateTimeOffset.UtcNow.ToString("O")
                });
            }
        }
        var assignment = await ReviewedAsync(releaseId);
        Assert.Equal(PreferredArtworkCommitOutcome.Committed,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        Assert.Equal(PreferredArtworkCommitOutcome.Replayed,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);

        var afterFirst = await repository.GetEffectiveAssetCoverAsync(_firstAsset);
        var afterSecond = await repository.GetEffectiveAssetCoverAsync(_secondAsset);
        Assert.Equal(_editionArtwork, afterFirst!.Variant!.ArtworkAssetId);
        Assert.Equal("Edition", afterFirst.SourceEntityType);
        Assert.False(afterFirst.IsInherited);
        Assert.Equal(_workArtwork, afterSecond!.Variant!.ArtworkAssetId);
        Assert.Equal("Work", afterSecond.SourceEntityType);

        var laterAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@laterAsset, @edition, @hash, 'C:/fixture/later.bin', @library);
                    """, new
            {
                laterAsset,
                edition = _firstEdition,
                hash = Guid.NewGuid().ToString("N"),
                library = _firstLibrary.ToString("D")
            });
        }
        var inherited = await repository.GetEffectiveAssetCoverAsync(laterAsset);
        Assert.Equal(_editionArtwork, inherited!.Variant!.ArtworkAssetId);
        Assert.Equal(_firstEdition, inherited.SourceEntityId);

        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_assets
            WHERE entity_id=@edition AND entity_type='Edition' AND asset_type='CoverArt'
              AND is_preferred=1;
            """, new { edition = _firstEdition }));
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_artwork_links
            WHERE entity_id=@work AND entity_type='Work' AND is_preferred=1;
            """, new { work = _work }));
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_edition_artwork_commits;"));
    }

    [Fact]
    public async Task MusicRequiresExactEditionReleaseAndRejectsDisagreement()
    {
        Seed("Music");
        var repository = new MediaEditorEditionArtworkRepository(_database);
        var assignment = await ReviewedAsync(Guid.NewGuid().ToString("D"));
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        var releaseId = Guid.NewGuid().ToString("D");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                    VALUES(@edition, 'musicbrainz_release_id', @releaseId, @now);
                    INSERT INTO bridge_ids(id, entity_id, id_type, id_value)
                    VALUES(@bridge, @edition, 'musicbrainz_release_id', @otherRelease);
                    """, new
            {
                edition = _firstEdition,
                releaseId,
                otherRelease = Guid.NewGuid().ToString("D"),
                bridge = Guid.NewGuid(),
                now = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        assignment = await ReviewedAsync(releaseId);
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    UPDATE bridge_ids SET id_value=@releaseId
                    WHERE entity_id=@edition AND id_type='musicbrainz_release_id';
                    """, new { edition = _firstEdition, releaseId });
        }
        assignment = await ReviewedAsync(releaseId);
        Assert.Equal(PreferredArtworkCommitOutcome.Committed,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
    }

    [Fact]
    public async Task StaleMembershipLibraryPreferenceAndReusedTokenDoNotChangeCover()
    {
        Seed("Books");
        var repository = new MediaEditorEditionArtworkRepository(_database);
        var assignment = await ReviewedAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library WHERE id=@asset;",
                    new { library = Guid.NewGuid().ToString("D"), asset = _firstAsset });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET library_id=@library WHERE id=@asset;",
                    new { library = _firstLibrary.ToString("D"), asset = _firstAsset });
        }
        assignment = await ReviewedAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO entity_artwork_links
                        (id, entity_id, entity_type, artwork_asset_id, role, context,
                         source_asset_type, is_preferred)
                    VALUES(@id, @edition, 'Edition', @art, 'Primary', '', 'CoverArt', 0);
                    """, new { id = Guid.NewGuid(), edition = _firstEdition, art = _workArtwork });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        assignment = await ReviewedAsync();
        Assert.Equal(PreferredArtworkCommitOutcome.Committed,
            (await repository.CommitVerifiedCoverAsync(assignment)).Outcome);
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await repository.CommitVerifiedCoverAsync(assignment with
            { ArtworkAssetId = _workArtwork })).Outcome);
        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.QuerySingle<int>(
            "SELECT COUNT(*) FROM media_editor_edition_artwork_commits;"));
    }

    [Fact]
    public async Task NewFileBeforeCommitMustBeIncludedAndAuthorizedByCaller()
    {
        Seed("Audiobooks");
        var assignment = await ReviewedAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id)
                    VALUES(@asset, @edition, @hash, 'C:/fixture/new-segment.bin', @library);
                    """, new
            {
                asset = Guid.NewGuid(),
                edition = _firstEdition,
                hash = Guid.NewGuid().ToString("N"),
                library = _firstLibrary.ToString("D")
            });
        }
        Assert.Equal(PreferredArtworkCommitOutcome.Conflict,
            (await new MediaEditorEditionArtworkRepository(_database)
                .CommitVerifiedCoverAsync(assignment)).Outcome);
    }

    [Fact]
    public async Task ReviewDerivesExactEditionVariantRevisionAndCompleteImpact()
    {
        Seed("Books");
        var review = await new MediaEditorEditionArtworkRepository(_database)
            .ReviewAssetCoverAsync(_firstAsset, _editionArtwork);

        Assert.NotNull(review);
        Assert.Equal(_firstAsset, review.AssetId);
        Assert.Equal(_firstEdition, review.EditionId);
        Assert.Equal(_work, review.WorkId);
        Assert.Equal("edition-cover", review.VariantContentHash);
        Assert.StartsWith("v1:", review.EditionRevision);
        Assert.Equal([new VerifiedArtworkAssetLibrary(_firstAsset, _firstLibrary)],
            review.AffectedAssetLibraries);
        Assert.Null(review.MusicBrainzReleaseId);
    }

    [Fact]
    public async Task ReviewFailsClosedForUnprovenMusicReleaseAndUnsafeDescendant()
    {
        Seed("Music");
        var repository = new MediaEditorEditionArtworkRepository(_database);
        Assert.Null(await repository.ReviewAssetCoverAsync(_firstAsset, _editionArtwork));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                    VALUES(@edition, 'musicbrainz_release_id', @release, @now);
                    UPDATE media_assets SET status='Orphaned' WHERE id=@asset;
                    """, new
            {
                edition = _firstEdition,
                release = Guid.NewGuid().ToString("D"),
                now = DateTimeOffset.UtcNow.ToString("O"),
                asset = _firstAsset
            });
        }

        Assert.Null(await repository.ReviewAssetCoverAsync(_firstAsset, _editionArtwork));
    }

    [Fact]
    public void StartupMigrationPreservesWorkArtworkAndAllowsEditionOwner()
    {
        var legacyWork = Guid.NewGuid();
        var legacyArt = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works(id, media_type) VALUES(@work, 'Books');",
                new { work = legacyWork });
            connection.Execute("""
                INSERT INTO entity_assets(id, entity_id, entity_type, asset_type,
                    is_preferred, is_user_override)
                VALUES(@art, @work, 'Work', 'CoverArt', 1, 1);
                """, new { art = legacyArt, work = legacyWork });
            var currentDdl = connection.QuerySingle<string>("""
                SELECT sql FROM sqlite_master WHERE type='table' AND name='entity_assets';
                """);
            var oldDdl = currentDdl.Replace("'Work','Edition','Person'",
                "'Work','Person'", StringComparison.Ordinal);
            Assert.NotEqual(currentDdl, oldDdl);
            connection.Execute("ALTER TABLE entity_assets RENAME TO entity_assets_with_edition;");
            connection.Execute(oldDdl);
            connection.Execute("INSERT INTO entity_assets SELECT * FROM entity_assets_with_edition;");
            connection.Execute("DROP TABLE entity_assets_with_edition;");
        }

        _database.RunStartupChecks();
        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM entity_assets WHERE id=@art AND entity_id=@work
              AND entity_type='Work' AND is_preferred=1 AND is_user_override=1;
            """, new { art = legacyArt, work = legacyWork }));
        verify.Execute("""
            INSERT INTO entity_assets(id, entity_id, entity_type, asset_type, is_preferred)
            VALUES(@id, @edition, 'Edition', 'CoverArt', 1);
            """, new { id = Guid.NewGuid(), edition = Guid.NewGuid() });
        Assert.Equal(1, verify.QuerySingle<int>("""
            SELECT COUNT(*) FROM schema_migrations
            WHERE migration_id='007_edition_artwork_owner';
            """));
    }

    private void Seed(string mediaType)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id, media_type, work_kind) VALUES(@work, @mediaType, 'standalone');
            INSERT INTO editions(id, work_id) VALUES(@firstEdition, @work), (@secondEdition, @work);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, library_id) VALUES
                (@firstAsset, @firstEdition, @firstHash, 'C:/fixture/first.bin', @firstLibrary),
                (@secondAsset, @secondEdition, @secondHash, 'C:/fixture/second.bin', @secondLibrary);
            INSERT INTO artwork_assets(id, content_hash, original_path) VALUES
                (@workArtwork, 'work-cover', 'C:/fixture/work.jpg'),
                (@editionArtwork, 'edition-cover', 'C:/fixture/edition.jpg');
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred)
            VALUES(@linkId, @work, 'Work', @workArtwork, 'Primary', '', 'CoverArt', 1);
            """, new
        {
            work = _work,
            mediaType,
            firstEdition = _firstEdition,
            secondEdition = _secondEdition,
            firstAsset = _firstAsset,
            secondAsset = _secondAsset,
            firstHash = Guid.NewGuid().ToString("N"),
            secondHash = Guid.NewGuid().ToString("N"),
            firstLibrary = _firstLibrary.ToString("D"),
            secondLibrary = _secondLibrary.ToString("D"),
            workArtwork = _workArtwork,
            editionArtwork = _editionArtwork,
            linkId = Guid.NewGuid()
        });
    }

    private async Task<VerifiedEditionCoverAssignment> ReviewedAsync(string? releaseId = null)
    {
        var revision = await new MediaEditorEditionArtworkRepository(_database)
            .GetEditionRevisionAsync(_firstEdition);
        Assert.NotNull(revision);
        return new(Guid.NewGuid().ToString("D"), _firstEdition, _work,
            _editionArtwork, "edition-cover", revision,
            [new(_firstAsset, _firstLibrary)], releaseId);
    }

    public void Dispose()
    {
        TestTemp.DeleteDatabase(_path);
    }
}
