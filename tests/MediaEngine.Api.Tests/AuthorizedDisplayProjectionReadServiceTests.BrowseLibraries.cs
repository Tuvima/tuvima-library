using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;

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

        var service = CreateService(HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId), new StubRawProjection([], []));
        return new BrowseLibraryListService(service, BrowseLibrariesConfig(), new VaultLibraryRepository(_database));
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
    public async Task BrowseLibraries_AProfileGrantedOnlyLectures_GetsOnlyLectures()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { LecturesLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([LecturesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_AVaultLibraryIsNeverListed_EvenForAnAdministratorHoldingAGrantOnIt()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { HiddenVaultLibrary, LecturesLibrary }, administrator: true);

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
            new HashSet<Guid> { MoviesLibrary, MusicLibrary, BooksLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.DoesNotContain(libraries, library => library.Area == "listen");
        Assert.Equal([MoviesLibrary, BooksLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_ViewLibrariesAreNotListed()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { PhotosLibrary, MoviesLibrary });

        var libraries = await CreateBrowseLibraryList(account).ListAsync(default);

        Assert.Equal([MoviesLibrary], libraries.Select(library => library.Id).ToArray());
    }

    [Fact]
    public async Task BrowseLibraries_AProfileWithNoGrantsGetsNothing()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());

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
    public async Task Browse_TheLibraryFilter_ForALibraryTheProfileHasNoGrantOn_IsEmptyLikeAnEmptyLibrary()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary });
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
    public async Task Browse_TheUnmatchedFilter_KeepsOnlyRowsWithAnUnmatchedFileThatIsNotKept()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        var kept = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Kept", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);
        await MarkUnmatchedAsync(kept.AssetId, MoviesLibrary, keep: true);

        var page = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        Assert.Equal([open.AssetId], page.Catalog.Select(card => card.AssetId!.Value).ToArray());
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_ForAProfileThatIsNotAnAdministrator_FiltersItsOwnGrantedRows()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary });
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open", "Movie");
        await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Matched", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);

        var all = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48);
        var filtered = await BrowseComposer(account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        Assert.Equal(2, all.TotalCount);
        Assert.Equal([open.AssetId], filtered.Catalog.Select(card => card.AssetId!.Value).ToArray());
    }

    [Fact]
    public async Task Browse_TheUnmatchedFilter_NeverWidensAccess_ToAnUnmatchedFileInALibraryTheProfileCannotSee()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary });
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
