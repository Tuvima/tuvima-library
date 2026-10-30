using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Libraries overview (/settings/libraries) and the library page shell (/settings/libraries/{libraryId}), Library pages WP2:
// four area tabs, one table of that area's libraries (one row per library), status words, and a page per library.
public sealed class LibrariesOverviewTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";
    private const string HomeVideosId = "77777777-7777-4777-8777-777777777771";
    private const string OtherVideosId = "77777777-7777-4777-8777-777777777772";
    private const string YoutubeId = "88888888-8888-4888-8888-888888888888";

    private LibrariesConfigurationDto? _configuration = BuildConfiguration();
    private LibraryOverviewDto? _overview = new() { MediaTypeCounts = new() { ["Movies"] = 1284, ["Audiobooks"] = 12 } };
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };
    private IReadOnlyList<ViewSourceAdminDto> _viewSources = [];
    private List<LibraryKindDto> _kinds = LibraryKindsTestData.BuiltIn();

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"],
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-old", Path = "F:\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audio-old", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    public LibrariesOverviewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ => Task.FromResult(_overview));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_kinds));
            stub.SetHandler(nameof(IEngineApiClient.GetProviderCatalogueAsync), _ => Task.FromResult<IReadOnlyList<ProviderCatalogueDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => Task.FromResult<PathTestResultDto?>(_pathProbe((string)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(new((Guid)args![0]!, null, _viewSources, [])));
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(
                new List<ProfileViewModel> { new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow) }));
            stub.SetHandler("get_LastError", _ => _configuration is null ? "The Engine did not answer." : null);
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

    private async Task<PathTestResultDto?> ProbeAfterAsync(Task gate, string path)
    {
        await gate;
        return _pathProbe(path);
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    private IRenderedComponent<LibrariesTab> Open(string? subsection = null, string? detail = null, string? query = null)
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

            if (query is not null)
            {
                parameters.AddCascadingValue(RoutedPageQuery.CascadingName, $"http://localhost/settings/libraries{(subsection is null ? string.Empty : "/" + subsection)}?{query}");
            }
        });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading libraries", cut.Markup));
        return cut;
    }

    private IRenderedComponent<LibrariesTab> OpenAndSettle(string? subsection = null, string? query = null)
    {
        var cut = Open(subsection, null, query);
        // The counts and status arrive after the first render, once the folder checks finish.
        // View's folders are never checked, so its rows keep a dash.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".libs__group:not([data-area=view]) [data-library] [data-status=unknown]")));
        return cut;
    }

    // The library page's own tab strip (the folders panel keeps a strip of its own inside the Folders tab).
    private const string PageTabs = "[aria-label='Library sections'] [role=tab]";

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<LibrariesTab> cut, string id) => cut.Find($"tr[data-library='{id}']");

    // 1. One table grouped under Watch, Listen, Read and View (Libraries redesign D2), beside a rail of the same areas (D1).
    private const string GroupHeadings = ".libs__group-heading";

    [Fact]
    public void TheOverviewGroupsEveryAreaInOneListAndTheRailListsTheSameAreasAndLibraries()
    {
        var cut = Open();

        Assert.Equal(["Watch1", "Listen1", "Readnothing yetAdd a Read library", "View3"], cut.FindAll(GroupHeadings).Select(Text).ToArray());
        Assert.Equal(["Watch", "Listen", "Read", "View"], cut.FindAll(".librail__heading > span").Select(Text).ToArray());
        Assert.Equal([MoviesId, AudiobooksId], cut.FindAll(".librail__lib[data-library]").Select(link => link.GetAttribute("data-library")!).Take(2).ToArray());
        Assert.Empty(cut.FindAll("[aria-label='Library areas']"));
        Assert.DoesNotContain("Vault", cut.Markup);
    }

    // 2. Rows, counts and status per area.
    [Fact]
    public void TheWatchAreaListsItsLibrariesWithContentTypeCountsAndStatusAndNoAccessColumn()
    {
        var cut = OpenAndSettle();

        var row = Row(cut, MoviesId);
        Assert.Equal("Movies", Text(row.QuerySelector(".libs__link")!));
        var cells = row.QuerySelectorAll("td").Select(Text).ToArray();
        Assert.Equal("Movies", cells[1]);
        Assert.Equal("2 folders · 1,284 titles", cells[2]);
        Assert.Contains("Healthy", cells[3]);
        Assert.DoesNotContain(cut.FindAll("th"), header => Text(header) == "Access");
        Assert.Equal($"/settings/libraries/{MoviesId}", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Empty(cut.FindAll($".libs__group[data-area=watch] tr[data-library='{AudiobooksId}']"));
    }

    [Fact]
    public void TheListenGroupHoldsOnlyListenLibraries()
    {
        var cut = OpenAndSettle();

        var row = Row(cut, AudiobooksId);
        Assert.Equal("1 folder · 12 titles", Text(row.QuerySelectorAll("td")[2]));
        Assert.Empty(cut.FindAll($".libs__group[data-area=listen] tr[data-library='{MoviesId}']"));
        Assert.NotEmpty(cut.FindAll($".libs__group[data-area=listen] tr[data-library='{AudiobooksId}']"));
    }

    [Fact]
    public void WhenTheOverviewCannotBeReadOnlyFolderCountsShow()
    {
        _overview = null;
        var cut = OpenAndSettle();

        Assert.Equal("2 folders", Text(Row(cut, MoviesId).QuerySelectorAll("td")[2]));
    }

    // 3. The rail never goes away: on a library's own page it still lists every library and highlights the open one (Libraries redesign A1).
    [Fact]
    public void TheRailStaysOnALibrarysPageAndHighlightsTheOpenLibrary()
    {
        var cut = OpenAndSettle(MoviesId);

        Assert.Equal(["Watch", "Listen", "Read", "View"], cut.FindAll(".librail__heading > span").Select(Text).ToArray());
        Assert.Equal(MoviesId, cut.Find(".librail__lib.librail__on").GetAttribute("data-library"));
        cut.Find($".librail__lib[data-library='{AudiobooksId}']").GetAttribute("href");
        Assert.Equal($"/settings/libraries/{AudiobooksId}", cut.Find($".librail__lib[data-library='{AudiobooksId}']").GetAttribute("href"));
    }

    // 4. The whole row opens the library.
    [Fact]
    public void ClickingAnywhereOnARowOpensThatLibrarysPage()
    {
        var cut = Open();

        Row(cut, MoviesId).Click();

        Assert.EndsWith($"/settings/libraries/{MoviesId}", CurrentUri, StringComparison.Ordinal);
    }

    // 5. Several libraries of one kind are each their own row.
    [Fact]
    public void ASecondOtherVideosLibraryIsItsOwnRowWithItsOwnAddress()
    {
        _configuration!.Libraries.Add(new()
        {
            Id = HomeVideosId,
            Name = "Home videos",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "home-1", Path = "E:\\Home" }],
        });
        _configuration.Libraries.Add(new()
        {
            Id = OtherVideosId,
            Name = "Other videos",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "other-1", Path = "E:\\Other" }, new() { Id = "other-2", Path = "E:\\Other2" }],
        });
        var cut = OpenAndSettle();

        Assert.Equal(["Movies", "Home videos", "Other videos"], cut.FindAll(".libs__group[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.Equal("1 folder", Text(Row(cut, HomeVideosId).QuerySelectorAll("td")[2]));
        Assert.Equal("2 folders", Text(Row(cut, OtherVideosId).QuerySelectorAll("td")[2]));
        Assert.Equal($"/settings/libraries/{OtherVideosId}", Row(cut, OtherVideosId).QuerySelector("a")!.GetAttribute("href"));
    }

    // D22: Other videos can hold several named libraries, one row each; Movies keeps one.
    [Fact]
    public void ThreeOtherVideosLibrariesShowAsThreeRows()
    {
        foreach (var (id, name) in new[] { (HomeVideosId, "Home videos"), (OtherVideosId, "TikTok edits"), ("77777777-7777-4777-8777-777777777773", "Other videos") })
        {
            _configuration!.Libraries.Add(new()
            {
                Id = id,
                Name = name,
                KindKey = "other_videos",
                Area = "watch",
                MediaTypes = ["Videos"],
                Sources = [new() { Id = $"src-{id}", Path = $"E:\\{name}" }],
            });
        }

        var cut = OpenAndSettle();

        Assert.Equal(["Movies", "Home videos", "TikTok edits", "Other videos"], cut.FindAll(".libs__group[data-area=watch] .libs__link").Select(Text).ToArray());
    }

    // 6. The status words.
    [Fact]
    public void AnOfflineFolderReadsFolderOfflineAndAnEmptyLibraryReadsEmpty()
    {
        _pathProbe = path => path.StartsWith("F:", StringComparison.Ordinal)
            ? new PathTestResultDto { Exists = false }
            : new PathTestResultDto { Exists = true, HasRead = true, HasWrite = true };
        _configuration!.Libraries[1].Sources.Clear();
        var cut = OpenAndSettle();

        Assert.Contains("Folder offline", Text(Row(cut, MoviesId).QuerySelector("[data-status]")!));
        Assert.Equal("folder-offline", Row(cut, MoviesId).QuerySelector("[data-status]")!.GetAttribute("data-status"));
        Assert.Equal("Empty", Text(Row(cut, AudiobooksId).QuerySelector("[data-status]")!));
        Assert.Equal("Nothing added yet", Text(Row(cut, AudiobooksId).QuerySelectorAll("td")[2]));
    }

    [Fact]
    public void UnidentifiedFilesAndNeedsReviewItemsAreTwoCountsEachLinkingToItsOwnList()
    {
        _configuration!.Libraries[0].UnmatchedCount = 12;
        _configuration.Libraries[0].ReviewCount = 3;
        var cut = OpenAndSettle();

        var unidentified = Row(cut, MoviesId).QuerySelector("a[data-status=not-identified]")!;
        Assert.Equal("12 files not identified", Text(unidentified));
        Assert.Equal($"/settings/libraries/{MoviesId}?tab=unidentified", unidentified.GetAttribute("href"));
        var review = Row(cut, MoviesId).QuerySelector("a[data-status=needs-review]")!;
        Assert.Equal("3 need review", Text(review));
        Assert.Equal($"/settings/review?library={MoviesId}", review.GetAttribute("href"));
        // The rail carries the same total beside the library.
        Assert.Equal("15", Text(cut.Find($".librail__lib[data-library='{MoviesId}'] .librail__count")));
    }

    [Fact]
    public void AFolderBeingScannedReadsScanning()
    {
        _configuration!.Libraries[0].Sources[0].FirstCheck = new() { State = "checking", Tried = 1, TotalFiles = 40 };
        var cut = OpenAndSettle();

        Assert.Equal("Scanning", Text(Row(cut, MoviesId).QuerySelector("[data-status=scanning]")!));
    }

    [Fact]
    public void NoStatusIsClaimedWhileTheFolderChecksAreStillRunning()
    {
        var gate = new TaskCompletionSource();
        _pathProbe = _ => new PathTestResultDto { Exists = true, HasRead = true, HasWrite = true };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_kinds));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ => Task.FromResult(_overview));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => ProbeAfterAsync(gate.Task, (string)args![0]!));
        }));

        var cut = Open();

        Assert.NotNull(Row(cut, MoviesId).QuerySelector("[data-status=unknown]"));
        Assert.DoesNotContain("Healthy", Row(cut, MoviesId).TextContent);
        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("Healthy", Row(cut, MoviesId).TextContent));
    }

    // 7. The Vault is a fifth area, drawn only when the host says the Vault may be shown; its libraries are listed only there.
    [Fact]
    public void AVaultLibraryIsListedOnlyInTheVaultAreaAndTheVaultGroupExistsOnlyWhileTheVaultMayBeShown()
    {
        var kinds = LibraryKindsTestData.Catalog;
        var configuration = BuildConfiguration();
        configuration.Libraries.Add(new() { Id = "99999999-9999-4999-8999-999999999999", Name = "Private movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"], IsVault = true, Sources = [new() { Id = "v1", Path = "E:\\Vault" }] });

        var shown = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, kinds).Add(x => x.Configuration, configuration).Add(x => x.ShowVault, true));
        var hidden = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, kinds).Add(x => x.Configuration, configuration));
        var rail = Render<LibrariesRail>(parameters => parameters.Add(x => x.Kinds, kinds).Add(x => x.Configuration, configuration));

        Assert.Equal(["Watch", "Listen", "Read", "View", "Vault"], shown.FindAll(".libs__group").Select(group => group.GetAttribute("aria-label")!).ToArray());
        Assert.Equal(["Movies"], shown.FindAll(".libs__group[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.Equal(["Private movies"], shown.FindAll(".libs__group[data-area=vault] .libs__link").Select(Text).ToArray());
        Assert.NotNull(shown.Find("tr[data-library='99999999-9999-4999-8999-999999999999'] .libs__lock"));
        Assert.Equal(["Watch", "Listen", "Read", "View"], hidden.FindAll(".libs__group").Select(group => group.GetAttribute("aria-label")!).ToArray());
        Assert.Equal(["Movies"], hidden.FindAll(".libs__group[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.DoesNotContain("Vault", hidden.Markup);
        Assert.DoesNotContain("Private", hidden.Markup);
        // The rail is the same: no Vault group, row, count or word for someone who may not open it.
        Assert.DoesNotContain("Vault", rail.Markup);
        Assert.DoesNotContain("Private", rail.Markup);
    }

    // 8. Empty area, search, status filter, loading and error.
    [Fact]
    public void AnAreaWithNoLibrariesSaysNothingYetAndOffersToAddOne()
    {
        var cut = Open();

        var heading = Text(cut.Find(".libs__group[data-area=read] .libs__group-heading"));
        Assert.Equal("Readnothing yetAdd a Read library", heading);
        Assert.Empty(cut.FindAll(".libs__group[data-area=read] table"));
        Assert.Contains(cut.FindAll("button"), button => Text(button) == "Add library");
    }

    [Fact]
    public void SearchNarrowsTheRowsAndTheStatusFilterIsThere()
    {
        _configuration!.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "h", Path = "E:\\Home" }] });
        var cut = OpenAndSettle();
        Assert.NotNull(cut.Find("[aria-label='Filter by status']"));

        cut.Find("input[type=search], input").Input("home");

        cut.WaitForAssertion(() => Assert.Equal(["Home videos"], cut.FindAll(".libs__link").Select(Text).ToArray()));
        cut.Find("input[type=search], input").Input("zzz");
        cut.WaitForAssertion(() => Assert.Contains("libraries match", cut.Markup));
    }

    [Fact]
    public void WhileTheLibrariesLoadTheLoadingStateShows()
    {
        var gate = new TaskCompletionSource<LibrariesConfigurationDto?>();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => gate.Task);
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_kinds));
        }));

        var cut = Render<LibrariesTab>();

        cut.WaitForAssertion(() => Assert.Contains("Loading libraries", cut.Markup));
        gate.SetResult(null);
    }

    [Fact]
    public void WhenTheLibrariesCannotBeLoadedThePageSaysSoAndOffersTryAgain()
    {
        _configuration = null;
        var cut = Open();

        Assert.Contains("Libraries could not be loaded", cut.Markup);
        _configuration = BuildConfiguration();
        cut.FindAll("button").Single(button => Text(button) == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Contains("Movies", Text(cut.Find(".libs__group[data-area=watch] .libs__link"))));
    }

    // 9. View libraries sit in the same table and open the existing View settings.
    [Fact]
    public void TheViewTabListsPhotosPersonalVideosAndDocumentsWithTheirFolderCountsAndOpensTheViewSettings()
    {
        _viewSources =
        [
            new(Guid.NewGuid(), "local_folder", "Pictures", "linked", "D:\\Pictures", true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ContentKind: "photos"),
            new(Guid.NewGuid(), "local_folder", "Paperwork", "linked", "C:\\Paperwork", true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ContentKind: "documents"),
        ];
        var cut = OpenAndSettle();

        cut.WaitForAssertion(() => Assert.Equal(["1 folder", "Nothing added yet", "1 folder"], cut.FindAll(".libs__group[data-area=view] tbody tr").Select(row => Text(row.QuerySelectorAll("td")[2])).ToArray()));
        Assert.Equal(["Photos", "Personal videos", "Documents"], cut.FindAll(".libs__group[data-area=view] .libs__link").Select(Text).ToArray());
        Assert.Equal("/settings/libraries/view/photos", cut.FindAll(".libs__group[data-area=view] .libs__link")[0].GetAttribute("href"));
    }

    // 10. Phone: counts and status, nothing that changes anything.
    [Fact]
    public async Task OnAPhoneTheOverviewShowsRowsWithTheNoteAndNoControlToChangeAnything()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var cut = OpenAndSettle();

        Assert.Contains("Use a desktop or tablet to change folders.", cut.Markup);
        Assert.NotEmpty(cut.FindAll($"tr[data-library='{MoviesId}']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Add library");
    }

    [Fact]
    public async Task OnATelevisionTheLibrariesAreUnavailable()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("television", "tv", false));
        var cut = Render<LibrariesTab>();

        Assert.Contains("Library settings are unavailable", cut.Markup);
    }

    // 11. The library page.
    [Fact]
    public void ALibrarysOwnPageShowsItsTypeCountsStatusActionsAndTheFiveTabsOnlyWhereTheyApply()
    {
        var cut = Open(MoviesId);
        cut.WaitForElement(".folder-grid");

        var header = Text(cut.Find(".libpage__header"));
        Assert.Contains("Movies", header);
        Assert.Contains("2 folders", header);
        cut.WaitForAssertion(() => Assert.Contains("Healthy", Text(cut.Find(".libpage__header"))));
        Assert.Equal(["Folders", "File handling", "Activity"], cut.FindAll(PageTabs).Select(Text).ToArray());
        Assert.Equal(["Scan now", "Add folder", "Add files"], cut.FindAll(".libpage__actions button").Select(Text).ToArray());
    }

    [Fact]
    public void ALibraryOfAKindWithLookupSettingsGetsALookupTab()
    {
        _kinds = LibraryKindsTestData.All();
        _configuration!.Libraries.Add(new() { Id = YoutubeId, Name = "YouTube", KindKey = "youtube", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "yt", Path = "E:\\YouTube" }] });

        var cut = Open(YoutubeId);

        cut.WaitForAssertion(() => Assert.Equal(["Folders", "File handling", "Activity", "Lookup"], cut.FindAll(PageTabs).Select(Text).ToArray()));
    }

    [Fact]
    public void TheActivityTabIsSelectedFromTheAddressAndShowsNoFolderTable()
    {
        var cut = Open(MoviesId, query: "tab=activity");

        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll(PageTabs)[2].GetAttribute("aria-selected")));
        Assert.DoesNotContain("Coming in the next step", cut.Markup);
        Assert.Empty(cut.FindAll(".folder-table"));
    }

    [Fact]
    public void TheFileHandlingTabShowsItsFourCardsInsteadOfAPlaceholder()
    {
        var cut = Open(MoviesId, query: "tab=file-handling");

        cut.WaitForAssertion(() => Assert.Contains("Deleted files", cut.Markup));
        Assert.Equal("true", cut.FindAll(PageTabs)[1].GetAttribute("aria-selected"));
        Assert.DoesNotContain("Coming in the next step", cut.Markup);
        Assert.Empty(cut.FindAll(".folder-grid"));
    }

    [Fact]
    public void SelectingATabOfALibrarysPageKeepsTheTabInTheAddress()
    {
        var cut = Open(MoviesId);
        cut.WaitForElement(".folder-grid");

        cut.FindAll(PageTabs)[1].Click();

        Assert.EndsWith($"/settings/libraries/{MoviesId}?tab=file-handling", CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFoldersTabShowsOnlyThisLibrarysFoldersEvenWhenTheKindHoldsSeveralLibraries()
    {
        _configuration!.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "home-1", Path = "E:\\Home" }] });
        _configuration.Libraries.Add(new() { Id = OtherVideosId, Name = "Other videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "other-1", Path = "E:\\Other" }] });

        var cut = Open(OtherVideosId);
        cut.WaitForElement(".folder-grid");

        Assert.Contains("E:\\Other", cut.Markup);
        Assert.DoesNotContain("E:\\Home", cut.Markup);
    }

    [Fact]
    public async Task OnAPhoneALibrarysPageKeepsTheCountsAndStatusWithoutAnyControlThatChangesFolders()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var cut = Open(MoviesId);
        cut.WaitForElement(".libpage");

        Assert.Contains("Use a desktop or tablet to change folders.", cut.Markup);
        Assert.Empty(cut.FindAll(".libpage__actions"));
    }

    [Fact]
    public void AnUnknownLibraryAddressShowsThePlainNotFoundPage()
    {
        var cut = Open("00000000-0000-4000-8000-000000000000");

        Assert.Contains("This library page does not exist", cut.Markup);
    }

    // 12. The earlier group and type addresses land on what replaced them.
    [Theory]
    [InlineData("watch", null, "/settings/libraries?area=watch")]
    [InlineData("listen", null, "/settings/libraries?area=listen")]
    [InlineData("view", null, "/settings/libraries?area=view")]
    [InlineData("vault", null, "/settings/libraries")]
    [InlineData("watch", "movies", $"/settings/libraries/{MoviesId}")]
    [InlineData("listen", "audiobooks", $"/settings/libraries/{AudiobooksId}")]
    [InlineData("read", "books", "/settings/libraries?area=read")]
    public void AnEarlierGroupOrTypeAddressRedirects(string subsection, string? detail, string expected)
    {
        Open(subsection, detail);

        Assert.EndsWith(expected, CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEarlierVideosBookmarkLandsOnTheOtherVideosLibrary()
    {
        _configuration!.Libraries.Add(new() { Id = OtherVideosId, Name = "Other videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "o", Path = "E:\\Other" }] });

        Open("watch", "videos");

        Assert.EndsWith($"/settings/libraries/{OtherVideosId}", CurrentUri, StringComparison.Ordinal);
    }

    // 13. The pure rules behind the rows.
    [Fact]
    public void StatusIsTheWorstStateFirst()
    {
        var library = new LibraryFolderDto { Sources = [new() { Path = "E:\\a" }], UnmatchedCount = 2 };
        var offline = new LibraryHealth(false, "1 folder unavailable");

        Assert.Equal(LibraryStatusKind.FolderOffline, LibraryListing.StatusOf(library, 10, offline).Status);
        Assert.Equal(LibraryStatusKind.NotIdentified, LibraryListing.StatusOf(library, 10, LibraryHealth.Ok).Status);
        library.UnmatchedCount = 0;
        library.ReviewCount = 1;
        Assert.Equal(LibraryStatusKind.NeedsReview, LibraryListing.StatusOf(library, 10, LibraryHealth.Ok).Status);
        library.ReviewCount = 0;
        Assert.Equal(LibraryStatusKind.Empty, LibraryListing.StatusOf(library, 0, LibraryHealth.Ok).Status);
        Assert.Equal(LibraryStatusKind.Healthy, LibraryListing.StatusOf(library, null, LibraryHealth.Ok).Status);
        Assert.Null(LibraryListing.StatusOf(library, 5, null).Status);
        Assert.Equal(LibraryStatusKind.Empty, LibraryListing.StatusOf(new LibraryFolderDto(), null, null).Status);
    }
}
