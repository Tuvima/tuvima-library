using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Profiles;
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
    // Holds every folder check open until released, to look at the page while the checks are still running.
    private TaskCompletionSource? _pathGate;
    private bool _profilesFail;
    // The Engine's list of library kinds; a test adds a kind here the way a plugin would.
    private List<LibraryKindDto> _kinds = LibraryKindsTestData.BuiltIn();
    private bool _viewSourcesUnavailable;
    // The profile's Personal Space; the View kind pages list a profile's folders only once it has one.
    private ViewPersonalSpaceAdminDto? _personalSpace;
    private static readonly ViewPersonalSpaceAdminDto TestSpace = new(Guid.NewGuid(), "C:\\View\\Profiles\\Test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    // What the View kind pages read and send: the profile's saved policy, the policy saves and the folders added.
    private static readonly Guid TestProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private ViewProfilePolicyDto _policy = new()
    {
        ProfileId = TestProfileId,
        ViewEnabled = true,
        AccessSharedLibrary = true,
        SubmitToSharedLibrary = true,
        AllowGallerySharing = true,
        AllowPhotos = false,
        AllowPersonalVideos = false,
        AllowDocuments = true,
    };
    private readonly List<UpdateViewProfilePolicyRequest> _policySaves = [];
    private readonly List<CreateViewSourceRequest> _createdSources = [];

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
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"],
                PrimaryDestinationSourceId = "movies-main",
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", Notes = "Library Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-old", Path = "F:\\Movies", Notes = "Old drive", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
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
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_kinds));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => ProbeAsync((string)args![0]!));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(_viewSourcesUnavailable ? null : new((Guid)args![0]!, _personalSpace, _viewSources, [])));
            // The client answers an empty list and records an error when the profiles cannot be read.
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(_profilesFail
                ? new List<ProfileViewModel>()
                : [new ProfileViewModel(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow)]));
            stub.SetHandler("get_LastError", _ => _profilesFail ? "The Engine did not answer." : null);
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfilePolicyAsync), _ => Task.FromResult<ViewProfilePolicyDto?>(_policy));
            stub.SetHandler(nameof(IEngineApiClient.UpdateViewProfilePolicyAsync), args =>
            {
                var request = (UpdateViewProfilePolicyRequest)args![1]!;
                _policySaves.Add(request);
                _policy = new ViewProfilePolicyDto
                {
                    ProfileId = TestProfileId,
                    ViewEnabled = request.ViewEnabled,
                    AccessSharedLibrary = request.AccessSharedLibrary,
                    SubmitToSharedLibrary = request.SubmitToSharedLibrary,
                    AllowGallerySharing = request.AllowGallerySharing,
                    AllowPhotos = request.AllowPhotos ?? _policy.AllowPhotos,
                    AllowPersonalVideos = request.AllowPersonalVideos ?? _policy.AllowPersonalVideos,
                    AllowDocuments = request.AllowDocuments ?? _policy.AllowDocuments,
                };
                return Task.FromResult<ViewProfilePolicyDto?>(_policy);
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateViewProfileSourceAsync), args =>
            {
                var request = (CreateViewSourceRequest)args![1]!;
                _createdSources.Add(request);
                return Task.FromResult<ViewSourceAdminDto?>(ViewSource(request.Path ?? "C:\\New", request.Name, request.ContentKind ?? "photos"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), _ => Task.FromResult<IReadOnlyList<ExcludedFileDto>>([]));
        }));
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
    }

    private async Task<PathTestResultDto?> ProbeAsync(string path)
    {
        if (_pathGate is not null)
        {
            await _pathGate.Task;
        }

        return _pathProbe(path);
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static ViewSourceAdminDto ViewSource(string location) =>
        new(Guid.NewGuid(), "local_folder", "Photos", "linked", location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ViewSourceAdminDto ViewSource(
        string location, string name, string kind, int skipped = 0, bool photosAndVideos = false) =>
        new(Guid.NewGuid(), "local_folder", name, "linked", location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContentKind: kind, SkippedFileCount: skipped, AcceptsPhotosAndVideos: photosAndVideos);

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

    // A kind a plugin adds is just another entry in the list, so every screen drawn from the list shows it with no code change.
    [Fact]
    public void AKindFromAPluginShowsOnTheFrontDoorCardAndTheGroupPageAndOpensItsOwnAddress()
    {
        _kinds = LibraryKindsTestData.BuiltIn(LibraryKindsTestData.Podcasts());

        var door = Open();
        Assert.Equal("Music · Audiobooks · Podcasts", Text(Card(door, "listen").QuerySelector(".lib-door__types")!));
        Assert.Equal("Movies · TV Shows · Videos", Text(Card(door, "watch").QuerySelector(".lib-door__types")!));

        var group = Open("listen");
        Assert.Equal(["Music", "Audiobooks", "Podcasts"], group.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal("/settings/libraries/listen/podcasts", group.Find(".lib-page__row[data-type=podcasts]").GetAttribute("href"));

        var page = Open("listen", "podcasts");
        Assert.Empty(page.FindAll(".lib-page__row"));
        Assert.DoesNotContain("This library page does not exist", page.Markup);
    }

    // The kinds that are defined but not offered yet (YouTube, Other audio, Other reading) appear on no screen.
    [Fact]
    public void KindsMarkedUnavailableAreOfferedOnNoScreen()
    {
        var door = Open();
        var group = Open("listen");

        foreach (var hidden in new[] { "YouTube", "Other audio", "Other reading" })
        {
            Assert.DoesNotContain(hidden, door.Markup);
            Assert.DoesNotContain(hidden, group.Markup);
        }

        Assert.Equal(["Music", "Audiobooks"], group.FindAll(".lib-page__row strong").Select(Text).ToArray());
    }

    [Fact]
    public void WhenTheListOfKindsCannotBeReadThePageSaysSoInsteadOfShowingAnEmptyLibrary()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(null));
        }));

        var cut = Open();

        Assert.Contains("list of library kinds could not be loaded", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleAndBreadcrumbOfAKindFromAPluginComeFromTheList()
    {
        var kinds = new LibraryKindCatalog(LibraryKindsTestData.BuiltIn(LibraryKindsTestData.Podcasts()));

        var level = LibraryAddress.Describe(kinds, "listen", "podcasts", showVault: false);

        Assert.Equal("Podcasts", level.Title);
        Assert.Equal(["Libraries", "Listen", "Podcasts"], level.Crumbs.Select(crumb => crumb.Label).ToArray());
        Assert.Equal("Libraries", LibraryAddress.Describe(LibraryKindsTestData.Catalog, "listen", "podcasts", showVault: false).Title);
    }

    [Fact]
    public void FrontDoorDrawsTheVaultLastAsAStripOnlyWhenTheHostAllowsIt()
    {
        var withVault = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration).Add(x => x.ShowVault, true));
        var without = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration));

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

    // Health is shown only once it is known: no tick before the folder checks finish, and View's folders are never checked.
    [Fact]
    public async Task NoHealthIconShowsWhileTheFolderChecksAreRunningAndTheWarningAppearsWhenTheyFinish()
    {
        _pathGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pathProbe = path => path == "F:\\Movies" ? new() { Exists = false } : new() { Exists = true, HasRead = true, HasWrite = true };
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(Card(cut, "watch").QuerySelector(".lib-door__counts")!)));
        Assert.Empty(cut.FindAll(".lib-health"));

        var group = Open("watch");
        group.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(group.Find(".lib-page__row[data-type=movies] .lib-page__counts"))));
        Assert.Empty(group.FindAll(".lib-health"));

        _pathGate.SetResult();
        await Task.Yield();
        cut.WaitForAssertion(() => Assert.Equal("1 folder unavailable", Card(cut, "watch").QuerySelector(".lib-health--warning")!.GetAttribute("aria-label")));
        Assert.NotEmpty(Card(cut, "listen").QuerySelectorAll(".lib-health--ok"));
    }

    [Fact]
    public void ViewShowsNoHealthIconOnItsCardOrItsRowBecauseItsFoldersAreNeverChecked()
    {
        var cut = Open();
        cut.WaitForAssertion(() => Assert.NotEmpty(Card(cut, "watch").QuerySelectorAll(".lib-health--ok")));
        Assert.Empty(Card(cut, "view").QuerySelectorAll(".lib-health"));

        var group = Open("view");
        group.WaitForElement(".lib-page__row");
        Assert.Empty(group.FindAll(".lib-health"));
    }

    [Fact]
    public void TheWarningWordsOpenFromTheLinkItselfSoKeyboardFocusShowsThem()
    {
        _pathProbe = path => path == "F:\\Movies" ? new() { Exists = false } : new() { Exists = true, HasRead = true, HasWrite = true };
        var cut = Open();
        cut.WaitForAssertion(() => Assert.NotEmpty(Card(cut, "watch").QuerySelectorAll(".lib-health--warning")));

        var tip = Card(cut, "watch").ParentElement!;
        Assert.Equal("1 folder unavailable", tip.GetAttribute("data-playback-tooltip"));
        Assert.Null(Card(cut, "listen").ParentElement!.GetAttribute("data-playback-tooltip"));

        var group = Open("watch");
        group.WaitForAssertion(() => Assert.Equal("1 folder unavailable", group.Find(".lib-page__row[data-type=movies]").ParentElement!.GetAttribute("data-playback-tooltip")));
    }

    // The View folder count is unknown (no counts line), never "Nothing added yet", when it could not be read.
    [Fact]
    public void ViewShowsNoCountsLineWhenTheProfilesCannotBeRead()
    {
        _profilesFail = true;
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("2 folders · 1,284 titles", Text(Card(cut, "watch").QuerySelector(".lib-door__counts")!)));
        cut.WaitForAssertion(() => Assert.NotEmpty(Card(cut, "watch").QuerySelectorAll(".lib-health")));
        Assert.Equal(string.Empty, Text(Card(cut, "view").QuerySelector(".lib-door__counts")!));
        Assert.DoesNotContain("Nothing added yet", Text(Card(cut, "view")));
    }

    [Fact]
    public void ViewShowsNoCountsLineWhenAProfilesSourcesCannotBeRead()
    {
        _viewSourcesUnavailable = true;
        var cut = Open();

        cut.WaitForAssertion(() => Assert.NotEmpty(Card(cut, "watch").QuerySelectorAll(".lib-health")));
        Assert.Equal(string.Empty, Text(Card(cut, "view").QuerySelector(".lib-door__counts")!));
    }

    [Fact]
    public void ViewShowsNothingAddedYetWhenThereAreNoProfilesAndNoErrorWasRecorded()
    {
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("Nothing added yet", Text(Card(cut, "view").QuerySelector(".lib-door__counts")!)));
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
    public void WatchOpensTheGroupPageWithItsTypeRows()
    {
        var cut = Open("watch");

        Assert.Equal(["Movies", "TV Shows", "Videos"], cut.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal("Name Folders Status", Text(cut.Find(".lib-page__head")));
        // The Settings frame carries the breadcrumb and the group's name as the title; the page repeats neither.
        Assert.Empty(cut.FindAll(".libraries h1, .libraries h2, .libraries h3, .libraries nav[aria-label=Breadcrumb]"));
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

        Assert.Empty(cut.FindAll(".libraries nav[aria-label=Breadcrumb]"));
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
        Assert.Equal(["Movies", "TV Shows", "Videos"], cut.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Empty(cut.FindAll(".folder-table"));
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
    public void AnOldBookmarkToTheVideosTypePageLandsOnTheOtherVideosPage()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/libraries/watch/videos");

        Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, "watch").Add(x => x.Detail, "videos"));

        Assert.Equal("http://localhost/settings/libraries/watch/other_videos", navigation.Uri);
    }

    [Fact]
    public void AnOldVideosAddressUnderAnotherGroupIsNotRedirected()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/libraries/listen/videos");

        var cut = Open("listen", "videos");

        Assert.Equal("http://localhost/settings/libraries/listen/videos", navigation.Uri);
        Assert.Contains("This library page does not exist", cut.Markup);
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
        Assert.Equal(["Photos", "Personal videos", "Documents"], group.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal(
            ["/settings/libraries/view/photos", "/settings/libraries/view/personal_videos", "/settings/libraries/view/documents"],
            group.FindAll(".lib-page__row").Select(row => row.GetAttribute("href")!).ToArray());
        Assert.Empty(group.FindAll(".library-detail"));

        var photos = Open("view", "photos");
        photos.WaitForElement(".library-detail");
        Assert.Empty(photos.FindAll(".libraries nav[aria-label=Breadcrumb]"));
    }

    [Fact]
    public void ImportOpensTheImportFolderPanelWithoutATypeList()
    {
        var cut = Open("import");
        cut.WaitForElement("#library-import-title");

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
        Assert.Empty(page.FindAll(".libraries nav[aria-label=Breadcrumb]"));
    }

    [Fact]
    public void SeveralNamedLibrariesOfAPlainKindAreEachARowOnTheWatchGroupPage()
    {
        _configuration.Libraries.Add(new()
        {
            Id = "55555555-5555-4555-8555-555555555555",
            Name = "Lectures",
            Kind = "catalogued",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "lec-1", Path = "C:\\media\\Lectures", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
        _configuration.Libraries.Add(new()
        {
            Id = OtherId,
            Name = "Family recordings",
            Kind = "catalogued",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "fam-1", Path = "C:\\media\\Family", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
        var cut = Open("watch");

        Assert.Equal(["Movies", "TV Shows", "Lectures", "Family recordings"], cut.FindAll(".lib-page__row strong").Select(Text).ToArray());
        Assert.Equal($"/settings/libraries/watch/other-{OtherId}", cut.Find($".lib-page__row[data-type=other-{OtherId}]").GetAttribute("href"));
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
        Assert.Empty(page.FindAll(".libraries nav[aria-label=Breadcrumb]"));
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
        var cut = Render<LibraryFoldersPanel>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.ShowTypeNav, false).Add(x => x.TypeKey, "podcasts"));

        cut.WaitForAssertion(() => Assert.Contains("This library type does not exist", cut.Markup));
        Assert.Equal("/settings/libraries", cut.Find("a[href='/settings/libraries']").GetAttribute("href"));
        Assert.Empty(cut.FindAll(".folder-table"));
    }

    // The group mapping and the folder health words.
    [Fact]
    public void EveryStandardTypeBelongsToExactlyOneGroupAndMatchesItsArea()
    {
        var kinds = LibraryKindsTestData.Catalog;
        foreach (var type in kinds.Types)
        {
            var owners = kinds.Groups.Where(group => group.TypeKeys.Contains(type.Key)).ToList();
            var owner = Assert.Single(owners);
            Assert.Equal(type.Area, owner.Key);
        }

        Assert.Equal(["watch", "listen", "read", "view", "vault"], kinds.Groups.Select(group => group.Key).ToArray());
        Assert.Equal("view", kinds.GroupFor("photos")!.Key);
        Assert.Equal(["watch", "listen", "read", "view"], kinds.Visible(false).Select(group => group.Key).ToArray());
    }

    [Fact]
    public void EveryGroupKeyIsAnAddressTheSettingsPageAndPhonesAccept()
    {
        foreach (var group in LibraryKindsTestData.Catalog.Groups)
        {
            Assert.True(SettingsNav.IsLibraryRouteSegment(group.Key), group.Key);
        }

        Assert.True(SettingsNav.IsLibraryRouteSegment("import"));
        Assert.False(SettingsNav.IsLibraryRouteSegment("providers"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "watch", "movies"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries));
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "nowhere"));
    }

    // The Settings frame shows each level's title and breadcrumb; this is the one rule for both.
    [Theory]
    [InlineData(null, null, "Libraries", "")]
    [InlineData("watch", null, "Watch", "Libraries>/settings/libraries|Watch")]
    [InlineData("WATCH", "Movies", "Movies", "Libraries>/settings/libraries|Watch>/settings/libraries/watch|Movies")]
    [InlineData("listen", "audiobooks", "Audiobooks", "Libraries>/settings/libraries|Listen>/settings/libraries/listen|Audiobooks")]
    [InlineData("view", null, "View", "Libraries>/settings/libraries|View")]
    [InlineData("view", "photos", "Photos", "Libraries>/settings/libraries|View>/settings/libraries/view|Photos")]
    [InlineData("view", "personal_videos", "Personal videos", "Libraries>/settings/libraries|View>/settings/libraries/view|Personal videos")]
    [InlineData("view", "documents", "Documents", "Libraries>/settings/libraries|View>/settings/libraries/view|Documents")]
    [InlineData("view", "podcasts", "Libraries", "")]
    [InlineData("import", null, "Import folder", "Libraries>/settings/libraries|Import folder")]
    [InlineData("other", "other-abc", "Other library", "Libraries>/settings/libraries|Other library")]
    [InlineData("listen", "other-abc", "Other library", "Libraries>/settings/libraries|Listen>/settings/libraries/listen|Other library")]
    [InlineData("watch", "podcasts", "Libraries", "")]
    [InlineData("view", "movies", "Libraries", "")]
    [InlineData("import", "movies", "Libraries", "")]
    [InlineData("nowhere", null, "Libraries", "")]
    [InlineData("vault", null, "Libraries", "")]
    public void AnAddressDescribesItsTitleAndBreadcrumbForTheSettingsFrame(string? subsection, string? detail, string title, string crumbs)
    {
        var level = LibraryAddress.Describe(LibraryKindsTestData.Catalog, subsection, detail, showVault: false);

        Assert.Equal(title, level.Title);
        Assert.Equal(crumbs, string.Join("|", level.Crumbs.Select(crumb => crumb.Href is null ? crumb.Label : $"{crumb.Label}>{crumb.Href}")));
    }

    [Fact]
    public void TheVaultAddressHasATitleAndBreadcrumbOnlyWhenTheVaultMayBeShown()
    {
        Assert.Equal("Vault", LibraryAddress.Describe(LibraryKindsTestData.Catalog, "vault", null, showVault: true).Title);
        Assert.Equal("Libraries", LibraryAddress.Describe(LibraryKindsTestData.Catalog, "vault", null, showVault: false).Title);
        Assert.Empty(LibraryAddress.Describe(LibraryKindsTestData.Catalog, "vault", null, showVault: false).Crumbs);
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

    // 9. View has a page per kind: Photos, Personal videos and Documents.
    [Fact]
    public void TheViewGroupPageShowsAPhotosPersonalVideosAndDocumentsRowEachWithItsOwnFolderCount()
    {
        _viewSources =
        [
            ViewSource("D:\\Pictures", "Pictures", "photos"),
            ViewSource("E:\\Phone", "Phone", "photos"),
            ViewSource("C:\\Paperwork", "Paperwork", "documents"),
        ];
        var group = Open("view");

        group.WaitForAssertion(() => Assert.Equal(["2 folders", "Nothing added yet", "1 folder"],
            group.FindAll(".lib-page__row").Select(row => Text(row.QuerySelector(".lib-page__counts")!)).ToArray()));
        Assert.Equal(["Photos", "Personal videos", "Documents"], group.FindAll(".lib-page__row strong").Select(Text).ToArray());
    }

    [Fact]
    public void TheDocumentsPageListsOnlyDocumentsFolders()
    {
        _personalSpace = TestSpace;
        _viewSources =
        [
            ViewSource("D:\\Pictures", "Pictures", "photos"),
            ViewSource("C:\\Paperwork", "Paperwork", "documents"),
            ViewSource("C:\\Clips", "Clips", "personal_videos"),
        ];
        var page = Open("view", "documents");

        page.WaitForAssertion(() => Assert.Equal(["Paperwork"], page.FindAll(".view-source-row strong").Select(Text).ToArray()));
        Assert.Contains("Who can see documents", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAFolderOnTheDocumentsPageSendsTheDocumentsKind()
    {
        var page = Render<ViewLibrarySettings>(parameters => parameters
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kind, "documents")
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.StartAdding, true)
            .Add(x => x.InitialPath, "F:\\Paperwork"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".libraries-form-grid")));
        var owner = page.FindComponents<MediaEngine.Web.Components.Shared.AppSelect>().First(select => select.Instance.Label == "Owner profile");
        await page.InvokeAsync(() => owner.Instance.ValueChanged.InvokeAsync(TestProfileId.ToString()));

        page.FindAll("button").Last(button => Text(button) == "Add folder").Click();

        page.WaitForAssertion(() => Assert.Single(_createdSources));
        Assert.Equal("documents", _createdSources[0].ContentKind);
        Assert.Equal("F:\\Paperwork", _createdSources[0].Path);
    }

    [Fact]
    public void AFolderWithSkippedFilesSaysSoWithATooltipAndOneWithNoneSaysNothing()
    {
        _personalSpace = TestSpace;
        _viewSources =
        [
            ViewSource("D:\\Pictures", "Pictures", "photos", skipped: 12),
            ViewSource("E:\\Phone", "Phone", "photos"),
        ];
        var page = Open("view", "photos");

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".view-source-row").Count));
        var rows = page.FindAll(".view-source-row");
        var skipped = rows[0].QuerySelector("small")!;
        Assert.Equal("12 files skipped", Text(skipped));
        Assert.Equal("These files aren't photos, so this folder leaves them out. They stay where they are.",
            skipped.ParentElement!.GetAttribute("data-playback-tooltip"));
        Assert.DoesNotContain("skipped", Text(rows[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePhoneBackupSourceShowsOnPhotosAndPersonalVideosMarkedPhotosAndVideosAndNotOnDocuments()
    {
        _personalSpace = TestSpace;
        _viewSources =
        [
            ViewSource("D:\\Backup", "Phone backup", "photos", photosAndVideos: true),
            ViewSource("C:\\Paperwork", "Paperwork", "documents"),
        ];

        foreach (var (kind, expected) in new[] { ("photos", new[] { "Phone backup" }), ("personal_videos", ["Phone backup"]), ("documents", ["Paperwork"]) })
        {
            var page = Open("view", kind);
            page.WaitForAssertion(() => Assert.Equal(expected, page.FindAll(".view-source-row strong").Select(Text).ToArray()));
            Assert.Equal(kind != "documents", Text(page.Find(".view-source-row")).Contains("Photos and videos", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ThePermissionSwitchOnThePersonalVideosPageSendsPersonalVideosAndKeepsTheOtherFlags()
    {
        var page = Open("view", "personal_videos");
        page.WaitForAssertion(() => Assert.Contains("Who can see personal videos", page.Markup, StringComparison.Ordinal));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".view-kind-access__list input[role=switch]")));
        Assert.False(page.Find(".view-kind-access__list input[role=switch]").HasAttribute("checked"));

        page.Find(".view-kind-access__list input[role=switch]").Change(true);

        page.WaitForAssertion(() => Assert.Single(_policySaves));
        var saved = _policySaves[0];
        Assert.True(saved.AllowPersonalVideos);
        // The photos and documents flags are sent back as they were saved.
        Assert.False(saved.AllowPhotos);
        Assert.True(saved.AllowDocuments);
        Assert.True(saved.ViewEnabled && saved.AccessSharedLibrary && saved.SubmitToSharedLibrary && saved.AllowGallerySharing);
    }

    [Fact]
    public void AViewKindThatDoesNotExistShowsTheNotFoundPage()
    {
        var page = Open("view", "recordings");

        Assert.Contains("This library page does not exist", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll(".library-detail"));
    }
}
