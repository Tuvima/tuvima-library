using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The group Match (universal editor v5, D36): one show and season, or one release, is picked once and every ticked item is lined up by its
/// own number. Items with no number, a number the target lacks, a file name that names another season, or a number another ticked item
/// already takes come back as "Weak match" (not ticked), never silently placed. Real temp data store for the items; hand-written handlers
/// stand in for TheTVDB and MusicBrainz.
/// </summary>
public sealed class EditorGroupMatchServiceTests : IDisposable
{
    private const string ShowId = "100";
    private const string ReleaseId = "22222222-2222-4222-8222-222222222222";

    private const string ShowExtended = """
        {"data":{"id":100,"name":"The Last Signal","year":"2019","seasons":[
          {"id":2001,"number":1,"type":{"type":"default"}},
          {"id":2002,"number":2,"type":{"type":"default"}}]}}
        """;

    private const string SeasonTwoEpisodes = """
        {"data":{"episodes":[
          {"id":9201,"name":"Echoes","number":1,"seasonNumber":2,"seasonId":2002,"aired":"2021-01-04","runtime":45},
          {"id":9202,"name":"Static","number":2,"seasonNumber":2,"seasonId":2002,"aired":"2021-01-11","runtime":45},
          {"id":9203,"name":"Carrier Wave","number":3,"seasonNumber":2,"seasonId":2002,"aired":"2021-01-18","runtime":46}]},
         "links":{"next":null}}
        """;

    private const string ReleaseDetail = """
        {"id":"22222222-2222-4222-8222-222222222222","title":"Low Water (Deluxe)","date":"2013-06-10","country":"GB",
         "artist-credit":[{"name":"The Tides"}],"release-group":{"id":"rg-2"},
         "media":[
           {"position":1,"format":"CD","tracks":[
              {"id":"t-1-1","position":1,"title":"Opening","length":180000,"recording":{"id":"rec-1","title":"Opening"}},
              {"id":"t-1-2","position":2,"title":"Slack Water","length":181000,"recording":{"id":"rec-2","title":"Slack Water"}}]}
         ]}
        """;

    private readonly EditorHeaderTestWorld _world = new("The Last Signal");
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"tuvima_group_match_{Guid.NewGuid():N}");
    private readonly Dictionary<Guid, string> _kinds = [];
    private readonly Guid _show;
    private readonly Guid _season;

    public EditorGroupMatchServiceTests()
    {
        _show = _world.Work("TV", "parent", "The Last Signal");
        _season = _world.Work("TV", "parent", "Season 2", _show, 2);
    }

    public void Dispose()
    {
        _world.Dispose();
        if (Directory.Exists(_config))
        {
            Directory.Delete(_config, recursive: true);
        }
    }

    // ── Episodes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachTickedEpisode_IsLinedUpWithTheEpisodeThatHasItsOwnNumber()
    {
        var first = Episode("The Last Signal S02E01.mkv");
        var second = Episode("The Last Signal S02E02.mkv");

        var answer = await Service().LineUpAsync(EpisodesRequest(first, second), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Null(answer.MessageCode);
        Assert.Equal("The Last Signal · Season 2", answer.TargetLabel);
        Assert.Equal("TV", answer.SubjectMediaType);
        Assert.Equal("episode", answer.SubjectScopeId);
        Assert.Equal("show_episode", answer.SubjectTargetGroup);
        Assert.All(answer.Lines, line => Assert.True(line.Fits));
        Assert.Equal(["Echoes", "Static"], answer.Lines.Select(line => line.TargetTitle!).ToArray());
        Assert.Equal(["9201", "9202"], answer.Lines.Select(line => line.Candidate!.ProviderItemId!).ToArray());
        Assert.All(answer.Lines, line => Assert.NotNull(line.Placement));
    }

    [Fact]
    public async Task AnEpisodeWithNoNumber_OrANumberTheSeasonLacks_IsLeftOut_NotGuessedAt()
    {
        var numbered = Episode("The Last Signal S02E01.mkv");
        var bonus = Episode("bonus-feature.mkv");
        var missing = Episode("The Last Signal S02E14.mkv");

        var answer = await Service().LineUpAsync(EpisodesRequest(numbered, bonus, missing), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.True(answer.Lines[0].Fits);
        Assert.False(answer.Lines[1].Fits);
        Assert.Equal(EditorMatchMessageCodes.NoNumber, answer.Lines[1].NoteCode);
        Assert.Null(answer.Lines[1].Candidate);
        Assert.False(answer.Lines[2].Fits);
        Assert.Equal(EditorMatchMessageCodes.NoSuchNumber, answer.Lines[2].NoteCode);
        Assert.Equal("14", answer.Lines[2].NoteValue);
        Assert.Null(answer.Lines[2].Candidate);
    }

    [Fact]
    public async Task AFileThatNamesAnotherSeason_IsACheckMatch_EvenThoughItsEpisodeNumberExists()
    {
        var seasonOne = Episode("The Last Signal S01E03.mkv");
        var seasonTwo = Episode("The Last Signal S02E03.mkv");

        var answer = await Service().LineUpAsync(EpisodesRequest(seasonOne, seasonTwo), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        var misfit = answer.Lines[0];
        Assert.False(misfit.Fits);
        Assert.Equal(EditorMatchMessageCodes.OtherSeason, misfit.NoteCode);
        Assert.Equal("1", misfit.NoteValue);
        Assert.NotNull(misfit.Candidate);
        Assert.True(answer.Lines[1].Fits);
    }

    [Fact]
    public async Task TwoTickedEpisodesAimingAtTheSameEpisode_AreBothLeftUnticked_AndSayWhy()
    {
        var original = Episode("The Last Signal S02E02.mkv");
        var copy = Episode("The Last Signal S02E02 (copy).mkv");
        var other = Episode("The Last Signal S02E01.mkv");

        var answer = await Service().LineUpAsync(EpisodesRequest(original, copy, other), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.False(answer.Lines[0].Fits);
        Assert.False(answer.Lines[1].Fits);
        Assert.Equal(EditorMatchMessageCodes.Duplicate, answer.Lines[0].NoteCode);
        Assert.Equal(EditorMatchMessageCodes.Duplicate, answer.Lines[1].NoteCode);
        Assert.NotNull(answer.Lines[0].Candidate);
        Assert.True(answer.Lines[2].Fits);
    }

    [Fact]
    public async Task WhenTheProviderDoesNotAnswer_NothingIsLinedUp_AndTheReasonIsACode_NotASentence()
    {
        var episode = Episode("The Last Signal S02E01.mkv");

        var answer = await Service(tvdb: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .LineUpAsync(EpisodesRequest(episode), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Empty(answer.Lines);
        Assert.Equal(EditorMatchMessageCodes.ProviderSilent, answer.MessageCode);
    }

    [Fact]
    public async Task ASeasonTheShowDoesNotHave_LinesNothingUp()
    {
        var episode = Episode("The Last Signal S02E01.mkv");
        var request = EpisodesRequest(episode);
        request.SeasonNumber = 7;

        var answer = await Service().LineUpAsync(request, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Empty(answer.Lines);
        Assert.Equal(EditorMatchMessageCodes.NotOnProvider, answer.MessageCode);
    }

    // ── Songs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachTickedSong_IsLinedUpWithTheTrackThatHasItsOwnNumber_AndOneThatHasNoneIsLeftOut()
    {
        var one = Song("01 - Opening.flac");
        var two = Song("02 - Slack Water.flac");
        var nine = Song("09 - Lost.flac");

        var answer = await Service().LineUpAsync(SongsRequest(one, two, nine), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Equal("Music", answer.SubjectMediaType);
        Assert.Equal("track", answer.SubjectScopeId);
        Assert.Equal("track", answer.SubjectTargetGroup);
        Assert.Equal("Low Water (Deluxe) · The Tides", answer.TargetLabel);
        Assert.True(answer.Lines[0].Fits);
        Assert.True(answer.Lines[1].Fits);
        Assert.Equal(["Opening", "Slack Water"], answer.Lines.Take(2).Select(line => line.TargetTitle!).ToArray());
        Assert.False(answer.Lines[2].Fits);
        Assert.Equal(EditorMatchMessageCodes.NoSuchNumber, answer.Lines[2].NoteCode);
        Assert.Equal("9", answer.Lines[2].NoteValue);
    }

    [Fact]
    public async Task TwoTickedSongsAimingAtTheSameTrack_AreBothLeftUnticked()
    {
        var one = Song("01 - Opening.flac");
        var again = Song("01 - Opening (demo).flac");

        var answer = await Service().LineUpAsync(SongsRequest(one, again), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.All(answer.Lines, line =>
        {
            Assert.False(line.Fits);
            Assert.Equal(EditorMatchMessageCodes.Duplicate, line.NoteCode);
        });
    }

    [Fact]
    public async Task WhenMusicBrainzDoesNotAnswer_NoSongIsLinedUp()
    {
        var one = Song("01 - Opening.flac");

        var answer = await Service(musicBrainz: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            .LineUpAsync(SongsRequest(one), LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

        Assert.Empty(answer.Lines);
        Assert.Equal(EditorMatchMessageCodes.ProviderSilent, answer.MessageCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid Episode(string fileName)
    {
        var id = _world.Work("TV", "child", Path.GetFileNameWithoutExtension(fileName), _season, 1);
        _world.File(id, fileName, durationSeconds: 45 * 60);
        _kinds[id] = EditorKinds.TvEpisode;
        return id;
    }

    private Guid Song(string fileName)
    {
        var album = _world.Work("Music", "parent", "Low Water");
        var id = _world.Work("Music", "child", Path.GetFileNameWithoutExtension(fileName), album, 1);
        _world.File(id, fileName, durationSeconds: 180);
        _kinds[id] = EditorKinds.Song;
        return id;
    }

    private static EditorGroupMatchRequestDto EpisodesRequest(params Guid[] ids) => new()
    {
        Kind = EditorGroupMatchKinds.Episodes,
        EntityIds = [.. ids],
        SeriesId = ShowId,
        SeasonNumber = 2,
    };

    private static EditorGroupMatchRequestDto SongsRequest(params Guid[] ids) => new()
    {
        Kind = EditorGroupMatchKinds.Songs,
        EntityIds = [.. ids],
        ReleaseId = ReleaseId,
    };

    private EditorGroupMatchService Service(
        Func<HttpRequestMessage, HttpResponseMessage>? tvdb = null, Func<HttpRequestMessage, HttpResponseMessage>? musicBrainz = null)
    {
        var verdicts = new EditorMatchVerdictCalculator(new MatchVerdictSettings());
        var reader = new EditorMatchLocalEvidenceReader(
            new MetadataEditorRepository(_world.Database),
            new ItemCanonicalRepository(_world.Database, NullLogger<ItemCanonicalRepository>.Instance),
            new MetadataClaimRepository(_world.Database),
            _world.QuickFacts,
            NullLogger<EditorMatchLocalEvidenceReader>.Instance);
        var kinds = new FixedKinds(_kinds);
        var search = new EditorMatchSearchService(
            reader, new NoProviders(), new NoSearch(), new EditorConfigurationProvider(() => null), verdicts, kinds, new NoVideos());
        var coordinator = new ProviderRateLimiterCoordinator();
        var brainz = new MusicBrainzReleaseClient(
            new Factory(request => (musicBrainz ?? (_ => Json(ReleaseDetail)))(request)), coordinator, NullLogger<MusicBrainzReleaseClient>.Instance);
        return new EditorGroupMatchService(
            search, reader, kinds, new PlacementSpy(), brainz, TvdbClient(tvdb), NullLogger<EditorGroupMatchService>.Instance);
    }

    private ProviderApiClient TvdbClient(Func<HttpRequestMessage, HttpResponseMessage>? answer)
    {
        Directory.CreateDirectory(_config);
        var loader = new ConfigurationDirectoryLoader(_config);
        var path = Path.Combine(FindRepoRoot(), "config", "providers", "tvdb.json");
        var provider = JsonSerializer.Deserialize<ProviderConfiguration>(
            File.ReadAllText(path), new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })!;
        provider.HttpClient ??= new HttpClientConfig();
        provider.HttpClient.ApiKey = "installation-key";
        provider.RateLimit = new ProviderRateLimitConfiguration { RequestsPerSecond = 1000, Burst = 1000, MaxConcurrency = 4 };
        loader.SaveProvider(provider);
        return new ProviderApiClient(loader, new Factory(request => TvdbAnswer(request, answer)), new ProviderRateLimiterCoordinator());
    }

    private static HttpResponseMessage TvdbAnswer(HttpRequestMessage request, Func<HttpRequestMessage, HttpResponseMessage>? failure)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return Json("""{"data":{"token":"bearer-token"}}""");
        }

        if (failure is not null)
        {
            return failure(request);
        }

        if (path.Contains("/translations/", StringComparison.Ordinal))
        {
            return Json("""{"data":{"name":"The Last Signal"}}""");
        }

        if (path.EndsWith("/series/100/extended", StringComparison.Ordinal))
        {
            return Json(ShowExtended);
        }

        return path.Contains("/series/100/episodes/", StringComparison.Ordinal) ? Json(SeasonTwoEpisodes) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(answer), disposeHandler: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class FixedKinds(Dictionary<Guid, string> kinds) : IEditorMatchKindResolver
    {
        public Task<string?> ResolveAsync(Guid entityId, CancellationToken ct) => Task.FromResult(kinds.GetValueOrDefault(entityId));
    }

    private sealed class PlacementSpy : IEditorMatchPlacement
    {
        public Task<EditorMatchPlacementDto?> PlanAsync(
            Guid entityId, ItemCanonicalRetailCandidateDto candidate, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default) =>
            Task.FromResult<EditorMatchPlacementDto?>(new EditorMatchPlacementDto { DestinationId = Guid.NewGuid(), ParentLabel = "Somewhere", Managed = false });
    }

    private sealed class NoProviders : IEditorMatchProviderCatalog
    {
        public IReadOnlyList<RetailProviderInfo> List(string mediaType) => [];
    }

    private sealed class NoSearch : IEditorMatchCandidateSearch
    {
        public Task<EditorMatchSearchOutcome> SearchAsync(Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct) =>
            Task.FromResult(new EditorMatchSearchOutcome(null, "No search in this test."));
    }

    private sealed class NoVideos : IVideoIdentifyWriter
    {
        public Task<VideoIdentifyOutcome> LookupAsync(Guid entityId, string? pasted, CancellationToken ct = default) =>
            Task.FromResult(new VideoIdentifyOutcome(VideoIdentifyStatus.NotFound, "No video."));

        public Task<VideoIdentifyOutcome> ApplyAsync(Guid entityId, string? pasted, CancellationToken ct = default) => LookupAsync(entityId, pasted, ct);
    }
}
