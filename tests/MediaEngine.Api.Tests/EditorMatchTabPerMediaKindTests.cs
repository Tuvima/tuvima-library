using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Intelligence;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Tuvima.Wikidata;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 1065 (universal editor v8 D10): the Match tab missed a Wikidata match that Library had made (the book "Gods of Risk" in The Expanse), and a
/// provider match showed neither its cover or album art nor an "Open in provider" link. Every fact here is recorded the way ingestion records it (the
/// lineage-aware persist and score step, on a real temp SQLite data store) and read back through the Match tab's two Engine reads, so the same routing
/// that put a fact on the file's asset or on the container decides where the reads must look.
/// Cause one: a book's, film's or comic's Wikidata id is a fact about the file and is stored on the file's asset, but the Wikidata side read the item's Work alone.
/// Cause two: a provider's picture is saved as "cover" (an address on the provider's own server) and the current match read only "cover_url", the file's own
/// art; the card also dropped the provider's page, and Apple Music's ids were not among the ids that make an item matched.
/// </summary>
public sealed class EditorMatchTabPerMediaKindTests : IDisposable
{
    private const string BookQid = "Q55641906";
    private const string AppleCover = "https://is1-ssl.mzstatic.com/image/thumb/Publication/v4/cover/600x600bb.jpg";

    private readonly EditorHeaderTestWorld _world = new("Library");
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"tuvima_match_tab_{Guid.NewGuid():N}");
    private readonly MemoryCache _previews = new(new MemoryCacheOptions());

    // ----- the Wikidata side -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Books")]
    [InlineData("Audiobooks")]
    [InlineData("Movies")]
    [InlineData("Comics")]
    public async Task AFileKeptWikidataLink_IsShownOnTheWikidataSide_ForEveryKindThatStoresItOnTheFile(string mediaType)
    {
        var work = _world.Work(mediaType, "standalone", "Gods of Risk");
        var asset = _world.File(work, "Gods of Risk.bin");
        await IngestAsync(asset, WellKnownProviders.Wikidata, (BridgeIdKeys.WikidataQid, BookQid));

        var identity = await WikidataService(mediaType).ReadIdentityAsync(work);

        Assert.Equal(BookQid, identity!.Qid);
        Assert.Equal("Gods of Risk", identity.Label);
        Assert.Equal("novella by James S. A. Corey", identity.Description);
        Assert.Equal($"https://www.wikidata.org/wiki/{BookQid}", identity.Url);
        Assert.False(identity.Offline);
    }

    [Fact]
    public async Task ABookInASeries_ShowsItsOwnWikidataMatch_NotTheSeriesAndNotNothing()
    {
        var series = _world.Work("Books", "parent", "The Expanse");
        var book = _world.Work("Books", "standalone", "Gods of Risk", series, 5);
        var asset = _world.File(book, "Gods of Risk.epub");
        await IngestAsync(asset, WellKnownProviders.Wikidata, (BridgeIdKeys.WikidataQid, BookQid));

        Assert.Equal(BookQid, (await WikidataService("Books").ReadIdentityAsync(book))!.Qid);
        Assert.Null((await WikidataService("Books").ReadIdentityAsync(series))!.Qid);
    }

    [Fact]
    public async Task AnAlbumsWikidataMatch_IsShownOnTheAlbum_WhereMusicStoresIt()
    {
        var album = _world.Work("Music", "parent", "Random Access Memories");
        var track = _world.Work("Music", "child", "Get Lucky", album, 8);
        var asset = _world.File(track, "08 Get Lucky.flac");
        await IngestAsync(asset, WellKnownProviders.Wikidata, (BridgeIdKeys.WikidataQid, BookQid));

        var identity = await WikidataService("Music").ReadIdentityAsync(album);

        Assert.Equal(BookQid, identity!.Qid);
        Assert.Equal("Gods of Risk", identity.Label);
    }

    [Fact]
    public async Task AShowsWikidataMatch_IsShownOnTheShow_WhereTvStoresIt()
    {
        var show = _world.Work("TV", "parent", "The Expanse");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Dulcinea", season, 1);
        var asset = _world.File(episode, "The Expanse S01E01.mkv");
        await IngestAsync(asset, WellKnownProviders.Wikidata, (BridgeIdKeys.WikidataQid, BookQid));

        Assert.Equal(BookQid, (await WikidataService("TV").ReadIdentityAsync(show))!.Qid);
    }

    [Fact]
    public async Task AWikidataLinkThePersonSet_StillWinsOverTheOneIngestionKeptOnTheFile()
    {
        var work = _world.Work("Books", "standalone", "Gods of Risk");
        var asset = _world.File(work, "Gods of Risk.epub");
        await IngestAsync(asset, WellKnownProviders.Wikidata, (BridgeIdKeys.WikidataQid, "Q1"));
        _world.Canonical(work, BridgeIdKeys.WikidataQid, BookQid);

        Assert.Equal(BookQid, (await WikidataService("Books").ReadIdentityAsync(work))!.Qid);
    }

    // ----- the provider card: cover or album art, and the provider's page -----------------------------------------------------------------

    [Fact]
    public async Task ABookMatchedOnAppleBooks_ShowsItsCover_AndItsPageAtAppleBooks()
    {
        var book = _world.Work("Books", "standalone", "Gods of Risk");
        var asset = _world.File(book, "Gods of Risk.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Gods of Risk"), (MetadataFieldConstants.Author, "James S. A. Corey"),
            (BridgeIdKeys.AppleBooksId, "1234567890"), (MetadataFieldConstants.Cover, AppleCover));

        var record = (await CurrentMatch().ReadAsync(book))!.Record!;

        Assert.StartsWith("data:image/jpeg;base64,", record.ImageUrl, StringComparison.Ordinal);
        Assert.Equal("https://books.apple.com/book/id1234567890", record.SourceUrl);
        Assert.Equal("Apple Books", record.ProviderName);
    }

    [Fact]
    public async Task AnAlbumMatchedOnAppleMusic_ShowsItsAlbumArt_AndItsPageAtAppleMusic()
    {
        var album = _world.Work("Music", "parent", "Random Access Memories");
        var track = _world.Work("Music", "child", "Get Lucky", album, 8);
        var asset = _world.File(track, "08 Get Lucky.flac");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Get Lucky"), (MetadataFieldConstants.Album, "Random Access Memories"),
            (BridgeIdKeys.AppleMusicId, "617154366"), (BridgeIdKeys.AppleMusicCollectionId, "617154241"),
            (MetadataFieldConstants.Cover, AppleCover));

        var match = await CurrentMatch().ReadAsync(album);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("Album", match.Record!.Kind);
        Assert.Equal("Apple Music", match.Record.ProviderName);
        Assert.Equal("https://music.apple.com/album/617154241", match.Record.SourceUrl);
        Assert.StartsWith("data:image/jpeg;base64,", match.Record.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASongMatchedOnAppleMusic_ShowsItsAlbumArt_AndItsPageAtAppleMusic()
    {
        var album = _world.Work("Music", "parent", "Random Access Memories");
        var track = _world.Work("Music", "child", "Get Lucky", album, 8);
        var asset = _world.File(track, "08 Get Lucky.flac");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Get Lucky"), (MetadataFieldConstants.Album, "Random Access Memories"),
            (BridgeIdKeys.AppleMusicId, "617154366"), (BridgeIdKeys.AppleMusicCollectionId, "617154241"),
            (MetadataFieldConstants.Cover, AppleCover));

        var match = await CurrentMatch().ReadAsync(track);

        Assert.Equal(EditorCurrentMatchStates.Matched, match!.State);
        Assert.Equal("Song", match.Record!.Kind);
        Assert.Equal("https://music.apple.com/song/617154366", match.Record.SourceUrl);
        Assert.StartsWith("data:image/jpeg;base64,", match.Record.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlbumWithOnlyAppleMusicIds_IsMatched_InTheHeaderAsInTheMatchTab()
    {
        var album = _world.Work("Music", "parent", "Random Access Memories");
        var track = _world.Work("Music", "child", "Get Lucky", album, 8);
        var asset = _world.File(track, "08 Get Lucky.flac");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Get Lucky"), (BridgeIdKeys.AppleMusicCollectionId, "617154241"));

        Assert.True((await _world.Header().GetHeaderAsync(album, null))!.ProviderMatched);
        Assert.Equal(EditorCurrentMatchStates.Matched, (await CurrentMatch().ReadAsync(album))!.State);
    }

    [Fact]
    public async Task AFilmMatchedOnTmdb_ShowsItsPoster_AndItsPageAtTmdb()
    {
        var film = _world.Work("Movies", "standalone", "Arrival");
        var asset = _world.File(film, "Arrival (2016).mkv");
        await IngestAsync(asset, WellKnownProviders.Tmdb,
            (MetadataFieldConstants.Title, "Arrival"), (MetadataFieldConstants.Year, "2016"), (BridgeIdKeys.TmdbId, "329865"),
            (MetadataFieldConstants.Cover, "https://image.tmdb.org/t/p/w500/poster.jpg"));

        var record = (await CurrentMatch().ReadAsync(film))!.Record!;

        Assert.StartsWith("data:image/jpeg;base64,", record.ImageUrl, StringComparison.Ordinal);
        Assert.Equal("https://www.themoviedb.org/movie/329865", record.SourceUrl);
        Assert.Equal("TMDB", record.ProviderName);
    }

    [Fact]
    public async Task AShowMatchedOnTheTvdb_ShowsItsPosterThroughTheEnginesPreview_AndItsPageAtTheTvdb()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var asset = _world.File(episode, "Severance S01E01.mkv");
        await IngestAsync(asset, WellKnownProviders.Tvdb,
            (MetadataFieldConstants.Title, "Good News About Hell"), (MetadataFieldConstants.ShowName, "Severance"),
            (BridgeIdKeys.TvdbId, "371980"), (MetadataFieldConstants.Cover, "https://artworks.thetvdb.com/banners/posters/371980-1.jpg"));

        var record = (await CurrentMatch().ReadAsync(show))!.Record!;

        Assert.Matches($"^/metadata/{show:D}/tvdb-match/previews/[0-9a-f]{{32}}$", record.ImageUrl);
        Assert.Equal("https://thetvdb.com/dereferrer/series/371980", record.SourceUrl);
    }

    [Fact]
    public async Task TheProvidersPicture_IsShownRatherThanTheFilesOwnCover_WhenBothAreSaved()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Dune"), (BridgeIdKeys.AppleBooksId, "1234567890"),
            (MetadataFieldConstants.CoverUrl, $"/stream/{asset:D}/cover"), (MetadataFieldConstants.Cover, AppleCover));

        var record = (await CurrentMatch().ReadAsync(book))!.Record!;

        Assert.StartsWith("data:image/jpeg;base64,", record.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheProvidersPictureCannotBeFetched_TheFilesOwnCoverIsShown_NeverAProviderAddress()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Dune"), (BridgeIdKeys.AppleBooksId, "1234567890"),
            (MetadataFieldConstants.CoverUrl, $"/stream/{asset:D}/cover"), (MetadataFieldConstants.Cover, "https://covers.example.test/dune.jpg"));

        var record = (await CurrentMatch().ReadAsync(book))!.Record!;

        Assert.Equal($"/stream/{asset:D}/cover", record.ImageUrl);
    }

    [Fact]
    public async Task AProviderPictureOnAnUnapprovedHost_IsNeverFetched_AndNeverSentToTheBrowser()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        var asset = _world.File(book, "Dune.epub");
        await IngestAsync(asset, WellKnownProviders.AppleApi,
            (MetadataFieldConstants.Title, "Dune"), (BridgeIdKeys.AppleBooksId, "1234567890"), (MetadataFieldConstants.Cover, "https://covers.example.test/dune.jpg"));
        var pictures = new PictureHandler();

        var record = (await CurrentMatch(pictures).ReadAsync(book))!.Record!;

        Assert.Null(record.ImageUrl);
        Assert.Equal(0, pictures.Requests);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------------

    private EditorCurrentMatchService CurrentMatch(PictureHandler? pictures = null)
    {
        Directory.CreateDirectory(_config);
        var client = new ProviderApiClient(
            new ConfigurationDirectoryLoader(_config), new Factory(pictures ?? new PictureHandler()), new ProviderRateLimiterCoordinator());
        return new EditorCurrentMatchService(
            new MetadataEditorRepository(_world.Database),
            new CanonicalValueRepository(_world.Database),
            new MetadataClaimRepository(_world.Database),
            client,
            _previews,
            new EditorMatchVerdictCalculator(new MatchVerdictSettings()),
            _world.QuickFacts,
            NullLogger<EditorCurrentMatchService>.Instance,
            httpFactory: new Factory(pictures ?? new PictureHandler()));
    }

    private EditorWikidataMatchService WikidataService(string mediaType)
    {
        var reconciler = new WikidataReconciler(
            new HttpClient(new FakeWikidata(), disposeHandler: true), new WikidataReconcilerOptions { UserAgent = "Tuvima-Tests/1.0", MaxLag = 0 });
        return new EditorWikidataMatchService(
            reconciler.Reconcile, reconciler.Entities, new FixedKind(mediaType), new MetadataEditorRepository(_world.Database),
            new CanonicalValueRepository(_world.Database), new OpenGuard(), new WikidataMatchConfigurationProvider(() => null),
            NullLogger<EditorWikidataMatchService>.Instance);
    }

    // Records a fact the way ingestion does: the lineage of the file's asset, then the lineage-aware persist and score.
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

    private sealed class FixedKind(string mediaType) : IEditorMatchKindResolver
    {
        public Task<string?> ResolveAsync(Guid entityId, CancellationToken ct) => Task.FromResult<string?>(mediaType switch
        {
            "Books" => "book",
            "Audiobooks" => "audiobook",
            "Movies" => "movie",
            "Comics" => "comic",
            "Music" => "album",
            _ => "tv_show",
        });
    }

    private sealed class OpenGuard : IExternalLookupGuard
    {
        public bool BlocksLibrary(string? libraryId) => false;

        public IReadOnlyCollection<string> GetBlockedLibraryIds() => [];

        public Task<bool> BlocksEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class Factory(PictureHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Every picture address answers with a small purple picture, and every other address is refused (TheTVDB is never connected here).
    private sealed class PictureHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var bitmap = new SKBitmap(300, 450);
            bitmap.Erase(SKColors.Purple);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encoded.ToArray()) });
        }
    }

    // Wikidata answers any item with a label and a description.
    private sealed class FakeWikidata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var entities = new Dictionary<string, object>();
            foreach (var id in (query["ids"] ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                entities[id] = new
                {
                    type = "item",
                    id,
                    labels = new Dictionary<string, object> { ["en"] = new { language = "en", value = id == BookQid ? "Gods of Risk" : id } },
                    descriptions = new Dictionary<string, object> { ["en"] = new { language = "en", value = "novella by James S. A. Corey" } },
                    aliases = new Dictionary<string, object>(),
                    claims = new Dictionary<string, object>(),
                };
            }

            var json = JsonSerializer.Serialize(new { entities, success = 1 });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
