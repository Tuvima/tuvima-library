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

// The Settings > Libraries addresses (the overview, a library page, the Import folder) and the View kind pages.
public sealed class LibraryAddressesAndViewPagesTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";
    private const string TvId = "33333333-3333-4333-8333-333333333333";
    private const string OtherId = "44444444-4444-4444-8444-444444444444";

    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private LibraryOverviewDto? _overview = new() { MediaTypeCounts = new() { ["Movies"] = 1284, ["Audiobooks"] = 12 } };
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };
    private IReadOnlyList<ViewSourceAdminDto> _viewSources = [];
    // The Engine's list of library kinds; a test adds a kind here the way a plugin would.
    private List<LibraryKindDto> _kinds = LibraryKindsTestData.BuiltIn();
    // The provider catalogue the Metadata column reads its icons and tooltips from.
    private IReadOnlyList<ProviderCatalogueDto> _providerCatalogue =
    [
        new() { Name = "tmdb", DisplayName = "TMDB", IconPath = "images/providers/tmdb.svg", Capabilities = ["identity", "metadata", "artwork", "ratings", "people", "relationships"] },
        new() { Name = "subdl", DisplayName = "SubDL", IconPath = "images/providers/subdl.png", Capabilities = ["subtitles"] },
        new() { Name = "musicbrainz", DisplayName = "MusicBrainz", IconPath = "images/providers/musicbrainz.svg", Capabilities = ["identity", "metadata", "artwork", "mystery"] },
    ];
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
    private readonly List<UpdateViewSourceRequest> _sourceUpdates = [];

    // Libraries has pages below it (a group, then a type), so its menu row matches them; every other row stays exact.
    [Fact]
    public void SettingsMenu_LibrariesRow_StaysLitOnTheGroupAndTypePagesBelowIt()
    {
        var rows = Flatten(MediaEngine.Web.Services.Navigation.SettingsMenuBuilder.Build(
            new MediaEngine.Web.Services.Navigation.SettingsMenuContext(true, IsMobile: false, 0, false))).ToList();

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

    public LibraryAddressesAndViewPagesTests()
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
            stub.SetHandler(nameof(IEngineApiClient.GetProviderCatalogueAsync), _ => Task.FromResult(_providerCatalogue));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => ProbeAsync((string)args![0]!));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(new((Guid)args![0]!, _personalSpace, _viewSources, [])));
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(
                new List<ProfileViewModel> { new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow) }));
            stub.SetHandler("get_LastError", _ => null);
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
            stub.SetHandler(nameof(IEngineApiClient.UpdateViewProfileSourceAsync), args =>
            {
                _sourceUpdates.Add((UpdateViewSourceRequest)args![2]!);
                return Task.FromResult<ViewSourceAdminDto?>(ViewSource("D:\\Pictures", "Pictures", "photos"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), _ => Task.FromResult<IReadOnlyList<ExcludedFileDto>>([]));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
    }

    private Task<PathTestResultDto?> ProbeAsync(string path) => Task.FromResult<PathTestResultDto?>(_pathProbe(path));

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static ViewSourceAdminDto ViewSource(string location) =>
        new(Guid.NewGuid(), "local_folder", "Photos", "linked", location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ViewSourceAdminDto ViewSource(
        string location, string name, string kind, int skipped = 0, bool photosAndVideos = false, int keptInLibrary = 0) =>
        new(Guid.NewGuid(), "local_folder", name, "linked", location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContentKind: kind, SkippedFileCount: skipped, AcceptsPhotosAndVideos: photosAndVideos, KeptInLibraryCount: keptInLibrary);

    private IRenderedComponent<LibrariesTab> Open(string? subsection = null, string? detail = null, string? tab = null)
    {
        if (tab is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/settings/libraries/{subsection}/{detail}?tab={tab}");
        }

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
    public void ImportOpensTheImportFolderPanel()
    {
        var cut = Open("import");

        cut.WaitForElement("#library-import-title");
    }

    // Addresses that name nothing.
    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000000", null)]
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
        var cut = Render<LibraryFoldersPanel>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.TypeKey, "podcasts"));

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

    // The Settings frame shows each level's title and breadcrumb; this is the one rule for both. The older group, type and
    // "other" addresses redirect, so they describe nothing here.
    [Theory]
    [InlineData(null, null, "Libraries", "")]
    [InlineData("view", "photos", "Photos", "Libraries>/settings/libraries|View>/settings/libraries?area=view|Photos")]
    [InlineData("view", "personal_videos", "Personal videos", "Libraries>/settings/libraries|View>/settings/libraries?area=view|Personal videos")]
    [InlineData("view", "documents", "Documents", "Libraries>/settings/libraries|View>/settings/libraries?area=view|Documents")]
    [InlineData("view", "podcasts", "Libraries", "")]
    [InlineData("view", null, "Libraries", "")]
    [InlineData("import", null, "Import folder", "Libraries>/settings/libraries|Import folder")]
    [InlineData("import", "movies", "Libraries", "")]
    [InlineData("watch", null, "Libraries", "")]
    [InlineData("watch", "movies", "Libraries", "")]
    [InlineData("other", "other-abc", "Libraries", "")]
    [InlineData("nowhere", null, "Libraries", "")]
    [InlineData("vault", null, "Libraries", "")]
    public void AnAddressDescribesItsTitleAndBreadcrumbForTheSettingsFrame(string? subsection, string? detail, string title, string crumbs)
    {
        var level = LibraryAddress.Describe(LibraryKindsTestData.Catalog, subsection, detail, showVault: false);

        Assert.Equal(title, level.Title);
        Assert.Equal(crumbs, string.Join("|", level.Crumbs.Select(crumb => crumb.Href is null ? crumb.Label : $"{crumb.Label}>{crumb.Href}")));
    }

    // Libraries > Watch > Movies, from the library's own address: its name is the title, its area the middle step.
    [Fact]
    public void ALibraryAddressIsTitledWithTheLibrarysNameAndItsAreaIsTheMiddleStep()
    {
        var level = LibraryAddress.Describe(LibraryKindsTestData.Catalog, MoviesId, null, showVault: false, _configuration);

        Assert.Equal("Movies", level.Title);
        Assert.Equal("Libraries>/settings/libraries|Watch>/settings/libraries?area=watch|Movies", string.Join("|", level.Crumbs.Select(crumb => crumb.Href is null ? crumb.Label : $"{crumb.Label}>{crumb.Href}")));
        Assert.Equal("Library", LibraryAddress.Describe(LibraryKindsTestData.Catalog, MoviesId, null, showVault: false).Title);
        Assert.Equal("Libraries", LibraryAddress.Describe(LibraryKindsTestData.Catalog, "11111111-0000-4000-8000-000000000000", null, showVault: false, _configuration).Title);
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

    // 9. View has a page per kind: Photos, Personal videos and Documents (the overview lists them in its View tab).
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
        // Who can see the kind is on the Access tab, not on the Folders tab.
        Assert.DoesNotContain("Who can see documents", page.Markup, StringComparison.Ordinal);
        Assert.Equal("1 folder", Text(page.Find(".libpage__counts")));
    }

    // The page's own Add folder button opens View's add form once the folders have loaded.
    private static void OpenTheAddForm(IRenderedComponent<ViewKindPage> page)
    {
        page.WaitForAssertion(() => Assert.False(page.FindAll("button").First(button => Text(button) == "Add folder").HasAttribute("disabled")));
        page.FindAll("button").First(button => Text(button) == "Add folder").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".libraries-form-grid")));
    }

    [Fact]
    public async Task AddingAFolderOnTheDocumentsPageSendsTheDocumentsKind()
    {
        var page = Render<ViewKindPage>(parameters => parameters
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kind, "documents")
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog));
        OpenTheAddForm(page);
        var owner = page.FindComponents<MediaEngine.Web.Components.Shared.AppSelect>().First(select => select.Instance.Label == "Owner profile");
        await page.InvokeAsync(() => owner.Instance.ValueChanged.InvokeAsync(TestProfileId.ToString()));
        var name = page.FindComponents<MediaEngine.Web.Components.Shared.AppTextField>().First(field => field.Instance.Label == "Folder name");
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Paperwork"));

        page.FindAll("button").Last(button => Text(button) == "Add folder").Click();

        page.WaitForAssertion(() => Assert.Single(_createdSources));
        Assert.Equal("documents", _createdSources[0].ContentKind);
        Assert.Equal("Paperwork", _createdSources[0].Name);
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
    public void AFolderWithFilesKeptInLibraryOnlySaysSoNextToTheSkippedNote_AndOneWithNoneSaysNothing()
    {
        _personalSpace = TestSpace;
        _viewSources =
        [
            ViewSource("D:\\Pictures", "Pictures", "photos", skipped: 12, keptInLibrary: 3),
            ViewSource("E:\\Phone", "Phone", "photos", keptInLibrary: 1),
            ViewSource("F:\\Trips", "Trips", "photos"),
        ];
        var page = Open("view", "photos");

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".view-source-row").Count));
        var rows = page.FindAll(".view-source-row");
        var notes = rows[0].QuerySelectorAll("small").Select(Text).ToList();
        Assert.Equal(["12 files skipped", "3 files kept in Library only"], notes);
        Assert.Equal(["1 file kept in Library only"], rows[1].QuerySelectorAll("small").Select(Text).ToList());
        Assert.DoesNotContain("kept in Library", Text(rows[2]), StringComparison.Ordinal);
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
        var page = Open("view", "personal_videos", tab: "access");
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

    [Fact]
    public void TheDocumentsPageHasTheDocumentPanelsAndNoTimelineSwitch()
    {
        _personalSpace = TestSpace;
        _viewSources = [ViewSource("C:\\Paperwork", "Paperwork", "documents")];
        var page = Open("view", "documents");

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".view-source-row")));
        Assert.Empty(page.FindAll("input[role=switch][aria-label='Show in Photos timeline']"));
        Assert.DoesNotContain("Auto-filing rules", page.Markup, StringComparison.Ordinal);

        var handling = Open("view", "documents", tab: "file-handling");
        handling.WaitForAssertion(() => Assert.Contains("Auto-filing rules", handling.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePhotosPageHasTheTimelineSwitchAndNoDocumentPanels()
    {
        _personalSpace = TestSpace;
        _viewSources = [ViewSource("D:\\Pictures", "Pictures", "photos")];
        var page = Open("view", "photos");

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".view-source-row")));
        Assert.Single(page.FindAll("input[role=switch][aria-label='Show in Photos timeline']"));

        var handling = Open("view", "photos", tab: "file-handling");
        handling.WaitForAssertion(() => Assert.Contains("Originals remain protected", handling.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Auto-filing rules", handling.Markup, StringComparison.Ordinal);
    }

    // Library pages WP11: View's kinds open the same page shape as any library (header facts, Folders, Access, File handling).
    [Fact]
    public void AViewKindPageHasTheLibraryTabsAndNoIncludeSubfoldersSwitch()
    {
        _personalSpace = TestSpace;
        _viewSources = [ViewSource("D:\\Pictures", "Pictures", "photos")];
        var page = Open("view", "photos");

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".view-source-row")));
        Assert.Equal(["Folders", "Access", "File handling", "Activity"], page.FindAll("[role=tab]").Select(Text).ToArray());
        Assert.Equal("Photos", Text(page.Find(".libpage__type")));
        Assert.Equal("Per profile", Text(page.Find(".libpage__access")));
        Assert.DoesNotContain("Include subfolders", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Add library", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewViewFolderIsManagedByDefault()
    {
        var page = Render<ViewKindPage>(parameters => parameters
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kind, "photos")
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog));
        OpenTheAddForm(page);

        var mode = page.FindComponents<MediaEngine.Web.Components.Shared.AppSelect>().First(select => select.Instance.Label == "Folder management");
        Assert.Equal("managed", mode.Instance.Value);
    }

    // Library pages WP13, D24: the Show in Photos timeline switch is per source folder and is saved straight away.
    [Fact]
    public void TheTimelineSwitchOfASourceFolderIsSavedForThatFolder()
    {
        _personalSpace = TestSpace;
        _viewSources = [ViewSource("D:\\Pictures", "Pictures", "photos")];
        var page = Open("view", "photos");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".view-source-row")));

        page.Find("input[role=switch][aria-label='Show in Photos timeline']").Change(true);

        page.WaitForAssertion(() => Assert.Single(_sourceUpdates));
        Assert.Equal("Pictures", _sourceUpdates[0].Name);
        Assert.True(_sourceUpdates[0].IncludeInTimeline);
    }
}
