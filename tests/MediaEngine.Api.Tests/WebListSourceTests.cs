using System.Net;
using System.Text;
using Dapper;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Collections.ListImport.Sources;
using MediaEngine.Api.Services.Import;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The web list sources (Collections and lists spec D7, D8, 4d, 4h): TMDb lists and collections, MDBList, Open Library lists and
/// MusicBrainz collections. Every answer here is a small hand-made JSON body handed back by a fake HTTP handler: nothing in this
/// file opens a network connection.
/// </summary>
public sealed class WebListSourceTests : IDisposable
{
    private const string TmdbListPage1 = """
        { "id": "8136", "name": "Sci-fi picks", "page": 1, "total_pages": 2, "total_results": 3,
          "items": [
            { "id": 603, "media_type": "movie", "title": "The Matrix", "release_date": "1999-03-31" },
            { "id": 1396, "media_type": "tv", "name": "Breaking Bad", "first_air_date": "2008-01-20" }
          ] }
        """;

    private const string TmdbListPage2 = """
        { "page": 2, "total_pages": 2, "items": [ { "id": 78, "media_type": "movie", "title": "Blade Runner", "release_date": "1982-06-25" } ] }
        """;

    private const string TmdbCollection = """
        { "id": 2344, "name": "The Matrix Collection",
          "parts": [ { "id": 603, "title": "The Matrix", "release_date": "1999-03-31" }, { "id": 604, "title": "The Matrix Reloaded", "release_date": "2003-05-15" } ] }
        """;

    private const string MdbListInfo = """[ { "id": 1, "name": "Top sci-fi", "slug": "top-sci-fi" } ]""";

    private const string MdbListItems = """
        { "movies": [ { "id": 603, "rank": 1, "title": "The Matrix", "imdb_id": "tt0133093", "tvdb_id": null, "mediatype": "movie", "release_year": 1999 } ],
          "shows":  [ { "id": 1396, "rank": 2, "title": "Breaking Bad", "imdb_id": "tt0903747", "tvdb_id": 81189, "mediatype": "show", "release_year": 2008 } ] }
        """;

    private const string OpenLibraryInfo = """{ "name": "Mind-bending reads", "seed_count": 3 }""";

    private const string OpenLibrarySeeds = """
        { "size": 3, "entries": [
            { "url": "/works/OL45804W", "type": { "key": "/type/work" }, "title": "Fantastic Mr Fox" },
            { "url": "/subjects/science_fiction", "title": "Science fiction" },
            { "url": "/books/OL7353617M", "type": { "key": "/type/edition" }, "title": "Dune" }
        ] }
        """;

    private const string MusicBrainzCollectionId = "6c3f1a52-1111-4222-8333-444455556666";

    private const string MusicBrainzReleaseGroups = """
        { "release-group-count": 2, "release-group-offset": 0,
          "release-groups": [
            { "id": "b1392450-e666-3926-a536-22c65f834433", "title": "OK Computer", "first-release-date": "1997-05-21",
              "artist-credit": [ { "name": "Radiohead", "joinphrase": "" } ] },
            { "id": "c2392450-e666-3926-a536-22c65f834433", "title": "Kid A", "first-release-date": "2000-10-02",
              "artist-credit": [ { "name": "Radiohead", "joinphrase": "" } ] }
          ] }
        """;

    private const string MusicBrainzReleases = """
        { "release-count": 1, "release-offset": 0,
          "releases": [
            { "id": "11111111-e666-3926-a536-22c65f834433", "title": "OK Computer", "date": "1997-06-16",
              "release-group": { "id": "b1392450-e666-3926-a536-22c65f834433" },
              "artist-credit": [ { "name": "Radiohead", "joinphrase": "" } ] }
          ] }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_web_lists_{Guid.NewGuid():N}");
    private readonly RoutedHandler _handler = new();
    private readonly LimiterSpy _limiter = new();

    public WebListSourceTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "providers"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that is still in use is left for the system to clear.
        }
    }

    // ── Addresses ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.themoviedb.org/list/8136-sci-fi-picks?language=en", ListSourceKinds.TmdbList, "https://www.themoviedb.org/list/8136")]
    [InlineData("https://www.themoviedb.org/list/8136", ListSourceKinds.TmdbList, "https://www.themoviedb.org/list/8136")]
    [InlineData("https://themoviedb.org/collection/2344-the-matrix-collection", ListSourceKinds.TmdbCollection, "https://www.themoviedb.org/collection/2344")]
    [InlineData("https://www.themoviedb.org/fr/collection/2344", ListSourceKinds.TmdbCollection, "https://www.themoviedb.org/collection/2344")]
    [InlineData("https://mdblist.com/lists/SomeUser/Top-Sci-Fi/", ListSourceKinds.MdbList, "https://mdblist.com/lists/someuser/top-sci-fi")]
    [InlineData("https://openlibrary.org/people/george08/lists/OL1234L/Favourite_reads", ListSourceKinds.OpenLibrary, "https://openlibrary.org/people/george08/lists/OL1234L")]
    [InlineData("https://musicbrainz.org/collection/6C3F1A52-1111-4222-8333-444455556666", ListSourceKinds.MusicBrainz, "https://musicbrainz.org/collection/6c3f1a52-1111-4222-8333-444455556666")]
    public void AnAddress_IsRecognisedByItsSource_AndKeptInItsPlainForm(string pasted, string kind, string plain)
    {
        var recognised = Sources(Fixture()).Select(source => source.Recognise(new Uri(pasted))).Where(address => address is not null).ToList();

        var address = Assert.Single(recognised);
        Assert.Equal(kind, address!.Kind);
        Assert.Equal(plain, address.Address);
    }

    [Theory]
    [InlineData("https://www.imdb.com/list/ls000000001/")]
    [InlineData("https://letterboxd.com/someone/list/best-of/")]
    [InlineData("https://www.themoviedb.org/movie/603")]
    [InlineData("https://musicbrainz.org/artist/6c3f1a52-1111-4222-8333-444455556666")]
    [InlineData("https://mdblist.com/")]
    [InlineData("https://openlibrary.org/works/OL45804W")]
    [InlineData("https://evil.example/themoviedb.org/list/8136")]
    public void AnAddressThatIsNotAListOnASupportedSite_IsNotRecognised(string pasted)
    {
        Assert.All(Sources(Fixture()), source => Assert.Null(source.Recognise(new Uri(pasted))));
    }

    // ── Reading each site ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATmdbList_IsReadPageByPage_WithTmdbIdsAndMediaTypes()
    {
        _handler.Route("/3/list/8136", request => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? TmdbListPage2 : TmdbListPage1);
        var source = new TmdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://www.themoviedb.org/list/8136"))!, CancellationToken.None);

        var list = fetch.List!;
        Assert.Equal("Sci-fi picks", list.Name);
        Assert.Equal(ListSourceKinds.TmdbList, list.SourceKind);
        Assert.Equal("https://www.themoviedb.org/list/8136", list.SourceAddress);
        Assert.Equal(["The Matrix", "Breaking Bad", "Blade Runner"], list.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal([1, 2, 3], list.Entries.Select(entry => entry.Position).ToArray());
        Assert.Equal("603", list.Entries[0].Ids[ListIdentifierKeys.Tmdb]);
        Assert.Equal(1999, list.Entries[0].Year);
        Assert.Equal(["Movies"], list.Entries[0].MediaTypes);
        Assert.Equal(["TV"], list.Entries[1].MediaTypes);
        Assert.Equal(2008, list.Entries[1].Year);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ATmdbCollection_ListsItsMovies()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        var source = new TmdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://www.themoviedb.org/collection/2344"))!, CancellationToken.None);

        Assert.Equal("The Matrix Collection", fetch.List!.Name);
        Assert.Equal(ListSourceKinds.TmdbCollection, fetch.List.SourceKind);
        Assert.Equal(["603", "604"], fetch.List.Entries.Select(entry => entry.Ids[ListIdentifierKeys.Tmdb]).ToArray());
    }

    [Fact]
    public async Task TheTmdbKey_GoesToTmdbOnly_AndNeverIntoALogOrTheList()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        var source = new TmdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://www.themoviedb.org/collection/2344"))!, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("api.themoviedb.org", request.RequestUri!.Host);
        Assert.Contains("api_key=test-key", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", fetch.List!.SourceAddress, StringComparison.Ordinal);
        Assert.Equal("tmdb", _limiter.Names.Single());
    }

    [Fact]
    public async Task AnMdbList_IsReadInRankOrder_WithImdbTmdbAndTvdbIds()
    {
        _handler.Route("/lists/someuser/top-sci-fi/items", _ => MdbListItems);
        _handler.Route("/lists/someuser/top-sci-fi", _ => MdbListInfo);
        var source = new MdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<MdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://mdblist.com/lists/someuser/top-sci-fi"))!, CancellationToken.None);

        var list = fetch.List!;
        Assert.Equal("Top sci-fi", list.Name);
        Assert.Equal(["The Matrix", "Breaking Bad"], list.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal("tt0133093", list.Entries[0].Ids[ListIdentifierKeys.Imdb]);
        Assert.Equal("603", list.Entries[0].Ids[ListIdentifierKeys.Tmdb]);
        Assert.False(list.Entries[0].Ids.ContainsKey(ListIdentifierKeys.Tvdb));
        Assert.Equal("81189", list.Entries[1].Ids[ListIdentifierKeys.Tvdb]);
        Assert.Equal(["TV"], list.Entries[1].MediaTypes);
        Assert.All(_handler.Requests, request => Assert.Contains("apikey=test-key", request.RequestUri!.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnMdbListLongerThanOnePage_IsReadToTheEnd()
    {
        var firstPage = "{ \"movies\": [" + string.Join(",", Enumerable.Range(1, 500).Select(n => $"{{ \"id\": {n}, \"rank\": {n}, \"title\": \"Film {n}\", \"release_year\": 2000 }}")) + "], \"shows\": [] }";
        var secondPage = "{ \"movies\": [ { \"id\": 900, \"rank\": 501, \"title\": \"Film 501\", \"release_year\": 2001 } ], \"shows\": [] }";
        _handler.Route("/lists/someuser/long/items", request => request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal) ? firstPage : secondPage);
        _handler.Route("/lists/someuser/long", _ => """[ { "name": "Long one" } ]""");
        var source = new MdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<MdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://mdblist.com/lists/someuser/long"))!, CancellationToken.None);

        Assert.Equal(501, fetch.List!.Entries.Count);
        Assert.Equal("Film 501", fetch.List.Entries[^1].Title);
    }

    [Fact]
    public async Task AnOpenLibraryList_KeepsBooksOnly_AndNeedsNoKey()
    {
        _handler.Route("/people/george08/lists/OL1234L/seeds.json", _ => OpenLibrarySeeds);
        _handler.Route("/people/george08/lists/OL1234L.json", _ => OpenLibraryInfo);
        var source = new OpenLibraryListSource(Fixture(), _handler.Factory, _limiter, NullLogger<OpenLibraryListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://openlibrary.org/people/george08/lists/OL1234L"))!, CancellationToken.None);

        var list = fetch.List!;
        Assert.Equal("Mind-bending reads", list.Name);
        Assert.Equal(["Fantastic Mr Fox", "Dune"], list.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal(["Books", "Audiobooks"], list.Entries[0].MediaTypes);
        Assert.All(_handler.Requests, request => Assert.DoesNotContain("key", request.RequestUri!.Query, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AMusicBrainzCollection_IsAskedForEachKindOfThingUntilOneIsAccepted_AtTheProvidersRateLimit()
    {
        _handler.RouteStatus("/ws/2/release", HttpStatusCode.BadRequest);
        _handler.Route("/ws/2/release-group", _ => MusicBrainzReleaseGroups);
        var source = new MusicBrainzCollectionSource(Fixture(), _handler.Factory, _limiter, NullLogger<MusicBrainzCollectionSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri($"https://musicbrainz.org/collection/{MusicBrainzCollectionId}"))!, CancellationToken.None);

        var list = fetch.List!;
        Assert.Equal(["OK Computer", "Kid A"], list.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal(1997, list.Entries[0].Year);
        Assert.Equal("Radiohead", list.Entries[0].Creator);
        Assert.Equal("b1392450-e666-3926-a536-22c65f834433", list.Entries[0].Ids[ListIdentifierKeys.MusicBrainz]);
        Assert.Equal("b1392450-e666-3926-a536-22c65f834433", list.Entries[0].Ids["musicbrainz_release_group_id"]);
        Assert.Equal(["Music"], list.Entries[0].MediaTypes);
        // Every request went through the MusicBrainz source's own limiter entry (one a second) and carries its User-Agent.
        Assert.All(_limiter.Names, name => Assert.Equal("musicbrainz", name));
        Assert.All(_limiter.Rates, rate => Assert.Equal(1d, rate));
        Assert.All(_handler.Requests, request => Assert.Contains("Tuvima Test", request.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMusicBrainzCollectionOfReleases_GivesReleaseAndReleaseGroupIdentifiers()
    {
        _handler.Route("/ws/2/release", _ => MusicBrainzReleases);
        var source = new MusicBrainzCollectionSource(Fixture(), _handler.Factory, _limiter, NullLogger<MusicBrainzCollectionSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri($"https://musicbrainz.org/collection/{MusicBrainzCollectionId}"))!, CancellationToken.None);

        var ids = fetch.List!.Entries.Single().Ids;
        Assert.Equal("11111111-e666-3926-a536-22c65f834433", ids["musicbrainz_release_id"]);
        Assert.Equal("b1392450-e666-3926-a536-22c65f834433", ids["musicbrainz_release_group_id"]);
    }

    [Fact]
    public async Task AMusicBrainzCollectionNoKindAccepts_IsMissingOrPrivate()
    {
        _handler.RouteStatus("/ws/2/", HttpStatusCode.BadRequest);
        var source = new MusicBrainzCollectionSource(Fixture(), _handler.Factory, _limiter, NullLogger<MusicBrainzCollectionSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri($"https://musicbrainz.org/collection/{MusicBrainzCollectionId}"))!, CancellationToken.None);

        Assert.Equal(WebListFailure.NotFound, fetch.Failure);
        Assert.Null(fetch.List);
    }

    // ── Failure rules ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, WebListFailure.Down)]
    [InlineData(HttpStatusCode.TooManyRequests, WebListFailure.Down)]
    [InlineData(HttpStatusCode.InternalServerError, WebListFailure.Down)]
    [InlineData(HttpStatusCode.NotFound, WebListFailure.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, WebListFailure.KeyRejected)]
    public async Task ASiteThatRefusesOrIsDown_GivesAPlainFailure_NeverAnException(HttpStatusCode status, WebListFailure expected)
    {
        _handler.RouteStatus("/lists/", status);
        var source = new MdbListSource(Fixture(), _handler.Factory, _limiter, NullLogger<MdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://mdblist.com/lists/someuser/top-sci-fi"))!, CancellationToken.None);

        Assert.Equal(expected, fetch.Failure);
    }

    [Fact]
    public async Task ASiteThatCannotBeReached_IsDown()
    {
        _handler.Throw("/people/", new HttpRequestException("no route to host"));
        var source = new OpenLibraryListSource(Fixture(), _handler.Factory, _limiter, NullLogger<OpenLibraryListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://openlibrary.org/people/george08/lists/OL1234L"))!, CancellationToken.None);

        Assert.Equal(WebListFailure.Down, fetch.Failure);
    }

    [Fact]
    public async Task ASourceThatNeedsAKeyAndHasNone_AsksNothingFromTheSite()
    {
        var source = new MdbListSource(Fixture(mdblistKey: ""), _handler.Factory, _limiter, NullLogger<MdbListSource>.Instance);

        var fetch = await source.FetchAsync(source.Recognise(new Uri("https://mdblist.com/lists/someuser/top-sci-fi"))!, CancellationToken.None);

        Assert.Equal(WebListFailure.KeyMissing, fetch.Failure);
        Assert.Empty(_handler.Requests);
    }

    // ── Pasting an address ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASourceSwitchedOff_RefusesOnlyItsOwnAddresses_WithTheSentenceFromTheSpecAndALink()
    {
        var loader = Fixture(tmdbEnabled: false);
        var resolver = Resolver(loader);
        _handler.Route("/lists/someuser/top-sci-fi/items", _ => MdbListItems);
        _handler.Route("/lists/someuser/top-sci-fi", _ => MdbListInfo);

        var refused = await Assert.ThrowsAsync<WebListRefusedException>(
            () => resolver.ResolveAsync(Bytes("https://www.themoviedb.org/list/8136"), CancellationToken.None));
        var other = await resolver.ResolveAsync(Bytes("https://mdblist.com/lists/someuser/top-sci-fi"), CancellationToken.None);

        Assert.Equal("TMDb lists are switched off. Turn the source on in Settings → Plugins.", refused.Message);
        Assert.Equal("/settings/plugins", refused.LinkHref);
        Assert.NotNull(other);
        Assert.Equal(2, other.Entries.Count);
        Assert.All(_handler.Requests, request => Assert.Equal("api.mdblist.com", request.RequestUri!.Host));
    }

    [Fact]
    public async Task ASourceWithNoKey_AsksTheKeyBeforeAnythingIsSent()
    {
        var resolver = Resolver(Fixture(mdblistKey: ""));

        var refused = await Assert.ThrowsAsync<WebListRefusedException>(
            () => resolver.ResolveAsync(Bytes("https://mdblist.com/lists/someuser/top-sci-fi"), CancellationToken.None));

        Assert.Contains("need your own key", refused.Message, StringComparison.Ordinal);
        Assert.Equal("/settings/plugins", refused.LinkHref);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AnAddressOnASiteLibraryDoesNotRead_SaysWhichSitesItReads_AndSuggestsAFile()
    {
        var resolver = Resolver(Fixture());

        var refused = await Assert.ThrowsAsync<WebListRefusedException>(
            () => resolver.ResolveAsync(Bytes("https://www.imdb.com/list/ls000000001/"), CancellationToken.None));

        Assert.Contains("TMDb", refused.Message, StringComparison.Ordinal);
        Assert.Contains("MusicBrainz", refused.Message, StringComparison.Ordinal);
        Assert.Contains("export", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task APlainListOfTitles_IsNotTreatedAsAnAddress()
    {
        var resolver = Resolver(Fixture());

        Assert.Null(await resolver.ResolveAsync(Bytes("Heat (1995)\r\nThe Matrix (1999)\r\n"), CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(Bytes("See https://www.themoviedb.org/list/8136"), CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ASiteThatIsDown_GivesAnEmptyListThatWillTryAgain()
    {
        _handler.RouteStatus("/3/", HttpStatusCode.ServiceUnavailable);
        var resolver = Resolver(Fixture());

        var list = await resolver.ResolveAsync(Bytes("https://www.themoviedb.org/list/8136"), CancellationToken.None);

        Assert.NotNull(list);
        Assert.True(list.WillTryAgain);
        Assert.Empty(list.Entries);
        Assert.Equal(ListSourceKinds.TmdbList, list.SourceKind);
        Assert.Equal("https://www.themoviedb.org/list/8136", list.SourceAddress);
    }

    [Fact]
    public async Task AListJustRead_IsNotAskedForAgainByTheNextPreviewStep()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        var resolver = Resolver(Fixture());

        await resolver.ResolveAsync(Bytes("https://www.themoviedb.org/collection/2344"), CancellationToken.None);
        await resolver.ResolveAsync(Bytes("https://www.themoviedb.org/collection/2344?language=fr"), CancellationToken.None);

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task APrivateOrMissingList_IsRefusedWithAPlainSentence()
    {
        _handler.RouteStatus("/3/", HttpStatusCode.NotFound);
        var resolver = Resolver(Fixture());

        var refused = await Assert.ThrowsAsync<WebListRefusedException>(
            () => resolver.ResolveAsync(Bytes("https://www.themoviedb.org/list/8136"), CancellationToken.None));

        Assert.Contains("private", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSharedImport_PreviewsAnAddress_WithTheAddressOnlyPrivacyLine()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        using var db = new TestDb(_root);
        var handler = new CollectionListImportKindHandler(db.Service, new ListInputReader(Resolver(Fixture())));

        var preview = await handler.PreviewAsync(
            new ImportSource("pasted-list.txt", Bytes("https://www.themoviedb.org/collection/2344")), new ImportContext(null, null), CancellationToken.None);

        Assert.True(preview.Recognised);
        Assert.Equal("List address", preview.KindLabel);
        Assert.Equal("The Matrix Collection", preview.Heading);
        Assert.Equal("Only this address and the list's identifiers go to TMDb.", preview.PrivacyNote);
        Assert.True(preview.List!.IsWebSource);
        Assert.Equal(2, preview.List.EntryCount);
    }

    [Fact]
    public async Task TheSharedImport_RefusesASwitchedOffSource_WithALink_AndStillReadsFiles()
    {
        using var db = new TestDb(_root);
        var handler = new CollectionListImportKindHandler(db.Service, new ListInputReader(Resolver(Fixture(tmdbEnabled: false))));
        var context = new ImportContext(null, null);

        var refused = await handler.PreviewAsync(new ImportSource("pasted-list.txt", Bytes("https://www.themoviedb.org/list/8136")), context, CancellationToken.None);
        var file = await handler.PreviewAsync(new ImportSource("titles.txt", Bytes("Heat (1995)\r\n")), context, CancellationToken.None);

        Assert.False(refused.Recognised);
        Assert.Equal("TMDb lists are switched off. Turn the source on in Settings → Plugins.", refused.Message);
        Assert.Equal("/settings/plugins", refused.MessageLinkHref);
        Assert.True(file.Recognised);
        Assert.False(file.List!.IsWebSource);
        Assert.Equal(ImportPrivacy.FilesStayOnThisMachine, file.PrivacyNote);
    }

    // ── Saving, matching and refreshing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWebList_IsSavedWithItsAddress_MatchedByIdentifiers_AndRefreshedWeeklyByDefaultChoice()
    {
        _handler.Route("/lists/someuser/top-sci-fi/items", _ => MdbListItems);
        _handler.Route("/lists/someuser/top-sci-fi", _ => MdbListInfo);
        using var db = new TestDb(_root);
        var matrix = db.SeedWork("Movies", "The Matrix (restored cut)", 1999, ("imdb_id", "tt0133093"));
        var collectionId = db.SeedCollection();
        var parsed = (await Resolver(Fixture()).ResolveAsync(Bytes("https://mdblist.com/lists/someuser/top-sci-fi"), CancellationToken.None))!;

        var result = await db.Service.ImportAsync(
            collectionId, "pasted-list.txt", parsed, shelf: null, ListImportChoices.OrderList, ListImportChoices.RefreshWeekly,
            ListImportChoices.SyncFollow, [], allowedWorkIds: null, CancellationToken.None);

        Assert.Equal(2, result.EntryCount);
        Assert.Equal(1, result.MatchedCount);
        var source = (await db.Lists.GetSourceAsync(collectionId))!;
        Assert.Equal("https://mdblist.com/lists/someuser/top-sci-fi", source.SourceAddress);
        Assert.Equal(ListSourceKinds.MdbList, source.SourceKind);
        Assert.Equal("MDBList list", source.SourceLabel);
        Assert.Equal(ListImportChoices.RefreshWeekly, source.Refresh);
        Assert.Contains(matrix, db.ItemWorkIds(collectionId));
        var dto = (await db.Service.GetSourceAsync(collectionId, CancellationToken.None))!;
        Assert.True(dto.IsWebSource);
        Assert.Equal("https://mdblist.com/lists/someuser/top-sci-fi", dto.SourceAddress);
    }

    [Fact]
    public async Task AWebListWhoseSiteWasDown_IsSavedAnyway_AndTheRefreshJobFillsItWhenTheSiteIsBack()
    {
        _handler.RouteStatus("/3/", HttpStatusCode.ServiceUnavailable);
        var loader = Fixture();
        using var db = new TestDb(_root, fetchers: [new TmdbListSource(loader, _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance)]);
        var matrix = db.SeedWork("Movies", "The Matrix", 1999, ("tmdb_id", "603"));
        var collectionId = db.SeedCollection();
        var parsed = (await Resolver(loader).ResolveAsync(Bytes("https://www.themoviedb.org/list/8136"), CancellationToken.None))!;

        var saved = await db.Service.ImportAsync(
            collectionId, "pasted-list.txt", parsed, shelf: null, ListImportChoices.OrderList, ListImportChoices.RefreshNever,
            ListImportChoices.SyncFollow, [], allowedWorkIds: null, CancellationToken.None);

        Assert.Equal(0, saved.EntryCount);
        var source = (await db.Lists.GetSourceAsync(collectionId))!;
        Assert.Equal(ListImportChoices.RefreshWeekly, source.Refresh);
        Assert.NotNull(source.NextRefreshAt);
        Assert.True(source.NextRefreshAt < DateTimeOffset.UtcNow.AddHours(2));

        // The job tries while the site is still down: the collection is left as it is.
        db.MakeDue(collectionId);
        var worker = db.Worker([new TmdbListSource(loader, _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance)]);
        Assert.Equal(0, await worker.RunOnceAsync(CancellationToken.None));

        // The site is back: the next pass reads the list and the title Library owns joins the collection.
        _handler.Reset();
        _handler.Route("/3/list/8136", request => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? TmdbListPage2 : TmdbListPage1);
        Assert.Equal(1, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Contains(matrix, db.ItemWorkIds(collectionId));
        Assert.Equal(3, (await db.Lists.GetSourceAsync(collectionId))!.EntryCount);
    }

    [Fact]
    public async Task ManualRefresh_OfAWebListWhoseSiteIsDown_KeepsWhatItHas_AndSaysItWillTryAgain()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        var loader = Fixture();
        using var db = new TestDb(_root, fetchers: [new TmdbListSource(loader, _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance)]);
        var matrix = db.SeedWork("Movies", "The Matrix", 1999, ("tmdb_id", "603"));
        var collectionId = db.SeedCollection();
        var parsed = (await Resolver(loader).ResolveAsync(Bytes("https://www.themoviedb.org/collection/2344"), CancellationToken.None))!;
        await db.Service.ImportAsync(
            collectionId, "pasted-list.txt", parsed, null, ListImportChoices.OrderList, ListImportChoices.RefreshWeekly,
            ListImportChoices.SyncFollow, [], null, CancellationToken.None);

        _handler.Reset();
        _handler.RouteStatus("/3/", HttpStatusCode.ServiceUnavailable);
        var result = await db.Service.RefreshNowAsync(collectionId, fresh: null, allowedWorkIds: null, CancellationToken.None);

        Assert.True(result!.WillTryAgain);
        Assert.Equal(2, result.Matched + result.Unmatched);
        Assert.Contains(matrix, db.ItemWorkIds(collectionId));
    }

    [Fact]
    public async Task ARefreshOfASourceSwitchedOffSince_WaitsUntilItIsSwitchedBackOn()
    {
        _handler.Route("/3/collection/2344", _ => TmdbCollection);
        using var db = new TestDb(_root);
        var collectionId = db.SeedCollection();
        var onLoader = Fixture();
        var parsed = (await Resolver(onLoader).ResolveAsync(Bytes("https://www.themoviedb.org/collection/2344"), CancellationToken.None))!;
        await db.Service.ImportAsync(
            collectionId, "pasted-list.txt", parsed, null, ListImportChoices.OrderList, ListImportChoices.RefreshDaily,
            ListImportChoices.SyncFollow, [], null, CancellationToken.None);
        _handler.Reset();

        var offSource = new TmdbListSource(Fixture(tmdbEnabled: false), _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance);
        var source = (await db.Lists.GetSourceAsync(collectionId))!;

        Assert.True(offSource.CanFetch(source));
        Assert.Null(await offSource.FetchAsync(source, CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AMusicBrainzReleaseGroupIdentifier_MatchesAnAlbumLibraryHoldsByThatId()
    {
        using var db = new TestDb(_root);
        var album = db.SeedWork("Music", "OK Computer", 1997, ("musicbrainz_release_group_id", "b1392450-e666-3926-a536-22c65f834433"));
        db.SeedWork("Music", "OK Computer (live)", 1998);
        _handler.Route("/ws/2/release", _ => MusicBrainzReleases);
        var parsed = (await Resolver(Fixture()).ResolveAsync(Bytes($"https://musicbrainz.org/collection/{MusicBrainzCollectionId}"), CancellationToken.None))!;

        var preview = await db.Service.PreviewAsync("pasted-list.txt", parsed, null, [], null, CancellationToken.None);

        Assert.Equal(1, preview.OwnedCount);
        Assert.Contains(album, preview.MatchedWorkIds);
    }

    // ── The Plugins page ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachWebSource_AppearsOnThePluginsPage_WithItsLabelAudienceAndWhatItSends()
    {
        var config = Path.Combine(FindRepoRoot(), "config");
        var catalogue = new ProviderCapabilityCatalogue(new ConfigurationDirectoryLoader(config));
        var sources = catalogue.Sources().ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);

        var mdblist = sources["mdblist"];
        Assert.Equal("MDBList", mdblist.DisplayName);
        Assert.True(mdblist.RequiresKey);
        Assert.Equal("administrators", mdblist.Access.Audience);
        Assert.False(mdblist.Access.AudienceIsDefault);
        Assert.Equal("Your key, this address and the list's identifiers go to api.mdblist.com", mdblist.Access.DataLeaving);
        Assert.Contains("Movie lists", mdblist.Supplies);

        var openLibrary = sources["openlibrary"];
        Assert.False(openLibrary.RequiresKey);
        Assert.Equal("Only this address and the list's identifiers go to openlibrary.org", openLibrary.Access.DataLeaving);
        Assert.Contains("Book lists", openLibrary.Supplies);
        Assert.False(openLibrary.Access.AudienceIsDefault);

        Assert.Contains("Movie lists", sources["tmdb"].Supplies);
        Assert.Contains("Music lists", sources["musicbrainz"].Supplies);
        // TMDb and MusicBrainz already did other work; their label is not narrowed to lists.
        Assert.DoesNotContain("Only this address", sources["tmdb"].Access.DataLeaving, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedProviderFiles_ForTheWebSources_NameTheirSitesAndKeepTheKeyOutOfTheFile()
    {
        var providers = Path.Combine(FindRepoRoot(), "config", "providers");
        foreach (var name in new[] { "mdblist", "openlibrary" })
        {
            var text = File.ReadAllText(Path.Combine(providers, name + ".json"));
            Assert.Contains("\"lists\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"api_key\": \"", text.Replace("\"api_key\": \"\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private WebListResolver Resolver(IConfigurationLoader loader) => new(Sources(loader), loader);

    private List<IWebListSource> Sources(IConfigurationLoader loader) =>
    [
        new TmdbListSource(loader, _handler.Factory, _limiter, NullLogger<TmdbListSource>.Instance),
        new MdbListSource(loader, _handler.Factory, _limiter, NullLogger<MdbListSource>.Instance),
        new OpenLibraryListSource(loader, _handler.Factory, _limiter, NullLogger<OpenLibraryListSource>.Instance),
        new MusicBrainzCollectionSource(loader, _handler.Factory, _limiter, NullLogger<MusicBrainzCollectionSource>.Instance),
    ];

    /// <summary>A config folder holding the four provider files, each on or off and with or without its key.</summary>
    private ConfigurationDirectoryLoader Fixture(bool tmdbEnabled = true, string tmdbKey = "test-key", string mdblistKey = "test-key", bool mdblistEnabled = true)
    {
        var dir = Path.Combine(_root, "config", "providers");
        Write(dir, "tmdb", tmdbEnabled, "https://api.themoviedb.org/3", tmdbKey, rate: 20);
        Write(dir, "mdblist", mdblistEnabled, "https://api.mdblist.com", mdblistKey, rate: 2);
        Write(dir, "openlibrary", true, "https://openlibrary.org", null, rate: 1);
        Write(dir, "musicbrainz", true, "https://musicbrainz.org/ws/2", null, rate: 1);
        return new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    private static void Write(string dir, string name, bool enabled, string api, string? key, double rate)
    {
        var client = key is null
            ? """{ "user_agent": "Tuvima Test/1.0" }"""
            : $$"""{ "user_agent": "Tuvima Test/1.0", "api_key": "{{key}}" }""";
        File.WriteAllText(
            Path.Combine(dir, name + ".json"),
            $$"""
            {
              "name": "{{name}}", "version": "1.0", "enabled": {{(enabled ? "true" : "false")}},
              "provider_capabilities": ["lists"],
              "endpoints": { "api": "{{api}}" },
              "rate_limit": { "requests_per_second": {{rate}}, "burst": 1, "max_concurrency": 1 },
              "http_client": {{client}},
              "declaration": { "audience": "administrators" }
            }
            """);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers each request from a table of path fragments, newest route first, and remembers what was asked.</summary>
    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly List<(string Path, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public IHttpClientFactory Factory => new FactoryOf(this);

        public void Route(string path, Func<HttpRequestMessage, string> body) =>
            _routes.Insert(0, (path, request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request), Encoding.UTF8, "application/json") }));

        public void RouteStatus(string path, HttpStatusCode status) =>
            _routes.Insert(0, (path, _ => new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));

        public void Throw(string path, Exception exception) =>
            _routes.Insert(0, (path, _ => throw exception));

        public void Reset()
        {
            _routes.Clear();
            Requests.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // The most specific route (the longest path found in the request's path; the newest on a tie) wins.
            var best = _routes
                .Where(route => request.RequestUri!.AbsolutePath.Contains(route.Path, StringComparison.Ordinal))
                .OrderByDescending(route => route.Path.Length)
                .FirstOrDefault();
            return Task.FromResult(best.Answer is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : best.Answer(request));
        }

        private sealed class FactoryOf(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }

    /// <summary>Runs each request straight away and remembers which source asked and at what rate limit.</summary>
    private sealed class LimiterSpy : IProviderRateLimiterCoordinator
    {
        public List<string> Names { get; } = [];

        public List<double?> Rates { get; } = [];

        public Task<T> ExecuteAsync<T>(string providerName, ProviderRateLimitConfiguration? rateLimit, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            Names.Add(providerName);
            Rates.Add(rateLimit?.RequestsPerSecond);
            return operation(ct);
        }

        public Task ExecuteAsync(string providerName, ProviderRateLimitConfiguration? rateLimit, Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            Names.Add(providerName);
            Rates.Add(rateLimit?.RequestsPerSecond);
            return operation(ct);
        }

        public IReadOnlyList<ProviderActivitySnapshot> GetSnapshots() => [];
    }

    /// <summary>A real data store with the list tables, a small seeded library and the list service on top of it.</summary>
    private sealed class TestDb : IDisposable
    {
        private readonly DatabaseConnection _db;
        private readonly CandidateSpy _candidates = new();

        public TestDb(string root, IEnumerable<IListSourceFetcher>? fetchers = null)
        {
            DapperConfiguration.Configure();
            _db = new DatabaseConnection(Path.Combine(root, $"library_{Guid.NewGuid():N}.db"));
            _db.InitializeSchema();
            _db.RunStartupChecks();
            Lists = new CollectionListRepository(_db);
            Service = new ListImportService(
                Lists, _candidates, new FuzzyMatchingService(), new ConfigurationDirectoryLoader(Path.Combine(root, "config")), new LookupStub(),
                fetchers: fetchers);
        }

        public CollectionListRepository Lists { get; }

        public ListImportService Service { get; }

        public CollectionListRefreshWorker Worker(IEnumerable<IListSourceFetcher> fetchers) =>
            new(Lists, Service, fetchers, new RecorderStub(), NullLogger<CollectionListRefreshWorker>.Instance);

        public Guid SeedWork(string mediaType, string title, int year, params (string Key, string Value)[] ids)
        {
            var workId = Guid.NewGuid();
            using var connection = _db.CreateConnection();
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');",
                new { workId, mediaType });
            _candidates.Add(new ListImportCandidate(
                workId, mediaType, title, year, null, ids.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            return workId;
        }

        public Guid SeedCollection()
        {
            var collectionId = Guid.NewGuid();
            using var connection = _db.CreateConnection();
            connection.Execute("""
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
                VALUES (@collectionId, 'Imported', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
                """, new { collectionId });
            return collectionId;
        }

        public void MakeDue(Guid collectionId)
        {
            using var connection = _db.CreateConnection();
            connection.Execute(
                "UPDATE collection_list_sources SET next_refresh_at = @past WHERE collection_id = @collectionId",
                new { past = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), collectionId });
        }

        public IReadOnlyList<Guid> ItemWorkIds(Guid collectionId)
        {
            using var connection = _db.CreateConnection();
            return connection.Query<Guid>("SELECT work_id FROM collection_items WHERE collection_id = @collectionId", new { collectionId }).ToList();
        }

        public void Dispose()
        {
        }
    }

    private sealed class CandidateSpy : IListImportCandidateRepository
    {
        private readonly List<ListImportCandidate> _all = [];

        public void Add(ListImportCandidate candidate) => _all.Add(candidate);

        public Task<IReadOnlyList<ListImportCandidate>> GetCandidatesAsync(IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ListImportCandidate>>(
                _all.Where(candidate => allowedWorkIds is null || allowedWorkIds.Contains(candidate.WorkId)).ToList());
    }

    private sealed class LookupStub : ICollectionMediaLookupReadService
    {
        public Task<List<CollectionMediaLookupDto>> LookupAsync(
            string? query, string? mediaTypes, IReadOnlySet<Guid> existingWorkIds, IReadOnlySet<Guid> allowedWorkIds,
            int? offset, int? limit, CancellationToken ct) => Task.FromResult(new List<CollectionMediaLookupDto>());

        public Task<List<CollectionItemDto>> ResolveItemsAsync(
            Guid collectionId, IReadOnlyList<CollectionItem> items, CancellationToken ct, IReadOnlySet<Guid>? allowedWorkIds = null) =>
            Task.FromResult(new List<CollectionItemDto>());

        public Task<List<CollectionResolvedItemDto>> ResolveMetadataAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new List<CollectionResolvedItemDto>());

        public Task<Dictionary<string, int>> CountMediaTypesAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, int>());
    }

    private sealed class RecorderStub : IRecurringTaskRecorder
    {
        public Task RecordAsync(string taskKey, Func<CancellationToken, Task<int?>> run, CancellationToken ct) => run(ct);
    }
}
