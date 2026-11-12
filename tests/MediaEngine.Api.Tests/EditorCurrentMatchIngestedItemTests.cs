using System.Net;
using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Intelligence;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task AfterAMatchRemoval_TheItemReadsNotMatched_ButKeepsItsWikidataId()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        var asset = _world.File(movie, "Arrival (2016).mkv");
        await IngestAsync(asset, WellKnownProviders.Tmdb,
            (MetadataFieldConstants.Title, "Arrival"), (BridgeIdKeys.TmdbId, "329865"), (BridgeIdKeys.WikidataQid, "Q20814705"));
        Assert.Equal(EditorCurrentMatchStates.Matched, (await Service().ReadAsync(movie))!.State);

        var removal = new EditSessionService([new MatchRemovalEditSessionChangeHandler(RemovalScopes())], NullLogger<EditSessionService>.Instance);
        var saved = await removal.RunAsync(new EditSessionRequest
        {
            Changes = [new EditSessionChange { Id = "remove", Kind = EditSessionChangeKinds.MatchRemoval, EntityId = movie }],
        }, new Dictionary<Guid, string>());

        Assert.True(saved.DataSaved);
        var match = await Service().ReadAsync(movie);
        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
        Assert.Null(match.Record);
    }

    [Fact]
    public async Task ABookWithAnEpubAndAnAudiobook_ShowsTheWorksMatch_WhicheverFileComesFirst()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var epub = _world.File(book, "Dune.epub");
        var audiobook = _world.File(book, "Dune.m4b");
        _world.Canonical(book, BridgeIdKeys.AppleBooksId, "work-level-id");
        _world.Canonical(epub, BridgeIdKeys.AppleBooksId, "epub-id");
        _world.Canonical(audiobook, BridgeIdKeys.AppleBooksId, "audiobook-id");

        var match = await Service().ReadAsync(book);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("apple_books", match.Record!.ProviderId);
        Assert.Equal("work-level-id", match.Record.ProviderItemId);
    }

    [Fact]
    public async Task ABookInsideASeries_ThatIngestionMatched_ReadsMatchedInTheNavigationRead()
    {
        var series = _world.Work("Books", "parent", "Dune Chronicles");
        var matchedBook = _world.Work("Books", "standalone", "Dune", series, 1);
        var unmatchedBook = _world.Work("Books", "standalone", "Dune Messiah", series, 2);
        var asset = _world.File(matchedBook, "Dune.epub");
        _world.File(unmatchedBook, "Dune Messiah.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Dune"), (BridgeIdKeys.AppleBooksId, "1234567890"));

        var nodes = (await Navigation().GetNavigatorAsync(matchedBook, CancellationToken.None))!.Nodes;

        Assert.NotEqual("unmatched", Assert.Single(nodes, node => node.NodeId == matchedBook).MatchState);
        Assert.Equal("unmatched", Assert.Single(nodes, node => node.NodeId == unmatchedBook).MatchState);
    }

    [Fact]
    public async Task AMovieIngestionMatched_ReadsMatchedInTheNavigationRead_AndAnUnmatchedOneStaysUnmatched()
    {
        var matchedMovie = _world.Work("Movies", "standalone", "Arrival");
        await IngestAsync(_world.File(matchedMovie, "Arrival (2016).mkv"), WellKnownProviders.Tmdb,
            (MetadataFieldConstants.Title, "Arrival"), (BridgeIdKeys.TmdbId, "329865"));
        var unmatchedMovie = _world.Work("Movies", "standalone", "Home Video");
        _world.File(unmatchedMovie, "Home Video.mkv");

        var matched = (await Navigation().GetNavigatorAsync(matchedMovie, CancellationToken.None))!.Nodes;
        var unmatched = (await Navigation().GetNavigatorAsync(unmatchedMovie, CancellationToken.None))!.Nodes;

        Assert.NotEqual("unmatched", Assert.Single(matched, node => node.NodeId == matchedMovie).MatchState);
        Assert.Equal("unmatched", Assert.Single(unmatched, node => node.NodeId == unmatchedMovie).MatchState);
    }

    [Fact]
    public async Task AnEpisodeIngestionMatched_ReadsMatchedInTheNavigationRead_WithoutMatchingItsSiblingsOrItsShow()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var matchedEpisode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var unmatchedEpisode = _world.Work("TV", "child", "Half Loop", season, 2);
        await IngestAsync(_world.File(matchedEpisode, "Severance S01E01.mkv"), WellKnownProviders.Tvdb,
            (MetadataFieldConstants.Title, "Good News About Hell"), (BridgeIdKeys.TvdbEpisodeId, "8000001"));
        _world.File(unmatchedEpisode, "Severance S01E02.mkv");

        var nodes = (await Navigation().GetNavigatorAsync(matchedEpisode, CancellationToken.None))!.Nodes;

        Assert.NotEqual("unmatched", Assert.Single(nodes, node => node.NodeId == matchedEpisode).MatchState);
        Assert.Equal("unmatched", Assert.Single(nodes, node => node.NodeId == unmatchedEpisode).MatchState);
        Assert.Equal("unmatched", Assert.Single(nodes, node => node.NodeId == show).MatchState);
    }

    // The header says "matched" by the Match tab's own rule (EditorMatchRule), so it cannot disagree with the tab before the tab is opened. The family
    // dropdown's mark keeps its wider meaning (a Wikidata id, or what an episode inherits from its show), which is why the header has its own answer.

    [Fact]
    public async Task AWikidataOnlyItem_IsNotProviderMatched_InTheHeaderOrTheMatchTab_WhileTheDropdownStillCountsItsId()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        _world.Canonical(movie, BridgeIdKeys.WikidataQid, "Q20814705");

        var header = await _world.Header().GetHeaderAsync(movie, null);
        var match = await Service().ReadAsync(movie);
        var node = Assert.Single((await Navigation().GetNavigatorAsync(movie, CancellationToken.None))!.Nodes, n => n.NodeId == movie);

        Assert.False(header!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.NotMatched, match!.State);
        Assert.NotEqual("unmatched", node.MatchState);
    }

    [Fact]
    public async Task AnEpisodeUnderAMatchedShow_IsNotProviderMatched_ButItsSeasonAndShowAre_AndTheDropdownStillLetsTheEpisodeInherit()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        _world.File(episode, "Severance S01E01.mkv");
        _world.Canonical(show, BridgeIdKeys.TvdbId, "371980");

        var episodeHeader = await _world.Header().GetHeaderAsync(episode, null);
        var seasonHeader = await _world.Header().GetHeaderAsync(season, null);
        var showHeader = await _world.Header().GetHeaderAsync(show, null);
        var nodes = (await Navigation().GetNavigatorAsync(episode, CancellationToken.None))!.Nodes;

        Assert.False(episodeHeader!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.NotMatched, (await Service().ReadAsync(episode))!.State);
        Assert.NotEqual("unmatched", Assert.Single(nodes, node => node.NodeId == episode).MatchState);
        Assert.True(seasonHeader!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.Matched, (await Service().ReadAsync(season))!.State);
        Assert.True(showHeader!.ProviderMatched);
    }

    [Fact]
    public async Task AnEpisodeWithItsOwnTheTvdbId_OnItsFile_IsProviderMatched_InTheHeaderAndTheMatchTab()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var asset = _world.File(episode, "Severance S01E01.mkv");
        _world.Canonical(asset, BridgeIdKeys.TvdbEpisodeId, "8000001");

        var header = await _world.Header().GetHeaderAsync(episode, null);

        Assert.True(header!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.Matched, (await Service().ReadAsync(episode))!.State);
    }

    [Fact]
    public async Task ABookWithOnlyAnIsbnOnItsFile_IsProviderMatched_InTheHeaderAsInTheMatchTab()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        _world.Canonical(asset, BridgeIdKeys.Isbn13, "9780441172719");

        var header = await _world.Header().GetHeaderAsync(book, null);

        Assert.True(header!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.Matched, (await Service().ReadAsync(book))!.State);
    }

    [Fact]
    public async Task AContainerIsNeverReadFromTheFileUnderIt_ByTheHeaderOrTheMatchTab()
    {
        var series = _world.Work("Books", "parent", "Dune Chronicles");
        var book = _world.Work("Books", "standalone", "Dune", series, 1);
        var asset = _world.File(book, "Dune.epub");
        _world.Canonical(asset, BridgeIdKeys.AppleBooksId, "1234567890");

        var header = await _world.Header().GetHeaderAsync(series, null);

        Assert.False(header!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.NotMatched, (await Service().ReadAsync(series))!.State);
    }

    private MediaEditorNavigationReadService Navigation() =>
        new(_world.Database, null!, new HierarchyAlignmentService(_world.Database, null!));

    private IServiceScopeFactory RemovalScopes() => new ServiceCollection()
        .AddSingleton<IMetadataEditorRepository>(new MetadataEditorRepository(_world.Database))
        .AddSingleton<ICanonicalValueRepository>(new CanonicalValueRepository(_world.Database))
        .AddSingleton<IMetadataClaimRepository>(new MetadataClaimRepository(_world.Database))
        .AddSingleton<IBridgeIdRepository>(new BridgeIdRepository(_world.Database))
        .AddSingleton<IWorkRepository>(new WorkRepository(_world.Database))
        .AddSingleton<ISystemActivityRepository>(new SystemActivityRepository(_world.Database))
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

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
