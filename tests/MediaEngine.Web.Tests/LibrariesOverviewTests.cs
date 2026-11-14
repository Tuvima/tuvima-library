using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
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
    private int _libraryMutations;

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
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _libraryMutations++;
                var changed = ((LibraryMutationRequest)args![0]!).Library!;
                _configuration = new LibrariesConfigurationDto
                {
                    StorageLocations = _configuration!.StorageLocations,
                    Libraries = [.. _configuration.Libraries.Select(library => library.Id == changed.Id ? changed : library)],
                };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
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
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration?.StorageLocations ?? []));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "E:\\Library\\Movies" }));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddLogging();
        Services.AddScoped(sp => new MediaEngine.Web.Services.Playback.PlaybackSessionController(null!, sp.GetRequiredService<IEngineApiClient>()));
        Services.AddScoped<ShellActivityState>();
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
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tr[data-library]:not([data-area=view]):not([data-library=import]) [data-status=unknown]")));
        return cut;
    }

    // The library page's own tab strip (the folders panel keeps a strip of its own inside the Folders tab).
    private const string PageTabs = "[aria-label='Library sections'] [role=tab]";

    private static AngleSharp.Dom.IElement EditButton(IRenderedComponent<LibrariesTab> cut, string id) =>
        cut.Find($"tr[data-library='{id}'] button[data-edit]");

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<LibrariesTab> cut, string id) => cut.Find($"tr[data-library='{id}']");

    // 1. One table, filtered by area chips (Libraries and folder picker D1): All, Watch, Listen, Read, View, each with a count.
    private static string[] ChipTexts(IRenderedComponent<LibrariesTab> cut) => cut.FindAll(".lib-chip").Select(Text).ToArray();

    // The Libraries page has no progress card or View activity button, only the one status line above the table; while work runs the
    // status line's leading word links to Activity (library-activity follow-up).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOverviewHasNoProgressCardAndNoViewActivityButton_OnlyTheOneStatusLine(bool administrator)
    {
        var session = Services.GetRequiredService<DashboardSessionAccessor>();
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, administrator, administrator, null, 1, [], administrator ? ["settings.administration"] : [], administrator ? ["access.manage"] : []);
        session.Set("test-session-token", accountId, profileId, Guid.NewGuid(), authority);

        var cut = Open();

        Assert.Empty(cut.FindAll("[data-scan-card], .lib-scan"));
        Assert.Empty(cut.FindAll("[data-testid='activity-up-to-date'], [data-testid='activity-progress-card'], .lact-progress"));
        Assert.Empty(cut.FindAll("a.lact-progress__view"));
        Assert.DoesNotContain("View activity", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid='library-status-line']"));
    }

    [Fact]
    public void TheOverviewShowsAreaChipsWithCountsOverOneTableAndNoRail()
    {
        var cut = Open();

        Assert.Equal(["All2", "Watch1", "Listen1", "Read0", "View0"], ChipTexts(cut));
        Assert.Single(cut.FindAll("table"));
        Assert.Empty(cut.FindAll(".libs__group"));
        Assert.Empty(cut.FindAll(".librail, .libraries__layout"));
        Assert.DoesNotContain("Vault", cut.Markup);
    }

    [Fact]
    public void ChoosingAChipShowsOnlyThatAreasLibrariesAndKeepsSearchAndTheStatusFilter()
    {
        var cut = OpenAndSettle();
        Assert.NotEmpty(cut.FindAll($"tr[data-library='{MoviesId}']"));
        Assert.NotEmpty(cut.FindAll($"tr[data-library='{AudiobooksId}']"));

        cut.Find(".lib-chip[data-area=listen]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll($"tr[data-library='{MoviesId}']"));
            Assert.NotEmpty(cut.FindAll($"tr[data-library='{AudiobooksId}']"));
            Assert.Equal("true", cut.Find(".lib-chip[data-area=listen]").GetAttribute("aria-pressed"));
        });
        Assert.NotNull(cut.Find("[aria-label='Filter by status']"));
        Assert.NotNull(cut.Find("input[type=search], input"));
        Assert.EndsWith("/settings/libraries?area=listen", CurrentUri, StringComparison.Ordinal);
        // The choice is remembered in the browser under a per-profile key.
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "localStorage.setItem" && Equals(call.Arguments[1], "listen"));
    }

    [Fact]
    public void AnAreaInTheAddressSelectsItsChip()
    {
        var cut = Open(query: "area=watch");

        Assert.Equal("true", cut.Find(".lib-chip[data-area=watch]").GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll($"tr[data-library='{AudiobooksId}']"));
    }

    [Fact]
    public void TheImportFolderRowIsOneLineWithItsInfoIconBesideTheName()
    {
        var cut = OpenAndSettle();

        var title = cut.Find("tr[data-library=import] .libs__title");
        Assert.Contains("libs__title--inline", title.ClassList);
        Assert.NotNull(title.QuerySelector(".libs__link"));
        Assert.NotNull(title.QuerySelector(".libs__info .app-info-tip"));
        Assert.Null(title.QuerySelector(".libs__subtitle"));
        Assert.Null(cut.Find("tr[data-library=import]").QuerySelector(".libs__subtitle"));
        // Names are fixed by the kind, so no row repeats the kind under its name ("Movies / Movies").
        Assert.Null(Row(cut, MoviesId).QuerySelector(".libs__subtitle"));
        Assert.Equal("Movies", Text(Row(cut, MoviesId).QuerySelector(".libs__title")!));
    }

    [Fact]
    public void TheImportFolderIsAPinnedFirstRowThatStaysUnderEveryChipAndReadsNotSetUntilAFolderIsChosen()
    {
        var cut = OpenAndSettle(query: "area=listen");

        var first = cut.FindAll("tbody tr")[0];
        Assert.Equal("import", first.GetAttribute("data-library"));
        var cells = first.QuerySelectorAll("td").Select(Text).ToArray();
        Assert.Contains("Import folder", cells[0]);
        Assert.Equal("All media", cells[1]);
        Assert.Equal("Nothing waiting", cells[2]);
        Assert.Equal("Choose a folder", cells[3]);
        Assert.Contains("Not set", cells[4]);
        Assert.NotNull(first.QuerySelector("[data-edit=import]"));
        // The old rail entry and its page are gone.
        Assert.Empty(cut.FindAll("a[href='/settings/libraries/import']"));
    }

    [Fact]
    public void TheFolderCountIsAButtonThatListsTheRealPathsWithTheirStateAndNeverNavigates()
    {
        _pathProbe = path => path.StartsWith("F:", StringComparison.Ordinal)
            ? new PathTestResultDto { Exists = false }
            : new PathTestResultDto { Exists = true, HasRead = true, HasWrite = true };
        var cut = OpenAndSettle();
        var before = CurrentUri;

        var button = Row(cut, MoviesId).QuerySelector("[data-folder-count]")!;
        Assert.Equal("2 folders", Text(button));
        button.Click();

        var items = cut.FindAll($"tr[data-library='{MoviesId}'] [data-folder-list] li");
        Assert.Equal(2, items.Count);
        Assert.Contains("E:\\Library\\Movies", Text(items[0]));
        Assert.Equal("online", items[0].GetAttribute("data-state"));
        Assert.Equal("offline", items[1].GetAttribute("data-state"));
        Assert.Equal(before, CurrentUri);

        cut.Find("[data-folder-list]").KeyDown("Escape");
        Assert.Empty(cut.FindAll("[data-folder-list]"));
    }

    [Fact]
    public void EditOpensOneWindowWithTheNameAndFoldersAndNothingIsSavedUntilReview()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();

        EditButton(cut, MoviesId).Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        Assert.Equal(2, host.FindAll(".lib-edit__folder").Count);
        Assert.Contains(host.FindAll("button"), button => Text(button) == "Save changes" && button.HasAttribute("disabled"));
        Assert.Contains(host.FindAll("button"), button => Text(button) == "Cancel");
        Assert.Contains(host.FindAll("button"), button => Text(button) == "Remove library");
        // There is no "..." menu on the table row any more.
        Assert.Empty(Row(cut, MoviesId).QuerySelectorAll("[aria-haspopup=menu]"));
    }

    [Fact]
    public void TheLibrariesTableCallsItsThirdColumnContents()
    {
        var cut = OpenAndSettle();

        var headers = cut.FindAll(".libs__table th").Select(Text).Where(text => text.Length > 0).ToArray();
        Assert.Equal(["Name", "Area", "Contents", "Folders", "Status"], headers.Take(5).ToArray());
        Assert.DoesNotContain("Folders and titles", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderListShowsEachFoldersNameAboveItsPathAndASayingOfReadOnlyOrManaged()
    {
        _configuration!.Libraries[0].Sources[0].Label = "Family NAS";
        _configuration.Libraries[0].Sources[1].Label = "4K shelf";
        var cut = OpenAndSettle();

        Row(cut, MoviesId).QuerySelector("[data-folder-count]")!.Click();

        var items = cut.FindAll($"tr[data-library='{MoviesId}'] [data-folder-list] li");
        Assert.Equal("Family NAS", Text(items[0].QuerySelector("[data-folder-name]")!));
        Assert.Equal("E:\\Library\\Movies", Text(items[0].QuerySelector(".lib-folders__path")!));
        Assert.Equal("managed", items[0].QuerySelector("[data-handling]")!.GetAttribute("data-handling"));
        Assert.StartsWith("Managed", items[0].QuerySelector("[data-handling]")!.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal("4K shelf", Text(items[1].QuerySelector("[data-folder-name]")!));
        Assert.Equal("read-only", items[1].QuerySelector("[data-handling]")!.GetAttribute("data-handling"));
        Assert.StartsWith("Read-only", items[1].QuerySelector("[data-handling]")!.GetAttribute("aria-label"), StringComparison.Ordinal);
        // The copy button and the online state are still there.
        Assert.NotNull(items[0].QuerySelector("button[aria-label^='Copy']"));
        Assert.Equal("online", items[0].GetAttribute("data-state"));
    }

    [Fact]
    public void TheFolderListOfALibraryWithOneFolderShowsTheHandlingIconButNoName()
    {
        var cut = OpenAndSettle();

        Row(cut, AudiobooksId).QuerySelector("[data-folder-count]")!.Click();

        var item = cut.Find($"tr[data-library='{AudiobooksId}'] [data-folder-list] li");
        Assert.Null(item.QuerySelector("[data-folder-name]"));
        Assert.Equal("read-only", item.QuerySelector("[data-handling]")!.GetAttribute("data-handling"));
    }

    [Fact]
    public void TheEditWindowSaysTheNameIsFixedAndNeverOffersARename()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();

        EditButton(cut, MoviesId).Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        var lead = Text(host.Find(".app-dialog-shell__subtitle"));
        Assert.Equal("The library's name is fixed. Folders below can have optional names so you can tell them apart.", lead);
        Assert.DoesNotContain("Rename", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditWindowListsFoldersInATableWithFolderNamePathAndHowLibraryUsesIt()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();

        EditButton(cut, MoviesId).Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        var headers = host.FindAll("[data-folder-rows] th").Select(Text).Where(text => text.Length > 0 && text != "Actions").ToArray();
        Assert.Equal(["Folder name", "Path", "How Library uses it"], headers);
        var rows = host.FindAll("[data-folder-rows] tbody tr.lib-edit__folder");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Managed", Text(rows[0].QuerySelector("[data-handling]")!));
        Assert.Equal("Read-only", Text(rows[1].QuerySelector("[data-handling]")!));
        // There is no Online state column: a folder that is reachable says nothing.
        Assert.Empty(host.FindAll("[data-folder-rows] [data-state=online]"));
    }

    [Fact]
    public void TheEditWindowPutsScanNowAndRemoveLibraryAtTheLeftOfTheFooterNotInTheBody()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();

        EditButton(cut, MoviesId).Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        var start = host.Find(".app-dialog-shell__footer [data-footer-start]");
        Assert.Equal(["Scan now", "Remove library"], start.QuerySelectorAll("button").Select(Text).ToArray());
        Assert.DoesNotContain(host.FindAll(".lib-edit button"), button => Text(button) is "Scan now" or "Remove library");
        var footerButtons = host.FindAll(".app-dialog-shell__footer button").Select(Text).ToArray();
        Assert.Equal(["Scan now", "Remove library", "Cancel", "Save changes"], footerButtons);
    }

    [Fact]
    public void AnOfflineFolderIsMarkedBesideItsPathInTheEditWindow()
    {
        _pathProbe = path => path.StartsWith("F:", StringComparison.Ordinal)
            ? new PathTestResultDto { Exists = false }
            : new PathTestResultDto { Exists = true, HasRead = true, HasWrite = true };
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();

        EditButton(cut, MoviesId).Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        var rows = host.FindAll("[data-folder-rows] tbody tr.lib-edit__folder");
        Assert.Null(rows[0].QuerySelector("[data-state]"));
        Assert.NotNull(rows[1].QuerySelector("td .lib-edit__state[data-state=offline]"));
    }

    [Fact]
    public void AddFolderInEditOpensTheSameFolderWindowAsAddLibrarysThirdStep()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        EditButton(cut, MoviesId).Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));

        host.FindAll("button").Single(button => Text(button) == "Add folder").Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".server-folder-picker")));
        var title = host.FindAll(".app-dialog-shell__title").Last();
        Assert.Equal("Where is it? · Movies", Text(title));
        Assert.Equal(["Watch", "Movies"], host.Find("[data-picker-chips]").QuerySelectorAll("span").Select(Text).ToArray());
        Assert.NotNull(host.Find("[data-folder-name]"));
        Assert.NotNull(host.Find("[data-folder-handling]"));
        Assert.Empty(host.FindAll("[data-picker-safety]"));
        var buttons = host.FindAll(".server-folder-picker__footer button").Select(Text).ToArray();
        Assert.DoesNotContain("Choose folder", buttons);
        Assert.Contains("Add folder", buttons);
    }

    [Fact]
    public void RemovingAFolderInEditShowsReviewFirstAndLeavesTheFilesAlone()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        EditButton(cut, MoviesId).Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));

        host.Find(".lib-edit__folder[data-source='movies-old'] .app-overflow-menu__trigger").Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[role=menuitem]")));
        host.FindAll("[role=menuitem]").Single(item => Text(item).Contains("Remove", StringComparison.Ordinal)).Click();
        host.FindAll("button").Single(button => Text(button) == "Save changes").Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-review]")));
        var review = Text(host.Find("[data-review]"));
        Assert.Contains("Remove the folder F:\\Movies", review);
        Assert.Contains("files stay on disk", review);
        Assert.Contains("Your files are never changed.", review);
    }

    // 2. Rows, counts and status per area.
    [Fact]
    public void TheWatchAreaListsItsLibrariesWithContentTypeCountsAndStatusAndNoAccessColumn()
    {
        var cut = OpenAndSettle();

        var row = Row(cut, MoviesId);
        Assert.Equal("Movies", Text(row.QuerySelector(".libs__link")!));
        Assert.Null(row.QuerySelector(".libs__subtitle"));
        var cells = row.QuerySelectorAll("td").Select(Text).ToArray();
        Assert.Contains("Watch", cells[1]);
        Assert.Equal("2 folders · 1,284 titles", cells[2]);
        Assert.Equal("2 folders", cells[3]);
        Assert.Contains("Healthy", cells[4]);
        Assert.DoesNotContain(cut.FindAll("th"), header => Text(header) == "Access");
        Assert.Equal($"/settings/libraries/{MoviesId}", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("Watch", Text(row.QuerySelector("[data-area-tag]")!));
    }

    [Fact]
    public void TheListenGroupHoldsOnlyListenLibraries()
    {
        var cut = OpenAndSettle();

        var row = Row(cut, AudiobooksId);
        Assert.Equal("1 folder · 12 titles", Text(row.QuerySelectorAll("td")[2]));
        Assert.Equal("Listen", Text(row.QuerySelector("[data-area-tag]")!));
        cut.Find(".lib-chip[data-area=listen]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"tr[data-library='{MoviesId}']")));
        Assert.NotEmpty(cut.FindAll($"tr[data-library='{AudiobooksId}']"));
    }

    [Fact]
    public void WhenTheOverviewCannotBeReadOnlyFolderCountsShow()
    {
        _overview = null;
        var cut = OpenAndSettle();

        Assert.Equal("2 folders", Text(Row(cut, MoviesId).QuerySelectorAll("td")[2]));
    }

    // 3. No rail on a library's own page either (Libraries v3 D22); the page's own header carries the way back.
    [Fact]
    public void ALibrarysPageHasNoRail()
    {
        var cut = OpenAndSettle(MoviesId);

        Assert.Empty(cut.FindAll(".librail, .libraries__layout"));
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

        Assert.Equal(["Movies", "Home videos", "Other videos"], cut.FindAll("tr[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.Equal("1 folder", Text(Row(cut, HomeVideosId).QuerySelectorAll("td")[3]));
        Assert.Equal("2 folders", Text(Row(cut, OtherVideosId).QuerySelectorAll("td")[3]));
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

        Assert.Equal(["Movies", "Home videos", "TikTok edits", "Other videos"], cut.FindAll("tr[data-area=watch] .libs__link").Select(Text).ToArray());
    }

    // 6. The status words.
    [Fact]
    public void AnOfflineFolderReadsFolderOffline()
    {
        _pathProbe = path => path.StartsWith("F:", StringComparison.Ordinal)
            ? new PathTestResultDto { Exists = false }
            : new PathTestResultDto { Exists = true, HasRead = true, HasWrite = true };
        var cut = OpenAndSettle();

        Assert.Contains("Folder offline", Text(Row(cut, MoviesId).QuerySelector("[data-status]")!));
        Assert.Equal("folder-offline", Row(cut, MoviesId).QuerySelector("[data-status]")!.GetAttribute("data-status"));
    }

    [Fact]
    public void FilesNotMatchedAndItemsToConfirmAreOneNeedsReviewCountLinkingToNeedsReviewForTheLibrary()
    {
        _configuration!.Libraries[0].UnmatchedCount = 12;
        _configuration.Libraries[0].ReviewCount = 3;
        var cut = OpenAndSettle();

        var review = Row(cut, MoviesId).QuerySelector("a[data-status=needs-review]")!;
        Assert.Equal("15 need review", Text(review));
        Assert.Equal($"/settings/review?library={MoviesId}", review.GetAttribute("href"));
        Assert.Single(Row(cut, MoviesId).QuerySelectorAll("a[data-status]"));
        Assert.DoesNotContain("not identified", Row(cut, MoviesId).TextContent, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(["all", "watch", "listen", "read", "view", "vault"], shown.FindAll(".lib-chip").Select(chip => chip.GetAttribute("data-area")!).ToArray());
        Assert.Equal(["Movies"], shown.FindAll("tr[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.Equal(["Private movies"], shown.FindAll("tr[data-area=vault] .libs__link").Select(Text).ToArray());
        Assert.NotNull(shown.Find("tr[data-library='99999999-9999-4999-8999-999999999999'] .libs__lock"));
        Assert.Equal(["all", "watch", "listen", "read", "view"], hidden.FindAll(".lib-chip").Select(chip => chip.GetAttribute("data-area")!).ToArray());
        Assert.Equal(["Movies"], hidden.FindAll("tr[data-area=watch] .libs__link").Select(Text).ToArray());
        Assert.DoesNotContain("Vault", hidden.Markup);
        Assert.DoesNotContain("Private", hidden.Markup);
    }

    // 8. Empty area, search, status filter, loading and error.
    [Fact]
    public void AChipWithNoLibrariesSaysSoAndOffersToAddOne()
    {
        var cut = OpenAndSettle(query: "area=read");

        var empty = cut.Find("[data-empty=area]");
        Assert.Contains("No Read libraries yet", Text(empty));
        Assert.Contains("Add a Read library", Text(empty));
    }

    [Fact]
    public void WithNoLibrariesAtAllThePageHasAHeadingOneSentenceAndAnObviousAddButton()
    {
        _configuration!.Libraries.Clear();
        var cut = OpenAndSettle();

        var empty = cut.Find("[data-empty=none]");
        Assert.Contains("No libraries yet", Text(empty));
        Assert.Contains("Add a library for each kind of media you own. You can add more later in Settings.", Text(empty));
        Assert.Contains(empty.QuerySelectorAll("button"), button => Text(button) == "Add library");
    }

    [Fact]
    public void SearchNarrowsTheRowsAndTheStatusFilterIsThere()
    {
        _configuration!.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [new() { Id = "h", Path = "E:\\Home" }] });
        var cut = OpenAndSettle();
        Assert.NotNull(cut.Find("[aria-label='Filter by status']"));

        cut.Find("input[type=search], input").Input("home");

        cut.WaitForAssertion(() => Assert.Equal(["Home videos"], cut.FindAll("tr[data-area] .libs__link").Select(Text).ToArray()));
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

        cut.WaitForAssertion(() => Assert.Contains("Movies", Text(cut.Find("tr[data-area=watch] .libs__link"))));
    }

    // 9. View libraries sit in the same table and open the existing View settings. Photos and Personal videos are one "Photos and videos" row (D10).
    private static ViewSourceAdminDto ViewFolder(string name, string path, string kind) =>
        new(Guid.NewGuid(), "local_folder", name, "linked", path, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ContentKind: kind);

    [Fact]
    public void ViewListsOnePhotosAndVideosRowHoldingTheFoldersOfBothKindsAndDocumentsSeparately()
    {
        _viewSources =
        [
            ViewFolder("Pictures", "D:\\Pictures", "photos"),
            ViewFolder("Clips", "D:\\Clips", "personal_videos"),
            ViewFolder("Paperwork", "C:\\Paperwork", "documents"),
        ];
        var cut = OpenAndSettle();

        cut.WaitForAssertion(() => Assert.Equal(["2 folders", "1 folder"], cut.FindAll("tr[data-area=view]").Select(row => Text(row.QuerySelectorAll("td")[2])).ToArray()));
        Assert.Equal(["Photos and videos", "Documents"], cut.FindAll("tr[data-area=view] .libs__link").Select(Text).ToArray());
        Assert.Equal("/settings/libraries/view/photos", cut.FindAll("tr[data-area=view] .libs__link")[0].GetAttribute("href"));
        Assert.Empty(cut.FindAll("tr[data-library=personal_videos]"));
        Assert.Equal(["All4", "Watch1", "Listen1", "Read0", "View2"], ChipTexts(cut));

        cut.Find("tr[data-library=photos] [data-folder-count]").Click();
        var paths = cut.FindAll("tr[data-library=photos] [data-folder-list] li .lib-folders__path").Select(Text).ToArray();
        Assert.Equal(["D:\\Pictures", "D:\\Clips"], paths);
    }

    [Fact]
    public void FoldersOfPersonalVideosAloneStillShowUnderThePhotosAndVideosRow()
    {
        _viewSources = [ViewFolder("Clips", "D:\\Clips", "personal_videos")];
        var cut = OpenAndSettle();

        cut.WaitForAssertion(() => Assert.Equal(["Photos and videos"], cut.FindAll("tr[data-area=view] .libs__link").Select(Text).ToArray()));
        Assert.Equal("1 folder", Text(cut.Find("tr[data-library=photos]").QuerySelectorAll("td")[2]));
    }

    [Fact]
    public void ViewListsNoRowsWhileItHasNoFolders()
    {
        var cut = OpenAndSettle();

        Assert.Empty(cut.FindAll("tr[data-area=view]"));
        Assert.Equal("View0", ChipTexts(cut)[4]);
    }

    [Fact]
    public void TheEditWindowOfThePhotosAndVideosRowListsTheFoldersOfBothKinds()
    {
        _viewSources =
        [
            ViewFolder("Pictures", "D:\\Pictures", "photos"),
            ViewFolder("Clips", "D:\\Clips", "personal_videos"),
            ViewFolder("Paperwork", "C:\\Paperwork", "documents"),
        ];
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr[data-library=photos]")));

        cut.Find("tr[data-library=photos] button[data-edit]").Click();

        host.WaitForAssertion(() => Assert.Equal(["D:\\Pictures", "D:\\Clips"], host.FindAll("[data-view-kind-edit] .lib-edit__path").Select(Text).ToArray()));
        Assert.Equal("Edit Photos and videos", Text(host.Find(".app-dialog-shell__title")));
    }

    // D11: a library with no folders is not listed anywhere.
    [Fact]
    public void ALibraryWithNoFoldersIsNotInTheTableTheChipsTheCardOrTheFooterCount()
    {
        _configuration!.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [] });
        var cut = OpenAndSettle();

        Assert.Empty(cut.FindAll($"tr[data-library='{HomeVideosId}']"));
        Assert.Equal(["All2", "Watch1", "Listen1", "Read0", "View0"], ChipTexts(cut));
        Assert.Empty(cut.FindAll($"[data-lib-summary] [data-kind='{HomeVideosId}']"));
        Assert.Equal("2 libraries · 3 folders", Text(cut.Find("[data-libs-footer]")));
    }

    // The Add library dialog reaches such a library: its kind is a normal row there, and the folder is saved into it (CreateLibraryDialogTests).
    [Fact]
    public void ALibraryWithNoFoldersIsAbsentFromTheVaultAreaToo()
    {
        var configuration = BuildConfiguration();
        configuration.Libraries.Add(new() { Id = "99999999-9999-4999-8999-999999999999", Name = "Private movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"], IsVault = true, Sources = [] });

        var cut = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, configuration).Add(x => x.ShowVault, true));

        Assert.Empty(cut.FindAll("tr[data-area=vault]"));
        Assert.Equal("0", Text(cut.Find(".lib-chip[data-area=vault] .lib-chip__count")));
    }

    // A View read that failed is not the same as one that has not finished: the rows are listed with an unknown count and "No libraries yet" stays away.
    [Fact]
    public void WhenViewsFoldersCouldNotBeReadItsRowsAreListedWithAnUnknownCountAndNoLibrariesYetIsNotShown()
    {
        var configuration = new LibrariesConfigurationDto();

        var cut = Render<LibrariesOverview>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, configuration).Add(x => x.ViewFoldersUnreadable, true));

        Assert.Empty(cut.FindAll("[data-empty=none]"));
        Assert.Equal(["Photos and videos", "Documents"], cut.FindAll("tr[data-area=view] .libs__link").Select(Text).ToArray());
        Assert.All(cut.FindAll("tr[data-area=view] .libs__folders"), cell => Assert.Equal("—", Text(cell)));
    }

    [Fact]
    public void WhileViewsFoldersAreStillBeingReadItsRowsAreNotListedAndNoLibrariesYetDoesNotFlash()
    {
        var cut = Render<LibrariesOverview>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, new LibrariesConfigurationDto()));

        Assert.Empty(cut.FindAll("[data-empty=none]"));
        Assert.Empty(cut.FindAll("tr[data-area=view]"));
    }

    [Fact]
    public void ViewRowsAreListedWithNoCountWhenTheReadFailedAndHiddenWhenTheFoldersAreKnownToBeNone()
    {
        var kinds = LibraryKindsTestData.Catalog;
        var configuration = new LibrariesConfigurationDto();

        var failed = LibraryListing.RowsFor(kinds, "view", configuration, null, null, null, showVault: false, viewFoldersUnreadable: true);
        var loading = LibraryListing.RowsFor(kinds, "view", configuration, null, null, null, showVault: false);
        var none = LibraryListing.RowsFor(kinds, "view", configuration, null, new Dictionary<string, int> { ["photos"] = 0, ["documents"] = 0 }, null, showVault: false);

        Assert.Equal(2, failed.Count);
        Assert.All(failed, row => Assert.Null(row.Folders));
        Assert.Empty(loading);
        Assert.Empty(none);
    }

    [Fact]
    public void WithOnlyAnEmptyLibraryThePageSaysNoLibrariesYetAndTheImportFolderRowStaysListed()
    {
        _configuration!.Libraries.Clear();
        _configuration.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [] });
        var cut = OpenAndSettle();

        Assert.Contains("No libraries yet", Text(cut.Find("[data-empty=none]")));
        Assert.Empty(cut.FindAll("tr[data-area]"));
        Assert.Equal("not-set", cut.Find("tr[data-library=import] [data-import-status]").GetAttribute("data-import-status"));
    }

    [Fact]
    public void RemovingTheLastFolderAsksOnceAndKeepingItChangesNothing()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        EditButton(cut, AudiobooksId).Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        RemoveFolderAndReview(host, "audio-old");

        host.FindAll("button").Single(button => Text(button) == "Save").Click();

        host.WaitForAssertion(() => Assert.Contains("Remove the last folder?", host.Markup));
        Assert.Contains("will leave the Libraries list", host.Markup);
        host.FindAll("button").Single(button => Text(button) == "Keep").Click();

        host.WaitForAssertion(() => Assert.DoesNotContain("Remove the last folder?", host.Markup));
        Assert.Equal(0, _libraryMutations);
        Assert.NotEmpty(Row(cut, AudiobooksId).QuerySelectorAll("td"));
    }

    [Fact]
    public void ConfirmingTheRemovalOfTheLastFolderSavesAndTheRowDisappears()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        EditButton(cut, AudiobooksId).Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        RemoveFolderAndReview(host, "audio-old");

        host.FindAll("button").Single(button => Text(button) == "Save").Click();
        host.WaitForAssertion(() => Assert.Contains("Remove the last folder?", host.Markup));
        host.FindAll("button").Single(button => Text(button) == "Remove folder").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"tr[data-library='{AudiobooksId}']")));
        Assert.Equal(1, _libraryMutations);
        Assert.Equal(["All1", "Watch1", "Listen0", "Read0", "View0"], ChipTexts(cut));
    }

    [Fact]
    public void RemovingOneOfSeveralFoldersDoesNotAsk()
    {
        var host = Render<AppDialogHost>();
        var cut = OpenAndSettle();
        EditButton(cut, MoviesId).Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".lib-edit")));
        RemoveFolderAndReview(host, "movies-old");

        host.FindAll("button").Single(button => Text(button) == "Save").Click();

        host.WaitForAssertion(() => Assert.Equal(1, _libraryMutations));
        Assert.DoesNotContain("Remove the last folder?", host.Markup);
    }

    private static void RemoveFolderAndReview(IRenderedComponent<AppDialogHost> host, string sourceId)
    {
        host.Find($".lib-edit__folder[data-source='{sourceId}'] .app-overflow-menu__trigger").Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[role=menuitem]")));
        host.FindAll("[role=menuitem]").Single(item => Text(item).Contains("Remove", StringComparison.Ordinal)).Click();
        host.FindAll("button").Single(button => Text(button) == "Save changes").Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("[data-review]")));
    }

    [Fact]
    public void TheSetupTableListsLibrariesWithFoldersAndOnePhotosAndVideosRowButNoEmptyOnes()
    {
        var configuration = BuildConfiguration();
        configuration.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [] });
        IReadOnlyList<ViewSourceAdminDto> viewFolders =
        [
            ViewFolder("Pictures", "D:\\Pictures", "photos"),
            ViewFolder("Clips", "D:\\Clips", "personal_videos"),
        ];

        var cut = Render<SetupLibrariesTable>(parameters => parameters
            .Add(x => x.Configuration, configuration).Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.ViewFolders, viewFolders));

        Assert.Equal(["Movies", "Audiobooks", "Photos and videos"], cut.FindAll(".setup-libs__name strong").Select(Text).ToArray());
        Assert.Empty(cut.FindAll($"tr[data-library='{HomeVideosId}']"));
    }

    [Fact]
    public void TheSetupTableSaysNoLibrariesYetWhenOnlyEmptyLibrariesExist()
    {
        var configuration = new LibrariesConfigurationDto();
        configuration.Libraries.Add(new() { Id = HomeVideosId, Name = "Home videos", KindKey = "other_videos", Area = "watch", MediaTypes = ["Videos"], Sources = [] });

        var cut = Render<SetupLibrariesTable>(parameters => parameters.Add(x => x.Configuration, configuration).Add(x => x.Kinds, LibraryKindsTestData.Catalog));

        Assert.Contains("No libraries yet", Text(cut.Find("[data-empty=none]")));
        Assert.Empty(cut.FindAll("tr[data-library]"));
    }

    [Fact]
    public void PhotosAndVideosIsWrittenInSentenceCaseInTheLabelsAndTheCard()
    {
        Assert.Equal("Photos and videos", LibraryTypeCatalog.PhotosAndVideosLabel);
        var photos = LibraryKindsTestData.Catalog.FindViewKind("photos")!;
        Assert.Equal("Photos and videos", LibraryTypeCatalog.AddChoiceLabel(photos));
        Assert.Equal(["Photos and videos", "Documents"], LibraryKindsTestData.Catalog.ViewRowKinds.Select(kind => kind.Label).ToArray());

        var rows = LibraryListing.RowsFor(LibraryKindsTestData.Catalog, "view", new LibrariesConfigurationDto(), null, new Dictionary<string, int> { ["photos"] = 1, ["personal_videos"] = 2 }, null, showVault: false);
        var card = Render<LibrariesSummary>(parameters => parameters.Add(x => x.Rows, rows));

        Assert.Single(card.FindAll("[data-lib-summary] [data-kind=photos]"));
        Assert.Contains("Photos and videos", Text(card.Find("[data-lib-summary] [data-kind=photos] .lib-summary__label")));
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

    // Libraries v3 D22: Add library sits at the top right of the page header, on the title row, never in the page content.
    private IRenderedComponent<SettingsPageFrame> RenderInFrame(bool readOnly)
    {
        var kinds = LibraryKindsTestData.Catalog;
        var configuration = BuildConfiguration();
        return Render<SettingsPageFrame>(parameters => parameters
            .Add(frame => frame.Title, "Libraries")
            .Add(frame => frame.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<LibrariesOverview>(0);
                builder.AddAttribute(1, nameof(LibrariesOverview.Kinds), kinds);
                builder.AddAttribute(2, nameof(LibrariesOverview.Configuration), configuration);
                builder.AddAttribute(3, nameof(LibrariesOverview.ReadOnly), readOnly);
                builder.CloseComponent();
            })));
    }

    [Fact]
    public void AddLibraryIsInThePageHeaderRowBesideTheTitleAndNotInThePageContent()
    {
        var cut = RenderInFrame(readOnly: false);

        var header = cut.Find("[data-settings-frame-slot='header']");
        var inHeader = header.QuerySelectorAll(".app-page-header__actions button").Where(button => Text(button) == "Add library").ToList();
        Assert.Single(inHeader);
        Assert.NotNull(header.QuerySelector("h1"));
        var content = cut.Find("[data-settings-frame-slot='content']");
        Assert.DoesNotContain(content.QuerySelectorAll("button"), button => Text(button) == "Add library");
        Assert.Empty(content.QuerySelectorAll(".libs__header"));
    }

    [Fact]
    public void TheHeaderAddLibraryButtonDoesNotAppearWhereEditingIsNotAllowed()
    {
        var cut = RenderInFrame(readOnly: true);

        Assert.Empty(cut.FindAll(".app-page-header__actions"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Add library");
    }

    [Fact]
    public void OutsideTheSettingsFrameAddLibraryKeepsARowOfItsOwnAboveTheCollectionCard()
    {
        var cut = Render<LibrariesOverview>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, BuildConfiguration()));

        Assert.Single(cut.FindAll(".libs__header button"), button => Text(button) == "Add library");
    }

    [Fact]
    public void LeavingTheLibrariesPageTakesAddLibraryOutOfTheHeader()
    {
        var actions = new SettingsHeaderActions();
        var cut = Render<CascadingValue<SettingsHeaderActions>>(parameters => parameters
            .Add(value => value.Value, actions)
            .Add(value => value.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<LibrariesOverview>(0);
                builder.AddAttribute(1, nameof(LibrariesOverview.Kinds), LibraryKindsTestData.Catalog);
                builder.AddAttribute(2, nameof(LibrariesOverview.Configuration), BuildConfiguration());
                builder.CloseComponent();
            })));
        Assert.NotNull(actions.Content);

        cut.Render(parameters => parameters
            .Add(value => value.Value, actions)
            .Add(value => value.ChildContent, (RenderFragment)(_ => { })));

        Assert.Null(actions.Content);
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

    // 12. The earlier group and type addresses are not found; nothing forwards them (look-pass-v3 D17).
    [Theory]
    [InlineData("watch", null)]
    [InlineData("watch", "movies")]
    [InlineData("listen", "audiobooks")]
    [InlineData("vault", null)]
    [InlineData("watch", "videos")]
    public void AnEarlierGroupOrTypeAddressIsNotFoundAndIsNotForwarded(string subsection, string? detail)
    {
        var before = CurrentUri;

        var cut = Open(subsection, detail);

        Assert.Equal(before, CurrentUri);
        Assert.Contains("This library page does not exist", cut.Markup);
    }

    // 13. The pure rules behind the rows.
    [Fact]
    public void StatusIsTheWorstStateFirst()
    {
        var library = new LibraryFolderDto { Sources = [new() { Path = "E:\\a" }], UnmatchedCount = 2 };
        var offline = new LibraryHealth(false, "1 folder unavailable");

        Assert.Equal(LibraryStatusKind.FolderOffline, LibraryListing.StatusOf(library, 10, offline).Status);
        Assert.Equal(LibraryStatusKind.NeedsReview, LibraryListing.StatusOf(library, 10, LibraryHealth.Ok).Status);
        library.UnmatchedCount = 0;
        library.ReviewCount = 1;
        Assert.Equal(LibraryStatusKind.NeedsReview, LibraryListing.StatusOf(library, 10, LibraryHealth.Ok).Status);
        library.ReviewCount = 0;
        Assert.Equal(LibraryStatusKind.Empty, LibraryListing.StatusOf(library, 0, LibraryHealth.Ok).Status);
        Assert.Equal(LibraryStatusKind.Healthy, LibraryListing.StatusOf(library, null, LibraryHealth.Ok).Status);
        Assert.Null(LibraryListing.StatusOf(library, 5, null).Status);
        Assert.Equal(LibraryStatusKind.Empty, LibraryListing.StatusOf(new LibraryFolderDto(), null, null).Status);
    }

    // D12: one colour per kind, shared by the card cell and the row icon.
    // The kind's colour class an element carries (lib-kind-color--*); the class is the only thing the card cell and the row icon take their colour from.
    private static string ColorClassOf(AngleSharp.Dom.IElement element) =>
        Assert.Single(element.ClassList, name => name.StartsWith("lib-kind-color--", StringComparison.Ordinal));

    private static (LibraryKindCatalog Catalog, LibrariesConfigurationDto Configuration, IReadOnlyList<ViewSourceAdminDto> ViewFolders) EveryKindEverywhere()
    {
        // Every kind the Engine offers, plus one a plugin adds (Podcasts), each with a folder so each is listed.
        var catalog = new LibraryKindCatalog(LibraryKindsTestData.All(LibraryKindsTestData.Podcasts()));
        var configuration = new LibrariesConfigurationDto();
        foreach (var type in catalog.Types)
        {
            configuration.Libraries.Add(new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = type.Label,
                KindKey = type.Key,
                Area = type.Area,
                Kind = "catalogued",
                MediaTypes = [.. type.MediaTypes],
                Sources = [new() { Id = $"{type.Key}-folder", Path = $"E:\\{type.Key}", ManagementMode = "existing_library", AccessMode = "read_only" }],
            });
        }

        IReadOnlyList<ViewSourceAdminDto> viewFolders =
        [
            ViewFolder("Pictures", "D:\\Pictures", "photos"),
            ViewFolder("Clips", "D:\\Clips", "personal_videos"),
            ViewFolder("Paperwork", "D:\\Paperwork", "documents"),
        ];
        return (catalog, configuration, viewFolders);
    }

    private static IReadOnlyList<LibraryListRow> RowsOf(LibraryKindCatalog catalog, LibrariesConfigurationDto configuration)
    {
        var viewCounts = new Dictionary<string, int> { ["photos"] = 1, ["personal_videos"] = 1, ["documents"] = 1 };
        return [.. new[] { "watch", "listen", "read", "view" }.SelectMany(area => LibraryListing.RowsFor(catalog, area, configuration, null, viewCounts, null, showVault: false))];
    }

    [Fact]
    public void EveryKindHasTheSameColourOnItsCardCellAndItsSetupRowIcon()
    {
        var (catalog, configuration, viewFolders) = EveryKindEverywhere();
        var rows = RowsOf(catalog, configuration);
        Assert.Contains(rows, row => row.Key == "photos" && row.Name == "Photos and videos");
        Assert.Contains(rows, row => row.Type.Key == "podcasts");
        Assert.Contains(rows, row => row.Type.Key == "youtube");

        var card = Render<LibrariesSummary>(parameters => parameters.Add(x => x.Rows, rows));
        var table = Render<SetupLibrariesTable>(parameters => parameters.Add(x => x.Configuration, configuration).Add(x => x.Kinds, catalog).Add(x => x.ViewFolders, viewFolders));

        foreach (var row in rows)
        {
            var cell = card.Find($"[data-lib-summary] [data-kind='{row.Key}']");
            var icon = table.Find($"tr[data-library='{row.Key}'] .lib-kind-tile");
            Assert.Equal(LibraryKindLook.ColorClass(row.Type), ColorClassOf(cell));
            Assert.Equal(ColorClassOf(cell), ColorClassOf(icon));
        }
    }

    [Fact]
    public void EveryKindHasTheSameColourOnItsCardCellAndItsLibrariesRowIcon()
    {
        var (catalog, configuration, _) = EveryKindEverywhere();
        _kinds = LibraryKindsTestData.All(LibraryKindsTestData.Podcasts());
        _configuration = configuration;
        _overview = null; // no title counts known, so each kind's cell shows its folder count
        var cut = OpenAndSettle();

        // The Vault kind (Adult content) is not listed here, so it is not in the rows either.
        var listed = RowsOf(catalog, configuration).Where(row => row.Library is not null).ToList();
        Assert.True(listed.Count >= 10);
        foreach (var row in listed)
        {
            var cell = cut.Find($"[data-lib-summary] [data-kind='{row.Key}']");
            var icon = Row(cut, row.Key).QuerySelector(".lib-kind-tile")!;
            Assert.Equal(LibraryKindLook.ColorClass(row.Type), ColorClassOf(cell));
            Assert.Equal(ColorClassOf(cell), ColorClassOf(icon));
        }
    }

    [Fact]
    public void AudiobooksIsTheAudiobooksColourOnTheCardAndOnTheRow()
    {
        var cut = OpenAndSettle();

        var cell = cut.Find($"[data-lib-summary] [data-kind='{AudiobooksId}']");
        var icon = Row(cut, AudiobooksId).QuerySelector(".lib-kind-tile")!;
        Assert.Equal("lib-kind-color--audiobooks", ColorClassOf(cell));
        Assert.Equal("lib-kind-color--audiobooks", ColorClassOf(icon));
        Assert.Equal("lib-kind-color--movies", ColorClassOf(cut.Find($"[data-lib-summary] [data-kind='{MoviesId}']")));
    }

    [Theory]
    [InlineData("movies", "--tl-media-movies")]
    [InlineData("tv", "--tl-media-tv")]
    [InlineData("music", "--tl-media-music")]
    [InlineData("audiobooks", "--tl-media-audiobooks")]
    [InlineData("books", "--tl-media-books")]
    [InlineData("comics", "--tl-media-comics")]
    [InlineData("documents", "--tl-media-document")]
    [InlineData("youtube", "--tl-area-watch")]
    [InlineData("other_videos", "--tl-area-watch")]
    [InlineData("other_audio", "--tl-area-listen")]
    [InlineData("other_reading", "--tl-area-read")]
    [InlineData("photos", "--tl-area-view")]
    [InlineData("podcasts", "--tl-area-listen")]
    public void AKindUsesItsOwnMediaColourAndAKindWithoutOneUsesItsAreasColour(string key, string token)
    {
        var catalog = new LibraryKindCatalog(LibraryKindsTestData.All(LibraryKindsTestData.Podcasts()));

        Assert.Equal(token, LibraryKindLook.ColorToken(catalog.Find(key)!));
    }

    [Fact]
    public void TheDesignSystemDefinesEveryColorTokenAKindCanUse()
    {
        var tokens = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        var catalog = new LibraryKindCatalog(LibraryKindsTestData.All(LibraryKindsTestData.Podcasts()));

        foreach (var token in catalog.Types.Concat(catalog.ViewKinds).Select(LibraryKindLook.ColorToken).Distinct())
        {
            Assert.Contains($"{token}:", tokens, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryKindColorClassSetsTheKindColourToItsDesignSystemToken()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", "wwwroot", "app.css"));
        var catalog = new LibraryKindCatalog(LibraryKindsTestData.All(LibraryKindsTestData.Podcasts()));

        foreach (var type in catalog.Types.Concat(catalog.ViewKinds))
        {
            var rule = $".{LibraryKindLook.ColorClass(type)} {{ --lib-kind-color: var({LibraryKindLook.ColorToken(type)}); }}";
            Assert.Contains(rule, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACardCellShowsTheIconThenTheCountThenTheKindName()
    {
        var cut = OpenAndSettle();

        var cell = cut.Find($"[data-lib-summary] [data-kind='{MoviesId}']");
        Assert.Equal(["lib-summary__icon", "lib-summary__count", "lib-summary__label"], cell.Children.Select(child => child.ClassName ?? string.Empty).ToArray());
        Assert.NotNull(cell.Children[0].QuerySelector("svg"));
        Assert.Equal("1,284", Text(cell.Children[1]));
        Assert.Equal("Movies", Text(cell.Children[2]));
    }

    [Fact]
    public void TheAreaTagOnARowKeepsTheAreaColourNotTheKindColour()
    {
        var cut = OpenAndSettle();

        var tag = Row(cut, AudiobooksId).QuerySelector("[data-area-tag]")!;
        Assert.Contains("lib-area--listen", tag.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain(tag.ClassList, name => name.StartsWith("lib-kind-color--", StringComparison.Ordinal));
        Assert.Contains("lib-area--watch", Row(cut, MoviesId).QuerySelector("[data-area-tag]")!.ClassName, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
