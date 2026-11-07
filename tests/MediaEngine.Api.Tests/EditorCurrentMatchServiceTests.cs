using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match tab's first view for a matched item (universal editor v5, WP3, 3c "Existing match"): the matched record is found by the id the
/// Engine stored, never by a text search. A hand-written handler stands in for TheTVDB and records every call it gets.
/// </summary>
public sealed class EditorCurrentMatchServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Game of Thrones");
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"tuvima_current_match_{Guid.NewGuid():N}");
    private readonly MemoryCache _previews = new(new MemoryCacheOptions());

    [Fact]
    public async Task AMatchedShow_IsReadFromTheProviderByItsTheTvdbId_WithNoTextSearch()
    {
        var handler = new TvdbSpy();
        var show = _world.Work("TV", "parent", "Game of Thrones");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");
        _world.Canonical(show, BridgeIdKeys.WikidataQid, "Q23572");

        var match = await Service(handler, connected: true).ReadAsync(show);

        Assert.NotNull(match);
        Assert.Equal(EditorCurrentMatchStates.Matched, match.State);
        var record = match.Record!;
        Assert.Equal("tvdb", record.ProviderId);
        Assert.Equal("TheTVDB", record.ProviderName);
        Assert.Equal("121361", record.ProviderItemId);
        Assert.Equal("Show", record.Kind);
        Assert.Equal("Game of Thrones", record.Title);
        Assert.Equal("2011", record.Year);
        Assert.Matches($"^/metadata/{show:D}/tvdb-match/previews/[0-9a-f]{{32}}$", record.ImageUrl);
        Assert.DoesNotContain("thetvdb.com", record.ImageUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://thetvdb.com/dereferrer/series/121361", record.SourceUrl);
        Assert.Equal(EditorCurrentMatchSources.Provider, record.Source);
        Assert.Equal("Q23572", match.WikidataQid);
        Assert.Equal("https://www.wikidata.org/wiki/Q23572", match.WikidataUrl);
        Assert.Null(match.Note);

        // The record was asked for by its id, and no search of any kind was made.
        Assert.Contains(handler.Paths, path => path.EndsWith("/series/121361/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMatchedEpisode_IsReadByItsEpisodeId_AndSaysWhyItFitsTheFile()
    {
        var handler = new TvdbSpy();
        var show = _world.Work("TV", "parent", "Game of Thrones");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "The Kingsroad", season, 2);
        _world.File(episode, "Game of Thrones S01E02.mkv");
        _world.Canonical(episode, BridgeIdKeys.TvdbEpisodeId, "3254641");
        _world.Canonical(episode, BridgeIdKeys.TvdbId, "121361");
        _world.Canonical(episode, "year", "2011");
        _world.Canonical(show, "title", "Game of Thrones");
        await InsertClaimAsync(show, "title", "Game of Thrones", 0.93);

        var match = await Service(handler, connected: true).ReadAsync(episode);

        var record = match!.Record!;
        Assert.Equal("Episode", record.Kind);
        Assert.Equal("3254641", record.ProviderItemId);
        Assert.Equal("A Different Name", record.Title);
        Assert.Equal("Game of Thrones S01E02.mkv", match.FileName);
        Assert.Contains(handler.Paths, path => path.EndsWith("/episodes/3254641/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/search", StringComparison.Ordinal));

        var season1 = Assert.Single(match.Why, row => row.Field == "season_number");
        Assert.Equal("1", season1.YourFile);
        Assert.Equal("1", season1.MatchedRecord);
        Assert.Equal(EditorMatchEvidenceStates.Agrees, season1.State);
        var number = Assert.Single(match.Why, row => row.Field == "episode_number");
        Assert.Equal("2", number.YourFile);
        Assert.Equal(EditorMatchEvidenceStates.Agrees, number.State);
        var title = Assert.Single(match.Why, row => row.Field == "title");
        Assert.Equal("Game of Thrones", title.YourFile);
        Assert.Equal("Game of Thrones", title.MatchedRecord);
        Assert.Equal(EditorMatchEvidenceStates.Agrees, title.State);
        Assert.Equal(0.93, title.Score);
    }

    [Fact]
    public async Task AnEpisodesYear_IsComparedWithTheShowsYear_NotTheEpisodesAirYear()
    {
        var show = _world.Work("TV", "parent", "Game of Thrones");
        var season = _world.Work("TV", "parent", "Season 2", show, 2);
        var episode = _world.Work("TV", "child", "The North Remembers", season, 1);
        _world.File(episode, "Game of Thrones (2011) S02E01.mkv");
        _world.Canonical(episode, BridgeIdKeys.TvdbEpisodeId, "3254641");
        _world.Canonical(episode, "year", "2012");
        _world.Canonical(show, "year", "2011");
        _world.Canonical(show, "title", "Game of Thrones");

        var match = await Service(new TvdbSpy(), connected: false).ReadAsync(episode);

        var year = Assert.Single(match!.Why, row => row.Field == "year");
        Assert.Equal("2011", year.YourFile);
        Assert.Equal("2011", year.MatchedRecord);
        Assert.Equal(EditorMatchEvidenceStates.Agrees, year.State);
    }

    [Fact]
    public async Task AFilmsDirector_IsTheCreatorOfItsMatchedRecord()
    {
        var film = _world.Work("Movies", "standalone", "Arrival");
        _world.Canonical(film, BridgeIdKeys.TmdbId, "329865");
        _world.Canonical(film, "director", "Denis Villeneuve");

        var match = await Service(new TvdbSpy(), connected: true).ReadAsync(film);

        Assert.Equal("Denis Villeneuve", match!.Record!.Creator);
    }

    [Fact]
    public async Task AStoredTvdbPicture_IsAlsoShownThroughThePreview_NeverAsTheTvdbAddress()
    {
        var show = _world.Work("TV", "parent", "Game of Thrones");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");
        _world.Canonical(show, "cover_url", "https://artworks.thetvdb.com/series/121361/posters/1.jpg");

        var match = await Service(new TvdbSpy(), connected: false).ReadAsync(show);

        Assert.Equal(EditorCurrentMatchSources.Stored, match!.Record!.Source);
        Assert.Matches($"^/metadata/{show:D}/tvdb-match/previews/[0-9a-f]{{32}}$", match.Record.ImageUrl);
    }

    [Fact]
    public async Task AFactTheFileDoesNotSay_IsNotChecked_AndNeverCountsAgainstTheMatch()
    {
        var show = _world.Work("TV", "parent", "Game of Thrones");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");

        var match = await Service(new TvdbSpy(), connected: true).ReadAsync(show);

        var year = Assert.Single(match!.Why, row => row.Field == "year");
        Assert.Null(year.YourFile);
        Assert.Equal("2011", year.MatchedRecord);
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, year.State);
    }

    [Fact]
    public async Task WhenTheProviderIsNotConnected_TheStoredRecordIsShown_WithAPlainNote_AndNoCallIsMade()
    {
        var handler = new TvdbSpy();
        var show = _world.Work("TV", "parent", "Game of Thrones");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");
        _world.Canonical(show, "year", "2011");

        var match = await Service(handler, connected: false).ReadAsync(show);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("Game of Thrones", match.Record!.Title);
        Assert.Equal("2011", match.Record.Year);
        Assert.Equal(EditorCurrentMatchSources.Stored, match.Record.Source);
        Assert.Contains("not connected", match.Note, StringComparison.Ordinal);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task WhenTheProviderDoesNotAnswer_TheStoredRecordIsShown_WithAPlainNote()
    {
        var show = _world.Work("TV", "parent", "Game of Thrones");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");

        var match = await Service(new TvdbSpy { Fail = true }, connected: true).ReadAsync(show);

        Assert.Equal(EditorCurrentMatchSources.Stored, match!.Record!.Source);
        Assert.Equal("Game of Thrones", match.Record.Title);
        Assert.Contains("did not answer", match.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilmMatchedOnTmdb_IsShownFromWhatTheEngineStored_WithItsSourceLink()
    {
        var handler = new TvdbSpy();
        var film = _world.Work("Movies", "standalone", "Arrival");
        _world.Canonical(film, BridgeIdKeys.TmdbId, "329865");
        _world.Canonical(film, "year", "2016");

        var match = await Service(handler, connected: true).ReadAsync(film);

        var record = match!.Record!;
        Assert.Equal("tmdb", record.ProviderId);
        Assert.Equal("Film", record.Kind);
        Assert.Equal("Arrival", record.Title);
        Assert.Equal("https://www.themoviedb.org/movie/329865", record.SourceUrl);
        Assert.Equal(EditorCurrentMatchSources.Stored, record.Source);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task AnEpisodeWhoseMatchWasRemoved_ReadsNotMatchedYet_EvenWhenItsShowStillCarriesTheShowsId()
    {
        var handler = new TvdbSpy();
        var show = _world.Work("TV", "parent", "Game of Thrones");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "The Kingsroad", season, 2);
        _world.File(episode, "Game of Thrones S01E02.mkv");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");

        var match = await Service(handler, connected: true).ReadAsync(episode);

        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
        Assert.Null(match.Record);
        Assert.Empty(match.Why);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task ASeasonWhoseMatchWasRemoved_ReadsNotMatchedYet_ButTheShowItselfStaysMatched()
    {
        var show = _world.Work("TV", "parent", "Game of Thrones");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        _world.Canonical(show, BridgeIdKeys.TvdbId, "121361");

        Assert.Equal(EditorCurrentMatchStates.NotMatched, (await Service(new TvdbSpy(), connected: true).ReadAsync(season))!.State);
        Assert.Equal(EditorCurrentMatchStates.Matched, (await Service(new TvdbSpy(), connected: true).ReadAsync(show))!.State);
    }

    [Fact]
    public async Task AnItemWithNoProviderId_IsNotMatched_AndHasNoRecord()
    {
        var film = _world.Work("Movies", "standalone", "Arrival");

        var match = await Service(new TvdbSpy(), connected: true).ReadAsync(film);

        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
        Assert.Null(match.Record);
        Assert.Empty(match.Why);
    }

    [Fact]
    public async Task AnUnknownItem_HasNoCurrentMatch()
    {
        Assert.Null(await Service(new TvdbSpy(), connected: true).ReadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AMatchedMovie_SaysHowStronglyItFits_WithAVerdictOnEveryRow_AndAMeasuredRuntimeRow()
    {
        var movie = _world.Work("Movies", "standalone", "The Matrix");
        _world.File(movie, "The.Matrix.1999.1080p.BluRay.mkv", durationSeconds: 8160);
        _world.Canonical(movie, BridgeIdKeys.TmdbId, "603");
        _world.Canonical(movie, MetadataFieldConstants.Year, "1999");
        _world.Canonical(movie, MetadataFieldConstants.Runtime, "136");

        var match = await Service(new TvdbSpy(), connected: false).ReadAsync(movie);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("TMDB", match.Record!.ProviderName);
        Assert.Equal(EditorMatchVerdicts.Strong, match.Verdict);
        Assert.False(match.Pinned);
        Assert.All(match.Why.Where(row => row.State != EditorMatchEvidenceStates.NotChecked), row => Assert.Equal(EditorMatchVerdicts.Strong, row.Verdict));
        var runtime = Assert.Single(match.Why, row => row.Field == "runtime");
        Assert.Equal(EditorMatchEvidenceStates.Agrees, runtime.State);
        Assert.Equal("2:16:00", runtime.YourFile);
        Assert.Equal("136 min", runtime.MatchedRecord);
        Assert.Contains("rounded", runtime.Note);
    }

    [Fact]
    public async Task AMatchWhoseTitleDiffers_ReadsAsCheck_AndAPersonsMatchIsPinned()
    {
        var movie = _world.Work("Movies", "standalone", "Totally Different");
        _world.File(movie, "The.Matrix.1999.1080p.BluRay.mkv");
        _world.Canonical(movie, BridgeIdKeys.TmdbId, "999");
        _world.Canonical(movie, MetadataFieldConstants.Year, "1999");
        _world.Canonical(movie, MetadataFieldConstants.MatchPinned, "true");

        var match = await Service(new TvdbSpy(), connected: false).ReadAsync(movie);

        Assert.Equal(EditorMatchVerdicts.Check, match!.Verdict);
        Assert.True(match.Pinned);
        Assert.Equal(EditorMatchVerdicts.Check, match.Why.Single(row => row.Field == "title").Verdict);
    }

    private EditorCurrentMatchService Service(TvdbSpy handler, bool connected)
    {
        Directory.CreateDirectory(_config);
        var loader = new ConfigurationDirectoryLoader(_config);
        if (connected)
        {
            var path = Path.Combine(FindRepoRoot(), "config", "providers", "tvdb.json");
            var provider = JsonSerializer.Deserialize<MediaEngine.Domain.Configuration.ProviderConfiguration>(
                File.ReadAllText(path), new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })!;
            provider.HttpClient ??= new HttpClientConfig();
            provider.HttpClient.ApiKey = "installation-key";
            provider.RateLimit = new ProviderRateLimitConfiguration { RequestsPerSecond = 1000, Burst = 1000, MaxConcurrency = 4 };
            loader.SaveProvider(provider);
        }

        var client = new ProviderApiClient(loader, new Factory(handler), new ProviderRateLimiterCoordinator());
        return new EditorCurrentMatchService(
            new MetadataEditorRepository(_world.Database),
            new CanonicalValueRepository(_world.Database),
            new MetadataClaimRepository(_world.Database),
            client,
            _previews,
            new EditorMatchVerdictCalculator(new MatchVerdictSettings()),
            _world.QuickFacts,
            NullLogger<EditorCurrentMatchService>.Instance);
    }

    private async Task InsertClaimAsync(Guid entityId, string key, string value, double confidence)
    {
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT OR IGNORE INTO metadata_providers (id, name, version) VALUES (@id, 'tvdb', '1');",
                new { id = WellKnownProviders.Tvdb });
        }

        await new MetadataClaimRepository(_world.Database).InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(), EntityId = entityId, ProviderId = WellKnownProviders.Tvdb, ClaimKey = key, ClaimValue = value,
                Confidence = confidence, ClaimedAt = DateTimeOffset.UtcNow,
            },
        ]);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    public void Dispose()
    {
        _previews.Dispose();
        _world.Dispose();
        try
        {
            Directory.Delete(_config, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The OS temp sweep removes what is left.
        }
    }

    private sealed class Factory(TvdbSpy handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Stands in for TheTVDB: answers the lookups by id, and keeps every address it was asked for.</summary>
    private sealed class TvdbSpy : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<string> Paths { get; } = [];

        public bool Fail { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_gate)
            {
                Paths.Add(path);
            }

            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"data":{"token":"bearer-token"}}"""));
            }

            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (path.Contains("/translations/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"data":{"name":"Game of Thrones"}}"""));
            }

            if (path.EndsWith("/series/121361/extended", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"data":{"id":121361,"name":"Game of Thrones","year":"2011","image":"https://artworks.thetvdb.com/series/121361.jpg","overview":"Nine noble families fight for control."}}"""));
            }

            if (path.EndsWith("/episodes/3254641/extended", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"data":{"id":3254641,"name":"A Different Name","aired":"2011-04-24","seasonNumber":1,"number":2,"image":"https://artworks.thetvdb.com/episode/3254641.jpg","overview":"The king travels."}}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
