using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Review;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static MediaEngine.Web.Tests.Support.NeedsReviewTestRows;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings → Needs Review (spec needs-review-redesign): four groups with counts, rows as cards, the Engine's suggestion with Use this,
/// families, the choices strip, in-row done and failure lines, the kept items, and every state of the page. Only server administrators get
/// the list at all. Ticking rows (tick boxes, the group headings, families, a phone's Select) shows the action bar, whose buttons act on the
/// ticked rows only (WP5).
/// </summary>
public sealed class NeedsReviewPageTests : AsyncBunitContext
{
    private static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static Guid Id(int n) => Guid.Parse($"{n:D8}-1111-4111-8111-111111111111");

    private static readonly NeedsReviewBestGuessDto ConfirmGuess = new() { Action = "confirm_match", Name = "Cibola Burn", Year = "2014", Source = "Provider match" };
    private static readonly NeedsReviewBestGuessDto KeepGuess = new() { Action = "keep_provider_match", Name = "Leviathan Wakes", Year = "2011", Source = "Provider match" };
    private static readonly NeedsReviewBestGuessDto TvGuess = new() { Action = "move_to_tv", Name = "Dr. Horrible's Sing-Along Blog", Year = "2008", Source = "TMDB TV series" };

    private static AccountProfileGrantDto[] Grants(bool adminEnabled) =>
    [
        new(AccountId, ProfileId, "Test User", null, true, true, adminEnabled,
            new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow),
    ];

    private static DashboardAuthorityResponse ServerAdministrator() => new(
        AccountId, ProfileId, true, true, 1, 1, true, true, null, 1, Grants(adminEnabled: true),
        ["settings.administration"], ["access.manage", "metadata.manage"]);

    // An administrator the Engine does not (yet) let edit item details because the administrator PIN is locked: no "metadata.manage".
    // Edit is offered and asks for the PIN on tap; these tests register no PIN prompt, so nothing can open.
    private static DashboardAuthorityResponse ServerAdministratorWhoMayNotEditDetails() => new(
        AccountId, ProfileId, true, true, 1, 1, true, true, null, 1, Grants(adminEnabled: true),
        ["settings.administration"], ["access.manage"]);

    private static DashboardAuthorityResponse HouseholdAdministrator() => new(
        AccountId, ProfileId, true, true, 1, 1, false, false, null, 1, Grants(adminEnabled: false),
        [], ["household.manage"]);

    /// <summary>What the page asked of the Engine, and the switches a test flips to make an action fail.</summary>
    private sealed class Engine
    {
        public Func<string?, bool, NeedsReviewListAttempt> List { get; set; } = (_, _) => new NeedsReviewListAttempt(Page([]), null);

        public List<string> Calls { get; set; } = [];

        public List<(string? Library, int Skip, bool Kept)> ListReads { get; } = [];

        public List<Guid> Resolved { get; } = [];

        public List<Guid> Dismissed { get; } = [];

        public List<Guid> Skipped { get; } = [];

        public List<Guid> Moved { get; } = [];

        public List<(Guid Review, string Choice)> Chosen { get; } = [];

        public List<Guid> Reopened { get; } = [];

        public List<Guid> Retagged { get; } = [];

        public List<Guid> RetriedFiles { get; } = [];

        public List<Guid> KeptFiles { get; } = [];

        public List<Guid> UnkeptFiles { get; } = [];

        public List<Guid> OpenedInEditor { get; } = [];

        /// <summary>Names of Engine client methods that answer "no" (or nothing).</summary>
        public HashSet<string> Failing { get; } = [];

        public UniverseStateContainer State { get; } = new();

        public int NormalListReads => ListReads.Count(read => !read.Kept);
    }

    private Engine Register(Func<NeedsReviewListDto> list, DashboardAuthorityResponse? authority = null, Action<EngineApiClientStub>? configure = null) =>
        Register((_, _) => list(), authority, configure);

    private Engine Register(Func<string?, bool, NeedsReviewListDto> list, DashboardAuthorityResponse? authority = null, Action<EngineApiClientStub>? configure = null)
    {
        var engine = new Engine { List = (library, kept) => new NeedsReviewListAttempt(list(library, kept), null) };
        return RegisterEngine(engine, authority, configure);
    }

    private Engine RegisterEngine(Engine engine, DashboardAuthorityResponse? authority = null, Action<EngineApiClientStub>? configure = null)
    {
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", AccountId, ProfileId, Guid.NewGuid(), authority ?? ServerAdministrator());
        Services.AddSingleton(session);
        var api = EngineApiClientStub.Create(stub =>
        {
            engine.Calls = stub.Calls;
            stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), args =>
            {
                var kept = args![3] is true;
                engine.ListReads.Add(((string?)args[0], (int)args[1]!, kept));
                return Task.FromResult(engine.List((string?)args[0], kept));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), args =>
            {
                engine.OpenedInEditor.Add((Guid)args![1]!);
                return Task.FromResult<MediaEngine.Contracts.Details.DetailPageViewModel?>(new()
                {
                    Id = EditingModeTestData.MovieId.ToString("D"),
                    EntityType = MediaEngine.Contracts.Details.DetailEntityType.Work,
                    Title = "Cibola Burn",
                    OverflowActions = [new() { Key = "edit", Label = "Edit this title", Icon = "edit" }],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ =>
                Task.FromResult<MediaEngine.Contracts.Metadata.MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            // The ticked-rows editor reads each item's fields once when its Edit panel opens (Edit N items).
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1, Fields = [] })));
            stub.SetHandler(nameof(IEngineApiClient.ResolveReviewItemAsync), args =>
            {
                engine.Resolved.Add((Guid)args![0]!);
                return Task.FromResult(!engine.Failing.Contains(nameof(IEngineApiClient.ResolveReviewItemAsync)));
            });
            stub.SetHandler(nameof(IEngineApiClient.DismissReviewItemAsync), args =>
            {
                engine.Dismissed.Add((Guid)args![0]!);
                return Task.FromResult(!engine.Failing.Contains(nameof(IEngineApiClient.DismissReviewItemAsync)));
            });
            stub.SetHandler(nameof(IEngineApiClient.SkipUniverseAsync), args =>
            {
                engine.Skipped.Add((Guid)args![0]!);
                return Task.FromResult(!engine.Failing.Contains(nameof(IEngineApiClient.SkipUniverseAsync)));
            });
            stub.SetHandler(nameof(IEngineApiClient.ReopenReviewItemAsync), args =>
            {
                engine.Reopened.Add((Guid)args![0]!);
                return Task.FromResult(!engine.Failing.Contains(nameof(IEngineApiClient.ReopenReviewItemAsync)));
            });
            stub.SetHandler(nameof(IEngineApiClient.ChooseReviewAsync), args =>
            {
                var id = (Guid)args![0]!;
                var choice = (string)args[1]!;
                engine.Chosen.Add((id, choice));
                return Task.FromResult<ReviewChooseResponse?>(engine.Failing.Contains(nameof(IEngineApiClient.ChooseReviewAsync))
                    ? null
                    : new ReviewChooseResponse(true, id, choice, "retail"));
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveReviewItemToTvAsync), args =>
            {
                var id = (Guid)args![0]!;
                engine.Moved.Add(id);
                return Task.FromResult<ReviewMoveToTvResponse?>(engine.Failing.Contains(nameof(IEngineApiClient.MoveReviewItemToTvAsync))
                    ? null
                    : new ReviewMoveToTvResponse(true, id, Guid.NewGuid(), "Dr. Horrible's Sing-Along Blog", "tv-library", 1, [1, 2, 3], null));
            });
            stub.SetHandler(nameof(IEngineApiClient.RetryRetagForAssetAsync), args =>
            {
                engine.Retagged.Add((Guid)args![0]!);
                return Task.FromResult(!engine.Failing.Contains(nameof(IEngineApiClient.RetryRetagForAssetAsync)));
            });
            stub.SetHandler(nameof(IEngineApiClient.RetryUnmatchedItemAsync), args =>
            {
                engine.RetriedFiles.Add((Guid)args![0]!);
                return Task.FromResult<UnmatchedActionResultDto?>(engine.Failing.Contains(nameof(IEngineApiClient.RetryUnmatchedItemAsync)) ? null : new UnmatchedActionResultDto { Affected = 1 });
            });
            stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args =>
            {
                engine.KeptFiles.Add((Guid)args![0]!);
                return Task.FromResult<UnmatchedActionResultDto?>(engine.Failing.Contains(nameof(IEngineApiClient.KeepUnmatchedItemAsync)) ? null : new UnmatchedActionResultDto { Affected = 1 });
            });
            stub.SetHandler(nameof(IEngineApiClient.UnkeepUnmatchedItemAsync), args =>
            {
                engine.UnkeptFiles.Add((Guid)args![0]!);
                return Task.FromResult<UnmatchedActionResultDto?>(engine.Failing.Contains(nameof(IEngineApiClient.UnkeepUnmatchedItemAsync)) ? null : new UnmatchedActionResultDto { Affected = 1 });
            });
            // Two libraries: a file the Engine could not match has no type of its own, so its kind comes from its library.
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<MediaEngine.Contracts.Settings.LibrariesConfigurationDto?>(new()
            {
                Libraries =
                [
                    new() { Id = "movies", Name = "Movies", Area = "watch", MediaTypes = ["Movies"] },
                    new() { Id = "tv", Name = "TV", Area = "watch", MediaTypes = ["TV"] },
                ],
            }));
            configure?.Invoke(stub);
        });
        Services.AddSingleton(api);
        Services.AddSingleton(engine.State);
        Services.AddSingleton(provider => new UIOrchestratorService(
            api,
            engine.State,
            new ActiveProfileSessionService(
                provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: session),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: session));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return engine;
    }

    private Task UseDesktopAsync() =>
        Services.GetRequiredService<DeviceContextService>()
            .InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private Task UsePhoneAsync() =>
        Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

    private Task UseTvAsync() =>
        Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true));

    private IRenderedComponent<NeedsReviewPageContent> RenderLoaded(int expectedRows = 1, Action<ComponentParameterCollectionBuilder<NeedsReviewPageContent>>? parameters = null)
    {
        var cut = parameters is null ? Render<NeedsReviewPageContent>() : Render(parameters);
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("[data-testid='needs-review-row'], [data-testid='needs-review-family-row']").Count >= expectedRows));
        return cut;
    }

    private static string RowSelector(NeedsReviewRowDto row) => $"[data-testid='needs-review-row'][data-key='{row.Key}']";

    private static string DoneSelector(NeedsReviewRowDto row) => $"[data-testid='needs-review-done'][data-key='{row.Key}']";

    private static string GroupHeading(AngleSharp.Dom.IElement group) => group.QuerySelector("h2, h3")!.TextContent.Trim();

    private static void OpenMenuAndClick(IRenderedComponent<NeedsReviewPageContent> cut, string rowSelector, string label)
    {
        cut.Find($"{rowSelector} .app-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.app-menu-item"), button => button.TextContent.Contains(label, StringComparison.Ordinal)));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    private static string[] MenuLabels(IRenderedComponent<NeedsReviewPageContent> cut, string rowSelector)
    {
        cut.Find($"{rowSelector} .app-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        return cut.FindAll("button.app-menu-item").Select(button => button.TextContent.Trim()).ToArray();
    }

    // ---- States ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhileTheListLoads_ThePageShowsPlaceholderRows_NotTheEmptyMessage()
    {
        var release = new TaskCompletionSource<NeedsReviewListAttempt>();
        Register(() => Page([]), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => release.Task));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-loading']")));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-empty']"));

        release.SetResult(new NeedsReviewListAttempt(Page([ReviewRow(Id(1), "Loaded Book")]), null));
        cut.WaitForAssertion(() => Assert.Contains("Loaded Book", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-loading']"));
    }

    [Fact]
    public void WithNothingToReview_ThePageSaysNothingNeedsReview()
    {
        Register(() => Page([]));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing needs review", cut.Find("[data-testid='needs-review-empty']").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("could not be loaded", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='needs-review-group']"));
    }

    [Fact]
    public void ALoadThatFails_ShowsTheErrorWithTryAgain_NeverTheEmptyMessage_AndTryAgainShowsTheRows()
    {
        var failing = true;
        var engine = new Engine { List = (_, _) => failing ? new NeedsReviewListAttempt(null, "The Engine answered 500.") : new NeedsReviewListAttempt(Page([ReviewRow(Id(1), "Recovered Book")]), null) };
        RegisterEngine(engine);

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("Needs Review could not be loaded.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing needs review", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='needs-review-row']"));

        failing = false;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-row']")));
        Assert.DoesNotContain("could not be loaded", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Recovered Book", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AHouseholdAdministrator_GetsNoList_AndNoReviewRequestIsMade()
    {
        var engine = Register(() => Page([ReviewRow(Id(1), "Pending Book")]), authority: HouseholdAdministrator());

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("server administrators", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-row']"));
        Assert.DoesNotContain("Nothing needs review", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNeedsReviewListAsync), engine.Calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), engine.Calls);
    }

    [Fact]
    public void ASearchThatLeavesNothing_SaysSoAndHowManyItemsStillNeedReview()
    {
        Register(() => Page([ReviewRow(Id(1), "Dune"), ReviewRow(Id(2), "Emma")]));
        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.InitialSearch, "zzz"));

        var message = cut.WaitForElement("[data-testid='needs-review-no-match']").TextContent;

        Assert.Contains("No items match \"zzz\"", message, StringComparison.Ordinal);
        Assert.Contains("2 items still need review.", message, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='needs-review-group']"));
    }

    [Fact]
    public void TheSearch_StartsFromTheQueryText_AndNarrowsTheList()
    {
        Register(() => Page([ReviewRow(Id(1), "Dune"), ReviewRow(Id(2), "Emma")]));

        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.InitialSearch, "dune"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-row']")));
        Assert.Contains("Dune", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Emma", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithALibrary_TheListIsRequestedForThatLibraryOnly_AndTheRequestCarriesNoKind()
    {
        var engine = Register(() => Page([ReviewRow(Id(1), "Pending Book")]));

        RenderLoaded(1, p => p.Add(c => c.Library, "library-a"));

        var read = Assert.Single(engine.ListReads);
        Assert.Equal("library-a", read.Library);
        Assert.False(read.Kept);
    }

    [Fact]
    public void WhenTheEngineHoldsMoreRowsThanThePageReadSoFar_ShowMoreReadsTheNextPage()
    {
        var second = ReviewRow(Id(2), "Second Page Book");
        var engine = Register((_, _) => Page([ReviewRow(Id(1), "First Page Book")], total: 2));
        engine.List = (_, _) => engine.ListReads.Count(read => !read.Kept) <= 1
            ? new NeedsReviewListAttempt(Page([ReviewRow(Id(1), "First Page Book")], total: 2), null)
            : new NeedsReviewListAttempt(Page([second], total: 2), null);
        var cut = RenderLoaded();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Show more").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='needs-review-row']").Count));
        Assert.Contains(engine.ListReads, read => read.Skip == 1);
    }

    [Fact]
    public async Task WhileFilesAreStillBeingImported_ALineSaysMoreItemsMayAppear_AndLinksToActivity()
    {
        var engine = Register(() => Page([ReviewRow(Id(1), "Pending Book")]));
        engine.State.PushIngestionQueueProgress(new IngestionQueueProgressDto { State = IngestionQueueProgressText.WorkingState, FilesFound = 40, FilesDone = 25 });

        var cut = RenderLoaded();

        var note = cut.Find("[data-testid='needs-review-importing']");
        Assert.Contains("still working on 15 files", note.TextContent, StringComparison.Ordinal);
        Assert.Equal("/settings/general/library-activity", note.QuerySelector("a")!.GetAttribute("href"));

        await cut.InvokeAsync(() => engine.State.PushIngestionQueueProgress(
            new IngestionQueueProgressDto { State = IngestionQueueProgressText.IdleState, FilesFound = 40, FilesDone = 40, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='needs-review-importing']")));
    }

    // ---- Groups and rows ------------------------------------------------------------------------------------------------------------

    private static NeedsReviewListDto OneRowPerGroup() => Page(
    [
        ReviewRow(Id(1), "Ready Film", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.84),
        ReviewRow(Id(2), "Choose Film", trigger: "RetailMatchAmbiguous", group: NeedsReviewGroups.Choose),
        FileRow(Id(3), "Nothing.Close.mkv"),
        ReviewRow(Id(4), "Locked Song", trigger: "WritebackFailed", group: NeedsReviewGroups.FileProblem),
    ]);

    [Fact]
    public void TheListIsSplitIntoFourGroupsInOrder_EachWithItsCount_AndASummaryOfEverythingWaiting()
    {
        Register(() => OneRowPerGroup());

        var cut = RenderLoaded(4);

        var groups = cut.FindAll("[data-testid='needs-review-group']");
        Assert.Equal(["Ready to confirm", "Choose a match", "No match found", "File problems"], groups.Select(GroupHeading).ToArray());
        Assert.All(groups, group => Assert.Equal("1", group.QuerySelector("[data-testid='needs-review-group-count']")!.TextContent.Trim()));
        Assert.Contains("4 items need review", cut.Find("[data-testid='needs-review-summary']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupWithNoItems_IsNotDrawn()
    {
        Register(() => Page([FileRow(Id(1), "a.mkv"), FileRow(Id(2), "b.mkv")]));

        var cut = RenderLoaded(2);

        Assert.Equal(["No match found"], cut.FindAll("[data-testid='needs-review-group']").Select(GroupHeading).ToArray());
        Assert.Contains("2 items need review", cut.Find("[data-testid='needs-review-summary']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWordOnThePage_SaysNotMatchedNotSureOrUnmatched()
    {
        Register(() => OneRowPerGroup());

        var cut = RenderLoaded(4);

        var text = cut.Find("[data-testid='needs-review']").TextContent;
        Assert.DoesNotContain("Not matched", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Not sure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unmatched", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dismiss from review", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep provider match", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARow_ShowsItsName_AKindLibraryAndFolderLine_AndOnePlainSentenceForWhy()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", folder: "C:/media/movies/Action/2019");
        Register(() => Page([file]));

        var cut = RenderLoaded();

        var row = cut.Find(RowSelector(file));
        Assert.Equal("Mystery.2019.mkv", row.QuerySelector("[data-testid='needs-review-title']")!.TextContent.Trim());
        Assert.Equal("Movie · Movies · …/Action/2019", row.QuerySelector("[data-testid='needs-review-meta']")!.TextContent.Trim());
        Assert.Contains("found nothing close enough", row.QuerySelector("[data-testid='needs-review-reason']")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileTheEngineWillLookUpAgain_SaysWhen_AndHasATickBoxOnADesktop()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9), nextRetryAt: DateTimeOffset.Now.AddDays(3));
        Register(() => Page([file]));
        await UseDesktopAsync();

        var cut = RenderLoaded();

        Assert.Contains("3", cut.Find($"{RowSelector(file)} [data-testid='needs-review-retry']").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll($"{RowSelector(file)} input[type='checkbox']"));
    }

    // ---- Suggestion and Use this ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheEnginesSuggestion_ShowsItsNameAndHowSureItIs_BesideUseThis()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.84);
        Register(() => Page([row]));

        var cut = RenderLoaded();

        var suggestion = cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion']");
        Assert.Contains("Cibola Burn (2014)", suggestion.TextContent, StringComparison.Ordinal);
        Assert.Contains("84% sure", suggestion.TextContent, StringComparison.Ordinal);
        Assert.Equal("Use this", suggestion.QuerySelector("button")!.TextContent.Trim());
    }

    [Fact]
    public void UseThis_OnAConfirmedMatch_ResolvesTheItem_NotDismissesIt_AndTheRowBecomesAMatchedLine()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.84);
        var other = ReviewRow(Id(2), "Other Film", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.9);
        var engine = Register(() => Page([row, other]));
        var cut = RenderLoaded(2);

        cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Cibola Burn matched to Cibola Burn (2014)", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([row.Review!.Id], engine.Resolved);
        Assert.Empty(engine.Dismissed);
        Assert.Empty(engine.Skipped);
        Assert.Empty(cut.FindAll(RowSelector(row)));
        Assert.NotNull(cut.Find($"{DoneSelector(row)} .nr-done__tick"));
    }

    [Fact]
    public void UseThis_OnAProviderMatchTheEngineKept_RunsTheKeepProviderMatchAction()
    {
        var row = ReviewRow(Id(1), "Leviathan Wakes", trigger: "MultipleQidMatches", group: NeedsReviewGroups.Ready, guess: KeepGuess);
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(row))));
        Assert.Equal([row.Review!.Id], engine.Skipped);
        Assert.Empty(engine.Resolved);
    }

    [Fact]
    public void UseThis_ForAFilmThatIsReallyATvShow_MovesItToTv()
    {
        var row = ReviewRow(Id(1), "Dr Horribles", trigger: "RetailMatchAmbiguous", group: NeedsReviewGroups.Ready, guess: TvGuess);
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("moved to TV", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([row.Review!.Id], engine.Moved);
    }

    [Fact]
    public void WhenTheEngineRefusesUseThis_TheRowStays_AndAReasonShowsUnderIt()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.84);
        var engine = Register(() => Page([row]));
        engine.Failing.Add(nameof(IEngineApiClient.ResolveReviewItemAsync));
        var cut = RenderLoaded();

        cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Cibola Burn could not be matched.", cut.Find($"{RowSelector(row)} [data-review-failure]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(DoneSelector(row)));
        Assert.Contains("1 item needs review", cut.Find("[data-testid='needs-review-summary']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCountsDropAtOnce_WhenARowFinishes_WithoutWaitingForTheListToReload()
    {
        var first = ReviewRow(Id(1), "Cibola Burn", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.84);
        var second = ReviewRow(Id(2), "Other Film", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.9);
        var engine = Register(() => Page([first, second]));
        var cut = RenderLoaded(2);
        var readsBefore = engine.NormalListReads;

        cut.Find($"{RowSelector(first)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(first))));
        Assert.Equal("1", cut.Find("[data-testid='needs-review-group-count']").TextContent.Trim());
        Assert.Contains("1 item needs review", cut.Find("[data-testid='needs-review-summary']").TextContent, StringComparison.Ordinal);
        Assert.Equal(readsBefore, engine.NormalListReads);
        Assert.Contains(nameof(IEngineApiClient.GetReviewCountAsync), engine.Calls);
    }

    // ---- The choices strip ----------------------------------------------------------------------------------------------------------

    private static string TvCandidates() => System.Text.Json.JsonSerializer.Serialize(new[]
    {
        new MovieTvSuggestionDto
        {
            TmdbTvId = "5739",
            Name = "Dr. Horrible's Sing-Along Blog",
            FirstAirYear = 2008,
            Type = "Miniseries",
            Seasons = 1,
            Episodes = 3,
            PosterUrl = "https://image.tmdb.org/t/p/w500/dr.jpg",
        },
    });

    [Fact]
    public void AFilmThatMayBeATvShow_ShowsAChoicesStripWithMoveToTvAndKeepAsAFilm_AndNoSeparateSuggestionCard()
    {
        var row = ReviewRow(Id(1), "Dr Horribles", trigger: "MovieMatchedAsTv", group: NeedsReviewGroups.Choose, candidatesJson: TvCandidates(), guess: TvGuess);
        Register(() => Page([row]));

        var cut = RenderLoaded();

        var strip = cut.Find($"{RowSelector(row)} [data-testid='needs-review-choices']");
        Assert.Equal(
            ["Move to TV", "Keep"],
            strip.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("Dr. Horrible's Sing-Along Blog (TV, 2008)", strip.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 season · 3 episodes", strip.TextContent, StringComparison.Ordinal);
        Assert.Contains("Keep as a film", strip.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".movie-as-tv-card"));
        Assert.Empty(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-suggestion']"));
    }

    [Fact]
    public void MoveToTv_OnTheChoicesStrip_MovesTheFilm_AndTheRowBecomesAMovedLine()
    {
        var row = ReviewRow(Id(1), "Dr Horribles", trigger: "MovieMatchedAsTv", group: NeedsReviewGroups.Choose, candidatesJson: TvCandidates());
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("Dr Horribles moved to TV", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([row.Review!.Id], engine.Moved);
    }

    [Fact]
    public void KeepAsAFilm_OnTheChoicesStrip_KeepsTheFilm_AndItJoinsTheHiddenItems()
    {
        var row = ReviewRow(Id(1), "Dr Horribles", trigger: "MovieMatchedAsTv", group: NeedsReviewGroups.Choose, candidatesJson: TvCandidates());
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains("kept as it is", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([row.Review!.Id], engine.Dismissed);
        Assert.Contains("1 item is hidden because you kept it as it is.", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);
    }

    private static NeedsReviewRowDto RowWithTwoMatches()
    {
        var row = ReviewRow(Id(1), "Dune", trigger: "RetailMatchAmbiguous", group: NeedsReviewGroups.Choose);
        row.Choices.Add(new NeedsReviewChoiceDto { Id = "retail:tmdb:438631", Kind = NeedsReviewChoiceKinds.Retail, Title = "Dune (2021)", Subtitle = "Denis Villeneuve" });
        row.Choices.Add(new NeedsReviewChoiceDto { Id = "retail:tmdb:841", Kind = NeedsReviewChoiceKinds.Retail, Title = "Dune (1984)", Subtitle = "David Lynch" });
        return row;
    }

    [Fact]
    public void AnItemWithSeveralMatches_ShowsEachAsAChoiceWithItsOwnUseThisButton()
    {
        var row = RowWithTwoMatches();
        Register(() => Page([row]));

        var cut = RenderLoaded();

        var strip = cut.Find($"{RowSelector(row)} [data-testid='needs-review-choices']");
        Assert.Equal(["Use this", "Use this"], strip.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("Dune (2021)", strip.TextContent, StringComparison.Ordinal);
        Assert.Contains("David Lynch", strip.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-suggestion']"));
    }

    [Fact]
    public void UseThis_OnOneOfSeveralMatches_SendsThatChoice_AndTheRowBecomesAMatchedLine()
    {
        var row = RowWithTwoMatches();
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains("Dune matched to Dune (1984)", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([(row.Review!.Id, "retail:tmdb:841")], engine.Chosen);
    }

    [Fact]
    public void WhenTheEngineRefusesAChoice_TheStripStays_AndAReasonShowsUnderTheRow()
    {
        var row = RowWithTwoMatches();
        var engine = Register(() => Page([row]));
        engine.Failing.Add(nameof(IEngineApiClient.ChooseReviewAsync));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("Dune could not be matched.", cut.Find($"{RowSelector(row)} [data-review-failure]").TextContent, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button").Count);
    }

    [Fact]
    public void ABookListedInTwoSeries_PicksOne_AndTheRowSaysWhereItWent()
    {
        var row = ReviewRow(Id(1), "The Hobbit", trigger: "SeriesPlacementAmbiguous", group: NeedsReviewGroups.Choose, mediaType: "Books");
        row.Choices.Add(new NeedsReviewChoiceDto { Id = "series:aaaa", Kind = NeedsReviewChoiceKinds.Series, Title = "Middle-earth" });
        row.Choices.Add(new NeedsReviewChoiceDto { Id = "series:bbbb", Kind = NeedsReviewChoiceKinds.Series, Title = "The Lord of the Rings" });
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("The Hobbit added to Middle-earth", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([(row.Review!.Id, "series:aaaa")], engine.Chosen);
    }

    [Fact]
    public void UseThis_OnTheOneMatchTheEngineFound_SendsItsChoice_AndShowsWhereItCameFrom()
    {
        var guess = new NeedsReviewBestGuessDto { Action = "use_retail_match", Name = "Dune", Year = "2021", Source = "TMDB", ChoiceId = "retail:tmdb:438631" };
        var row = ReviewRow(Id(1), "dune.2021", trigger: "RetailMatchAmbiguous", group: NeedsReviewGroups.Ready, guess: guess);
        row.Choices.Add(new NeedsReviewChoiceDto { Id = "retail:tmdb:438631", Kind = NeedsReviewChoiceKinds.Retail, Title = "Dune (2021)" });
        var engine = Register(() => Page([row]));
        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices']"));
        var suggestion = cut.Find($"{RowSelector(row)} [data-testid='needs-review-suggestion']");
        Assert.Contains("TMDB", suggestion.TextContent, StringComparison.Ordinal);
        suggestion.QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("dune.2021 matched to Dune (2021)", cut.Find(DoneSelector(row)).TextContent, StringComparison.Ordinal));
        Assert.Equal([(row.Review!.Id, "retail:tmdb:438631")], engine.Chosen);
    }

    [Fact]
    public void WhenMoveToTvFails_TheStripStays_AndAReasonShowsUnderTheRow()
    {
        var row = ReviewRow(Id(1), "Dr Horribles", trigger: "MovieMatchedAsTv", group: NeedsReviewGroups.Choose, candidatesJson: TvCandidates());
        var engine = Register(() => Page([row]));
        engine.Failing.Add(nameof(IEngineApiClient.MoveReviewItemToTvAsync));
        var cut = RenderLoaded();

        cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices'] button")[0].Click();

        cut.WaitForAssertion(() => Assert.Contains("Dr Horribles could not be moved to TV.", cut.Find($"{RowSelector(row)} [data-review-failure]").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-choices']"));
    }

    // ---- The row menu ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRowMenu_OffersTryAgainKeepAsItIsShowInActivityAndDelete_InThatOrder_AndNeverTheOldWording()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        Register(() => Page([file]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        var labels = MenuLabels(cut, RowSelector(file));

        Assert.Equal(["Try again", "Keep as it is", "Show in Activity", "Delete"], labels);
    }

    [Fact]
    public async Task Delete_IsLeftOutOfTheMenu_ForAFileThatHasNoWorkYet()
    {
        var file = FileRow(Id(1), "Orphan.mkv", workId: null);
        Register(() => Page([file]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.DoesNotContain("Delete", MenuLabels(cut, RowSelector(file)));
    }

    [Fact]
    public async Task TryAgain_OnAFile_LooksItUpAgain_AndTheRowBecomesATryingAgainLine()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var engine = Register(() => Page([file]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        OpenMenuAndClick(cut, RowSelector(file), "Try again");

        cut.WaitForAssertion(() => Assert.Contains("Mystery.2019.mkv: trying again", cut.Find(DoneSelector(file)).TextContent, StringComparison.Ordinal));
        Assert.Equal([file.Unmatched!.AssetId], engine.RetriedFiles);
    }

    [Fact]
    public async Task TryAgain_WhenTheEngineDoesNotAnswer_SaysSoUnderTheRow()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var engine = Register(() => Page([file]));
        engine.Failing.Add(nameof(IEngineApiClient.RetryUnmatchedItemAsync));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        OpenMenuAndClick(cut, RowSelector(file), "Try again");

        cut.WaitForAssertion(() => Assert.Contains("Mystery.2019.mkv could not be tried again. Try again in a moment.", cut.Find($"{RowSelector(file)} [data-review-failure]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepAsItIs_OnAFile_KeepsIt_HidesIt_AndAddsItToTheHiddenCount()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var engine = Register(() => Page([file], keptTotal: 1));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Assert.Contains("1 item is hidden", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);

        OpenMenuAndClick(cut, RowSelector(file), "Keep as it is");

        cut.WaitForAssertion(() => Assert.Contains("Mystery.2019.mkv kept as it is", cut.Find(DoneSelector(file)).TextContent, StringComparison.Ordinal));
        Assert.Equal([file.Unmatched!.AssetId], engine.KeptFiles);
        Assert.Contains("2 items are hidden because you kept them as they are.", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepAsItIs_OnAnItemTheEngineWasUnsureAbout_RecordsTheChoiceInsteadOfDeletingAnything()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", trigger: "RetailMatchFailed");
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        OpenMenuAndClick(cut, RowSelector(row), "Keep as it is");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(row))));
        Assert.Equal([row.Review!.Id], engine.Dismissed);
    }

    [Fact]
    public async Task ShowInActivity_OpensTheActivityListSearchedForTheTitle()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", trigger: "RetailMatchFailed");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        OpenMenuAndClick(cut, RowSelector(row), "Show in Activity");

        cut.WaitForAssertion(() => Assert.EndsWith("/settings/general/library-activity?q=Cibola%20Burn", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    // ---- File problems --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileWhoseTagsCouldNotBeSaved_OffersTryAgainAsItsButton_AndHandsTheFileToTheRetagSweep()
    {
        var entityId = Id(7);
        var row = ReviewRow(Id(1), "Locked Song", trigger: "WritebackFailed", group: NeedsReviewGroups.FileProblem, entityId: entityId, mediaType: "Music");
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        var buttons = cut.FindAll($"{RowSelector(row)} .nr-row__actions button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Contains("Try again", buttons);
        Assert.DoesNotContain("Fix", buttons);

        cut.FindAll($"{RowSelector(row)} .nr-row__actions button").First(button => button.TextContent.Trim() == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(row))));
        Assert.Equal([entityId], engine.Retagged);
    }

    [Fact]
    public async Task AFileProblemWithNoRetryAction_KeepsFixAsItsButton_AndOffersNoTryAgain()
    {
        var row = ReviewRow(Id(1), "Broken Film", trigger: "MediaInspectionFailed", group: NeedsReviewGroups.FileProblem);
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        var buttons = cut.FindAll($"{RowSelector(row)} .nr-row__actions button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Contains("Fix", buttons);
        Assert.DoesNotContain("Try again", buttons);
        Assert.DoesNotContain("Try again", MenuLabels(cut, RowSelector(row)));
    }

    // ---- Families -------------------------------------------------------------------------------------------------------------------

    private static readonly Guid ShowWorkId = Guid.Parse("99999999-9999-4999-8999-999999999999");

    private static NeedsReviewRowDto[] Episodes(int count) => Enumerable.Range(1, count)
        .Select(n => FileRow(Id(100 + n), $"Severance.S01E0{n}.mkv", workId: Id(200 + n), familyKey: "severance", familyTitle: "Severance", familyWorkId: ShowWorkId, folder: "C:/media/tv/Severance/Season 1", library: "tv"))
        .ToArray();

    [Fact]
    public void TwoOrMoreFilesOfOneShow_ShareOneRow_CollapsedUntilTheArrowIsPressed()
    {
        Register(() => Page(Episodes(3)));
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");

        var row = cut.Find("[data-testid='needs-review-family-row']");
        Assert.Equal("Severance", row.QuerySelector("[data-testid='needs-review-title']")!.TextContent.Trim());
        Assert.Contains("3 episodes", row.QuerySelector("[data-testid='needs-review-meta']")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='needs-review-family-files']"));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-row']"));
        Assert.Equal("3", cut.Find("[data-testid='needs-review-group-count']").TextContent.Trim());

        cut.Find("[data-testid='needs-review-family-toggle']").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='needs-review-family-files'] [data-testid='needs-review-row']").Count));
        Assert.Contains("Severance.S01E01.mkv", cut.Find("[data-testid='needs-review-family-files']").TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("[data-testid='needs-review-family-toggle']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task FixOnAFamily_OpensTheShowItselfInTheEditorOnMatch()
    {
        var engine = Register(() => Page(Episodes(3)));
        await UseDesktopAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");

        cut.FindAll("[data-testid='needs-review-family-row'] .nr-row__actions button").Single(button => button.TextContent.Trim() == "Fix").Click();

        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        Assert.Equal(EditTool.Match, host.Session.ActiveTool);
        Assert.Contains(ShowWorkId, engine.OpenedInEditor);
    }

    [Fact]
    public async Task KeepAsTheyAre_OnAFamily_ActsOnEveryFileInIt_AndTheFamilyBecomesOneLine()
    {
        var engine = Register(() => Page(Episodes(3)));
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");

        OpenMenuAndClick(cut, "[data-testid='needs-review-family-row']", "Keep as they are");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-done']")));
        Assert.Contains("Severance kept as they are", cut.Find("[data-testid='needs-review-done']").TextContent, StringComparison.Ordinal);
        Assert.Equal(3, engine.KeptFiles.Count);
    }

    [Fact]
    public async Task TryAgain_OnAFamily_LooksUpEveryFileAgain()
    {
        var engine = Register(() => Page(Episodes(2)));
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");

        OpenMenuAndClick(cut, "[data-testid='needs-review-family-row']", "Try again");

        cut.WaitForAssertion(() => Assert.Equal(2, engine.RetriedFiles.Count));
        cut.WaitForAssertion(() => Assert.Contains("Severance: trying again", cut.Find("[data-testid='needs-review-done']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheEngineRefusesEveryFileOfAFamily_TheFamilyStaysAndSaysWhy_AndNothingFinishes()
    {
        var engine = Register(() => Page(Episodes(2)));
        engine.Failing.Add(nameof(IEngineApiClient.KeepUnmatchedItemAsync));
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");

        OpenMenuAndClick(cut, "[data-testid='needs-review-family-row']", "Keep as they are");

        cut.WaitForAssertion(() => Assert.Contains("could not be kept as it is", cut.Find("[data-testid='needs-review-family-row'] [data-review-failure]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-done']"));
        Assert.Equal(2, engine.KeptFiles.Count);
    }

    // ---- Fix ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fix_OnADesktop_OpensThePopOverOverTheListOnMatch_AndTheListReadsAgainAfterAMatch()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", trigger: "RetailMatchFailed");
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded();
        var readsBefore = engine.NormalListReads;

        cut.FindAll($"{RowSelector(row)} button").Single(button => button.TextContent.Trim() == "Fix").Click();

        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-panel[data-tool='match']")));
        Assert.True(host.IsOpen);
        Assert.Equal(EditTool.Match, host.Session.ActiveTool);
        Assert.Contains(row.Review!.EntityId, engine.OpenedInEditor);
        Assert.Equal(before, navigation.Uri);
        Assert.Single(cut.FindAll(RowSelector(row)));

        await cut.InvokeAsync(async () =>
        {
            await host.NotifyCommittedAsync();
            await host.RequestCloseAsync();
        });
        cut.WaitForAssertion(() => Assert.True(engine.NormalListReads > readsBefore));
    }

    [Fact]
    public async Task Fix_ThatChangedNothing_DoesNotReadTheListAgain()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", trigger: "RetailMatchFailed");
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded();
        cut.FindAll($"{RowSelector(row)} button").Single(button => button.TextContent.Trim() == "Fix").Click();
        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        var readsBefore = engine.NormalListReads;

        await cut.InvokeAsync(() => host.RequestCloseAsync());

        Assert.False(host.IsOpen);
        Assert.Equal(readsBefore, engine.NormalListReads);
    }

    [Fact]
    public async Task OnATv_NoRowCarriesFix_AndNothingOpens()
    {
        var row = ReviewRow(Id(1), "Cibola Burn", trigger: "RetailMatchFailed");
        Register(() => Page([row]));
        await UseTvAsync();
        var before = Services.GetRequiredService<NavigationManager>().Uri;
        var cut = RenderLoaded();

        Assert.DoesNotContain(cut.FindAll($"{RowSelector(row)} button"), button => button.TextContent.Trim() == "Fix");
        Assert.Equal(before, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.False(Services.GetRequiredService<EditorHost>().IsOpen);
    }

    [Fact]
    public async Task AFileWithNoWork_OffersNoFix()
    {
        var withWork = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var orphan = FileRow(Id(2), "Orphan.mkv", workId: null);
        Register(() => Page([withWork, orphan]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);

        Assert.Contains(cut.FindAll($"{RowSelector(withWork)} button"), button => button.TextContent.Trim() == "Fix");
        Assert.DoesNotContain(cut.FindAll($"{RowSelector(orphan)} button"), button => button.TextContent.Trim() == "Fix");
    }

    // ---- The kept items -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenNothingIsKept_NoHiddenLineIsDrawn()
    {
        Register(() => Page([ReviewRow(Id(1), "Cibola Burn")]));

        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("[data-testid='needs-review-kept-line']"));
    }

    [Fact]
    public void TheHiddenLine_SaysHowManyAreKept_AndShowThemListsThemWithPutBack()
    {
        var kept = ReviewRow(Id(5), "Kept Film", trigger: "RetailMatchFailed");
        var keptFile = FileRow(Id(6), "Kept.File.mkv");
        var engine = Register((_, wantKept) => wantKept ? Page([kept, keptFile], keptTotal: 2) : Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 2));
        var cut = RenderLoaded();
        Assert.Contains("2 items are hidden because you kept them as they are.", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='needs-review-kept']"));
        Assert.DoesNotContain(engine.ListReads, read => read.Kept);

        cut.Find("[data-testid='needs-review-kept-line'] button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='needs-review-kept'] button").Count(button => button.TextContent.Trim() == "Put back")));
        Assert.Contains(engine.ListReads, read => read.Kept);
        Assert.Equal("Hide them", cut.Find("[data-testid='needs-review-kept-line'] button").TextContent.Trim());

        cut.Find("[data-testid='needs-review-kept-line'] button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='needs-review-kept']")));
    }

    [Fact]
    public void PutBack_OnAKeptItem_ReopensIt_AndBothListsAreReadAgain()
    {
        var kept = ReviewRow(Id(5), "Kept Film", trigger: "RetailMatchFailed");
        var engine = Register((_, wantKept) => wantKept ? Page([kept], keptTotal: 1) : Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 1));
        var cut = RenderLoaded();
        cut.Find("[data-testid='needs-review-kept-line'] button").Click();
        cut.WaitForElement("[data-testid='needs-review-kept'] button");
        var readsBefore = engine.NormalListReads;

        cut.FindAll("[data-testid='needs-review-kept'] button").Single(button => button.TextContent.Trim() == "Put back").Click();

        cut.WaitForAssertion(() => Assert.True(engine.NormalListReads > readsBefore));
        Assert.Equal([kept.Review!.Id], engine.Reopened);
    }

    [Fact]
    public void PutBack_OnAKeptFile_UnkeepsIt()
    {
        var keptFile = FileRow(Id(6), "Kept.File.mkv");
        var engine = Register((_, wantKept) => wantKept ? Page([keptFile], keptTotal: 1) : Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 1));
        var cut = RenderLoaded();
        cut.Find("[data-testid='needs-review-kept-line'] button").Click();
        cut.WaitForElement("[data-testid='needs-review-kept'] button");

        cut.FindAll("[data-testid='needs-review-kept'] button").Single(button => button.TextContent.Trim() == "Put back").Click();

        cut.WaitForAssertion(() => Assert.Equal([keptFile.Unmatched!.AssetId], engine.UnkeptFiles));
    }

    [Fact]
    public void WhenPutBackFails_TheKeptItemStays_AndSaysWhy()
    {
        var kept = ReviewRow(Id(5), "Kept Film", trigger: "RetailMatchFailed");
        var engine = Register((_, wantKept) => wantKept ? Page([kept], keptTotal: 1) : Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 1));
        engine.Failing.Add(nameof(IEngineApiClient.ReopenReviewItemAsync));
        var cut = RenderLoaded();
        cut.Find("[data-testid='needs-review-kept-line'] button").Click();
        cut.WaitForElement("[data-testid='needs-review-kept'] button");

        cut.FindAll("[data-testid='needs-review-kept'] button").Single(button => button.TextContent.Trim() == "Put back").Click();

        cut.WaitForAssertion(() => Assert.Contains("Kept Film could not be put back.", cut.Find("[data-testid='needs-review-kept'] [data-review-failure]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenOnlyKeptItemsRemain_ThePageSaysNothingNeedsReview_AndStillOffersToShowThem()
    {
        Register(() => Page([], keptTotal: 3));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-empty']")));
        Assert.Contains("3 items are hidden", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);
    }

    // ---- Reloads, late answers and rows that are busy -------------------------------------------------------------------------------

    [Fact]
    public async Task AFinishedLine_StaysInPlaceWhenTheListIsReadAgain_ForExampleAfterTheEngineSaysItemsWereRemoved()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var engine = Register(() => Page([one, two]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        OpenMenuAndClick(cut, RowSelector(one), "Keep as it is");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(one))));
        var readsBefore = engine.NormalListReads;
        var three = FileRow(Id(3), "Three.mkv", workId: Id(93));
        engine.List = (_, _) => new NeedsReviewListAttempt(Page([two, three]), null);

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([], [])));

        cut.WaitForAssertion(() =>
        {
            Assert.True(engine.NormalListReads > readsBefore);
            Assert.Single(cut.FindAll(RowSelector(three)));
        });
        Assert.Contains("One.mkv kept as it is", cut.Find(DoneSelector(one)).TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(RowSelector(two)));
    }

    [Fact]
    public void ShowMore_AfterARowFinished_StartsAfterTheRowsStillWaiting_AndTheCountsStayRight()
    {
        var first = ReviewRow(Id(1), "First Book");
        var second = ReviewRow(Id(2), "Second Book");
        var third = ReviewRow(Id(3), "Third Book");
        var engine = Register(() => Page([first, second], total: 3));
        engine.List = (_, _) => engine.NormalListReads <= 1
            ? new NeedsReviewListAttempt(Page([first, second], total: 3), null)
            : new NeedsReviewListAttempt(Page([third], total: 2), null);
        var cut = RenderLoaded(2);
        OpenMenuAndClick(cut, RowSelector(first), "Keep as it is");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(first))));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Show more").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(RowSelector(third))));
        Assert.Contains(engine.ListReads, read => read.Skip == 1 && !read.Kept);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Show more");
        Assert.Contains("2 items", cut.Find("[data-testid='needs-review-summary']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderAnswerThatArrivesLate_NeverReplacesTheNewerList()
    {
        var older = new TaskCompletionSource<NeedsReviewListAttempt>();
        var calls = 0;
        var stale = ReviewRow(Id(1), "Stale Book");
        var current = ReviewRow(Id(2), "Current Book");
        Register(() => Page([]), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), args =>
        {
            if ((bool)args![3]!)
            {
                return Task.FromResult(new NeedsReviewListAttempt(Page([]), null));
            }

            return Interlocked.Increment(ref calls) == 1
                ? older.Task
                : Task.FromResult(new NeedsReviewListAttempt(Page([current]), null));
        }));
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(1, calls));

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([], [])));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(RowSelector(current))));
        older.SetResult(new NeedsReviewListAttempt(Page([stale]), null));
        await cut.InvokeAsync(() => Task.CompletedTask);
        await Task.Delay(100);

        Assert.Single(cut.FindAll(RowSelector(current)));
        Assert.Empty(cut.FindAll(RowSelector(stale)));
    }

    [Fact]
    public void WhenOnlyKeptItemsRemain_AndTheyCannotBeLoaded_ThePageSaysSo()
    {
        RegisterEngine(new Engine { List = (_, kept) => kept ? new NeedsReviewListAttempt(null, "No answer.") : new NeedsReviewListAttempt(Page([], keptTotal: 3), null) });
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-empty']");

        cut.Find("[data-testid='needs-review-kept-line'] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("The kept items could not be loaded.", cut.Find("[data-review-load-more-failure]").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-testid='needs-review-empty']"));
    }

    [Fact]
    public async Task KeepAsItIs_WhileTheKeptItemsAreShown_ReadsThemAgain_SoTheNewlyKeptFileIsListed()
    {
        var kept = FileRow(Id(5), "Kept.Earlier.mkv", workId: Id(95));
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var keptNow = new List<NeedsReviewRowDto> { kept };
        var engine = Register((_, wantKept) => wantKept ? Page(keptNow, keptTotal: keptNow.Count) : Page([file], keptTotal: keptNow.Count));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        cut.Find("[data-testid='needs-review-kept-line'] button").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-kept'] button"), button => button.TextContent.Trim() == "Put back"));
        var keptReadsBefore = engine.ListReads.Count(read => read.Kept);
        keptNow.Add(file);

        OpenMenuAndClick(cut, RowSelector(file), "Keep as it is");

        cut.WaitForAssertion(() =>
        {
            Assert.True(engine.ListReads.Count(read => read.Kept) > keptReadsBefore);
            Assert.Equal(2, cut.FindAll("[data-testid='needs-review-kept'] button").Count(button => button.TextContent.Trim() == "Put back"));
        });
    }

    // Holds the Engine's answer to Keep as it is for one file until the test lets it go, so the row stays busy.
    private Engine RegisterWithAKeepThatWaits(NeedsReviewListDto page, Guid holdAssetId, TaskCompletionSource<bool> release)
    {
        var engine = new Engine { List = (_, _) => new NeedsReviewListAttempt(page, null) };
        return RegisterEngine(engine, configure: stub => stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args => KeepAsync(engine, (Guid)args![0]!, holdAssetId, release)));
    }

    private static async Task<UnmatchedActionResultDto?> KeepAsync(Engine engine, Guid assetId, Guid holdAssetId, TaskCompletionSource<bool> release)
    {
        engine.KeptFiles.Add(assetId);
        if (assetId == holdAssetId)
        {
            await release.Task;
        }

        return new UnmatchedActionResultDto { Affected = 1 };
    }

    [Fact]
    public async Task WhileARowIsWorking_ItsMenuItemsAreDisabled_AndNothingIsSentAgain()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        var release = new TaskCompletionSource<bool>();
        var engine = RegisterWithAKeepThatWaits(Page([file]), file.Unmatched!.AssetId, release);
        await UseDesktopAsync();
        var cut = RenderLoaded();

        OpenMenuAndClick(cut, RowSelector(file), "Keep as it is");
        cut.WaitForAssertion(() => Assert.Single(engine.KeptFiles));
        MenuLabels(cut, RowSelector(file));

        cut.WaitForAssertion(() => Assert.All(cut.FindAll("button.app-menu-item"), item => Assert.True(item.HasAttribute("disabled"))));
        Assert.Single(engine.KeptFiles);
        release.SetResult(true);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(file))));
        Assert.Single(engine.KeptFiles);
    }

    [Fact]
    public async Task WhileOneFileOfAFamilyIsWorking_TheFamilyHeadersButtonsAreDisabled()
    {
        var files = Episodes(2);
        var release = new TaskCompletionSource<bool>();
        var engine = RegisterWithAKeepThatWaits(Page(files), files[0].Unmatched!.AssetId, release);
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");
        cut.Find("[data-testid='needs-review-family-toggle']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='needs-review-family-files'] [data-testid='needs-review-row']").Count));
        Assert.All(cut.FindAll("[data-testid='needs-review-family-row'] .nr-row__actions button.tl-button"), button => Assert.False(button.HasAttribute("disabled")));

        OpenMenuAndClick(cut, RowSelector(files[0]), "Keep as it is");

        cut.WaitForAssertion(() => Assert.Single(engine.KeptFiles));
        cut.WaitForAssertion(() =>
        {
            var header = cut.FindAll("[data-testid='needs-review-family-row'] .nr-row__actions button").Single(button => button.TextContent.Trim() == "Fix");
            Assert.True(header.HasAttribute("disabled"));
        });
        release.SetResult(true);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(files[0]))));
    }

    [Fact]
    public async Task KeepOnTheBar_SkipsATickedRowThatIsAlreadyWorking_SoNothingIsSentTwice()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var release = new TaskCompletionSource<bool>();
        var engine = RegisterWithAKeepThatWaits(Page([one, two]), one.Unmatched!.AssetId, release);
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, one, two);
        OpenMenuAndClick(cut, RowSelector(one), "Keep as it is");
        cut.WaitForAssertion(() => Assert.Equal([one.Unmatched!.AssetId], engine.KeptFiles));

        PressBar(cut, "needs-review-bar-keep");
        WaitForDialog<AppConfirmDialog>(cut).Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(two))));
        release.SetResult(true);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(one))));
        Assert.Equal([one.Unmatched!.AssetId, two.Unmatched!.AssetId], engine.KeptFiles);
    }

    // ---- The shell's count ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReviewCount_ForAServerAdministrator_IsFetchedFromTheEngine()
    {
        var engine = Register(() => Page([]));

        var count = await Services.GetRequiredService<UIOrchestratorService>().RefreshReviewCountAsync();

        Assert.Equal(3, count);
        Assert.Contains(nameof(IEngineApiClient.GetReviewCountAsync), engine.Calls);
    }

    [Fact]
    public async Task ReviewCount_ForAHouseholdAdministrator_MakesNoEngineRequestAndStaysZero()
    {
        var engine = Register(() => Page([]), authority: HouseholdAdministrator());

        var count = await Services.GetRequiredService<UIOrchestratorService>().RefreshReviewCountAsync();

        Assert.Equal(0, count);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), engine.Calls);
    }

    // ---- Tick boxes and the action bar (WP5) ----------------------------------------------------------------------------------------

    private const string BarSelector = "[data-testid='needs-review-bar']";

    private static string TickSelector(NeedsReviewRowDto row) => $"[data-key='{row.Key}'] input[data-testid='needs-review-tick']";

    private static string HeadingTickSelector(string group) => $"section[data-group='{group}'] .nr-section__head input[data-testid='needs-review-tick']";

    private static string[] BarLabels(IRenderedComponent<NeedsReviewPageContent> cut) =>
        cut.FindAll($"{BarSelector} button").Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim()).ToArray();

    private static string BarCount(IRenderedComponent<NeedsReviewPageContent> cut) =>
        cut.Find("[data-testid='needs-review-bar-count']").TextContent.Trim();

    private static string TickState(IRenderedComponent<NeedsReviewPageContent> cut, string selector) =>
        cut.Find(selector).GetAttribute("data-state")!;

    private static void Tick(IRenderedComponent<NeedsReviewPageContent> cut, params NeedsReviewRowDto[] rows)
    {
        foreach (var row in rows)
        {
            cut.Find(TickSelector(row)).Change(true);
        }
    }

    private static void PressBar(IRenderedComponent<NeedsReviewPageContent> cut, string testId) =>
        cut.Find($"{BarSelector} [data-testid='{testId}']").Click();

    private AppDialogEntry WaitForDialog<TComponent>(IRenderedComponent<NeedsReviewPageContent> cut)
    {
        var dialogs = Services.GetRequiredService<AppDialogService>();
        cut.WaitForAssertion(() => Assert.Contains(dialogs.Dialogs, entry => entry.ComponentType == typeof(TComponent)));
        return dialogs.Dialogs.First(entry => entry.ComponentType == typeof(TComponent));
    }

    private static NeedsReviewRowDto ReadyRow(int n, string title) =>
        ReviewRow(Id(n), title, group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.9);

    [Fact]
    public async Task WithNothingTicked_NoActionBarIsDrawn_AndEveryWaitingRowAndGroupHasATickBox()
    {
        var ready = ReadyRow(1, "Cibola Burn");
        var file = FileRow(Id(2), "Mystery.2019.mkv", workId: Id(9));
        Register(() => Page([ready, file]));
        await UseDesktopAsync();

        var cut = RenderLoaded(2);

        Assert.Empty(cut.FindAll(BarSelector));
        Assert.Equal("Select Cibola Burn", cut.Find(TickSelector(ready)).GetAttribute("aria-label"));
        Assert.Equal("Select Mystery.2019.mkv", cut.Find(TickSelector(file)).GetAttribute("aria-label"));
        Assert.Equal("Select all in Ready to confirm", cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).GetAttribute("aria-label"));
        Assert.Equal("Select all in No match found", cut.Find(HeadingTickSelector(NeedsReviewGroups.NoMatch)).GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TickingARow_ShowsTheBarAsALabelledRegion_WithTheCountFirst_AndUntickingItHidesTheBar()
    {
        var ready = ReadyRow(1, "Cibola Burn");
        var other = ReadyRow(2, "Other Film");
        Register(() => Page([ready, other]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);

        Tick(cut, ready);

        var bar = cut.Find(BarSelector);
        Assert.Equal("Selected items", bar.GetAttribute("aria-label"));
        Assert.Equal("1 selected", BarCount(cut));
        Tick(cut, other);
        Assert.Equal("2 selected", BarCount(cut));

        cut.Find(TickSelector(ready)).Change(false);
        cut.Find(TickSelector(other)).Change(false);

        Assert.Empty(cut.FindAll(BarSelector));
    }

    [Fact]
    public async Task TickingAGroupHeading_TicksEveryRowInThatGroupOnly_AndItIsPartlyTickedWhenOnlySomeAre()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        var noMatch = FileRow(Id(3), "Mystery.2019.mkv", workId: Id(9));
        Register(() => Page([first, second, noMatch]));
        await UseDesktopAsync();
        var cut = RenderLoaded(3);

        cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).Change(true);

        Assert.Equal("all", TickState(cut, TickSelector(first)));
        Assert.Equal("all", TickState(cut, TickSelector(second)));
        Assert.Equal("none", TickState(cut, TickSelector(noMatch)));
        Assert.Equal("all", TickState(cut, HeadingTickSelector(NeedsReviewGroups.Ready)));
        Assert.Equal("none", TickState(cut, HeadingTickSelector(NeedsReviewGroups.NoMatch)));
        Assert.Equal("2 selected", BarCount(cut));

        cut.Find(TickSelector(second)).Change(false);

        Assert.Equal("some", TickState(cut, HeadingTickSelector(NeedsReviewGroups.Ready)));
        Assert.Equal("mixed", cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).GetAttribute("aria-checked"));

        cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).Change(true);
        Assert.Equal("all", TickState(cut, TickSelector(second)));

        cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).Change(false);
        Assert.Empty(cut.FindAll(BarSelector));
    }

    [Fact]
    public async Task TickingAFamilyRow_TicksAllItsFiles_AndTickingOnlyOneFileMakesTheFamilyPartlyTicked()
    {
        var episodes = Episodes(3);
        Register(() => Page(episodes));
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");
        const string familyTick = "[data-testid='needs-review-family-row'] input[data-testid='needs-review-tick']";
        Assert.Equal("Select Severance", cut.Find(familyTick).GetAttribute("aria-label"));

        cut.Find(familyTick).Change(true);

        Assert.Equal("3 selected", BarCount(cut));
        cut.Find("[data-testid='needs-review-family-toggle']").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='needs-review-family-files'] input[data-testid='needs-review-tick']").Count));
        Assert.All(episodes, episode => Assert.Equal("all", TickState(cut, TickSelector(episode))));

        cut.Find(TickSelector(episodes[1])).Change(false);

        Assert.Equal("some", TickState(cut, familyTick));
        Assert.Equal("mixed", cut.Find(familyTick).GetAttribute("aria-checked"));
        Assert.Equal("2 selected", BarCount(cut));
    }

    [Fact]
    public async Task UseSuggestions_StatesTheNumberFirst_ThenAppliesOnlyTickedRowsThatHaveOneSuggestion_AndSkippedRowsStayTicked()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        var untouched = ReadyRow(3, "Never Ticked");
        var noSuggestion = ReviewRow(Id(4), "No Idea", group: NeedsReviewGroups.NoMatch);
        var engine = Register(() => Page([first, second, untouched, noSuggestion]));
        await UseDesktopAsync();
        var cut = RenderLoaded(4);
        Tick(cut, first, second, noSuggestion);

        PressBar(cut, "needs-review-bar-use");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Use suggestions", dialog.Parameters["Title"]);
        Assert.Equal("Library will apply its suggestion to 2 items.", dialog.Parameters["Message"]);
        Assert.Equal("Use suggestions", dialog.Parameters["PrimaryLabel"]);
        Assert.Equal("Cancel", dialog.Parameters["SecondaryLabel"]);
        Assert.Empty(engine.Resolved);

        dialog.Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(DoneSelector(first)));
            Assert.Single(cut.FindAll(DoneSelector(second)));
        });
        Assert.Equal([first.Review!.Id, second.Review!.Id], engine.Resolved);
        Assert.Single(cut.FindAll(RowSelector(untouched)));
        Assert.Single(cut.FindAll(RowSelector(noSuggestion)));
        Assert.Equal("none", TickState(cut, TickSelector(untouched)));
        cut.WaitForAssertion(() => Assert.Equal("1 selected", BarCount(cut)));
        Assert.Equal("all", TickState(cut, TickSelector(noSuggestion)));
    }

    [Fact]
    public async Task UseSuggestions_ForOneRow_SaysItemInTheSingular_AndCancelChangesNothing()
    {
        var row = ReadyRow(1, "Cibola Burn");
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);

        PressBar(cut, "needs-review-bar-use");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Library will apply its suggestion to 1 item.", dialog.Parameters["Message"]);
        dialog.Close(AppDialogResult.Ok(AppConfirmResult.Secondary));

        cut.WaitForAssertion(() => Assert.False(Services.GetRequiredService<AppDialogService>().Dialogs.Any()));
        Assert.Empty(engine.Resolved);
        Assert.Single(cut.FindAll(RowSelector(row)));
        Assert.Equal("1 selected", BarCount(cut));
    }

    [Fact]
    public async Task TryAgain_OnTheBar_LooksUpOnlyTheTickedFilesAgain_AndLeavesTheOthersAlone()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var three = FileRow(Id(3), "Three.mkv", workId: Id(93));
        var engine = Register(() => Page([one, two, three]));
        await UseDesktopAsync();
        var cut = RenderLoaded(3);
        Tick(cut, one, three);

        PressBar(cut, "needs-review-bar-retry");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(DoneSelector(one)));
            Assert.Single(cut.FindAll(DoneSelector(three)));
        });
        Assert.Equal([one.Unmatched!.AssetId, three.Unmatched!.AssetId], engine.RetriedFiles);
        Assert.Single(cut.FindAll(RowSelector(two)));
        Assert.Empty(cut.FindAll(DoneSelector(two)));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(BarSelector)));
    }

    [Fact]
    public async Task KeepAsTheyAre_OnTheBar_AsksOnce_ThenKeepsOnlyTheTickedRows_AndTheyJoinTheHiddenItems()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var three = FileRow(Id(3), "Three.mkv", workId: Id(93));
        var engine = Register(() => Page([one, two, three]));
        await UseDesktopAsync();
        var cut = RenderLoaded(3);
        Tick(cut, one, two);

        PressBar(cut, "needs-review-bar-keep");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Keep 2 items as they are?", dialog.Parameters["Title"]);
        Assert.Equal("They will be hidden from Needs Review. You can show them again from the bottom of the list.", dialog.Parameters["Message"]);
        Assert.Equal("Keep", dialog.Parameters["PrimaryLabel"]);
        Assert.Equal("Cancel", dialog.Parameters["SecondaryLabel"]);
        Assert.Empty(engine.KeptFiles);
        dialog.Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(DoneSelector(one)));
            Assert.Single(cut.FindAll(DoneSelector(two)));
        });
        Assert.Equal([one.Unmatched!.AssetId, two.Unmatched!.AssetId], engine.KeptFiles);
        Assert.Single(cut.FindAll(RowSelector(three)));
        Assert.Contains("2 items are hidden", cut.Find("[data-testid='needs-review-kept-line']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepAsTheyAre_OnTheBar_WhenCancelled_KeepsNothing()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var engine = Register(() => Page([one]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, one);

        PressBar(cut, "needs-review-bar-keep");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Keep 1 item as it is?", dialog.Parameters["Title"]);
        dialog.Close(AppDialogResult.Cancel());

        cut.WaitForAssertion(() => Assert.False(Services.GetRequiredService<AppDialogService>().Dialogs.Any()));
        Assert.Empty(engine.KeptFiles);
        Assert.Empty(cut.FindAll(DoneSelector(one)));
    }

    [Fact]
    public async Task AFamilyWhoseEveryFileIsTicked_IsKeptAsOneLineNamedForTheShow()
    {
        var episodes = Episodes(3);
        var engine = Register(() => Page(episodes));
        await UseDesktopAsync();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForElement("[data-testid='needs-review-family-row']");
        cut.Find("[data-testid='needs-review-family-row'] input[data-testid='needs-review-tick']").Change(true);

        PressBar(cut, "needs-review-bar-keep");
        WaitForDialog<AppConfirmDialog>(cut).Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-done']")));
        Assert.Contains("Severance kept as they are", cut.Find("[data-testid='needs-review-done']").TextContent, StringComparison.Ordinal);
        Assert.Equal(3, engine.KeptFiles.Count);
    }

    [Fact]
    public async Task AButtonThatAppliesToNoTickedRow_IsLeftOutOfTheBar_NeverGreyed()
    {
        var plain = ReviewRow(Id(1), "No Idea", group: NeedsReviewGroups.NoMatch);
        Register(() => Page([plain]));
        await UsePhoneAsync();
        var cut = RenderLoaded();
        cut.Find("[data-key='" + plain.Key + "'] .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Select").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(BarSelector)));

        // A phone may edit; the item has no suggestion and nothing an Engine action can try again, and nothing here can be deleted.
        Assert.Equal(["Keep as they are", "Edit 1 item", "Clear selection"], BarLabels(cut));
        Assert.DoesNotContain(cut.FindAll($"{BarSelector} button"), button => button.HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnADesktop_TheBarOffersEditAndDelete_ForTickedFilesThatHaveAWork_InTheMockOrder()
    {
        var file = FileRow(Id(1), "One.mkv", workId: Id(91));
        var ready = ReadyRow(2, "Cibola Burn");
        Register(() => Page([ready, file]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);

        Tick(cut, file);
        Assert.Equal(["Try again", "Keep as they are", "Edit 1 item", "Delete", "Clear selection"], BarLabels(cut));

        Tick(cut, ready);
        Assert.Equal(["Use suggestions", "Try again", "Keep as they are", "Edit 2 items", "Delete", "Clear selection"], BarLabels(cut));
    }

    [Fact]
    public async Task EditNItems_OpensTheSharedEditorWindowForTheTickedRowsOnly_OnTheEditPanel()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var three = FileRow(Id(3), "Three.mkv", workId: Id(93));
        Register(() => Page([one, two, three]));
        await UseDesktopAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded(3);
        Tick(cut, one, three);

        PressBar(cut, "needs-review-bar-edit");

        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        cut.WaitForAssertion(() => Assert.Equal(BulkPanelKind.Edit, host.Batch.Panel));
        Assert.Equal([Id(91), Id(93)], host.Batch.Ids.Order());
        Assert.DoesNotContain(Id(92), host.Batch.Ids);
    }

    [Fact]
    public async Task Delete_OnTheBar_AsksOnceForTheTickedDeletableRowsOnly_AndThoseRowsBecomeDeletedLines()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var three = FileRow(Id(3), "Three.mkv", workId: Id(93));
        var orphan = FileRow(Id(4), "Orphan.mkv", workId: null);
        Register(() => Page([one, two, three, orphan]));
        await UseDesktopAsync();
        var cut = RenderLoaded(4);
        Tick(cut, one, orphan, two);

        PressBar(cut, "needs-review-bar-delete");

        var dialog = WaitForDialog<DeleteFromLibraryDialog>(cut);
        Assert.Equal([Id(91), Id(92)], ((IReadOnlyList<Guid>)dialog.Parameters["WorkIds"]).Order());
        dialog.Close(AppDialogResult.Ok(new LibraryItemDeletionDialogResult(null, true, "item")));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(DoneSelector(one)));
            Assert.Single(cut.FindAll(DoneSelector(two)));
        });
        Assert.Contains("One.mkv deleted", cut.Find(DoneSelector(one)).TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(RowSelector(three)));
        Assert.Single(cut.FindAll(RowSelector(orphan)));
        Assert.Equal("1 selected", BarCount(cut));
    }

    [Fact]
    public async Task Escape_ClearsTheSelection_AndTheBarGoesWithIt()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        Assert.Single(cut.FindAll(BarSelector));

        cut.Find("[data-testid='needs-review']").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll(BarSelector));
        Assert.Equal("none", TickState(cut, TickSelector(row)));
    }

    [Fact]
    public async Task TheBarsCloseButton_ClearsTheSelection()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        Register(() => Page([first, second]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        cut.Find(HeadingTickSelector(NeedsReviewGroups.Ready)).Change(true);
        Assert.Equal("2 selected", BarCount(cut));

        PressBar(cut, "needs-review-bar-close");

        Assert.Empty(cut.FindAll(BarSelector));
        Assert.Equal("none", TickState(cut, HeadingTickSelector(NeedsReviewGroups.Ready)));
    }

    [Fact]
    public async Task ARowThatFinishesLosesItsTickBox_AndLeavesTheSelection()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        Register(() => Page([first, second]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, first, second);

        cut.Find($"{RowSelector(first)} [data-testid='needs-review-suggestion'] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(DoneSelector(first))));
        Assert.Empty(cut.FindAll(TickSelector(first)));
        Assert.Equal("1 selected", BarCount(cut));
    }

    [Fact]
    public async Task ASearch_UnticksRowsItHides_SoTheBarNeverActsOnWhatCannotBeSeen()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Leviathan Wakes");
        Register(() => Page([first, second]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, first, second);
        Assert.Equal("2 selected", BarCount(cut));

        cut.Find(".needs-review-page__search input").Input("Cibola");

        cut.WaitForAssertion(() => Assert.Equal("1 selected", BarCount(cut)));
    }

    [Fact]
    public async Task TheKeptItems_HaveNoTickBoxes()
    {
        var kept = ReviewRow(Id(5), "Kept Film", trigger: "RetailMatchFailed");
        Register((_, wantKept) => wantKept ? Page([kept], keptTotal: 1) : Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 1));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        cut.Find("[data-testid='needs-review-kept-line'] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='needs-review-kept']")));
        Assert.Empty(cut.FindAll("[data-testid='needs-review-kept'] input[data-testid='needs-review-tick']"));
    }

    [Fact]
    public async Task ShowThem_IsDrawnInTheAccentLinkColour()
    {
        Register(() => Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 2));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.Contains("app-tone--primary", cut.Find("[data-testid='needs-review-kept-line'] button").ClassList);
    }

    // ---- Phone: ticking starts from Select ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhone_NoTickBoxShowsUntilSelectIsChosenFromARowsMenu_AndTheyGoWhenNothingIsTicked()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        Register(() => Page([first, second]));
        await UsePhoneAsync();
        var cut = RenderLoaded(2);

        Assert.Empty(cut.FindAll("input[data-testid='needs-review-tick']"));
        Assert.Empty(cut.FindAll(BarSelector));

        cut.Find($"{RowSelector(first)} .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Select").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(BarSelector)));
        Assert.Equal("1 selected", BarCount(cut));
        Assert.Equal("all", TickState(cut, TickSelector(first)));
        Assert.Equal("none", TickState(cut, TickSelector(second)));
        Assert.NotEmpty(cut.FindAll(HeadingTickSelector(NeedsReviewGroups.Ready)));

        cut.Find(TickSelector(first)).Change(false);

        Assert.Empty(cut.FindAll("input[data-testid='needs-review-tick']"));
        Assert.Empty(cut.FindAll(BarSelector));
    }

    [Fact]
    public async Task OnAPhone_TheRowSheetListsFixTryAgainKeepShowInActivityAndSelect_InThatOrder_AndOtherSizesHaveNoSelectItem()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        Register(() => Page([file]));
        await UsePhoneAsync();
        var cut = RenderLoaded();

        cut.Find($"{RowSelector(file)} .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));

        Assert.Equal(["Fix", "Try again", "Keep as it is", "Show in Activity", "Select"], cut.FindAll("button.app-menu-item").Select(button => button.TextContent.Trim()).Take(5).ToArray());
    }

    [Fact]
    public async Task OnADesktop_TheRowMenuHasNoSelectItem()
    {
        var file = FileRow(Id(1), "Mystery.2019.mkv", workId: Id(9));
        Register(() => Page([file]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.DoesNotContain("Select", MenuLabels(cut, RowSelector(file)));
    }

    [Fact]
    public async Task OnAPhone_ACardWhoseOnlyButtonIsTryAgain_PutsItOnItsOwnFullWidthLineUnderTheText()
    {
        var file = FileRow(Id(1), "Unreadable.mkv", workId: Id(9), group: NeedsReviewGroups.FileProblem);
        Register(() => Page([file]));
        await UsePhoneAsync();
        var cut = RenderLoaded();

        var own = cut.FindAll($"{RowSelector(file)} .nr-row__fixfull button");
        Assert.Equal("Try again", Assert.Single(own).TextContent.Trim());
        Assert.Contains("tl-full-width", own[0].ClassList);
        Assert.DoesNotContain(cut.FindAll($"{RowSelector(file)} .nr-row__actions button"), button => button.TextContent.Trim() == "Try again");
    }

    [Fact]
    public async Task OnADesktop_TryAgainStaysBesideTheItem_NotOnALineOfItsOwn()
    {
        var file = FileRow(Id(1), "Unreadable.mkv", workId: Id(9), group: NeedsReviewGroups.FileProblem);
        Register(() => Page([file]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll($"{RowSelector(file)} .nr-row__fixfull"));
        Assert.Contains(cut.FindAll($"{RowSelector(file)} .nr-row__actions button"), button => button.TextContent.Trim() == "Try again");
    }

    // ---- Review follow-ups: unknown actions, Escape in menus, focus, counts and late answers ---------------------------------------

    private static readonly NeedsReviewBestGuessDto UnknownGuess = new() { Action = "do_something_new", Name = "Odd Film", Year = "2020", Source = "Provider match" };

    private static readonly Microsoft.AspNetCore.Components.Web.KeyboardEventArgs EscapeKey = new() { Key = "Escape" };

    private int FocusCalls() => JSInterop.Invocations.Count(invocation => invocation.Identifier == "Blazor._internal.domWrapper.focus");

    [Fact]
    public async Task ABestGuessWithAnActionThePageDoesNotKnow_ShowsNoSuggestion_KeepsFix_AndIsLeftOutOfUseSuggestions()
    {
        var row = ReviewRow(Id(1), "Odd Film", group: NeedsReviewGroups.Ready, guess: UnknownGuess);
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-suggestion']"));
        Assert.Contains(cut.FindAll($"{RowSelector(row)} .nr-row__actions button"), button => button.TextContent.Trim() == "Fix");
        Tick(cut, row);
        Assert.DoesNotContain("Use suggestions", BarLabels(cut));
        Assert.Empty(engine.Skipped);
        Assert.Empty(engine.Resolved);
        Assert.DoesNotContain(nameof(IEngineApiClient.SkipUniverseAsync), engine.Calls);
    }

    [Fact]
    public async Task ARetailGuessWithNoChoiceToApply_ShowsNoSuggestion()
    {
        var guess = new NeedsReviewBestGuessDto { Action = "use_retail_match", Name = "Dune", Year = "2021", Source = "TMDB", ChoiceId = null };
        var row = ReviewRow(Id(1), "Dune", group: NeedsReviewGroups.Ready, guess: guess);
        var engine = Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll($"{RowSelector(row)} [data-testid='needs-review-suggestion']"));
        Assert.Empty(engine.Chosen);
    }

    [Fact]
    public async Task Escape_PressedInARowsMenu_ClosesTheMenuOnly_AndTheTicksStay()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        cut.Find($"{RowSelector(row)} .app-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));

        cut.Find($"{RowSelector(row)} .nr-row__menu .app-overflow-menu").KeyDown(EscapeKey);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button.app-menu-item")));
        Assert.Single(cut.FindAll(BarSelector));
        Assert.Equal("all", TickState(cut, TickSelector(row)));

        // With no menu in the way, the same key on the page clears the ticks.
        cut.Find("[data-testid='needs-review']").KeyDown(EscapeKey);
        Assert.Empty(cut.FindAll(BarSelector));
    }

    [Fact]
    public async Task OnAPhone_EscapePressedInARowsSheet_DoesNotClearTheTicks()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UsePhoneAsync();
        var cut = RenderLoaded();
        cut.Find($"{RowSelector(row)} .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Select").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(BarSelector)));
        cut.Find($"{RowSelector(row)} .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"{RowSelector(row)} .nr-row__menu dialog")));

        // The key stops at the row's menu wrapper, so the page above never sees it (nothing is left to handle it, which bUnit reports).
        Assert.Throws<Bunit.MissingEventHandlerException>(() => cut.Find($"{RowSelector(row)} .nr-row__menu dialog").KeyDown(EscapeKey));

        Assert.Single(cut.FindAll(BarSelector));
        Assert.Equal("all", TickState(cut, TickSelector(row)));
    }

    [Fact]
    public async Task WhenTheBarsCloseButtonRemovesTheBar_FocusMovesToTheList()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        var before = FocusCalls();

        PressBar(cut, "needs-review-bar-close");

        cut.WaitForAssertion(() => Assert.True(FocusCalls() > before));
        Assert.Equal("-1", cut.Find("[data-testid='needs-review']").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task WhenEscapeRemovesTheBar_FocusMovesToTheList()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        var before = FocusCalls();

        cut.Find("[data-testid='needs-review']").KeyDown(EscapeKey);

        cut.WaitForAssertion(() => Assert.True(FocusCalls() > before));
    }

    [Fact]
    public async Task WhenABarActionFinishesEveryTickedRow_FocusMovesToTheList()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        Register(() => Page([first, second]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, first, second);
        PressBar(cut, "needs-review-bar-use");
        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        var before = FocusCalls();

        dialog.Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(BarSelector));
            Assert.True(FocusCalls() > before);
        });
    }

    [Fact]
    public async Task UnticksTheLastRowByHand_DoesNotMoveFocusAwayFromTheTickBox()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        var before = FocusCalls();

        cut.Find(TickSelector(row)).Change(false);

        Assert.Empty(cut.FindAll(BarSelector));
        Assert.Equal(before, FocusCalls());
    }

    [Fact]
    public async Task ChangingTheLibraryFilter_ClearsTheSelection_EvenForARowThatIsInBothLists()
    {
        var row = ReadyRow(1, "Cibola Burn");
        Register(() => Page([row]));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        Tick(cut, row);
        Assert.Single(cut.FindAll(BarSelector));

        cut.Render(p => p.Add(c => c.Library, "tv"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(BarSelector)));
        Assert.Equal("none", TickState(cut, TickSelector(row)));
    }

    [Fact]
    public async Task EditNItems_CountsWhatTheEditorWillOpen_SoTwoRowsOfOneWorkReadEdit1Item()
    {
        var work = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var first = ReviewRow(Id(1), "Cibola Burn", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.9, entityId: work);
        var second = ReviewRow(Id(2), "Cibola Burn (second file)", group: NeedsReviewGroups.Ready, guess: ConfirmGuess, confidence: 0.9, entityId: work);
        Register(() => Page([first, second]));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);

        Tick(cut, first, second);

        Assert.Equal("2 selected", BarCount(cut));
        Assert.Contains("Edit 1 item", BarLabels(cut));
    }

    [Fact]
    public async Task EditNItems_OnAPhone_OpensTheEditorThroughTheSameHandler()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        Register(() => Page([one, two]));
        await UsePhoneAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded(2);
        // On a phone the tick boxes appear once Select in a row's sheet ticks that row.
        cut.Find($"{RowSelector(one)} .app-row-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Select").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(BarSelector)));
        Tick(cut, two);
        Assert.Contains("Edit 2 items", BarLabels(cut));

        PressBar(cut, "needs-review-bar-edit");

        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        Assert.Equal(2, host.Batch.Ids.Count);
    }

    [Fact]
    public async Task EditNItems_IsRefusedByTheHandler_OnADeviceThatMayNotEdit_NotOnlyHiddenFromTheBar()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        Register(() => Page([one, two]));
        await UseDesktopAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded(2);
        Tick(cut, one, two);
        Assert.Contains("Edit 2 items", BarLabels(cut));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(new DeviceContextSnapshot("television", "tv", false));
        var handler = typeof(NeedsReviewPageContent).GetMethod("BarEditAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)handler.Invoke(cut.Instance, null)!);

        var host = Services.GetRequiredService<EditorHost>();
        Assert.False(host.IsOpen);
        Assert.Empty(host.Batch.Ids);
        Assert.Equal(BulkPanelKind.None, host.Batch.Panel);
    }

    [Fact]
    public async Task AnAdministratorWhoseSettingsAreLocked_IsStillOfferedFixAndEditNItems()
    {
        var file = FileRow(Id(1), "One.mkv", workId: Id(91));
        var other = FileRow(Id(2), "Two.mkv", workId: Id(92));
        Register(() => Page([file, other]), ServerAdministratorWhoMayNotEditDetails());
        await UseDesktopAsync();
        var cut = RenderLoaded(2);

        // Tapping either asks for the PIN first (EditorDeviceRule.EnsureMayEditItemsAsync); nothing opens without it.
        Assert.Contains(cut.FindAll($"{RowSelector(file)} button"), button => button.TextContent.Trim() == "Fix");
        Tick(cut, file, other);

        Assert.Equal(["Try again", "Keep as they are", "Edit 2 items", "Delete", "Clear selection"], BarLabels(cut));
    }

    [Fact]
    public async Task EditNItems_OpensNothing_ForALockedAdministratorWhenNoPinPromptIsAvailable()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        Register(() => Page([one, two]), ServerAdministratorWhoMayNotEditDetails());
        await UseDesktopAsync();
        var popover = Render<MediaEngine.Web.Components.Editor.EditorPopover>();
        var cut = RenderLoaded(2);
        Tick(cut, one, two);
        var handler = typeof(NeedsReviewPageContent).GetMethod("BarEditAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await cut.InvokeAsync(() => (Task)handler.Invoke(cut.Instance, null)!);

        var host = Services.GetRequiredService<EditorHost>();
        Assert.False(host.IsOpen);
        Assert.Empty(host.Batch.Ids);
        Assert.Equal(BulkPanelKind.None, host.Batch.Panel);
    }

    [Fact]
    public async Task UseSuggestions_CountsOnlyTheRowsThatWillReallyBeActedOn_NotARowAlreadyWorking()
    {
        var first = ReadyRow(1, "Cibola Burn");
        var second = ReadyRow(2, "Other Film");
        var gate = new TaskCompletionSource<bool>();
        Register(() => Page([first, second]), configure: stub => stub.SetHandler(nameof(IEngineApiClient.ResolveReviewItemAsync), args =>
            (Guid)args![0]! == first.Review!.Id ? gate.Task : Task.FromResult(true)));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, first, second);
        cut.Find($"{RowSelector(first)} [data-testid='needs-review-suggestion'] button").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Find($"{RowSelector(first)} [data-testid='needs-review-suggestion'] button").HasAttribute("disabled")));

        PressBar(cut, "needs-review-bar-use");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Library will apply its suggestion to 1 item.", dialog.Parameters["Message"]);
        Assert.Equal("2 selected", BarCount(cut));
        dialog.Close(AppDialogResult.Cancel());
        gate.SetResult(true);
    }

    [Fact]
    public async Task KeepAsTheyAre_CountsOnlyTheRowsThatWillReallyBeActedOn_NotARowAlreadyWorking()
    {
        var one = FileRow(Id(1), "One.mkv", workId: Id(91));
        var two = FileRow(Id(2), "Two.mkv", workId: Id(92));
        var gate = new TaskCompletionSource<bool>();
        Register(() => Page([one, two]), configure: stub => stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args =>
            (Guid)args![0]! == one.Unmatched!.AssetId
                ? gate.Task.ContinueWith(_ => (UnmatchedActionResultDto?)new UnmatchedActionResultDto { Affected = 1 })
                : Task.FromResult<UnmatchedActionResultDto?>(new UnmatchedActionResultDto { Affected = 1 })));
        await UseDesktopAsync();
        var cut = RenderLoaded(2);
        Tick(cut, one, two);
        OpenMenuAndClick(cut, RowSelector(one), "Keep as it is");
        cut.WaitForAssertion(() => Assert.True(cut.Find($"{RowSelector(one)} .nr-row__actions button").HasAttribute("disabled")));

        PressBar(cut, "needs-review-bar-keep");

        var dialog = WaitForDialog<AppConfirmDialog>(cut);
        Assert.Equal("Keep 1 item as it is?", dialog.Parameters["Title"]);
        dialog.Close(AppDialogResult.Cancel());
        gate.SetResult(true);
    }

    [Fact]
    public async Task AUseSuggestionsRun_ReadsTheReviewCountOnce_AtTheEnd_NotAfterEveryRow()
    {
        var rows = new[] { ReadyRow(1, "One"), ReadyRow(2, "Two"), ReadyRow(3, "Three") };
        var engine = Register(() => Page(rows));
        await UseDesktopAsync();
        var cut = RenderLoaded(3);
        Tick(cut, rows);
        var before = engine.Calls.Count(call => call == nameof(IEngineApiClient.GetReviewCountAsync));
        PressBar(cut, "needs-review-bar-use");

        WaitForDialog<AppConfirmDialog>(cut).Close(AppDialogResult.Ok(AppConfirmResult.Primary));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='needs-review-done']").Count));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(BarSelector)));
        Assert.Equal(3, engine.Resolved.Count);
        Assert.Equal(before + 1, engine.Calls.Count(call => call == nameof(IEngineApiClient.GetReviewCountAsync)));
    }

    [Fact]
    public async Task AnOlderKeptListAnswerThatArrivesLate_NeverReplacesTheNewerKeptList()
    {
        var older = new TaskCompletionSource<NeedsReviewListAttempt>();
        var keptReads = 0;
        var staleKept = ReviewRow(Id(5), "Stale Kept", trigger: "RetailMatchFailed");
        var currentKept = ReviewRow(Id(6), "Current Kept", trigger: "RetailMatchFailed");
        Register(() => Page([]), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), args =>
        {
            if ((bool)args![3]!)
            {
                return Interlocked.Increment(ref keptReads) == 1
                    ? older.Task
                    : Task.FromResult(new NeedsReviewListAttempt(Page([currentKept], keptTotal: 1), null));
            }

            return Task.FromResult(new NeedsReviewListAttempt(Page([ReviewRow(Id(1), "Cibola Burn")], keptTotal: 1), null));
        }));
        await UseDesktopAsync();
        var cut = RenderLoaded();
        cut.Find("[data-testid='needs-review-kept-line'] button").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, keptReads));

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([], [])));
        cut.WaitForAssertion(() => Assert.Contains("Current Kept", cut.Find("[data-testid='needs-review-kept']").TextContent, StringComparison.Ordinal));
        older.SetResult(new NeedsReviewListAttempt(Page([staleKept], keptTotal: 1), null));
        await cut.InvokeAsync(() => Task.CompletedTask);
        await Task.Delay(100);

        Assert.Contains("Current Kept", cut.Find("[data-testid='needs-review-kept']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale Kept", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
