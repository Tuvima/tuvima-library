using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class PersonCreditReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public PersonCreditReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_person_credits_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_CollapsesTvEpisodesToSeriesAndPreservesEveryRole()
    {
        var personId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var showWorkId = Guid.NewGuid();
        var showCoverId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO collections (id, display_name, collection_type, wikidata_qid, created_at)
                    VALUES ($collectionId, 'Sample Crime Show', 'Series', 'QSHOW', $now);

                INSERT INTO works (id, collection_id, media_type, wikidata_qid, work_kind)
                    VALUES ($showWorkId, $collectionId, 'TV', 'QSHOW', 'parent');
                INSERT INTO entity_assets (
                    id, entity_id, entity_type, asset_type, image_url,
                    asset_class, storage_location, owner_scope, is_preferred, created_at
                ) VALUES (
                    $showCoverId, $showWorkId, 'Work', 'CoverArt', '/show-cover.jpg',
                    'Artwork', 'Central', 'Work', 1, $now
                );

                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Bryan Test Person', $now);
                INSERT INTO person_roles (person_id, role)
                    VALUES ($personId, 'Director');

                INSERT INTO fictional_entities (
                    id, wikidata_qid, label, entity_sub_type, fictional_universe_qid,
                    fictional_universe_label, created_at
                )
                    VALUES ($characterId, 'QCHAR', 'Lead Chemistry Teacher', 'Character', 'QSHOW', 'Sample Crime Show', $now);
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($personId, $characterId, 'QSHOW');
                """;
            AddGuid(cmd, "$collectionId", collectionId);
            AddGuid(cmd, "$showWorkId", showWorkId);
            AddGuid(cmd, "$showCoverId", showCoverId);
            AddGuid(cmd, "$personId", personId);
            AddGuid(cmd, "$characterId", characterId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();

            for (var episode = 1; episode <= 3; episode++)
            {
                var workId = Guid.NewGuid();
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();

                using var episodeCmd = conn.CreateCommand();
                episodeCmd.CommandText = """
                    INSERT INTO works (id, parent_work_id, collection_id, media_type, wikidata_qid, work_kind, ordinal)
                        VALUES ($workId, $showWorkId, $collectionId, 'TV', $workQid, 'child', $episode);
                    INSERT INTO editions (id, work_id)
                        VALUES ($editionId, $workId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                        VALUES ($assetId, $editionId, $hash, $path);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES ($workId, 'title', $title, $now);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES ($workId, 'year', '2008', $now);
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                        VALUES ($workId, 'director', 0, 'Bryan Test Person');
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                        VALUES ($workId, 'cast_member', 0, 'Bryan Test Person');
                    """;
                AddGuid(episodeCmd, "$workId", workId);
                AddGuid(episodeCmd, "$showWorkId", showWorkId);
                AddGuid(episodeCmd, "$collectionId", collectionId);
                episodeCmd.Parameters.AddWithValue("$workQid", $"QEP{episode}");
                episodeCmd.Parameters.AddWithValue("$episode", episode);
                AddGuid(episodeCmd, "$editionId", editionId);
                AddGuid(episodeCmd, "$assetId", assetId);
                episodeCmd.Parameters.AddWithValue("$hash", $"sample-crime-show-{episode}");
                episodeCmd.Parameters.AddWithValue("$path", $"C:/library/Sample Crime Show/S01E0{episode}.mkv");
                episodeCmd.Parameters.AddWithValue("$title", $"Episode {episode}");
                AddGuid(episodeCmd, "$personId", personId);
                episodeCmd.Parameters.AddWithValue("$now", now);
                episodeCmd.ExecuteNonQuery();
            }
        }

        var service = CreateService();
        var credits = await service.GetLibraryCreditsAsync(personId, CancellationToken.None);

        Assert.Equal(2, credits.Count);
        Assert.All(credits, credit =>
        {
            Assert.Equal(collectionId, credit.CollectionId);
            Assert.Equal("Sample Crime Show", credit.Title);
            Assert.Equal($"/stream/artwork/{showCoverId:D}", credit.CoverUrl);
        });
        var actorCredit = Assert.Single(credits, credit => credit.Role == "Actor");
        Assert.Single(actorCredit.Characters);
        Assert.Equal("Lead Chemistry Teacher", actorCredit.Characters[0].CharacterName);
        Assert.Single(credits, credit => credit.Role == "Director");
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_PreservesMultipleRolesForOneOwnedWork()
    {
        var personId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Test Musician', $now);
                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($workId, 'Music', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'multi-role-album', 'C:/library/Multi Role Album.flac');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($workId, 'title', 'Multi Role Album', $now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'artist', 0, 'Test Musician');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'composer', 0, 'Test Musician');
                """;
            AddGuid(cmd, "$personId", personId);
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var service = CreateService();
        var credits = await service.GetLibraryCreditsAsync(personId, CancellationToken.None);

        Assert.Equal(["Artist", "Composer"], credits.Select(credit => credit.Role).Order());
        Assert.Single(credits.Select(credit => credit.WorkId).Distinct());
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_RejectsContributorRolesThatDoNotBelongToTheMediaType()
    {
        var personId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var audiobookEditionId = Guid.NewGuid();
        var audiobookAssetId = Guid.NewGuid();
        var musicWorkId = Guid.NewGuid();
        var musicEditionId = Guid.NewGuid();
        var musicAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Cross Media Contributor', $now);

                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($audiobookWorkId, 'Audiobooks', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($audiobookEditionId, $audiobookWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($audiobookAssetId, $audiobookEditionId, 'role-filter-audiobook', 'C:/library/Novel.m4b');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($audiobookWorkId, 'title', 'Spoken Novel', $now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($audiobookWorkId, 'author', 0, 'Cross Media Contributor');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($audiobookWorkId, 'artist', 0, 'Cross Media Contributor');

                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($musicWorkId, 'Music', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($musicEditionId, $musicWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($musicAssetId, $musicEditionId, 'role-filter-music', 'C:/library/Album.flac');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($musicWorkId, 'title', 'Studio Album', $now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($musicWorkId, 'artist', 0, 'Cross Media Contributor');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($musicWorkId, 'author', 0, 'Cross Media Contributor');
                """;
            AddGuid(cmd, "$personId", personId);
            AddGuid(cmd, "$audiobookWorkId", audiobookWorkId);
            AddGuid(cmd, "$audiobookEditionId", audiobookEditionId);
            AddGuid(cmd, "$audiobookAssetId", audiobookAssetId);
            AddGuid(cmd, "$musicWorkId", musicWorkId);
            AddGuid(cmd, "$musicEditionId", musicEditionId);
            AddGuid(cmd, "$musicAssetId", musicAssetId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var credits = await CreateService().GetLibraryCreditsAsync(personId, CancellationToken.None);

        Assert.Equal(2, credits.Count);
        Assert.Equal("Author", Assert.Single(credits, credit => credit.MediaType == "Audiobooks").Role);
        Assert.Equal("Artist", Assert.Single(credits, credit => credit.MediaType == "Music").Role);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_CollapsesMusicTracksToOneAlbumWithoutTrackAttribution()
    {
        var personId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var setup = conn.CreateCommand();
            setup.CommandText = """
                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Album Artist', $now);
                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($albumId, 'Music', 'parent');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($albumId, 'title', 'The Complete Album', $now);
                """;
            AddGuid(setup, "$personId", personId);
            AddGuid(setup, "$albumId", albumId);
            setup.Parameters.AddWithValue("$now", now);
            setup.ExecuteNonQuery();

            for (var track = 1; track <= 2; track++)
            {
                var trackId = Guid.NewGuid();
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                using var trackCommand = conn.CreateCommand();
                trackCommand.CommandText = """
                    INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal)
                        VALUES ($trackId, $albumId, 'Music', 'child', $track);
                    INSERT INTO editions (id, work_id)
                        VALUES ($editionId, $trackId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                        VALUES ($assetId, $editionId, $hash, $path);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES ($trackId, 'title', $title, $now);
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                        VALUES ($trackId, 'artist', 0, 'Album Artist');
                    """;
                AddGuid(trackCommand, "$trackId", trackId);
                AddGuid(trackCommand, "$albumId", albumId);
                trackCommand.Parameters.AddWithValue("$track", track);
                AddGuid(trackCommand, "$editionId", editionId);
                AddGuid(trackCommand, "$assetId", assetId);
                trackCommand.Parameters.AddWithValue("$hash", $"album-track-{track}");
                trackCommand.Parameters.AddWithValue("$path", $"C:/library/The Complete Album/{track:00}.flac");
                trackCommand.Parameters.AddWithValue("$title", $"Track {track}");
                trackCommand.Parameters.AddWithValue("$now", now);
                trackCommand.ExecuteNonQuery();
            }
        }

        var service = CreateService();
        var credit = Assert.Single(await service.GetLibraryCreditsAsync(personId, CancellationToken.None));

        Assert.Equal(albumId, credit.WorkId);
        Assert.Equal("The Complete Album", credit.Title);
        Assert.Equal("Artist", credit.Role);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_IncludesDateScopedWorksThroughAGroupWithoutInventingDirectCredit()
    {
        var groupId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var includedAlbumId = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO persons (id, name, is_group, created_at)
            VALUES (@GroupId, 'Window Test Band', 1, @Now),
                   (@MemberId, 'Window Test Member', 0, @Now);
            INSERT INTO person_group_members (group_id, member_id, start_date, end_date)
            VALUES (@GroupId, @MemberId, '1980', '1989');
            """,
            new { GroupId = groupId, MemberId = memberId, Now = now });

        async Task AddAlbumAsync(Guid albumId, int year, string title)
        {
            var trackId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@AlbumId, 'Music', 'parent');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal)
                VALUES (@TrackId, @AlbumId, 'Music', 'child', 1);
                INSERT INTO editions (id, work_id) VALUES (@EditionId, @TrackId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@AssetId, @EditionId, @Hash, @Path);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@AlbumId, 'title', @Title, @Now),
                       (@TrackId, 'release_year', @Year, @Now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@TrackId, 'artist', 0, 'Window Test Band');
                """,
                new
                {
                    AlbumId = albumId,
                    TrackId = trackId,
                    EditionId = editionId,
                    AssetId = assetId,
                    Hash = $"window-{year}",
                    Path = $"C:/library/Window Test Band/{title}/01.flac",
                    Title = title,
                    Year = year.ToString(),
                    Now = now,
                });
        }

        await AddAlbumAsync(includedAlbumId, 1985, "Inside the Window");
        await AddAlbumAsync(excludedAlbumId, 1995, "Outside the Window");

        var credits = await CreateService().GetLibraryCreditsAsync(memberId, CancellationToken.None);

        var credit = Assert.Single(credits);
        Assert.Equal(includedAlbumId, credit.WorkId);
        Assert.Equal("ThroughGroup", credit.AssociationType);
        Assert.Equal(groupId, credit.ViaGroupId);
        Assert.Equal("Window Test Band", credit.ViaGroupName);
        Assert.Equal("With Window Test Band", credit.Role);
        Assert.False(credit.AssociationIsInferred);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_DoesNotAttributeCollectivePseudonymWorkToLinkedIdentities()
    {
        var pseudonymId = Guid.NewGuid();
        var linkedIdentityId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, is_pseudonym, created_at)
                VALUES (@pseudonymId, 'James S. A. Corey', 1, CURRENT_TIMESTAMP),
                       (@linkedIdentityId, 'Daniel Abraham', 0, CURRENT_TIMESTAMP);
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@workId, 'Books', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @hash, 'C:/library/Leviathan Wakes.epub');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', 'Leviathan Wakes', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@assetId, 'author', 0, 'James S. A. Corey'),
                       (@assetId, 'author', 1, 'Daniel Abraham');
                """,
                new
                {
                    pseudonymId,
                    linkedIdentityId,
                    workId,
                    editionId,
                    assetId,
                    hash = Guid.NewGuid().ToString("N"),
                });
        }

        var service = CreateService();
        Assert.Single(await service.GetLibraryCreditsAsync(pseudonymId, CancellationToken.None));
        Assert.Empty(await service.GetLibraryCreditsAsync(linkedIdentityId, CancellationToken.None));
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_UsesOwnedAssetTitleBeforeCollectionTitle()
    {
        var personId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Test Author', $now);
                INSERT INTO collections (id, display_name, collection_type, created_at)
                    VALUES ($collectionId, 'The Parent Series', 'Series', $now);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                    VALUES ($workId, $collectionId, 'Books', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'asset-title-fallback', 'C:/library/First Novel.epub');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($assetId, 'title', 'First Novel', $now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($workId, 'author', 0, 'Test Author');
                """;
            AddGuid(cmd, "$personId", personId);
            AddGuid(cmd, "$collectionId", collectionId);
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var service = CreateService();
        var credit = Assert.Single(await service.GetLibraryCreditsAsync(personId, CancellationToken.None));

        Assert.Equal("First Novel", credit.Title);
        Assert.NotEqual("The Parent Series", credit.Title);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_UsesAudiobookTitleInsteadOfSeriesRootTitle()
    {
        var personId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var seriesRootId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'James S. A. Corey', $now);
                INSERT INTO collections (id, display_name, collection_type, created_at)
                    VALUES ($collectionId, 'The Expanse', 'Series', $now);
                INSERT INTO works (id, collection_id, media_type, work_kind)
                    VALUES ($seriesRootId, $collectionId, 'Audiobooks', 'parent');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($seriesRootId, 'title', 'The Expanse', $now);
                INSERT INTO works (id, parent_work_id, collection_id, media_type, work_kind)
                    VALUES ($audiobookWorkId, $seriesRootId, $collectionId, 'Audiobooks', 'child');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $audiobookWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'leviathan-wakes-audiobook', 'C:/library/Leviathan Wakes.m4b');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($assetId, 'title', 'Leviathan Wakes', $now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                    VALUES ($audiobookWorkId, 'author', 0, 'James S. A. Corey');
                """;
            AddGuid(cmd, "$personId", personId);
            AddGuid(cmd, "$collectionId", collectionId);
            AddGuid(cmd, "$seriesRootId", seriesRootId);
            AddGuid(cmd, "$audiobookWorkId", audiobookWorkId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var credit = Assert.Single(
            await CreateService().GetLibraryCreditsAsync(personId, CancellationToken.None));

        Assert.Equal("Leviathan Wakes", credit.Title);
        Assert.NotEqual("The Expanse", credit.Title);
        Assert.Equal("Author", credit.Role);
    }

    [Fact]
    public async Task BuildForWorkAsync_UsesTmdbCharacterClaimsWhenExplicitCharacterLinksAreMissing()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var providerId = WellKnownProviders.Tmdb;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                    VALUES ($providerId, 'tmdb', '1.0', 1);

                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($workId, 'Movies', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'arrival-test-hash', 'C:/library/Arrival.mkv');

                INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Jeremy Renner', $now);
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                    VALUES ($assetId, $personId, 'Actor');

                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim1, $workId, $providerId, 'cast_member', 'Amy Adams', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim2, $workId, $providerId, 'cast_member_character', 'Dr. Louise Banks', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim3, $workId, $providerId, 'cast_member', 'Jeremy Renner', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim4, $workId, $providerId, 'cast_member_character', 'Ian Donnelly', 0.90, $now);
                """;
            AddGuid(cmd, "$providerId", providerId);
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            AddGuid(cmd, "$personId", personId);
            cmd.Parameters.AddWithValue("$now", now);
            AddGuid(cmd, "$claim1", Guid.NewGuid());
            AddGuid(cmd, "$claim2", Guid.NewGuid());
            AddGuid(cmd, "$claim3", Guid.NewGuid());
            AddGuid(cmd, "$claim4", Guid.NewGuid());
            cmd.ExecuteNonQuery();
        }

        var service = CreateService();
        var credits = await service.BuildForWorkAsync(workId, CancellationToken.None);

        var jeremy = Assert.Single(credits, credit => credit.Name == "Jeremy Renner");
        Assert.Equal(personId, jeremy.PersonId);
        Assert.Equal("Ian Donnelly", Assert.Single(jeremy.Characters).CharacterName);
    }

    [Fact]
    public async Task BuildForWorkAsync_RejectsUncorroboratedPositionalCharacterLinksAndPrefersCanonicalIdentity()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var katieId = Guid.NewGuid();
        var liamId = Guid.NewGuid();
        var rachelId = Guid.NewGuid();
        var alfredId = Guid.NewGuid();
        var henriId = Guid.NewGuid();
        var rasId = Guid.NewGuid();
        var scarecrowId = Guid.NewGuid();
        var providerId = WellKnownProviders.Tmdb;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                    VALUES ($providerId, 'tmdb', '1.0', 1);
                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($workId, 'Movies', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'batman-role-regression', 'C:/library/Batman Begins.mkv');
                INSERT INTO identity_jobs (
                    id, entity_id, entity_type, media_type, state, pass, resolved_qid, created_at, updated_at)
                    VALUES ($jobId, $assetId, 'MediaAsset', 'Movies', 'Ready', 'Quick', 'Q166262', $now, $now);

                INSERT INTO persons (id, name, wikidata_qid, created_at)
                    VALUES ($katieId, 'Katie Holmes', 'Q174346', $now);
                INSERT INTO persons (id, name, wikidata_qid, created_at)
                    VALUES ($liamId, 'Liam Neeson', 'Q58444', $now);

                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($rachelId, 'Q2567497', 'Rachel Dawes', 'Character', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($alfredId, 'Q159051', 'Alfred Pennyworth', 'Character', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($henriId, 'Q3131007', 'Henri Ducard', 'Character', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($rasId, 'Q158931', 'Ra''s al Ghul', 'Character', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($scarecrowId, 'Q295375', 'Scarecrow', 'Character', $now);

                -- The first links are qualified Wikidata actor/character pairs.
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($katieId, $rachelId, 'Q166262');
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($liamId, $henriId, 'Q166262');
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($liamId, $rasId, 'Q166262');

                -- These later links reproduce the old incorrect ordinal zip.
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($katieId, $alfredId, 'Q166262');
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($liamId, $scarecrowId, 'Q166262');

                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($workId, 'cast_member', 0, 'Katie Holmes', 'Q174346');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($workId, 'cast_member', 1, 'Liam Neeson', 'Q58444');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($workId, 'characters', 0, 'Alfred Pennyworth', 'Q159051');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($workId, 'characters', 1, 'Scarecrow', 'Q295375');

                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim1, $workId, $providerId, 'cast_member', 'Katie Holmes', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim2, $workId, $providerId, 'cast_member_character', 'Rachel Dawes', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim3, $workId, $providerId, 'cast_member', 'Liam Neeson', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim4, $workId, $providerId, 'cast_member_character', 'Ducard', 0.90, $now);
                """;
            AddGuid(cmd, "$providerId", providerId);
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            AddGuid(cmd, "$jobId", jobId);
            AddGuid(cmd, "$katieId", katieId);
            AddGuid(cmd, "$liamId", liamId);
            AddGuid(cmd, "$rachelId", rachelId);
            AddGuid(cmd, "$alfredId", alfredId);
            AddGuid(cmd, "$henriId", henriId);
            AddGuid(cmd, "$rasId", rasId);
            AddGuid(cmd, "$scarecrowId", scarecrowId);
            AddGuid(cmd, "$claim1", Guid.NewGuid());
            AddGuid(cmd, "$claim2", Guid.NewGuid());
            AddGuid(cmd, "$claim3", Guid.NewGuid());
            AddGuid(cmd, "$claim4", Guid.NewGuid());
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var credits = await CreateService().BuildForWorkAsync(workId, CancellationToken.None);

        var katie = Assert.Single(credits, credit => credit.Name == "Katie Holmes");
        Assert.Equal("Rachel Dawes", katie.CharacterName);
        Assert.DoesNotContain(katie.Characters, character => character.CharacterName == "Alfred Pennyworth");

        var liam = Assert.Single(credits, credit => credit.Name == "Liam Neeson");
        Assert.Equal("Ra's al Ghul", liam.CharacterName);
        Assert.DoesNotContain(liam.Characters, character => character.CharacterName == "Scarecrow");

        var katieLibraryCredit = Assert.Single(
            await CreateService().GetLibraryCreditsAsync(katieId, CancellationToken.None),
            credit => credit.Role == "Actor");
        Assert.Contains(katieLibraryCredit.Characters, character => character.CharacterName == "Rachel Dawes");
        Assert.DoesNotContain(katieLibraryCredit.Characters, character => character.CharacterName == "Alfred Pennyworth");
    }

    [Fact]
    public async Task BuildForWorkAsync_UsesRootEvidenceWhenChildAndRootShareTheSameIdentity()
    {
        var rootWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var bryanId = Guid.NewGuid();
        var walterId = Guid.NewGuid();
        var saulId = Guid.NewGuid();
        var providerId = WellKnownProviders.Tmdb;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                    VALUES ($providerId, 'tmdb', '1.0', 1);
                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($rootWorkId, 'TV', 'parent');
                INSERT INTO works (id, parent_work_id, media_type, work_kind)
                    VALUES ($episodeWorkId, $rootWorkId, 'TV', 'child');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $episodeWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'breaking-bad-role-regression', 'C:/library/Breaking Bad.mkv');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($assetId, 'wikidata_qid', 'Q1079', $now);

                INSERT INTO persons (id, name, wikidata_qid, created_at)
                    VALUES ($bryanId, 'Bryan Cranston', 'Q23547', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($walterId, 'Q23554', 'Walter White', 'Character', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($saulId, 'Q7154377', 'Saul Goodman', 'Character', $now);

                -- Qualified mapping followed by the historical bad positional link.
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($bryanId, $walterId, 'Q1079');
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                    VALUES ($bryanId, $saulId, 'Q1079');

                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($rootWorkId, 'cast_member', 0, 'Bryan Cranston', 'Q23547');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($rootWorkId, 'characters', 0, 'Saul Goodman', 'Q7154377');

                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim1, $rootWorkId, $providerId, 'cast_member', 'Bryan Cranston', 0.90, $now);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES ($claim2, $rootWorkId, $providerId, 'cast_member_character', 'Walter White', 0.90, $now);
                """;
            AddGuid(cmd, "$providerId", providerId);
            AddGuid(cmd, "$rootWorkId", rootWorkId);
            AddGuid(cmd, "$episodeWorkId", episodeWorkId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            AddGuid(cmd, "$bryanId", bryanId);
            AddGuid(cmd, "$walterId", walterId);
            AddGuid(cmd, "$saulId", saulId);
            AddGuid(cmd, "$claim1", Guid.NewGuid());
            AddGuid(cmd, "$claim2", Guid.NewGuid());
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var credits = await CreateService().BuildForWorkAsync(episodeWorkId, CancellationToken.None);

        var bryan = Assert.Single(credits, credit => credit.Name == "Bryan Cranston");
        Assert.Equal("Walter White", bryan.CharacterName);
        Assert.DoesNotContain(bryan.Characters, character => character.CharacterName == "Saul Goodman");
    }

    [Fact]
    public async Task BuildForWorkAsync_RemovesABroadcastFirstCharacterAndUsesActorSpecificClaims()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var providerId = WellKnownProviders.Tmdb;
        var timId = Guid.NewGuid();
        var morganId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var andyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                    VALUES ($providerId, 'tmdb', '1.0', 1);
                INSERT INTO works (id, media_type, work_kind)
                    VALUES ($workId, 'Movies', 'standalone');
                INSERT INTO editions (id, work_id)
                    VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                    VALUES ($assetId, $editionId, 'shawshank-role-regression', 'C:/library/The Shawshank Redemption.mkv');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($assetId, 'wikidata_qid', 'Q172241', $now);

                INSERT INTO persons (id, name, wikidata_qid, created_at)
                VALUES
                    ($timId, 'Tim Robbins', 'Q95048', $now),
                    ($morganId, 'Morgan Freeman', 'Q48337', $now),
                    ($bobId, 'Bob Gunton', 'Q352203', $now);
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at)
                    VALUES ($andyId, 'Q56240620', 'Andy Dufresne', 'Character', $now);

                -- Historical ingestion incorrectly broadcast this one structural
                -- character to every actor in the film.
                INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
                VALUES
                    ($timId, $andyId, 'Q172241'),
                    ($morganId, $andyId, 'Q172241'),
                    ($bobId, $andyId, 'Q172241');

                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                VALUES
                    ($workId, 'cast_member', 0, 'Tim Robbins', 'Q95048'),
                    ($workId, 'cast_member', 1, 'Morgan Freeman', 'Q48337'),
                    ($workId, 'cast_member', 2, 'Bob Gunton', 'Q352203'),
                    ($workId, 'characters', 0, 'Andy Dufresne', 'Q56240620');

                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES
                    ($claim1, $workId, $providerId, 'cast_member', 'Tim Robbins', 0.90, $now),
                    ($claim2, $workId, $providerId, 'cast_member_character', 'Andy Dufresne', 0.90, $now),
                    ($claim3, $workId, $providerId, 'cast_member', 'Morgan Freeman', 0.90, $now),
                    ($claim4, $workId, $providerId, 'cast_member_character', 'Ellis Boyd Red Redding', 0.90, $now),
                    ($claim5, $workId, $providerId, 'cast_member', 'Bob Gunton', 0.90, $now),
                    ($claim6, $workId, $providerId, 'cast_member_character', 'Warden Norton', 0.90, $now);
                """;
            AddGuid(cmd, "$providerId", providerId);
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            AddGuid(cmd, "$timId", timId);
            AddGuid(cmd, "$morganId", morganId);
            AddGuid(cmd, "$bobId", bobId);
            AddGuid(cmd, "$andyId", andyId);
            for (var i = 1; i <= 6; i++)
            {
                AddGuid(cmd, $"$claim{i}", Guid.NewGuid());
            }

            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var credits = await CreateService().BuildForWorkAsync(workId, CancellationToken.None);

        Assert.Equal("Andy Dufresne", Assert.Single(credits, credit => credit.Name == "Tim Robbins").CharacterName);
        Assert.Equal("Ellis Boyd Red Redding", Assert.Single(credits, credit => credit.Name == "Morgan Freeman").CharacterName);
        Assert.Equal("Warden Norton", Assert.Single(credits, credit => credit.Name == "Bob Gunton").CharacterName);
    }

    [Fact]
    public async Task BuildForWorkAsync_DoesNotShiftCharactersPastAnActorWithNoCharacterClaim()
    {
        var workId = Guid.NewGuid();
        var providerId = WellKnownProviders.Tmdb;
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
                VALUES (@ProviderId, 'tmdb', '1.0', 1);
                INSERT INTO works (id, media_type, work_kind)
                VALUES (@WorkId, 'Movies', 'standalone');
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES
                    (@Claim1, @WorkId, @ProviderId, 'cast_member', 'First Actor', 0.90, @Now),
                    (@Claim2, @WorkId, @ProviderId, 'cast_member_character', 'First Role', 0.90, @Now),
                    (@Claim3, @WorkId, @ProviderId, 'cast_member', 'Uncredited Role Actor', 0.90, @Now),
                    (@Claim4, @WorkId, @ProviderId, 'cast_member', 'Third Actor', 0.90, @Now),
                    (@Claim5, @WorkId, @ProviderId, 'cast_member_character', 'Third Role', 0.90, @Now);
                """,
                new
                {
                    ProviderId = providerId,
                    WorkId = workId,
                    Claim1 = Guid.NewGuid(),
                    Claim2 = Guid.NewGuid(),
                    Claim3 = Guid.NewGuid(),
                    Claim4 = Guid.NewGuid(),
                    Claim5 = Guid.NewGuid(),
                    Now = now,
                });
        }

        var credits = await CreateService().BuildForWorkAsync(workId, CancellationToken.None);

        Assert.Equal("First Role", Assert.Single(credits, credit => credit.Name == "First Actor").CharacterName);
        Assert.Empty(Assert.Single(credits, credit => credit.Name == "Uncredited Role Actor").Characters);
        Assert.Equal("Third Role", Assert.Single(credits, credit => credit.Name == "Third Actor").CharacterName);
    }

    [Fact]
    public async Task GetGroupMembersAsync_ReturnsMembersAndParentGroupsInNameOrder()
    {
        var groupId = Guid.NewGuid();
        var firstMemberId = Guid.NewGuid();
        var secondMemberId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO persons (id, name, is_group, created_at)
                    VALUES ($groupId, 'The Test Group', 1, $now);
                INSERT INTO persons (id, name, created_at)
                    VALUES ($firstMemberId, 'Zed Member', $now);
                INSERT INTO persons (id, name, created_at)
                    VALUES ($secondMemberId, 'Amy Member', $now);
                UPDATE persons
                    SET headshot_url = 'https://images.example.test/zed.jpg'
                    WHERE id = $firstMemberId;
                UPDATE persons
                    SET headshot_url = 'https://images.example.test/amy.jpg'
                    WHERE id = $secondMemberId;
                INSERT INTO person_group_members (group_id, member_id, start_date, end_date)
                    VALUES ($groupId, $firstMemberId, '1988', '1990');
                INSERT INTO person_group_members (group_id, member_id)
                    VALUES ($groupId, $secondMemberId);
                """;
            AddGuid(cmd, "$groupId", groupId);
            AddGuid(cmd, "$firstMemberId", firstMemberId);
            AddGuid(cmd, "$secondMemberId", secondMemberId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var service = CreateService();

        var members = await service.GetGroupMembersAsync(groupId, isGroup: true, MediaEngine.Application.Services.ViewerCatalogue.Nothing, CancellationToken.None);
        var groups = await service.GetGroupMembersAsync(firstMemberId, isGroup: false, MediaEngine.Application.Services.ViewerCatalogue.Nothing, CancellationToken.None);

        Assert.Equal(["Amy Member", "Zed Member"], members.Select(member => member.Name));
        Assert.All(members, member => Assert.Contains($"/persons/{member.Id:D}/headshot", member.HeadshotUrl));
        Assert.Equal("1988 - 1990", Assert.Single(members, member => member.Id == firstMemberId).DateRange);
        Assert.Equal("The Test Group", Assert.Single(groups).Name);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_ReturnsEmptyListWhenPersonHasNoCredits()
    {
        var service = CreateService();

        var credits = await service.GetLibraryCreditsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(credits);
    }

    [Fact]
    public async Task GetLibraryCreditsAsync_UsesOriginalWorkDatesAcrossMediaTypes()
    {
        var personId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var cases = new[]
        {
            new DateCreditCase(
                "The Hobbit",
                "Books",
                "author",
                "1937",
                new Dictionary<string, string>
                {
                    ["date"] = "1937-09-21",
                    ["release_year"] = "1938",
                    ["year"] = "2012",
                }),
            new DateCreditCase(
                "The Hobbit",
                "Audiobooks",
                "author",
                "1937",
                new Dictionary<string, string>
                {
                    ["release_year"] = "2000",
                    ["year"] = "2007",
                }),
            new DateCreditCase(
                "Interstellar",
                "Movies",
                "cast_member",
                "2014",
                new Dictionary<string, string>
                {
                    ["year"] = "2014",
                    ["release_year"] = "2025",
                }),
            new DateCreditCase(
                "Sample TV Show",
                "TV",
                "director",
                "2008",
                new Dictionary<string, string>
                {
                    ["first_air_date"] = "2008-01-20",
                    ["year"] = "2013",
                }),
            new DateCreditCase(
                "Akira",
                "Comics",
                "author",
                "1984",
                new Dictionary<string, string>
                {
                    ["year"] = "1984",
                    ["release_year"] = "1988",
                }),
            new DateCreditCase(
                "Hunky Dory",
                "Music",
                "artist",
                "1971",
                new Dictionary<string, string>
                {
                    ["original_release_date"] = "1971-12-17",
                    ["year"] = "2019",
                }),
        };

        using (var conn = _db.CreateConnection())
        {
            using (var personCommand = conn.CreateCommand())
            {
                personCommand.CommandText = """
                    INSERT INTO persons (id, name, created_at)
                    VALUES ($personId, 'Cross-Media Person', $now);
                    """;
                AddGuid(personCommand, "$personId", personId);
                personCommand.Parameters.AddWithValue("$now", now);
                personCommand.ExecuteNonQuery();
            }

            foreach (var item in cases)
            {
                var workId = Guid.NewGuid();
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();

                using (var itemCommand = conn.CreateCommand())
                {
                    itemCommand.CommandText = """
                        INSERT INTO works (id, media_type, work_kind)
                        VALUES ($workId, $mediaType, 'standalone');
                        INSERT INTO editions (id, work_id)
                        VALUES ($editionId, $workId);
                        INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                        VALUES ($assetId, $editionId, $hash, $path);
                        INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES ($assetId, 'title', $title, $now);
                        INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                        VALUES ($assetId, $creditKey, 0, 'Cross-Media Person');
                        """;
                    AddGuid(itemCommand, "$workId", workId);
                    AddGuid(itemCommand, "$editionId", editionId);
                    AddGuid(itemCommand, "$assetId", assetId);
                    itemCommand.Parameters.AddWithValue("$mediaType", item.MediaType);
                    itemCommand.Parameters.AddWithValue("$hash", Guid.NewGuid().ToString("N"));
                    itemCommand.Parameters.AddWithValue("$path", $"C:/library/{item.Title}.media");
                    itemCommand.Parameters.AddWithValue("$title", item.Title);
                    itemCommand.Parameters.AddWithValue("$creditKey", item.CreditKey);
                    itemCommand.Parameters.AddWithValue("$now", now);
                    itemCommand.ExecuteNonQuery();
                }

                foreach (var (key, value) in item.Dates)
                {
                    using var dateCommand = conn.CreateCommand();
                    dateCommand.CommandText = """
                        INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES ($assetId, $key, $value, $now);
                        """;
                    AddGuid(dateCommand, "$assetId", assetId);
                    dateCommand.Parameters.AddWithValue("$key", key);
                    dateCommand.Parameters.AddWithValue("$value", value);
                    dateCommand.Parameters.AddWithValue("$now", now);
                    dateCommand.ExecuteNonQuery();
                }
            }
        }

        var credits = await CreateService().GetLibraryCreditsAsync(personId, CancellationToken.None);

        Assert.Equal(cases.Length, credits.Count);
        foreach (var expected in cases)
        {
            var credit = Assert.Single(
                credits,
                value => value.Title == expected.Title && value.MediaType == expected.MediaType);
            Assert.Equal(expected.ExpectedYear, credit.Year);
        }
    }

    private PersonCreditReadService CreateService() =>
        new(new CanonicalValueArrayRepository(_db), new PersonRepository(_db), _db);

    private static void AddGuid(Microsoft.Data.Sqlite.SqliteCommand command, string name, Guid value)
    {
        command.Parameters.AddWithValue(name, GuidSql.ToBlob(value));
    }

    private sealed record DateCreditCase(
        string Title,
        string MediaType,
        string CreditKey,
        string ExpectedYear,
        IReadOnlyDictionary<string, string> Dates);
}
