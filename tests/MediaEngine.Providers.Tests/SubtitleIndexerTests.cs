using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class SubtitleIndexerTests : IDisposable
{
    private const string FilmQid = "Q127367";

    private readonly string _root;
    private readonly DatabaseConnection _db;
    private readonly SubtitleIndexRepository _repository;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFfmpeg _ffmpeg = new();
    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _frodo = Guid.NewGuid();
    private readonly Guid _mordor = Guid.NewGuid();
    private readonly string _videoPath;

    public SubtitleIndexerTests()
    {
        DapperConfiguration.Configure();
        _root = Path.Combine(Path.GetTempPath(), $"tuvima_subtitle_index_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _repository = new SubtitleIndexRepository(_db);
        _videoPath = Path.Combine(_root, "The Fellowship of the Ring.mkv");
        File.WriteAllText(_videoPath, "not really a film");

        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works(id, media_type, work_kind, is_catalog_only, ownership, wikidata_qid)
            VALUES(@work, 'Movies', 'standalone', 0, 'Owned', @qid);
            INSERT INTO editions(id, work_id) VALUES(@edition, @work);
            INSERT INTO media_assets(id, edition_id, content_hash, file_path_root)
            VALUES(@asset, @edition, @hash, @path);
            """,
            new { work, edition, asset = _asset, qid = FilmQid, hash = Guid.NewGuid().ToString("N"), path = _videoPath });
        AddEntity(_frodo, "Q-frodo", "Frodo Baggins", "Character", FilmQid);
        AddEntity(_mordor, "Q-mordor", "Mordor", "Location", FilmQid);
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temporary folder is cleaned by the operating system if a file is still held open.
        }
    }

    [Fact]
    public async Task FilmSubtitleFile_YieldsFrodoAndMordorTagsAtTheRightTimeWindows()
    {
        var track = AddStoredTrack("any-source", "en", FilmSubtitles);

        var result = await Indexer().RunOnceAsync(10);

        Assert.Equal(1, result.TracksIndexed);
        var trackId = SubtitleTrackIds.ForTextTrack(track);
        var lines = await _repository.GetLinesAsync(trackId);
        Assert.Equal(5, lines.Count);
        var tags = await _repository.GetTagsAsync(trackId);

        var frodo = Assert.Single(tags, tag => tag.EntityId == _frodo);
        Assert.Equal(61_000, frodo.WindowStartMs);
        Assert.Equal(75_500, frodo.WindowEndMs);
        Assert.Equal(2, frodo.MentionCount);

        var mordor = tags.Where(tag => tag.EntityId == _mordor).OrderBy(tag => tag.WindowStartMs).ToList();
        Assert.Equal(2, mordor.Count);
        Assert.Equal((72_000L, 75_500L), (mordor[0].WindowStartMs, mordor[0].WindowEndMs));
        Assert.Equal(5_400_000L, mordor[1].WindowStartMs);
    }

    [Fact]
    public async Task TracksFromAnySource_AreIndexed_WithoutNamingASource()
    {
        AddStoredTrack("subdl", "en", FilmSubtitles);
        AddStoredTrack("a-future-plugin", "fr", FilmSubtitles);
        AddStoredTrack("local", "de", FilmSubtitles);

        var result = await Indexer().RunOnceAsync(10);

        Assert.Equal(3, result.TracksIndexed);
        using var connection = _db.CreateConnection();
        Assert.Equal(3, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM subtitle_index_tracks WHERE state = 'Indexed' AND source_kind = 'TextTrack';"));
    }

    [Fact]
    public async Task AnIndexedTrack_IsNotReadAgain_UntilItsStoredFileChanges()
    {
        var track = AddStoredTrack("any-source", "en", FilmSubtitles);
        var indexer = Indexer();
        await indexer.RunOnceAsync(10);

        var idle = await indexer.RunOnceAsync(10);
        Assert.False(idle.DidWork);

        using (var connection = _db.CreateConnection())
        {
            connection.Execute("UPDATE text_tracks SET updated_at = @at WHERE id = @track;", new { at = _clock.GetUtcNow().AddMinutes(1).ToString("o"), track });
        }

        var again = await indexer.RunOnceAsync(10);
        Assert.Equal(1, again.TracksIndexed);
    }

    [Fact]
    public async Task AnEntityAddedLater_IsTaggedAtTheNextRefresh_WithoutReadingTheFileAgain()
    {
        var track = AddStoredTrack("any-source", "en", FilmSubtitles);
        var indexer = Indexer();
        await indexer.RunOnceAsync(10);
        var trackId = SubtitleTrackIds.ForTextTrack(track);

        var gandalf = Guid.NewGuid();
        AddEntity(gandalf, "Q-gandalf", "Gandalf", "Character", FilmQid);
        File.Delete(StoredPath(track));

        _clock.Advance(SubtitleIndexer.TagRefreshInterval + TimeSpan.FromMinutes(1));
        var refreshed = await indexer.RunOnceAsync(10);

        Assert.Equal(1, refreshed.TracksRetagged);
        Assert.Equal(0, refreshed.TracksFailed);
        var tags = await _repository.GetTagsAsync(trackId);
        var gandalfTag = Assert.Single(tags, tag => tag.EntityId == gandalf);
        Assert.Equal(40_000, gandalfTag.WindowStartMs);
        Assert.Contains(tags, tag => tag.EntityId == _frodo);

        _clock.Advance(SubtitleIndexer.TagRefreshInterval + TimeSpan.FromMinutes(1));
        var unchanged = await indexer.RunOnceAsync(10);
        Assert.Equal(0, unchanged.TracksRetagged);
    }

    [Fact]
    public async Task AMissingFile_IsRecordedAsFailed_AndTriedAgainAfterTheDelay()
    {
        var track = AddStoredTrack("any-source", "en", FilmSubtitles);
        File.Delete(StoredPath(track));
        var indexer = Indexer();

        var first = await indexer.RunOnceAsync(10);
        Assert.Equal(1, first.TracksFailed);

        var tooSoon = await indexer.RunOnceAsync(10);
        Assert.False(tooSoon.DidWork);

        File.WriteAllText(StoredPath(track), FilmSubtitles);
        _clock.Advance(SubtitleIndexer.RetryDelay + TimeSpan.FromMinutes(1));
        var retried = await indexer.RunOnceAsync(10);
        Assert.Equal(1, retried.TracksIndexed);
        using var connection = _db.CreateConnection();
        Assert.Equal("Indexed", connection.ExecuteScalar<string>("SELECT state FROM subtitle_index_tracks;"));
    }

    [Fact]
    public async Task EmbeddedTextStreams_AreExtractedAndIndexed_AndChapterTimesStored()
    {
        _ffmpeg.Available = true;
        _ffmpeg.Probe = new MediaProbeResult
        {
            Chapters = [new(0, "Prologue", 0, 62.5), new(1, null, 62.5, null)],
            SubtitleStreams =
            [
                new(2, "subrip", "eng", false, false),
                new(3, "hdmv_pgs_subtitle", "fre", false, false),
                new(4, "subrip", "ger", true, false),
                new(5, "ass", "eng", false, true),
            ],
        };
        _ffmpeg.ExtractedText = FilmSubtitles;
        var indexer = Indexer();

        var result = await indexer.RunOnceAsync(10);

        Assert.Equal(1, result.FilesProbed);
        Assert.Equal(1, result.TracksIndexed);
        Assert.Single(_ffmpeg.Extractions);
        Assert.Contains("0:2", _ffmpeg.Extractions[0]);

        var chapters = await _repository.GetChaptersAsync(_asset);
        Assert.Equal(2, chapters.Count);
        Assert.Equal(new MediaAssetChapter(0, 0, 62_500, "Prologue"), chapters[0]);
        Assert.Equal(new MediaAssetChapter(1, 62_500, null, null), chapters[1]);

        var embeddedId = SubtitleTrackIds.ForEmbedded(_asset, 2);
        var tags = await _repository.GetTagsAsync(embeddedId);
        Assert.Contains(tags, tag => tag.EntityId == _frodo);
        Assert.Contains(tags, tag => tag.EntityId == _mordor);

        var again = await indexer.RunOnceAsync(10);
        Assert.False(again.DidWork);
        Assert.Single(_ffmpeg.Extractions);
    }

    [Fact]
    public async Task EmbeddedStream_IsSkipped_WhenAStoredTrackCoversItsLanguage()
    {
        AddStoredTrack("any-source", "eng", FilmSubtitles);
        _ffmpeg.Available = true;
        _ffmpeg.Probe = new MediaProbeResult { SubtitleStreams = [new(2, "subrip", "eng", false, false)] };

        var result = await Indexer().RunOnceAsync(10);

        Assert.Equal(1, result.FilesProbed);
        Assert.Empty(_ffmpeg.Extractions);
    }

    [Fact]
    public async Task WithoutFfmpeg_VideoFilesWaitForTheirProbe()
    {
        _ffmpeg.Available = false;

        var result = await Indexer().RunOnceAsync(10);

        Assert.Equal(0, result.FilesProbed);
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM subtitle_index_assets;"));
    }

    [Fact]
    public async Task AFailedExtraction_IsRecordedForThatStreamOnly()
    {
        _ffmpeg.Available = true;
        _ffmpeg.ExtractionExitCode = 1;
        _ffmpeg.Probe = new MediaProbeResult { SubtitleStreams = [new(2, "subrip", "eng", false, false)] };

        var result = await Indexer().RunOnceAsync(10);

        Assert.Equal(1, result.FilesProbed);
        Assert.Equal(1, result.TracksFailed);
        using var connection = _db.CreateConnection();
        Assert.Equal("Failed", connection.ExecuteScalar<string>("SELECT state FROM subtitle_index_tracks WHERE source_kind = 'Embedded';"));
    }

    [Fact]
    public void SelectEmbeddedStreams_KeepsOneTextStreamPerLanguage_AndSkipsForcedAndPictureStreams()
    {
        var chosen = SubtitleIndexer.SelectEmbeddedStreams(
            [
                new(3, "hdmv_pgs_subtitle", "eng", false, false),
                new(4, "subrip", "eng", true, false),
                new(5, "subrip", "eng", false, false),
                new(6, "ass", "eng", false, true),
                new(7, "mov_text", "spa", false, false),
                new(8, "webvtt", "fra", false, false),
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fra" });

        Assert.Equal([5, 7], chosen.Select(stream => stream.StreamIndex));
    }

    private const string FilmSubtitles = """
        WEBVTT

        1
        00:00:10.000 --> 00:00:12.000
        Good morning.

        2
        00:00:40.000 --> 00:00:42.000
        Gandalf is late again.

        3
        00:01:01.000 --> 00:01:03.000
        Frodo, we must leave the Shire.

        4
        00:01:12.000 --> 00:01:15.500
        We go into Mordor, Frodo.

        5
        01:30:00.000 --> 01:30:03.000
        Mordor at last.
        """;

    private SubtitleIndexer Indexer() =>
        new(_repository, _ffmpeg, NullLogger<SubtitleIndexer>.Instance, _clock);

    private string StoredPath(Guid trackId) => Path.Combine(_root, trackId.ToString("N") + ".vtt");

    private Guid AddStoredTrack(string provider, string language, string content)
    {
        var id = Guid.NewGuid();
        File.WriteAllText(StoredPath(id), content);
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO text_tracks(id, asset_id, kind, language, provider, source_format, normalized_format, local_path, created_at)
            VALUES(@id, @asset, 'Subtitles', @language, @provider, 'srt', 'vtt', @path, @created);
            """,
            new { id, asset = _asset, language, provider, path = StoredPath(id), created = _clock.GetUtcNow().ToString("o") });
        return id;
    }

    private void AddEntity(Guid id, string qid, string label, string subType, string workQid)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO fictional_entities(id, wikidata_qid, label, entity_sub_type, created_at)
            VALUES(@id, @qid, @label, @subType, @created);
            INSERT INTO fictional_entity_work_links(id, appearance_key, entity_id, work_qid)
            VALUES(@link, @key, @id, @workQid);
            """,
            new { id, qid, label, subType, created = _clock.GetUtcNow().ToString("o"), link = Guid.NewGuid(), key = qid + "|" + workQid, workQid });
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeFfmpeg : IFFmpegService
    {
        public bool Available { get; set; }
        public MediaProbeResult? Probe { get; set; }
        public string ExtractedText { get; set; } = string.Empty;
        public int ExtractionExitCode { get; set; }
        public List<string> Extractions { get; } = [];

        public string? FfmpegPath => "fake";
        public string? FfprobePath => "fake";
        public bool IsAvailable => Available;
        public HardwareCapabilities HardwareCapabilities => new();

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(Probe);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Extractions.Add(string.Join(' ', arguments));
            if (ExtractionExitCode == 0)
            {
                File.WriteAllText(arguments[^1], ExtractedText);
            }

            return Task.FromResult((ExtractionExitCode, string.Empty, ExtractionExitCode == 0 ? string.Empty : "Stream map matches no streams."));
        }
    }
}
