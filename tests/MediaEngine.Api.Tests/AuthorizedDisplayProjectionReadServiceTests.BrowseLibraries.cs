using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

// The browse tabs (Library setup spec D54), the per-library filter and the Unmatched filter (D34): what each profile may see of them.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly Guid MoviesLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid ShowsLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid LecturesLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000003");
    private static readonly Guid MusicLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000004");
    private static readonly Guid BooksLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000005");
    private static readonly Guid PhotosLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000006");
    private static readonly Guid HiddenVaultLibrary = Guid.Parse("b0000000-0000-4000-8000-000000000007");

    private static LibraryFolderConfig Library(Guid id, string name, string kindKey, string area, string mediaType, bool withFolder) => new()
    {
        Id = id.ToString("D"),
        Name = name,
        KindKey = kindKey,
        Area = area,
        MediaTypes = [mediaType],
        Sources = withFolder ? [new LibrarySourceConfig { Id = $"{kindKey}-folder", Path = $"/media/{kindKey}" }] : [],
    };

    private static LibrariesOnlyBrowseLoader BrowseLibrariesConfig() => new(
        // Written out of order on purpose: the list must come back area first, then the kinds' own order, then name.
        Library(BooksLibrary, "Books", "books", LibraryAreas.Read, "Books", true),
        Library(MusicLibrary, "Music", "music", LibraryAreas.Listen, "Music", true),
        Library(LecturesLibrary, "Lectures", "other_videos", LibraryAreas.Watch, "Videos", true),
        Library(ShowsLibrary, "TV Shows", "tv", LibraryAreas.Watch, "TV", false),
        Library(MoviesLibrary, "Movies", "movies", LibraryAreas.Watch, "Movies", true),
        Library(PhotosLibrary, "Photos", "photos", LibraryAreas.View, "Images", true),
        Library(HiddenVaultLibrary, "Private", "other_videos", LibraryAreas.Watch, "Videos", true));

    // Not async: the request context lives in an async-local the service reads later, so it must be set in the caller's own flow.
    private BrowseLibraryListService CreateBrowseLibraryList(Guid account)
    {
        using (var connection = _database.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute(
                "INSERT OR IGNORE INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@library,'none',@now,@now,@now);",
                new { library = HiddenVaultLibrary.ToString("D"), now });
        }

        var config = BrowseLibrariesConfig();
        var service = CreateService(HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId), new StubRawProjection([], []), config);
        return new BrowseLibraryListService(service, config, new VaultLibraryRepository(_database));
    }

    [Fact]
    public async Task BrowseLibraries_AnAdministratorGetsEveryNonVaultLibrary_WithItsKindAreaAndWhetherItHasFolders()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal(["Movies", "TV Shows", "Lectures", "Music", "Books"], libraries.Select(library => library.Name).ToArray());
        var shows = libraries.Single(library => library.Name == "TV Shows");
        Assert.Equal(new BrowseLibraryDto(ShowsLibrary, "TV Shows", "tv", "watch", "TV", false), shows);
        var lectures = libraries.Single(library => library.Name == "Lectures");
        Assert.Equal(new BrowseLibraryDto(LecturesLibrary, "Lectures", "other_videos", "watch", "Videos", true), lectures);
        Assert.Equal("listen", libraries.Single(library => library.Name == "Music").Area);
        Assert.Equal("read", libraries.Single(library => library.Name == "Books").Area);
    }

    [Fact]
    public async Task BrowseLibraries_AProfileWithEveryOtherLibraryHidden_GetsOnlyLectures()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary, ShowsLibrary, MusicLibrary, BooksLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([LecturesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_ANewLibraryIsListedForAProfileWithItsArea_WithNoRowWrittenForIt()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([MoviesLibrary, ShowsLibrary, LecturesLibrary, MusicLibrary, BooksLibrary], libraries.Select(library => library.Id).ToArray());
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM account_library_hidden;"));
    }

    [Fact]
    public async Task BrowseLibraries_ALibraryHiddenFromOneProfile_IsStillListedForAnotherWithTheSameAreas()
    {
        var sam = Guid.NewGuid();
        var other = Guid.NewGuid();
        await CreateHumanAsync(sam, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        await CreateHumanAsync(other, EveryFeature, new HashSet<Guid>());

        var forSam = await CreateBrowseLibraryList(sam).ListAsync(default);
        var forOther = await CreateBrowseLibraryList(other).ListAsync(default);

        Assert.DoesNotContain(forSam, library => library.Id == LecturesLibrary);
        Assert.Contains(forOther, library => library.Id == LecturesLibrary);
    }

    [Fact]
    public async Task BrowseLibraries_AVaultLibraryIsNeverListed_EvenForAnAdministratorWithAStaleRowOnIt()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { HiddenVaultLibrary }, administrator: true);

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.DoesNotContain(libraries, library => library.Id == HiddenVaultLibrary);
        Assert.DoesNotContain(libraries, library => library.Name == "Private");
    }

    [Fact]
    public async Task BrowseLibraries_AProfileWithoutTheListenFeature_GetsNoListenLibraries_ButKeepsTheOtherLanes()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(
            account,
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch, AccountFeatureId.Read },
            new HashSet<Guid>());

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.DoesNotContain(libraries, library => library.Area == "listen");
        Assert.Equal([MoviesLibrary, ShowsLibrary, LecturesLibrary, BooksLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Theory]
    [InlineData("PG")]
    [InlineData("G")]
    public async Task BrowseLibraries_ALimitedProfile_StillGetsTheVideosLibrary_BecauseItsItemsAreFilteredOneByOne(string limit)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { ShowsLibrary, MusicLibrary, BooksLibrary });
        SetContentLimit(limit);

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([MoviesLibrary, LecturesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_AnUnlimitedProfile_GetsVideosLibrariesWhateverTheLimitedProfileSetting()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary, ShowsLibrary, MusicLibrary, BooksLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([LecturesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_ViewLibrariesAreNotListed()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { ShowsLibrary, LecturesLibrary, MusicLibrary, BooksLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([MoviesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_AProfileWithoutAnyArea_GetsNothing_EvenWithNothingHidden()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId>(), new HashSet<Guid>());

        Assert.Empty(await CreateBrowseLibraryList(account).ListAsync(default));
    }

    [Fact]
    public async Task Browse_TheLibraryFilter_ReturnsOnlyThatLibrarysRows()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var movie = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "A film", "Movie");
        var lecture = await InsertOwnedWorkWithIdAsync(LecturesLibrary, "A lecture", "Movie");
        var composer = BrowseComposer(account);

        var all = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48);
        var lectures = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: LecturesLibrary);

        Assert.Equal(2, all.TotalCount);
        Assert.Equal(1, lectures.TotalCount);
        Assert.Equal([lecture.AssetId], lectures.Catalog.Select(card => card.AssetId!.Value).ToArray());
        Assert.DoesNotContain(lectures.Catalog, card => card.AssetId == movie.AssetId);
    }

    [Fact]
    public async Task Browse_TheLibraryFilter_ForALibraryHiddenFromTheProfile_IsEmptyLikeAnEmptyLibrary()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "A film", "Movie");
        await InsertOwnedWorkWithIdAsync(LecturesLibrary, "A lecture", "Movie");
        var composer = BrowseComposer(account);

        var notGranted = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: LecturesLibrary);
        var empty = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: Guid.NewGuid());

        Assert.Equal(0, notGranted.TotalCount);
        Assert.Empty(notGranted.Catalog);
        Assert.Equal(empty.TotalCount, notGranted.TotalCount);
        Assert.Equal(empty.Catalog.Count, notGranted.Catalog.Count);
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_ListsOpenAndKeptUnmatchedWorks_AndMarksOnlyTheKeptOneKept()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        var kept = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Kept", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);
        await MarkUnmatchedAsync(kept.AssetId, MoviesLibrary, keep: true);

        var page = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        Assert.Equal(new[] { open.AssetId, kept.AssetId }.Order(), page.Catalog.Select(card => card.AssetId!.Value).Order());
        Assert.False(page.Catalog.Single(card => card.AssetId == open.AssetId).Flags.KeptAsIs);
        Assert.True(page.Catalog.Single(card => card.AssetId == kept.AssetId).Flags.KeptAsIs);
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_ForAProfileThatIsNotAnAdministrator_FiltersItsOwnGrantedRows()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);

        var all = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48);
        var filtered = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        // The Unmatched file is only under the filter; the lane holds the identified film alone.
        Assert.Equal(1, all.TotalCount);
        Assert.DoesNotContain(all.Catalog, card => card.AssetId == open.AssetId);
        Assert.Equal([open.AssetId], filtered.Catalog.Select(card => card.AssetId!.Value).ToArray());
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_NeverWidensAccess_ToAnUnmatchedFileInALibraryTheProfileCannotSee()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        var hidden = await InsertOwnedWorkWithIdAsync(LecturesLibrary, "Hidden", "Movie");
        var vault = await InsertOwnedWorkWithIdAsync(HiddenVaultLibrary, "Secret", "Movie");
        await MarkUnmatchedAsync(hidden.AssetId, LecturesLibrary, keep: false);
        await MarkUnmatchedAsync(vault.AssetId, HiddenVaultLibrary, keep: false);
        using (var connection = _database.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute(
                "INSERT OR IGNORE INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@library,'none',@now,@now,@now);",
                new { library = HiddenVaultLibrary.ToString("D"), now });
        }

        var page = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);
        var admin = Guid.NewGuid();
        await CreateHumanAsync(admin, EveryFeature, new HashSet<Guid>(), administrator: true);
        var adminPage = await BrowseComposer(admin).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        Assert.Empty(page.Catalog);
        Assert.Equal([hidden.AssetId], adminPage.Catalog.Select(card => card.AssetId!.Value).ToArray());
    }

    [Fact]
    public async Task Browse_TheLibraryFilter_ListsAWorkUnderEveryLibraryItHasAFileIn_YetStillOneRowPerWork()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var work = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Two copies", "Movie");
        await InsertAssetForWorkAsync(work.WorkId, Guid.NewGuid(), LecturesLibrary, "Second copy", createWork: false);
        var composer = BrowseComposer(account);

        var all = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48);
        var inMovies = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: MoviesLibrary);
        var inLectures = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: LecturesLibrary);

        // Whichever file the work's one row shows, the work belongs to both tabs.
        Assert.Equal(1, all.TotalCount);
        Assert.Equal(1, inMovies.TotalCount);
        Assert.Equal(1, inLectures.TotalCount);
        Assert.Equal(all.Catalog[0].AssetId, inLectures.Catalog[0].AssetId);
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_CountsAWorkWhenAnyOfItsFilesIsUnmatched_NotJustTheOneItsRowShows()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var work = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Two files", "Movie");
        var second = Guid.NewGuid();
        await InsertAssetForWorkAsync(work.WorkId, second, MoviesLibrary, "Second file", createWork: false);
        var other = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Fine", "Movie");
        // Mark both files in turn: whichever one the row shows, the work counts while the other is the unmatched one.
        foreach (var unmatchedFile in new[] { work.AssetId, second })
        {
            using (var connection = _database.CreateConnection())
            {
                connection.Execute("DELETE FROM unmatched_assets;");
            }

            await MarkUnmatchedAsync(unmatchedFile, MoviesLibrary, keep: false);

            var page = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);
            var inLibrary = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: MoviesLibrary, unmatchedOnly: true);

            Assert.Equal(1, page.TotalCount);
            Assert.Equal(1, inLibrary.TotalCount);
            Assert.DoesNotContain(page.Catalog, card => card.AssetId == other.AssetId);
        }
    }

    [Fact]
    public async Task Browse_TheLibraryAndUnmatchedFilters_NeverUseAFileTheProfileMayNotSee()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        var work = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Seen in Movies", "Movie");
        var hiddenFile = Guid.NewGuid();
        await InsertAssetForWorkAsync(work.WorkId, hiddenFile, LecturesLibrary, "Hidden copy", createWork: false);
        await MarkUnmatchedAsync(hiddenFile, LecturesLibrary, keep: false);
        var composer = BrowseComposer(account);

        var inHiddenLibrary = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, libraryId: LecturesLibrary);
        var unmatched = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);
        var shownNormally = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48);

        Assert.Equal(1, shownNormally.TotalCount);
        Assert.Equal(0, inHiddenLibrary.TotalCount);
        Assert.Equal(0, unmatched.TotalCount);
    }

    [Fact]
    public async Task BrowseRoute_BindsLibraryIdAndUnmatchedFromTheQueryString()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await InsertOwnedWorkWithIdAsync(LecturesLibrary, "Lecture", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);
        var user = HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        var composer = BrowseComposer(account);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(Authorities());
        builder.Services.AddSingleton(composer);
        foreach (var inferredOnly in new[]
        {
            typeof(BrowseLibraryListService), typeof(DisplayRecentComposerService), typeof(ContributorShelfReadService),
            typeof(IUniversalSearchReadService), typeof(ArtworkLibraryReadService), typeof(ArtworkAssetService),
            typeof(CatalogueResourceAuthorizationService), typeof(MediaEditorEditionArtworkRepository),
        })
        {
            // Other routes in the same group take these; they are only here so route building can infer them.
            builder.Services.AddSingleton(inferredOnly, _ => null!);
        }

        var app = builder.Build();
        app.MapDisplayEndpoints();
        _routeApps.Add(app);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetDisplayBrowse");

        async Task<int> TotalAsync(string query)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { User = user.User, RequestServices = scope.ServiceProvider };
            context.Request.QueryString = new QueryString(query);
            context.Response.Body = new MemoryStream();
            await route.RequestDelegate!(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            using var json = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
            return json.RootElement.GetProperty("totalCount").GetInt32();
        }

        Assert.Equal(2, await TotalAsync("?lane=watch&mediaType=Movie"));
        Assert.Equal(1, await TotalAsync($"?lane=watch&mediaType=Movie&libraryId={MoviesLibrary:D}"));
        Assert.Equal(1, await TotalAsync("?lane=watch&mediaType=Movie&unmatched=true"));
        Assert.Equal(1, await TotalAsync($"?lane=watch&mediaType=Movie&libraryId={MoviesLibrary:D}&unmatched=true"));
        Assert.Equal(0, await TotalAsync($"?lane=watch&mediaType=Movie&libraryId={LecturesLibrary:D}&unmatched=true"));
    }

    // Not async: the request context lives in an async-local the service reads later, so it must be set in the caller's own flow.
    private Task<IReadOnlySet<Guid>> NarrowedWorkIdsAsync(Guid account, Guid? library, bool unmatchedOnly) =>
        BrowseNarrowing.VisibleWorkIdsAsync(
            CreateService(HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId), RealReaders()),
            new UnmatchedAssetRepository(_database),
            library,
            unmatchedOnly,
            default);

    [Fact]
    public async Task GroupNarrowing_TheLibraryFilter_KeepsOnlyThatLibrarysWorks_AndOnlyOnesTheProfileMaySee()
    {
        var admin = Guid.NewGuid();
        await CreateHumanAsync(admin, EveryFeature, new HashSet<Guid>(), administrator: true);
        var inMovies = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Film", "Movie");
        var inLectures = await InsertOwnedWorkWithIdAsync(LecturesLibrary, "Lecture", "Movie");

        Assert.Equal(new[] { inMovies.WorkId, inLectures.WorkId }.Order(), (await NarrowedWorkIdsAsync(admin, null, false)).Order());
        Assert.Equal([inMovies.WorkId], (await NarrowedWorkIdsAsync(admin, MoviesLibrary, false)).ToArray());
        Assert.Equal([inLectures.WorkId], (await NarrowedWorkIdsAsync(admin, LecturesLibrary, false)).ToArray());

        var limited = Guid.NewGuid();
        await CreateHumanAsync(limited, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        // The limited profile still sees its own library's work, with the library given and with none, so an empty answer cannot pass for "narrowed".
        Assert.Equal([inMovies.WorkId], (await NarrowedWorkIdsAsync(limited, MoviesLibrary, false)).ToArray());
        Assert.Equal([inMovies.WorkId], (await NarrowedWorkIdsAsync(limited, null, false)).ToArray());
        Assert.Empty(await NarrowedWorkIdsAsync(limited, LecturesLibrary, false));
    }

    [Fact]
    public async Task GroupNarrowing_TheUnmatchedFilter_ReturnsNothingForALibraryHiddenFromTheProfile_EvenWhenTheLibraryHasUnmatchedFiles()
    {
        var admin = Guid.NewGuid();
        await CreateHumanAsync(admin, EveryFeature, new HashSet<Guid>(), administrator: true);
        var limited = Guid.NewGuid();
        await CreateHumanAsync(limited, EveryFeature, new HashSet<Guid> { LecturesLibrary });
        var matched = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched film", "Movie");
        var lecture = await InsertOwnedWorkWithIdAsync(LecturesLibrary, "Unmatched lecture", "Movie");
        await MarkUnmatchedAsync(lecture.AssetId, LecturesLibrary, keep: false);

        // The administrator proves the file is there to be found.
        Assert.Equal([lecture.WorkId], (await NarrowedWorkIdsAsync(admin, null, true)).ToArray());
        Assert.Equal([lecture.WorkId], (await NarrowedWorkIdsAsync(admin, LecturesLibrary, true)).ToArray());

        Assert.Empty(await NarrowedWorkIdsAsync(limited, null, true));
        Assert.Empty(await NarrowedWorkIdsAsync(limited, LecturesLibrary, true));
        Assert.Empty(await NarrowedWorkIdsAsync(limited, MoviesLibrary, true));
        // And the limited profile still sees what it is granted.
        Assert.Equal([matched.WorkId], (await NarrowedWorkIdsAsync(limited, MoviesLibrary, false)).ToArray());
    }

    [Fact]
    public async Task GroupNarrowing_TheUnmatchedFilter_KeepsWorksWithAnOpenOrKeptUnmatchedFileInThatLibraryOnly()
    {
        var admin = Guid.NewGuid();
        await CreateHumanAsync(admin, EveryFeature, new HashSet<Guid>(), administrator: true);
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        var kept = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Kept", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);
        await MarkUnmatchedAsync(kept.AssetId, MoviesLibrary, keep: true);

        Assert.Equal(new[] { open.WorkId, kept.WorkId }.Order(), (await NarrowedWorkIdsAsync(admin, null, true)).Order());
        Assert.Equal(new[] { open.WorkId, kept.WorkId }.Order(), (await NarrowedWorkIdsAsync(admin, MoviesLibrary, true)).Order());
        Assert.Empty(await NarrowedWorkIdsAsync(admin, LecturesLibrary, true));
    }

    [Fact]
    public async Task GroupNarrowing_ALibraryWithMoreThanTwoHundredWorksKeepsEveryOne()
    {
        var admin = Guid.NewGuid();
        await CreateHumanAsync(admin, EveryFeature, new HashSet<Guid>(), administrator: true);
        for (var index = 0; index < 230; index++)
        {
            await InsertOwnedWorkWithIdAsync(LecturesLibrary, $"Video {index:000}", "Movie");
        }

        Assert.Equal(230, (await NarrowedWorkIdsAsync(admin, LecturesLibrary, false)).Count);
    }

    private async Task MarkUnmatchedAsync(Guid assetId, Guid library, bool keep)
    {
        var unmatched = new UnmatchedAssetRepository(_database);
        var now = DateTimeOffset.UtcNow;
        await unmatched.MarkUnmatchedAsync(assetId, library.ToString("D"), UnmatchedReasons.NoMatch, now, now.AddDays(7));
        if (keep)
        {
            await unmatched.KeepAsIsAsync(assetId, now);
        }
    }

    private RequestAuthorityResolver Authorities() => new(_accounts, _applications, new ProfileRepository(_database));

    private DisplayComposerService BrowseComposer(Guid account)
    {
        var service = CreateService(HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId), RealReaders());
        var cards = new DisplayCardBuilder();
        return new DisplayComposerService(service, cards, new DisplayShelfBuilder(cards), unmatched: new UnmatchedAssetRepository(_database));
    }

    /// <summary>A configuration loader that holds only the libraries list; the library kinds are the built-in ones.</summary>
    private sealed class LibrariesOnlyBrowseLoader : IConfigurationLoader
    {
        public LibrariesOnlyBrowseLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; }

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged { add { } remove { } }

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
