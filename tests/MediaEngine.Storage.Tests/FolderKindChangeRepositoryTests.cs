using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The data side of "Change what this folder is" (Library setup spec D40(c), D64, D69) over real temp SQLite and the same chain factory and
/// hierarchy resolver ingestion uses: re-placing a file under a Work of another media type, and dropping the details looked up for a file.
/// </summary>
public sealed class FolderKindChangeRepositoryTests : IDisposable
{
    private const string FilePath = @"C:\Media\Home\clip.mkv";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaEntityChainFactory _factory;
    private readonly FolderKindChangeRepository _repository;

    public FolderKindChangeRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_kindchange_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        var resolver = new HierarchyResolver(new WorkRepository(_db));
        _factory = new MediaEntityChainFactory(_db, resolver);
        _repository = new FolderKindChangeRepository(_db, resolver);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch { /* temp database cleanup is best effort */ }
    }

    [Fact]
    public async Task A_Movies_file_is_placed_under_a_Videos_work_and_the_emptied_movie_work_goes()
    {
        var (assetId, movieWork) = await IngestAsync(MediaType.Movies);

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        Assert.True(result.Replaced);
        using var conn = _db.CreateConnection();
        Assert.Equal("Videos", conn.ExecuteScalar<string>("SELECT media_type FROM works WHERE id = @id;", new { id = result.WorkId }));
        Assert.Equal(result.WorkId, conn.ExecuteScalar<Guid>(
            "SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id;", new { id = assetId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWork }));
        // The file's media type is a current claim and the value readers (and the refile template) use.
        Assert.Equal("Videos", conn.ExecuteScalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'media_type';", new { id = assetId }));
        Assert.Equal(1, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id AND claim_key = 'media_type' AND claim_value = 'Videos' AND is_current = 1;", new { id = assetId }));
    }

    [Fact]
    public async Task What_people_did_with_the_old_title_goes_to_the_new_one_and_watch_progress_stays_with_the_file()
    {
        var (assetId, movieWork) = await IngestAsync(MediaType.Movies);
        var person = Profile.SeedProfileId;
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@person, 'Movie', @movieWork, '2026-10-01T00:00:00Z');
                INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Movie', @movieWork, 'Love', '2026-10-01T00:00:00Z');
                INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@person, @movieWork, '["rewatch"]', 1, '2026-10-01T00:00:00Z');
                INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@person, @assetId, 40, '2026-10-01T00:00:00Z');
                """, new { person, movieWork, assetId });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        using var conn = _db.CreateConnection();
        Assert.Equal(result.WorkId, conn.ExecuteScalar<Guid>("SELECT entity_id FROM profile_reactions WHERE profile_id = @person;", new { person }));
        Assert.Equal("Love", conn.ExecuteScalar<string>("SELECT reaction FROM profile_reactions WHERE profile_id = @person;", new { person }));
        Assert.Equal(result.WorkId, conn.ExecuteScalar<Guid>("SELECT entity_id FROM profile_saved_items WHERE profile_id = @person;", new { person }));
        Assert.Equal("""["rewatch"]""", conn.ExecuteScalar<string>("SELECT local_tags_json FROM profile_work_preferences WHERE work_id = @id;", new { id = result.WorkId }));
        Assert.Equal(40, conn.ExecuteScalar<int>("SELECT progress_pct FROM user_states WHERE user_id = @person AND asset_id = @assetId;", new { person, assetId }));
    }

    [Fact]
    public async Task Details_a_person_set_on_the_old_title_follow_the_file_but_its_old_place_and_media_type_do_not()
    {
        var (assetId, movieWork) = await IngestAsync(MediaType.Movies);
        SeedProviders();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked)
                VALUES (@a, @movieWork, @manual, 'title', 'My own title', 1, '2026-01-01', 1),
                       (@b, @movieWork, @manual, 'season_number', '2', 1, '2026-01-01', 0),
                       (@c, @movieWork, @manual, 'media_type', 'Movies', 1, '2026-01-01', 1),
                       (@d, @movieWork, @tmdb, 'title', 'Looked up title', 0.9, '2026-01-01', 0);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@movieWork, 'title', 'My own title', '2026-01-01');
                """, new { a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), movieWork, manual = WellKnownProviders.UserManual, tmdb = WellKnownProviders.Tmdb });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        using var conn = _db.CreateConnection();
        var onNewWork = conn.Query<(string Key, string Value)>(
            "SELECT claim_key AS Key, claim_value AS Value FROM metadata_claims WHERE entity_id = @id ORDER BY claim_key;", new { id = result.WorkId }).ToList();
        // The title follows; the season number (where it sat in the old list) and the old locked media type do not. The only media_type claim is the new one.
        Assert.Equal([("media_type", "Videos"), ("title", "My own title")], onNewWork);
        Assert.Equal("My own title", conn.ExecuteScalar<string>("SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = result.WorkId }));
        // The looked-up claim went with the old title.
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id;", new { id = movieWork }));
    }

    [Fact]
    public async Task A_work_that_still_has_another_edition_stays_with_its_state_and_only_the_file_moves()
    {
        var (assetId, sharedWork) = await IngestAsync(MediaType.Movies);
        var otherEdition = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO editions (id, work_id) VALUES (@otherEdition, @sharedWork);
                INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Movie', @sharedWork, 'Love', '2026-10-01T00:00:00Z');
                """, new { otherEdition, sharedWork, person = Profile.SeedProfileId });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        Assert.True(result.Replaced);
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = sharedWork }));
        Assert.Equal(sharedWork, conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = @id;", new { id = otherEdition }));
        Assert.Equal(sharedWork, conn.ExecuteScalar<Guid>("SELECT entity_id FROM profile_reactions;"));
    }

    [Fact]
    public async Task A_file_whose_work_already_has_the_media_type_is_left_where_it_is()
    {
        var (assetId, work) = await IngestAsync(MediaType.Music);

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Music, Hints(assetId), FilePath);

        Assert.False(result.Replaced);
        Assert.Equal(work, result.WorkId);
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works;"));
    }

    [Fact]
    public async Task Placing_the_last_episode_elsewhere_removes_the_season_and_show_it_leaves_empty()
    {
        var (assetId, _) = await IngestAsync(MediaType.TV, new Dictionary<string, string>
        {
            ["title"] = "Pilot",
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        });
        using (var conn = _db.CreateConnection())
        {
            Assert.Equal(3, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal([result.WorkId], verify.Query<Guid>("SELECT id FROM works;").ToList());
    }

    [Fact]
    public async Task A_saved_and_hearted_show_whose_last_episode_moves_out_keeps_its_save_its_reaction_and_its_collection_entry_while_the_empty_season_goes()
    {
        var (assetId, episode) = await IngestAsync(MediaType.TV, new Dictionary<string, string>
        {
            ["title"] = "Pilot",
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        });
        var person = Profile.SeedProfileId;
        Guid show;
        Guid season;
        using (var seed = _db.CreateConnection())
        {
            season = seed.ExecuteScalar<Guid>("SELECT parent_work_id FROM works WHERE id = @episode;", new { episode });
            show = seed.ExecuteScalar<Guid>("SELECT parent_work_id FROM works WHERE id = @season;", new { season });
            seed.Execute("""
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@person, 'TvShow', @show, '2026-10-01T00:00:00Z');
                INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'TvShow', @show, 'Love', '2026-10-01T00:00:00Z');
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
                VALUES (@collection, 'Favourites', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@item, @collection, @show, 1);
                """, new { person, show, collection = Guid.NewGuid(), item = Guid.NewGuid() });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        using var conn = _db.CreateConnection();
        Assert.True(result.Replaced);
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id IN (@episode, @season);", new { episode, season }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @show;", new { show }));
        Assert.Equal(show, conn.ExecuteScalar<Guid>("SELECT entity_id FROM profile_saved_items WHERE profile_id = @person;", new { person }));
        Assert.Equal("Love", conn.ExecuteScalar<string>("SELECT reaction FROM profile_reactions WHERE entity_id = @show;", new { show }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM collection_items WHERE work_id = @show;", new { show }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM collections;"));
    }

    [Fact]
    public async Task A_leaf_with_uploaded_artwork_and_typed_details_keeps_both_on_the_freshly_made_work_and_the_uploaded_file_stays()
    {
        var (assetId, old) = await IngestAsync(MediaType.Movies);
        var upload = Path.Combine(Path.GetTempPath(), $"tuvima_kindchange_upload_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(upload, "uploaded picture");
        var artwork = Guid.NewGuid();
        var link = Guid.NewGuid();
        try
        {
            using (var seed = _db.CreateConnection())
            {
                seed.Execute("""
                    INSERT INTO artwork_assets (id, content_hash, original_path, source_provider) VALUES (@artwork, 'uploaded', @upload, 'user_upload');
                    INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_user_override) VALUES (@link, @old, 'Work', @artwork, 'Primary', 1);
                    INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path, source_provider, is_user_override)
                    VALUES (@asset, @old, 'Work', 'CoverArt', @upload, 'user_upload', 1);
                    UPDATE works SET display_overrides_json = '{"genre":"Drama"}' WHERE id = @old;
                    """, new { artwork, link, old, upload, asset = Guid.NewGuid() });
            }

            var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

            Assert.True(result.Replaced);
            Assert.NotEqual(old, result.WorkId);
            using var conn = _db.CreateConnection();
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @old;", new { old }));
            Assert.Equal("""{"genre":"Drama"}""", conn.ExecuteScalar<string>("SELECT display_overrides_json FROM works WHERE id = @id;", new { id = result.WorkId }));
            Assert.Equal(result.WorkId, conn.ExecuteScalar<Guid>("SELECT entity_id FROM entity_artwork_links WHERE id = @link;", new { link }));
            Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM artwork_assets WHERE id = @artwork;", new { artwork }));
            Assert.Equal(result.WorkId, conn.ExecuteScalar<Guid>("SELECT entity_id FROM entity_assets WHERE source_provider = 'user_upload';"));
            Assert.True(File.Exists(upload));
            Assert.Empty(result.OwnedFilesToRemove);
        }
        finally
        {
            File.Delete(upload);
        }
    }

    [Fact]
    public async Task A_leaf_with_a_persons_artwork_joining_a_title_that_has_its_own_is_refused_and_nothing_changes()
    {
        // An episode that already exists and carries typed details, and a movie file that would join it.
        var episodeHints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };
        var (_, episode) = await IngestAsync(MediaType.TV, new Dictionary<string, string>(episodeHints) { ["title"] = "Pilot" });
        var (assetId, movie) = await IngestAsync(MediaType.Movies);
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                UPDATE works SET display_overrides_json = '{"genre":"Comedy"}' WHERE id = @episode;
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override)
                VALUES (@asset, @movie, 'Work', 'CoverArt', 'user_upload', 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@episode, 'title', 'Pilot', '2026-01-01'), (@movie, 'title', 'My movie', '2026-01-01');
                """, new { episode, movie, asset = Guid.NewGuid() });
        }

        var refused = await Assert.ThrowsAsync<UserDetailsClashException>(() =>
            _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.TV, episodeHints, FilePath));

        // Both titles are named, with what to do.
        Assert.Equal(UserDetailsClashException.Describe("My movie", "Pilot"), refused.Message);
        Assert.Contains("\"My movie\" and \"Pilot\" both have your own artwork or details", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Editing Mode", refused.Message, StringComparison.Ordinal);
        using var conn = _db.CreateConnection();
        Assert.Equal(movie, conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = (SELECT edition_id FROM media_assets WHERE id = @assetId);", new { assetId }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets WHERE entity_id = @movie AND is_user_override = 1;", new { movie }));
        Assert.Equal("""{"genre":"Comedy"}""", conn.ExecuteScalar<string>("SELECT display_overrides_json FROM works WHERE id = @episode;", new { episode }));
    }

    [Fact]
    public async Task A_persons_preferred_picture_takes_the_slot_so_the_new_titles_looked_up_preferred_picture_is_let_go()
    {
        var episodeHints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };
        var (_, episode) = await IngestAsync(MediaType.TV, new Dictionary<string, string>(episodeHints) { ["title"] = "Pilot" });
        var (assetId, movie) = await IngestAsync(MediaType.Movies);
        var mine = Guid.NewGuid();
        var lookedUp = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override, is_preferred, image_url)
                VALUES (@mine, @movie, 'Work', 'CoverArt', 'user_upload', 1, 1, '/stream/mine'),
                       (@lookedUp, @episode, 'Work', 'CoverArt', 'tmdb', 0, 1, '/stream/theirs');
                """, new { mine, lookedUp, movie, episode });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.TV, episodeHints, FilePath);

        Assert.Equal(episode, result.WorkId);
        using var conn = _db.CreateConnection();
        Assert.Equal(episode, conn.ExecuteScalar<Guid>("SELECT entity_id FROM entity_assets WHERE id = @mine;", new { mine }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT is_preferred FROM entity_assets WHERE id = @mine;", new { mine }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT is_preferred FROM entity_assets WHERE id = @lookedUp;", new { lookedUp }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets WHERE entity_id = @episode AND asset_type = 'CoverArt' AND is_preferred = 1;", new { episode }));
    }

    [Fact]
    public async Task A_persons_picture_that_would_collide_with_one_the_new_title_already_has_is_a_clash_and_stays_on_the_old_title()
    {
        var episodeHints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };
        var (_, episode) = await IngestAsync(MediaType.TV, new Dictionary<string, string>(episodeHints) { ["title"] = "Pilot" });
        var (assetId, movie) = await IngestAsync(MediaType.Movies);
        var mine = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override, image_url)
                VALUES (@mine, @movie, 'Work', 'CoverArt', 'user_upload', 1, '/stream/same'),
                       (@theirs, @episode, 'Work', 'CoverArt', 'tmdb', 0, '/STREAM/same');
                """, new { mine, theirs = Guid.NewGuid(), movie, episode });
        }

        await Assert.ThrowsAsync<UserDetailsClashException>(() =>
            _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.TV, episodeHints, FilePath));

        using var conn = _db.CreateConnection();
        Assert.Equal(movie, conn.ExecuteScalar<Guid>("SELECT entity_id FROM entity_assets WHERE id = @mine;", new { mine }));
        Assert.Equal(movie, conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = (SELECT edition_id FROM media_assets WHERE id = @assetId);", new { assetId }));
    }

    [Fact]
    public async Task A_persons_artwork_link_that_would_collide_with_one_the_new_title_already_has_is_a_clash_and_stays_on_the_old_title()
    {
        var episodeHints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "1",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };
        var (_, episode) = await IngestAsync(MediaType.TV, new Dictionary<string, string>(episodeHints) { ["title"] = "Pilot" });
        var (assetId, movie) = await IngestAsync(MediaType.Movies);
        var artwork = Guid.NewGuid();
        var mine = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO artwork_assets (id, content_hash, source_provider) VALUES (@artwork, 'shared', 'user_upload');
                INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_user_override) VALUES
                    (@mine, @movie, 'Work', @artwork, 'Primary', 1),
                    (@theirs, @episode, 'Work', @artwork, 'Primary', 0);
                """, new { artwork, mine, theirs = Guid.NewGuid(), movie, episode });
        }

        await Assert.ThrowsAsync<UserDetailsClashException>(() =>
            _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.TV, episodeHints, FilePath));

        using var conn = _db.CreateConnection();
        Assert.Equal(movie, conn.ExecuteScalar<Guid>("SELECT entity_id FROM entity_artwork_links WHERE id = @mine;", new { mine }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @movie;", new { movie }));
    }

    [Fact]
    public async Task A_title_with_typed_details_that_still_has_another_file_keeps_them_when_one_file_is_placed_again()
    {
        var (assetId, work) = await IngestAsync(MediaType.Movies);
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("UPDATE works SET display_overrides_json = '{\"genre\":\"Drama\"}' WHERE id = @work;", new { work });
            seed.Execute("INSERT INTO editions (id, work_id) VALUES (@id, @work);", new { id = Guid.NewGuid(), work });
        }

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.Videos, Hints(assetId), FilePath);

        Assert.True(result.Replaced);
        using var conn = _db.CreateConnection();
        Assert.Equal("""{"genre":"Drama"}""", conn.ExecuteScalar<string>("SELECT display_overrides_json FROM works WHERE id = @work;", new { work }));
        Assert.True(string.IsNullOrEmpty(conn.ExecuteScalar<string?>("SELECT display_overrides_json FROM works WHERE id = @id;", new { id = result.WorkId })));
    }

    [Fact]
    public async Task An_episode_placed_as_TV_from_Movies_gets_its_show_and_season_from_the_hints()
    {
        var (assetId, movieWork) = await IngestAsync(MediaType.Movies);
        var hints = new Dictionary<string, string>
        {
            ["show_name"] = "Some Show",
            [MetadataFieldConstants.SeasonNumber] = "2",
            [MetadataFieldConstants.EpisodeNumber] = "5",
        };

        var result = await _repository.ReplaceUnderMediaTypeAsync(assetId, MediaType.TV, hints, FilePath);

        using var conn = _db.CreateConnection();
        Assert.Equal(("TV", "child", 5), conn.QuerySingle<(string, string, int?)>(
            "SELECT media_type, work_kind, ordinal FROM works WHERE id = @id;", new { id = result.WorkId }));
        Assert.Equal(3, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWork }));
    }

    [Fact]
    public async Task Dropping_the_looked_up_details_keeps_the_files_own_claims_and_a_persons_edits()
    {
        var (assetId, work) = await IngestAsync(MediaType.Movies);
        SeedProviders();
        var edition = EditionOf(assetId);
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked) VALUES
                    (@c1, @assetId, @local, 'title', 'clip', 0.6, '2026-01-01', 0),
                    (@c2, @assetId, @tmdb, 'title', 'The Looked-Up Title', 0.9, '2026-01-02', 0),
                    (@c3, @assetId, @tmdb, 'description', 'Looked-up text', 0.9, '2026-01-02', 0),
                    (@c4, @work, @tmdb, 'genre', 'Drama', 0.9, '2026-01-02', 0),
                    (@c5, @work, @manual, 'rating', 'PG', 1, '2026-01-03', 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    (@assetId, 'title', 'The Looked-Up Title', '2026-01-02'),
                    (@assetId, 'description', 'Looked-up text', '2026-01-02'),
                    (@assetId, 'cover_url', '/stream/cover', '2026-01-02'),
                    (@work, 'genre', 'Drama', '2026-01-02'),
                    (@work, 'rating', 'PG', '2026-01-03');
                INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@b1, @work, 'tmdb_id', '123'), (@b2, @assetId, 'imdb_id', 'tt1');
                UPDATE works SET wikidata_qid = 'Q1', external_identifiers = '{"tmdb_id":"123"}' WHERE id = @work;
                UPDATE editions SET wikidata_qid = 'Q2' WHERE id = @edition;
                """, new
            {
                assetId,
                work,
                edition,
                c1 = Guid.NewGuid(),
                c2 = Guid.NewGuid(),
                c3 = Guid.NewGuid(),
                c4 = Guid.NewGuid(),
                c5 = Guid.NewGuid(),
                b1 = Guid.NewGuid(),
                b2 = Guid.NewGuid(),
                local = WellKnownProviders.LocalProcessor,
                tmdb = WellKnownProviders.Tmdb,
                manual = WellKnownProviders.UserManual,
            });
        }

        var dropped = await _repository.DropProviderDetailsAsync(assetId);

        Assert.Equal(3, dropped.ClaimsDropped);
        Assert.Contains(assetId, dropped.EntityIds);
        Assert.Contains(work, dropped.EntityIds);
        using var conn = _db.CreateConnection();
        // The file's own claim and the person's locked claim stay; every claim from the provider goes.
        Assert.Equal(["clip"], conn.Query<string>("SELECT claim_value FROM metadata_claims WHERE entity_id = @assetId;", new { assetId }).ToList());
        Assert.Equal(["PG"], conn.Query<string>("SELECT claim_value FROM metadata_claims WHERE entity_id = @work;", new { work }).ToList());
        // The values those claims produced are gone for the caller to work out again; values nothing claimed are not touched.
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id = @assetId AND key IN ('title', 'description');", new { assetId }));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id = @assetId AND key = 'cover_url';", new { assetId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id = @work AND key = 'genre';", new { work }));
        Assert.Equal("PG", conn.ExecuteScalar<string>("SELECT value FROM canonical_values WHERE entity_id = @work AND key = 'rating';", new { work }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM bridge_ids;"));
        Assert.Null(conn.ExecuteScalar<string?>("SELECT wikidata_qid FROM works WHERE id = @work;", new { work }));
        Assert.Null(conn.ExecuteScalar<string?>("SELECT external_identifiers FROM works WHERE id = @work;", new { work }));
        Assert.Null(conn.ExecuteScalar<string?>("SELECT wikidata_qid FROM editions WHERE id = @edition;", new { edition }));
    }

    [Fact]
    public async Task Dropping_the_looked_up_details_removes_provider_artwork_and_keeps_the_files_own_and_a_persons_choice()
    {
        var (assetId, work) = await IngestAsync(MediaType.Movies);
        SeedProviders();
        var providerArt = Guid.NewGuid();
        var embeddedArt = Guid.NewGuid();
        var chosenArt = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO artwork_assets (id, content_hash, source_provider) VALUES (@providerArt, 'h1', 'tmdb'), (@embeddedArt, 'h2', 'embedded'), (@chosenArt, 'h3', 'tmdb');
                INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_user_override) VALUES
                    (@l1, @work, 'Work', @providerArt, 'Primary', 0),
                    (@l2, @work, 'Work', @embeddedArt, 'Primary', 0),
                    (@l3, @work, 'Work', @chosenArt, 'Background', 1);
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, source_provider, is_user_override) VALUES
                    (@a1, @work, 'Work', 'CoverArt', 'tmdb', 0),
                    (@a2, @work, 'Work', 'Banner', 'user_upload', 1);
                """, new { providerArt, embeddedArt, chosenArt, work, l1 = Guid.NewGuid(), l2 = Guid.NewGuid(), l3 = Guid.NewGuid(), a1 = Guid.NewGuid(), a2 = Guid.NewGuid() });
        }

        await _repository.DropProviderDetailsAsync(assetId);

        using var conn = _db.CreateConnection();
        Assert.Equivalent(
            new[] { embeddedArt, chosenArt },
            conn.Query<Guid>("SELECT artwork_asset_id FROM entity_artwork_links WHERE entity_id = @work;", new { work }).ToArray());
        Assert.Equal(["user_upload"], conn.Query<string>("SELECT source_provider FROM entity_assets WHERE entity_id = @work;", new { work }).ToList());
    }

    [Fact]
    public async Task Dropping_details_for_a_file_the_store_does_not_know_does_nothing()
    {
        var dropped = await _repository.DropProviderDetailsAsync(Guid.NewGuid());

        Assert.Equal(0, dropped.ClaimsDropped);
        Assert.Empty(dropped.EntityIds);
    }

    [Fact]
    public async Task A_plan_is_saved_once_read_back_by_job_and_by_folder_and_its_config_step_is_recorded_once()
    {
        var operationId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var plan = new FolderKindChangePlan(
            operationId, sourceId, "library-from", "library-to", "movies", "other_videos", "Videos",
            TargetIsPlain: true, StartsFirstCheck: false, DateTimeOffset.Parse("2026-10-23T10:00:00Z"),
            StartedByProfileId: Guid.NewGuid(), RepairsOperationId: Guid.NewGuid());

        await _repository.SavePlanAsync(plan);
        await _repository.SavePlanAsync(plan with { ToLibraryId = "someone-else" });

        var read = await _repository.GetPlanAsync(operationId);
        Assert.Equal(plan, read);
        Assert.Equal(plan, await _repository.GetLatestPlanAsync(sourceId));
        Assert.Null(await _repository.GetPlanAsync(Guid.NewGuid()));

        var first = DateTimeOffset.Parse("2026-10-23T10:05:00Z");
        Assert.True(await _repository.MarkConfigAppliedAsync(operationId, first));
        Assert.True(await _repository.MarkConfigAppliedAsync(operationId, first.AddHours(1)));
        Assert.Equal(first, (await _repository.GetPlanAsync(operationId))!.ConfigAppliedAt);
        Assert.False(await _repository.MarkConfigAppliedAsync(Guid.NewGuid(), first));
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    private async Task<(Guid AssetId, Guid WorkId)> IngestAsync(MediaType mediaType, Dictionary<string, string>? metadata = null)
    {
        var assetId = Guid.NewGuid();
        var editionId = await _factory.EnsureEntityChainAsync(mediaType, metadata ?? new Dictionary<string, string> { ["title"] = "Clip" }, FilePath);
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a, @e, @h, @p, 'Normal');",
            new { a = assetId, e = editionId, h = assetId.ToString("N"), p = FilePath });
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@a, 'media_type', @t, '2026-01-01');",
            new { a = assetId, t = mediaType.ToString() });
        return (assetId, conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = @e;", new { e = editionId }));
    }

    private Guid EditionOf(Guid assetId)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<Guid>("SELECT edition_id FROM media_assets WHERE id = @assetId;", new { assetId });
    }

    private static IReadOnlyDictionary<string, string> Hints(Guid assetId) =>
        new Dictionary<string, string> { ["title"] = "Clip" };

    private void SeedProviders()
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES
                (@local, 'local_processor', '1', 1), (@tmdb, 'tmdb', '1', 1), (@manual, 'user_manual', '1', 1);
            """,
            new { local = WellKnownProviders.LocalProcessor, tmdb = WellKnownProviders.Tmdb, manual = WellKnownProviders.UserManual });
    }
}
