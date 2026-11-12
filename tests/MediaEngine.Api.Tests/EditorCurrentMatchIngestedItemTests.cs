using System.Net;
using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Intelligence;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 871: every item that ingestion had matched said "Not matched yet" in the Match tab. These tests record the match the way ingestion
/// does (the lineage-aware persist-and-score step the retail match worker calls, on a real temp SQLite data store) and then read it back
/// through the Match tab's Engine read, for the id the editor sends (the item's Work). A fact about the file itself (a film's TMDB id, a
/// book's ISBN, an episode's TheTVDB id) is stored on the file's asset, never on the Work, so the read has to look there too.
/// </summary>
public sealed class EditorCurrentMatchIngestedItemTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Library");
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"tuvima_ingested_match_{Guid.NewGuid():N}");
    private readonly MemoryCache _previews = new(new MemoryCacheOptions());

    [Fact]
    public async Task AMovieIngestionMatchedOnTmdb_IsShownAsMatched_ByItsWorkId()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        var asset = _world.File(movie, "Arrival (2016).mkv");
        await IngestAsync(asset, WellKnownProviders.Tmdb,
            (MetadataFieldConstants.Title, "Arrival"), (MetadataFieldConstants.Year, "2016"), (BridgeIdKeys.TmdbId, "329865"));

        var match = await Service().ReadAsync(movie);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("tmdb", match.Record!.ProviderId);
        Assert.Equal("329865", match.Record.ProviderItemId);
        Assert.Equal("Film", match.Record.Kind);
    }

    [Fact]
    public async Task ABookIngestionMatchedOnAppleBooks_IsShownAsMatched_ByItsWorkId()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Dune"), (MetadataFieldConstants.Author, "Frank Herbert"), (BridgeIdKeys.AppleBooksId, "1234567890"));

        var match = await Service().ReadAsync(book);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("apple_books", match.Record!.ProviderId);
        Assert.Equal("1234567890", match.Record.ProviderItemId);
    }

    [Fact]
    public async Task ABookIngestionMatchedOnlyByIsbn_IsShownAsMatched_ByItsWorkId()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        await IngestAsync(asset, WellKnownProviders.OpenLibrary,
            (MetadataFieldConstants.Title, "Dune"), (BridgeIdKeys.Isbn13, "9780441172719"));

        var match = await Service().ReadAsync(book);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("isbn", match.Record!.ProviderId);
    }

    [Fact]
    public async Task ATvEpisodeIngestionMatchedOnTheTvdb_IsShownAsMatched_ByItsWorkId()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var asset = _world.File(episode, "Severance S01E01.mkv");
        await IngestAsync(asset, WellKnownProviders.Tvdb,
            (MetadataFieldConstants.Title, "Good News About Hell"), (MetadataFieldConstants.ShowName, "Severance"),
            (BridgeIdKeys.TvdbId, "371980"), (BridgeIdKeys.TvdbEpisodeId, "8000001"));

        var match = await Service().ReadAsync(episode);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("tvdb", match.Record!.ProviderId);
        Assert.Equal("Episode", match.Record.Kind);
        Assert.Equal("8000001", match.Record.ProviderItemId);
    }

    [Fact]
    public async Task ATvShowIngestionMatchedOnTheTvdb_IsShownAsTheShow_NotAsOneOfItsEpisodes()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var asset = _world.File(episode, "Severance S01E01.mkv");
        await IngestAsync(asset, WellKnownProviders.Tvdb,
            (MetadataFieldConstants.Title, "Good News About Hell"), (MetadataFieldConstants.ShowName, "Severance"),
            (BridgeIdKeys.TvdbId, "371980"), (BridgeIdKeys.TvdbEpisodeId, "8000001"));

        var match = await Service().ReadAsync(show);

        // The show's page must never borrow its first episode's id from the episode's file.
        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("Show", match.Record!.Kind);
        Assert.Equal("371980", match.Record.ProviderItemId);
    }

    [Fact]
    public async Task AnUnmatchedTvShow_StaysNotMatched_EvenWhenAnEpisodeUnderItIsMatched()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var asset = _world.File(episode, "Severance S01E01.mkv");
        _world.Canonical(asset, BridgeIdKeys.TvdbEpisodeId, "8000001");

        var match = await Service().ReadAsync(show);

        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
    }

    [Fact]
    public async Task AFileIngestionFoundNoMatchFor_StillSaysNotMatched()
    {
        var movie = _world.Work("Movies", "standalone", "Home Video");
        var asset = _world.File(movie, "Home Video.mkv");
        await IngestAsync(asset, WellKnownProviders.Tmdb, (MetadataFieldConstants.Title, "Home Video"));

        var match = await Service().ReadAsync(movie);

        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
    }

    // Records a match the way the retail match worker does: the lineage of the file's asset, then the lineage-aware persist and score.
    private async Task IngestAsync(Guid assetId, Guid providerId, params (string Key, string Value)[] facts)
    {
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute(
                "INSERT OR IGNORE INTO metadata_providers (id, name, version) VALUES (@id, @name, '1');",
                new { id = providerId, name = $"provider-{providerId:N}" });
        }

        Directory.CreateDirectory(_config);
        var loader = new ConfigurationDirectoryLoader(_config);
        var lineage = await new WorkRepository(_world.Database).GetLineageByAssetAsync(assetId);
        Assert.NotNull(lineage);
        await ScoringHelper.PersistAndScoreWithLineageDetailedAsync(
            assetId,
            [.. facts.Select(fact => new ProviderClaim(fact.Key, fact.Value, 0.95))],
            providerId,
            lineage,
            new MetadataClaimRepository(_world.Database),
            new CanonicalValueRepository(_world.Database),
            new PriorityCascadeEngine(loader, NullLogger<PriorityCascadeEngine>.Instance),
            loader,
            [],
            CancellationToken.None,
            arrayRepo: new CanonicalValueArrayRepository(_world.Database),
            logger: NullLogger.Instance);
    }

    private EditorCurrentMatchService Service()
    {
        Directory.CreateDirectory(_config);
        var client = new ProviderApiClient(
            new ConfigurationDirectoryLoader(_config), new Factory(), new ProviderRateLimiterCoordinator());
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

    // TheTVDB is never connected here, so the Engine must not make a call; any call is a failure.
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse(), disposeHandler: true);
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
