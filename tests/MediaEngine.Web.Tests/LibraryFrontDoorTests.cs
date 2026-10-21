using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Libraries front door, group pages and type pages (Settings > Libraries drills in: /settings/libraries, /watch, /watch/movies).
public sealed class LibraryFrontDoorTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";
    private const string TvId = "33333333-3333-4333-8333-333333333333";
    private const string OtherId = "44444444-4444-4444-8444-444444444444";

    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private LibraryOverviewDto? _overview = new() { MediaTypeCounts = new() { ["Movies"] = 1284, ["Audiobooks"] = 12 } };
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };
    private IReadOnlyList<ViewSourceAdminDto> _viewSources = [];

    // Libraries has pages below it (a group, then a type), so its menu row matches them; every other row stays exact.
    [Fact]
    public void SettingsMenu_LibrariesRow_StaysLitOnTheGroupAndTypePagesBelowIt()
    {
        var rows = Flatten(MediaEngine.Web.Services.Navigation.SettingsMenuBuilder.Build(
            new MediaEngine.Web.Services.Navigation.SettingsMenuContext(true, false, IsMobile: false, 0, false))).ToList();

        Assert.False(rows.Single(row => row.Route == "/settings/libraries").Exact);
        Assert.All(rows.Where(row => row.Route != "/settings/libraries"), row => Assert.True(row.Exact));
    }

    private static IEnumerable<MediaSectionNavigationItem> Flatten(
        IEnumerable<MediaSectionNavigationGroup> groups) =>
        groups.SelectMany(group => FlattenItems(group.Items));

    private static IEnumerable<MediaSectionNavigationItem> FlattenItems(
        IEnumerable<MediaSectionNavigationItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in FlattenItems(item.Children ?? []))
            {
                yield return child;
            }
        }
    }

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", Area = "watch", MediaTypes = ["Movies"],
                PrimaryDestinationSourceId = "movies-main",
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", Notes = "Library Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-old", Path = "F:\\Movies", Notes = "Old drive", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audio-old", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    public LibraryFrontDoorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ => Task.FromResult(_overview));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => Task.FromResult<PathTestResultDto?>(_pathProbe((string)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(new((Guid)args![0]!, null, _viewSources, [])));
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), _ => Task.FromResult<IReadOnlyList<ExcludedFileDto>>([]));
        }));
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static ViewSourceAdminDto ViewSource(string location) =>
        new(Guid.NewGuid(), "local_folder", "Photos", "linked", location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private IRenderedComponent<LibrariesTab> Open(string? subsection = null, string? detail = null)
    {
        var cut = Render<LibrariesTab>(parameters =>
        {
            if (subsection is not null)
            {
                parameters.Add(x => x.Subsection, subsection);
            }

            if (detail is not null)
            {
                parameters.Add(x => x.Detail, detail);
            }
        });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading libraries", cut.Markup));
        return cut;
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<LibrariesTab> cut, string group) => cut.Find($".lib-door__group[data-group={group}]");

    // 1. The groups, their order and their types.
    [Fact]
    public void FrontDoorShowsWatchListenReadAndViewInThatOrderEachListingItsTypes()
    {
        var cut = Open();

        var cards = cut.FindAll(".lib-door__group");
        Assert.Equal(["watch", "listen", "read", "view"], cards.Select(card => card.GetAttribute("data-group")!).ToArray());
        Assert.Equal("Movies · TV Shows · Videos", Text(cards[0].QuerySelector(".lib-door__types")!));
        Assert.Equal("Music · Audiobooks", Text(cards[1].QuerySelector(".lib-door__types")!));
        Assert.Equal("Books · Comics", Text(cards[2].QuerySelector(".lib-door__types")!));
        Assert.Contains("Photos", Text(cards[3].QuerySelector(".lib-door__types")!));
        Assert.Equal("/settings/libraries/watch", cards[0].GetAttribute("href"));
        Assert.Empty(cut.FindAll(".lib-door__group--vault"));
        Assert.DoesNotContain("Vault", cut.Markup);
    }

    [Fact]
    public void FrontDoorDrawsTheVaultLastAsAStripOnlyWhenTheHostAllowsIt()
    {
        var withVault = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Configuration, _configuration).Add(x => x.ShowVault, true));
        var without = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Configuration, _configuration));

        Assert.Equal(["watch", "listen", "read", "view", "vault"], withVault.FindAll(".lib-door__group").Select(card => card.GetAttribute("data-group")!).ToArray());
        Assert.Contains("lib-door__group--strip", withVault.FindAll(".lib-door__group").Last().ClassName);
        Assert.Equal(4, without.FindAll(".lib-door__group").Count);
    }

    // 2. Counts.
    [Fact]
    public void GroupCardShowsFoldersAndTitlesAndAnEmptyGroupSaysNothingAddedYet()
    {
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(Card(cut, "watch").QuerySelector(".lib-door__counts")!)));
        Assert.Equal("1 folder · 12 titles", Text(Card(cut, "listen").QuerySelector(".lib-door__counts")!));
        Assert.Equal("Nothing added yet", Text(Card(cut, "read").QuerySelector(".lib-door__counts")!));
        Assert.Contains("Watch, 2 folders, 1,284 titles, all folders healthy", Card(cut, "watch").GetAttribute("aria-label"));
    }

    [Fact]
    public void WhenTheOverviewCannotBeReadTheCardsShowFolderCountsOnly()
    {
        _overview = null;
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders", Text(Card(cut, "watch").QuerySelector(".lib-door__counts")!)));
        Assert.DoesNotContain("titles", Text(Card(cut, "listen")));
    }

    [Fact]
    public void ViewCardCountsTheDistinctFoldersAcrossProfilesAndShowsNoTitles()
    {
        _viewSources = [ViewSource("D:\\Photos"), ViewSource("d:\\photos"), ViewSource("E:\\Phone")];
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders", Text(Card(cut, "view").QuerySelector(".lib-door__counts")!)));
    }

    [Fact]
    public void WithNoFoldersAnywhereEveryCardSaysNothingAddedYet()
    {
        _configuration.Libraries.ForEach(library => library.Sources.Clear());
        var cut = Open();

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".lib-door__group"), card => Assert.Equal("Nothing added yet", Text(card.QuerySelector(".lib-door__counts")!))));
    }

    // 3. Worst health.
    [Fact]
    public void AnUnavailableFolderGivesItsGroupTheWarningIconAndTheOthersTheTick()
    {
        _pathProbe = path => path == "F:\\Movies" ? new() { Exists = false } : new() { Exists = true, HasRead = true, HasWrite = true };
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("1 folder unavailable", Card(cut, "watch").QuerySelector(".lib-health--warning")!.GetAttribute("aria-label")));
        Assert.Contains("1 folder unavailable", Card(cut, "watch").GetAttribute("aria-label"));
        Assert.Equal("All folders healthy", Card(cut, "listen").QuerySelector(".lib-health--ok")!.GetAttribute("aria-label"));
        Assert.Empty(Card(cut, "listen").QuerySelectorAll(".lib-health--warning"));
    }

    // 4. Phone.
    [Fact]
    public async Task OnAPhoneTheCardsAndCountsShowWithTheNoteAndNoControlToChangeFolders()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(Card(cut, "watch").QuerySelector(".lib-door__counts")!)));
        Assert.Equal("Use a desktop or tablet to change folders.", Text(cut.Find(".lib-door__note")));
        Assert.Empty(cut.FindAll("button"));
        foreach (var banned in new[] { "Add folder", "Add Folder", "Convert", "Merge" })
        {
            Assert.DoesNotContain(banned, cut.Markup);
        }
    }

    [Fact]
    public async Task OnAPhoneAGroupPageKeepsTheRowsAndTheNoteWithoutAnyControl()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var cut = Open("watch");

        Assert.Equal(3, cut.FindAll(".lib-page__row").Count);
        Assert.Equal("Use a desktop or tablet to change folders.", Text(cut.Find(".lib-page__note")));
        Assert.Empty(cut.FindAll("button"));
    }

    // 5. The three levels, each from its own parameters (a reload is the same parameters again).
    [Fact]
    public void WatchOpensTheGroupPageWithItsTypeRowsAndBreadcrumb()
    {
        var cut = Open("watch");

        Assert.Equal(["Movies", "TV Shows", "Videos"], cut.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal("Name Folders Status", Text(cut.Find(".lib-page__head")));
        Assert.Equal("Watch", Text(cut.Find(".lib-page__title")));
        Assert.Equal("/settings/libraries", cut.Find(".lib-crumbs a").GetAttribute("href"));
        Assert.Equal("Watch", Text(cut.Find(".lib-crumbs [aria-current=page]")));
        Assert.Equal("/settings/libraries/watch/movies", cut.Find(".lib-page__row[data-type=movies]").GetAttribute("href"));
        cut.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(cut.Find(".lib-page__row[data-type=movies] .lib-page__counts"))));
        Assert.Equal("Nothing added yet", Text(cut.Find(".lib-page__row[data-type=tv] .lib-page__counts")));
        Assert.Empty(cut.FindAll(".library-folders"));
    }

    [Fact]
    public void WatchMoviesOpensTheFolderTableWithoutTheTypeListAndTheRecycleBinLast()
    {
        var cut = Open("watch", "movies");
        cut.WaitForElement(".folder-table");

        Assert.Equal(["Libraries", "Watch"], cut.FindAll(".lib-crumbs a").Select(Text).ToArray());
        Assert.Equal("Movies", Text(cut.Find(".lib-crumbs [aria-current=page]")));
        Assert.Equal(2, cut.FindAll(".folder-table__row").Count);
        Assert.Empty(cut.FindAll(".library-folders__type"));
        Assert.Empty(cut.FindAll(".library-folders__types"));
        var recycle = cut.Find(".library-folders__detail .library-folders__recycle");
        Assert.Null(recycle.NextElementSibling);
        Assert.Contains("Recycle bin", recycle.TextContent);
    }

    [Fact]
    public void RenderingTheSameParametersAgainGivesTheSameLevel()
    {
        var first = Open("watch", "movies");
        first.WaitForElement(".folder-table");
        var second = Open("watch", "movies");
        second.WaitForElement(".folder-table");

        Assert.Equal(first.Find(".lib-crumbs").TextContent, second.Find(".lib-crumbs").TextContent);
        Assert.Equal(first.FindAll(".folder-table__row").Count, second.FindAll(".folder-table__row").Count);
        Assert.Equal(Text(first.Find(".folder-table__head")), Text(second.Find(".folder-table__head")));
    }

    [Fact]
    public void MovingFromTheGroupPageToATypeAndBackKeepsWorkingOnTheSameComponent()
    {
        var cut = Open("watch");
        cut.Render(parameters => parameters.Add(x => x.Subsection, "watch").Add(x => x.Detail, "movies"));
        cut.WaitForElement(".folder-table");
        cut.Render(parameters => parameters.Add(x => x.Subsection, "watch").Add(x => x.Detail, (string?)null));

        cut.WaitForElement(".lib-page__row");
        Assert.Equal("Watch", Text(cut.Find(".lib-page__title")));
    }

    // 6. Saved links.
    [Fact]
    public void ASavedTypeLinkOnTheFrontDoorRedirectsToThatTypesAddress()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/libraries?type=movies");

        Render<LibrariesTab>();

        Assert.Equal("http://localhost/settings/libraries/watch/movies", navigation.Uri);
    }

    [Fact]
    public void ASavedLinkToAnUnknownTypeStaysOnTheFrontDoor()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/libraries?type=podcasts");

        var cut = Open();

        Assert.Equal("http://localhost/settings/libraries?type=podcasts", navigation.Uri);
        Assert.Equal(4, cut.FindAll(".lib-door__group").Count);
    }

    // 7. View's photos and the Import folder.
    [Fact]
    public void ViewOpensItsGroupPageAndPhotosOpensTheViewSettings()
    {
        var group = Open("view");
        Assert.Equal(["Photos & personal videos"], group.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal("/settings/libraries/view/photos", group.Find(".lib-page__row").GetAttribute("href"));
        Assert.Empty(group.FindAll(".library-detail"));

        var photos = Open("view", "photos");
        photos.WaitForElement(".library-detail");
        Assert.Equal(["Libraries", "View"], photos.FindAll(".lib-crumbs a").Select(Text).ToArray());
        Assert.Equal("Photos & personal videos", Text(photos.Find(".lib-crumbs [aria-current=page]")));
    }

    [Fact]
    public void ImportOpensTheImportFolderPanelUnderTheLibrariesBreadcrumb()
    {
        var cut = Open("import");
        cut.WaitForElement("#library-import-title");

        Assert.Equal("Import folder", Text(cut.Find(".lib-crumbs [aria-current=page]")));
        Assert.Equal("Libraries", Text(cut.Find(".lib-crumbs a")));
        Assert.Empty(cut.FindAll(".library-folders__type"));
    }

    // 8. Libraries that are not a standard type stay visible.
    [Fact]
    public void AnOtherLibraryWithTheListenAreaIsARowOnTheListenGroupPage()
    {
        _configuration.Libraries.Add(new()
        {
            Id = OtherId,
            Name = "Podcasts",
            Area = "listen",
            MediaTypes = ["Podcasts"],
            Sources = [new() { Id = "pod-1", Path = "C:\\media\\Podcasts", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
        var cut = Open("listen");

        Assert.Equal(["Music", "Audiobooks", "Podcasts"], cut.FindAll(".lib-page__row strong").Select(Text).ToArray());
        var row = cut.Find($".lib-page__row[data-type=other-{OtherId}]");
        Assert.Equal($"/settings/libraries/listen/other-{OtherId}", row.GetAttribute("href"));
        Assert.Contains("1 folder", Text(row));

        var page = Open("listen", $"other-{OtherId}");
        page.WaitForElement(".folder-table");
        Assert.Equal("Podcasts", Text(page.Find(".lib-crumbs [aria-current=page]")));
    }

    [Fact]
    public void AnOtherLibraryWithNoKnownAreaIsARowUnderTheImportFolderOnTheFrontDoor()
    {
        _configuration.Libraries.Add(new()
        {
            Id = OtherId,
            Name = "Archive",
            Area = "",
            MediaTypes = ["Archive"],
            Sources = [new() { Id = "arc-1", Path = "C:\\media\\Archive", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
        var cut = Open();

        var rows = cut.FindAll(".lib-door__import");
        Assert.Equal(2, rows.Count);
        Assert.Equal($"/settings/libraries/other/other-{OtherId}", rows[1].GetAttribute("href"));

        var page = Open("other", $"other-{OtherId}");
        page.WaitForElement(".folder-table");
        Assert.Equal("Archive", Text(page.Find(".lib-crumbs [aria-current=page]")));
    }

    // Addresses that name nothing.
    [Theory]
    [InlineData("watch", "podcasts")]
    [InlineData("vault", null)]
    [InlineData("nowhere", null)]
    [InlineData("view", "movies")]
    [InlineData("import", "movies")]
    public void AnAddressThatNamesNothingShowsAPlainNotFoundWithALinkBack(string subsection, string? detail)
    {
        var cut = Open(subsection, detail);

        Assert.Contains("This library page does not exist", cut.Markup);
        Assert.Equal("/settings/libraries", cut.Find("a[href='/settings/libraries']").GetAttribute("href"));
        Assert.DoesNotContain("Vault", cut.Markup);
    }

    [Fact]
    public void ThePanelOnAnUnknownTypeKeyShowsASmallStateWithALinkBack()
    {
        var cut = Render<LibraryFoldersPanel>(parameters => parameters.Add(x => x.ShowTypeNav, false).Add(x => x.TypeKey, "podcasts"));

        cut.WaitForAssertion(() => Assert.Contains("This library type does not exist", cut.Markup));
        Assert.Equal("/settings/libraries", cut.Find("a[href='/settings/libraries']").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".folder-table"));
    }

    // The group mapping and the folder health words.
    [Fact]
    public void EveryStandardTypeBelongsToExactlyOneGroupAndMatchesItsArea()
    {
        foreach (var type in LibraryTypeCatalog.Types)
        {
            var owners = LibraryGroupCatalog.Groups.Where(group => group.TypeKeys.Contains(type.Key)).ToList();
            var owner = Assert.Single(owners);
            Assert.Equal(type.Area, owner.Key);
        }

        Assert.Equal(["watch", "listen", "read", "view", "vault"], LibraryGroupCatalog.Groups.Select(group => group.Key).ToArray());
        Assert.Equal("view", LibraryGroupCatalog.GroupFor("photos")!.Key);
        Assert.Equal(["watch", "listen", "read", "view"], LibraryGroupCatalog.Visible(false).Select(group => group.Key).ToArray());
    }

    [Fact]
    public void EveryGroupKeyIsAnAddressTheSettingsPageAndPhonesAccept()
    {
        foreach (var group in LibraryGroupCatalog.Groups)
        {
            Assert.True(SettingsNav.IsLibraryRouteSegment(group.Key), group.Key);
        }

        Assert.True(SettingsNav.IsLibraryRouteSegment("import"));
        Assert.False(SettingsNav.IsLibraryRouteSegment("providers"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "watch", "movies"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "nowhere"));
    }

    [Fact]
    public void WorstHealthNamesTheFoldersThatAreUnavailableOrNeedAccess()
    {
        var sources = new List<LibrarySourceDto>
        {
            new() { Id = "a", Path = "A:\\", ManagementMode = "managed_by_tuvima" },
            new() { Id = "b", Path = "B:\\", ManagementMode = "existing_library" },
            new() { Id = "c", Path = "C:\\", ManagementMode = "existing_library" },
            new() { Id = "d", Path = "D:\\", ManagementMode = "existing_library" },
        };
        var issues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a:\\"] = LibraryFolderHealth.NoAccess,
            ["B:\\"] = LibraryFolderHealth.NoAccess,
            ["C:\\"] = LibraryFolderHealth.Unavailable,
        };

        Assert.True(LibraryFolderHealth.Worst([sources[3]], issues).Healthy);
        Assert.Equal("1 folder unavailable", LibraryFolderHealth.Worst([sources[2]], issues).Text);
        Assert.Equal("2 folders need access", LibraryFolderHealth.Worst([sources[0], sources[1]], issues).Text);
        Assert.Equal("1 folder unavailable, 1 folder needs access", LibraryFolderHealth.Worst([sources[0], sources[2]], issues).Text);
    }

    [Fact]
    public async Task FolderHealthChecksManagedFoldersForWriteAndReadOnlyFoldersForReadAndSkipsUnreadableChecks()
    {
        var configuration = new LibrariesConfigurationDto
        {
            Libraries =
            [
                new()
                {
                    Id = "x", Name = "X",
                    Sources =
                    [
                        new() { Id = "m", Path = "M:\\", ManagementMode = "managed_by_tuvima" },
                        new() { Id = "r", Path = "R:\\", ManagementMode = "existing_library" },
                        new() { Id = "gone", Path = "G:\\", ManagementMode = "existing_library" },
                        new() { Id = "unknown", Path = "U:\\", ManagementMode = "existing_library" },
                    ],
                },
            ],
        };

        var issues = await LibraryFolderHealth.CheckAsync(configuration, path => Task.FromResult<PathTestResultDto?>(path switch
        {
            "M:\\" => new() { Exists = true, HasRead = true, HasWrite = false },
            "R:\\" => new() { Exists = true, HasRead = true, HasWrite = false },
            "G:\\" => new() { Exists = false },
            _ => null,
        }));

        Assert.Equal("No access", issues["M:\\"]);
        Assert.False(issues.ContainsKey("R:\\"));
        Assert.Equal("Folder unavailable", issues["G:\\"]);
        Assert.False(issues.ContainsKey("U:\\"));
    }
}
