using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class SubtitleIndexRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-subtitle-index-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SubtitleIndexRepository _repository;

    public SubtitleIndexRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new SubtitleIndexRepository(_database);
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_path);
    }

    [Fact]
    public async Task EntityNames_AreLimitedToTheFilesWorksAndTheirUniverse()
    {
        var show = SeedWork("TV", "Q-show", parent: null, collection: null);
        var episode = SeedWork("TV", "Q-episode", parent: show.Work, collection: null);
        var asset = SeedAsset(episode.Edition);
        var universeCollection = Guid.NewGuid();
        Execute("INSERT INTO narrative_roots(qid, label, level, created_at) VALUES('Q-universe', 'Middle-earth', 'Universe', '2026-10-21T00:00:00Z');");
        Execute(
            "INSERT INTO collections(id, collection_type, display_name, universe_qid) VALUES(@id, 'Series', 'Show Series', 'Q-universe');",
            new { id = universeCollection });
        Execute("UPDATE works SET collection_id = @id WHERE id = @work;", new { id = universeCollection, work = show.Work });

        var linkedToEpisode = SeedEntity("Q-sam", "Samwise Gamgee", "Character", universe: null, linkedWorkQid: "Q-episode");
        var linkedToShow = SeedEntity("Q-frodo", "Frodo Baggins", "Character", universe: null, linkedWorkQid: "Q-show");
        var sameUniverse = SeedEntity("Q-mordor", "Mordor", "Location", universe: "Q-universe", linkedWorkQid: "Q-other-work");
        var other = SeedEntity("Q-hogwarts", "Hogwarts", "Location", universe: "Q-wizards", linkedWorkQid: "Q-other-work");
        Execute(
            "INSERT INTO fictional_entity_user_overrides(entity_id, label) VALUES(@id, 'Frodo of the Shire');",
            new { id = linkedToShow });

        var names = await _repository.GetEntityNamesForAssetAsync(asset);

        Assert.Equal(
            new[] { "Frodo of the Shire", "Mordor", "Samwise Gamgee" }.Order(),
            names.Select(name => name.Label).Order());
        Assert.DoesNotContain(names, name => name.EntityId == other);
        Assert.Contains(names, name => name.EntityId == linkedToEpisode);
        Assert.Contains(names, name => name.EntityId == sameUniverse);
    }

    [Fact]
    public async Task SavedIndex_RoundTripsLinesTagsAndChapters_AndGoesWithTheFile()
    {
        var work = SeedWork("Movies", "Q-film", parent: null, collection: null);
        var asset = SeedAsset(work.Edition);
        var entity = SeedEntity("Q-frodo", "Frodo Baggins", "Character", universe: null, linkedWorkQid: "Q-film");
        var source = new SubtitleTrackSource(Guid.NewGuid(), asset, SubtitleSourceKind.Embedded, null, 3, "eng", "stamp", null);
        SubtitleLine[] lines = [new(0, 1_000, 2_000, "Frodo!"), new(1, 5_000, 6_000, "Run.")];
        SubtitleNameTag[] tags = [new(entity, 1_000, 2_000, 0, 0, 1, "Frodo")];

        await _repository.SaveIndexedTrackAsync(source, lines, tags, "sig", Now);
        await _repository.SaveProbedAssetAsync(asset, [new(0, 0, 90_000, "One"), new(1, 90_000, null, null)], 1, Now);

        Assert.Equal(lines, await _repository.GetLinesAsync(source.TrackId));
        Assert.Equal(tags, await _repository.GetTagsAsync(source.TrackId));
        Assert.Equal(2, (await _repository.GetChaptersAsync(asset)).Count);

        await _repository.SaveIndexedTrackAsync(source, lines[..1], [], "sig2", Now.AddMinutes(1));
        Assert.Single(await _repository.GetLinesAsync(source.TrackId));
        Assert.Empty(await _repository.GetTagsAsync(source.TrackId));

        Execute("DELETE FROM media_assets WHERE id = @asset;", new { asset });
        Assert.Empty(await _repository.GetLinesAsync(source.TrackId));
        Assert.Empty(await _repository.GetChaptersAsync(asset));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM subtitle_index_tracks;"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM subtitle_index_assets;"));
    }

    [Fact]
    public async Task ADeletedEntity_TakesItsTagsWithIt()
    {
        var work = SeedWork("Movies", "Q-film", parent: null, collection: null);
        var asset = SeedAsset(work.Edition);
        var entity = SeedEntity("Q-frodo", "Frodo Baggins", "Character", universe: null, linkedWorkQid: "Q-film");
        var source = new SubtitleTrackSource(Guid.NewGuid(), asset, SubtitleSourceKind.Embedded, null, 3, "eng", "stamp", null);
        await _repository.SaveIndexedTrackAsync(source, [new(0, 1_000, 2_000, "Frodo!")], [new(entity, 1_000, 2_000, 0, 0, 1, "Frodo")], "sig", Now);

        Execute("DELETE FROM fictional_entities WHERE id = @entity;", new { entity });

        Assert.Empty(await _repository.GetTagsAsync(source.TrackId));
        Assert.Single(await _repository.GetLinesAsync(source.TrackId));
    }

    [Fact]
    public async Task FailedTracks_AreRetriedOnlyAfterTheirDelay_AndOnlyUpToTheAttemptLimit()
    {
        var work = SeedWork("Movies", "Q-film", parent: null, collection: null);
        var asset = SeedAsset(work.Edition);
        var textTrack = Guid.NewGuid();
        Execute(
            """
            INSERT INTO text_tracks(id, asset_id, kind, language, provider, source_format, normalized_format, local_path, created_at)
            VALUES(@id, @asset, 'Subtitles', 'en', 'any', 'srt', 'vtt', '/x.vtt', '2026-10-21T00:00:00Z');
            """,
            new { id = textTrack, asset });
        var source = Assert.Single(await _repository.GetStoredTracksNeedingIndexAsync(10, Now));

        await _repository.MarkTrackFailedAsync(source, "missing", Now, Now.AddHours(1));
        Assert.Empty(await _repository.GetStoredTracksNeedingIndexAsync(10, Now.AddMinutes(30)));
        Assert.Single(await _repository.GetStoredTracksNeedingIndexAsync(10, Now.AddHours(2)));

        for (var attempt = 2; attempt <= SubtitleIndexRepository.MaxAttempts; attempt++)
        {
            await _repository.MarkTrackFailedAsync(source, "missing", Now, Now.AddHours(1));
        }

        Assert.Empty(await _repository.GetStoredTracksNeedingIndexAsync(10, Now.AddDays(30)));
        Execute("UPDATE text_tracks SET updated_at = '2026-11-01T00:00:00Z' WHERE id = @id;", new { id = textTrack });
        Assert.Single(await _repository.GetStoredTracksNeedingIndexAsync(10, Now));
    }

    [Fact]
    public async Task OnlyVideoFilesThatWereNotProbed_AreOffered()
    {
        var movie = SeedAsset(SeedWork("Movies", "Q-a", parent: null, collection: null).Edition);
        var show = SeedAsset(SeedWork("TV", "Q-b", parent: null, collection: null).Edition);
        SeedAsset(SeedWork("Books", "Q-c", parent: null, collection: null).Edition);

        var waiting = await _repository.GetAssetsAwaitingProbeAsync(10, Now);
        Assert.Equal(new[] { movie, show }.Order(), waiting.Select(a => a.AssetId).Order());

        await _repository.SaveProbedAssetAsync(movie, [], 0, Now);
        await _repository.MarkAssetProbeFailedAsync(show, "boom", Now, Now.AddHours(1));

        Assert.Empty(await _repository.GetAssetsAwaitingProbeAsync(10, Now));
        Assert.Equal(show, Assert.Single(await _repository.GetAssetsAwaitingProbeAsync(10, Now.AddHours(2))).AssetId);
    }

    private (Guid Work, Guid Edition) SeedWork(string mediaType, string qid, Guid? parent, Guid? collection)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Execute(
            """
            INSERT INTO works(id, media_type, work_kind, is_catalog_only, ownership, wikidata_qid, parent_work_id, collection_id)
            VALUES(@work, @mediaType, 'standalone', 0, 'Owned', @qid, @parent, @collection);
            INSERT INTO editions(id, work_id) VALUES(@edition, @work);
            """,
            new { work, edition, mediaType, qid, parent, collection });
        return (work, edition);
    }

    private Guid SeedAsset(Guid edition)
    {
        var asset = Guid.NewGuid();
        Execute(
            "INSERT INTO media_assets(id, edition_id, content_hash, file_path_root) VALUES(@asset, @edition, @hash, '/media/file.mkv');",
            new { asset, edition, hash = Guid.NewGuid().ToString("N") });
        return asset;
    }

    private Guid SeedEntity(string qid, string label, string subType, string? universe, string linkedWorkQid)
    {
        var id = Guid.NewGuid();
        Execute(
            """
            INSERT INTO fictional_entities(id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
            VALUES(@id, @qid, @label, @subType, @universe, '2026-10-21T00:00:00Z');
            INSERT INTO fictional_entity_work_links(id, appearance_key, entity_id, work_qid)
            VALUES(@link, @key, @id, @linkedWorkQid);
            """,
            new { id, qid, label, subType, universe, link = Guid.NewGuid(), key = qid + "|" + linkedWorkQid, linkedWorkQid });
        return id;
    }

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    private int Scalar(string sql)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(sql);
    }
}
