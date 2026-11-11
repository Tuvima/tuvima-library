using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings → Needs Review: the page reports a failed load instead of an empty queue, says so when Dismiss or
/// Keep provider match is refused, shows the From merge / Import folder badges, and resolves an item. Only server
/// administrators get the list at all. Recently Added no longer holds any review markup or request.
/// </summary>
public sealed class NeedsReviewPageTests : AsyncBunitContext
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private sealed record Registered(List<string?> HistoryStatuses, List<Guid> Dismissed, List<Guid> Kept)
    {
        /// <summary>Every Engine client method the Dashboard called, in order.</summary>
        public List<string> EngineCalls { get; set; } = [];

        /// <summary>How many times the Needs Review list was read.</summary>
        public int ReviewReads { get; set; }
    }

    private static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static AccountProfileGrantDto[] Grants(bool adminEnabled) =>
    [
        new(AccountId, ProfileId, "Test User", null, true, true, adminEnabled,
            new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow),
    ];

    /// <summary>What the Engine tells the Dashboard about a server administrator.</summary>
    private static DashboardAuthorityResponse ServerAdministrator() => new(
        AccountId, ProfileId, true, true, 1, 1, true, true, null, 1, Grants(adminEnabled: true),
        ["settings.administration"], ["access.manage"]);

    /// <summary>What the Engine tells the Dashboard about a household administrator who is not a server administrator.</summary>
    private static DashboardAuthorityResponse HouseholdAdministrator() => new(
        AccountId, ProfileId, true, true, 1, 1, false, false, null, 1, Grants(adminEnabled: false),
        [], ["household.manage"]);

    private Registered Register(
        Func<ReviewListAttempt> reviews,
        Func<Task<bool>>? dismiss = null,
        Func<Task<bool>>? skipUniverse = null,
        DashboardAuthorityResponse? authority = null,
        Func<string, ReviewListAttempt>? libraryReviews = null,
        Action<EngineApiClientStub>? configure = null)
    {
        var result = new Registered([], [], []);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", AccountId, ProfileId, Guid.NewGuid(), authority ?? ServerAdministrator());
        Services.AddSingleton(session);
        var api = EngineApiClientStub.Create(stub =>
        {
            result.EngineCalls = stub.Calls;
            stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsAttemptAsync), _ =>
            {
                result.ReviewReads++;
                return Task.FromResult(reviews());
            });
            // The Engine's own per-item answer for Fix: this item may be edited, and its fields can be read.
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => Task.FromResult<MediaEngine.Contracts.Details.DetailPageViewModel?>(new()
            {
                Id = MediaEngine.Web.Tests.Support.EditingModeTestData.MovieId.ToString("D"),
                EntityType = MediaEngine.Contracts.Details.DetailEntityType.Work,
                Title = "Cibola Burn",
                OverflowActions = [new() { Key = "edit", Label = "Edit this title", Icon = "edit" }],
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ =>
                Task.FromResult<MediaEngine.Contracts.Metadata.MediaEditorContextDto?>(MediaEngine.Web.Tests.Support.EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            if (libraryReviews is not null)
            {
                stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsForLibraryAttemptAsync), args => Task.FromResult(libraryReviews((string)args![0]!)));
            }
            stub.SetHandler(nameof(IEngineApiClient.DismissReviewItemAsync), args =>
            {
                result.Dismissed.Add((Guid)args![0]!);
                return (dismiss ?? (() => Task.FromResult(true)))();
            });
            stub.SetHandler(nameof(IEngineApiClient.SkipUniverseAsync), args =>
            {
                result.Kept.Add((Guid)args![0]!);
                return (skipUniverse ?? (() => Task.FromResult(true)))();
            });
            configure?.Invoke(stub);
            stub.SetHandler(nameof(IEngineApiClient.GetRecentIngestionAdditionsAsync), args =>
            {
                result.HistoryStatuses.Add((string?)args![7]);
                return Task.FromResult<PagedResponse<IngestionMediaGroupDto>?>(
                    new PagedResponse<IngestionMediaGroupDto>([], Offset: 0, Limit: 25, HasMore: false, TotalCount: 0));
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(
                provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: session),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: session));
        // The merged Activity list reads the live Libraries state and the context sidebar, as it does in the app.
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton(provider => new MediaEngine.Web.Services.Integration.IngestionLiveDashboardState(
            api,
            provider.GetRequiredService<UIOrchestratorService>(),
            provider.GetRequiredService<UniverseStateContainer>()));
        Services.AddSingleton(provider => new MediaEngine.Web.Services.Playback.ContextSidebarPreferences(
            provider.GetRequiredService<UIOrchestratorService>(),
            new MediaEngine.Web.Tests.Support.LiveLibraryTestContext.NoPlaybackPreferences()));
        Services.AddSingleton<MediaEngine.Web.Services.Playback.ContextSidebarCoordinator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        return result;
    }

    private static ReviewItemViewModel Item(Guid id, string title, string trigger = "LowConfidence") => new()
    {
        Id = id,
        EntityId = Guid.NewGuid(),
        EntityType = "MediaAsset",
        EntityTitle = title,
        Trigger = trigger,
        Status = "Pending",
        MediaType = "Books",
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
    };

    private static ReviewListAttempt Loaded(params ReviewItemViewModel[] items) => new([.. items], null);

    private IReadOnlyList<AppToast> Toasts => Services.GetRequiredService<AppToastService>().Toasts;

    private static void OpenMenuAndClick(IRenderedComponent<NeedsReviewPageContent> cut, string label)
    {
        cut.Find("tr.needs-review-table__row .app-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.app-menu-item"), button => button.TextContent.Contains(label, StringComparison.Ordinal)));
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    [Fact]
    public async Task Fix_OnADesktop_OpensThePopOverOverTheListOnMatch_AndTheListReadsAgainAfterAMatch()
    {
        var item = Item(FirstId, "Cibola Burn");
        var registered = Register(() => Loaded(item));
        await Services.GetRequiredService<DeviceContextService>()
            .InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = navigation.Uri;
        var popover = Render<MediaEngine.Web.Components.Details.EditorPopover>();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        var readsBefore = registered.ReviewReads;

        cut.FindAll("tr.needs-review-table__row button").Single(button => button.TextContent.Trim() == "Fix").Click();

        // The pop-over is open over the list, on Match, and the person is still on the list.
        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-panel[data-tool='match']")));
        Assert.True(host.IsOpen);
        Assert.Equal(EditTool.Match, host.Session.ActiveTool);
        Assert.Equal(before, navigation.Uri);
        Assert.Single(cut.FindAll("tr.needs-review-table__row"));

        // A match went through and the pop-over closed: the queue is read again, so the row shows its new state.
        await cut.InvokeAsync(async () =>
        {
            await host.NotifyCommittedAsync();
            await host.RequestCloseAsync();
        });
        cut.WaitForAssertion(() => Assert.True(registered.ReviewReads > readsBefore));
    }

    [Fact]
    public async Task Fix_ThatChangedNothing_DoesNotReadTheQueueAgain()
    {
        var item = Item(FirstId, "Cibola Burn");
        var registered = Register(() => Loaded(item));
        await Services.GetRequiredService<DeviceContextService>()
            .InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var popover = Render<MediaEngine.Web.Components.Details.EditorPopover>();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        cut.FindAll("tr.needs-review-table__row button").Single(button => button.TextContent.Trim() == "Fix").Click();
        var host = Services.GetRequiredService<EditorHost>();
        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        var readsBefore = registered.ReviewReads;

        await cut.InvokeAsync(() => host.RequestCloseAsync());

        Assert.False(host.IsOpen);
        Assert.Equal(readsBefore, registered.ReviewReads);
    }

    [Fact]
    public async Task Fix_OffTheDesktop_StaysOnTheListAndOpensNothing()
    {
        var item = Item(FirstId, "Cibola Burn");
        Register(() => Loaded(item));
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = navigation.Uri;
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));

        // Editing is desktop-only: off the desktop the row carries no Fix button at all.
        Assert.DoesNotContain(cut.FindAll("tr.needs-review-table__row button"), button => button.TextContent.Trim() == "Fix");
        Assert.Equal(before, navigation.Uri);
        Assert.False(Services.GetRequiredService<EditorHost>().IsOpen);
    }

    [Fact]
    public void ReviewLoadFailure_ShowsTheErrorAndRetry_NeverTheEmptyMessage_AndRetryShowsTheRows()
    {
        var failing = true;
        Register(() => failing ? new ReviewListAttempt(null, "The Engine answered 500.") : Loaded(Item(FirstId, "Recovered Book")));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("Needs Review could not load. Nothing was changed.", cut.Markup));
        Assert.DoesNotContain("No items need review", cut.Markup);
        Assert.Empty(cut.FindAll("tr.needs-review-table__row"));

        failing = false;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.DoesNotContain("could not load", cut.Markup);
        Assert.Contains("Recovered Book", cut.Markup);
    }

    [Fact]
    public void ReviewLoad_WithNothingPending_StillSaysNoItemsNeedReview()
    {
        Register(() => Loaded());

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("No items need review", cut.Markup));
        Assert.DoesNotContain("could not load", cut.Markup);
    }

    [Fact]
    public void OneReviewItemThatCannotBeDrawn_ShowsACouldntBeShownRowAndTheOtherItemsStayUsable()
    {
        var broken = Item(FirstId, "Cibola Burn");
        broken.DetectedFacts = null!;
        Register(() => Loaded(broken, Item(SecondId, "Leviathan Wakes")));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Contains("Leviathan Wakes", cut.Find("tr.needs-review-table__row").TextContent, StringComparison.Ordinal);
        var fallback = Assert.Single(cut.FindAll("[data-testid='row-error']"));
        Assert.Contains("This item couldn't be shown", fallback.TextContent, StringComparison.Ordinal);
        Assert.Contains("Cibola Burn", fallback.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".app-error-state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dismiss_WhenTheEngineRefusesOrFails_SaysSoAndKeepsTheRow(bool throws)
    {
        Register(
            () => Loaded(Item(FirstId, "Stubborn Book"), Item(SecondId, "Other Book")),
            dismiss: () => throws ? Task.FromException<bool>(new InvalidOperationException("boom")) : Task.FromResult(false));
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));

        OpenMenuAndClick(cut, "Dismiss from review");

        // The reason is an alert at the top of the list (a refusal is a warning, an unexpected error an error), never a toast.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-review-failure]")));
        var alert = cut.Find("[data-review-failure]");
        Assert.Contains("could not be dismissed", alert.TextContent);
        if (!throws)
        {
            Assert.Contains("tl-alert-warning", alert.ClassName);
            Assert.Contains("It is still in Needs Review.", alert.TextContent);
        }
        else
        {
            Assert.Contains("tl-alert-error", alert.ClassName);
        }

        Assert.Empty(Toasts);
        Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepProviderMatch_WhenTheEngineRefusesOrFails_SaysSoAndKeepsTheRow(bool throws)
    {
        var registered = Register(
            () => Loaded(Item(FirstId, "Stubborn Book", "MissingQid")),
            skipUniverse: () => throws ? Task.FromException<bool>(new InvalidOperationException("boom")) : Task.FromResult(false));
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));

        OpenMenuAndClick(cut, "Keep provider match");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-review-failure]")));
        var alert = cut.Find("[data-review-failure]");
        Assert.Equal([FirstId], registered.Kept);
        Assert.Contains("could not keep the provider match", alert.TextContent);
        Assert.Contains(throws ? "tl-alert-error" : "tl-alert-warning", alert.ClassName);
        Assert.Empty(Toasts);
        Assert.Single(cut.FindAll("tr.needs-review-table__row"));
    }

    [Fact]
    public void ReviewRows_ShowFromMergeAndImportFolderBadgesOnlyOnMatchingRows()
    {
        var merged = Item(FirstId, "Merged Book");
        merged.FromMerge = true;
        var imported = Item(SecondId, "Imported Book");
        imported.FromImportFolder = true;
        var plain = Item(Guid.Parse("33333333-3333-4333-8333-333333333333"), "Plain Book");
        Register(() => Loaded(merged, imported, plain));

        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tr.needs-review-table__row").Count));

        string[] BadgesOf(string title) => cut.FindAll("tr.needs-review-table__row")
            .Single(row => row.TextContent.Contains(title, StringComparison.Ordinal))
            .QuerySelectorAll(".needs-review-table__item .app-status-badge")
            .Select(badge => badge.TextContent.Trim())
            .ToArray();

        Assert.Equal(["From merge"], BadgesOf("Merged Book"));
        Assert.Equal(["From the Import folder"], BadgesOf("Imported Book"));
        Assert.Empty(BadgesOf("Plain Book"));
    }

    [Fact]
    public void ServerAdministrator_SeesTheNeedsReviewSection_AndTheCountIsFetched()
    {
        var registered = Register(() => Loaded(Item(FirstId, "Pending Book")));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Contains("Needs Review", cut.Markup);
        Assert.Contains(nameof(IEngineApiClient.GetNeedsReviewListAsync), registered.EngineCalls);
        Assert.Contains(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
    }

    [Fact]
    public void WithALibrary_TheListIsRequestedForThatLibrary()
    {
        var asked = new List<string>();
        var registered = Register(
            () => Loaded(Item(FirstId, "Pending Book"), Item(SecondId, "Other Library Book")),
            libraryReviews: library => { asked.Add(library); return Loaded(Item(FirstId, "Pending Book")); });

        var cut = Render<NeedsReviewPageContent>(parameters => parameters.Add(p => p.Library, "library-a"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Equal(["library-a"], asked);
        Assert.Contains(nameof(IEngineApiClient.GetNeedsReviewListAsync), registered.EngineCalls);
    }

    [Fact]
    public void HouseholdAdministrator_GetsNoNeedsReviewSection_AndNoReviewRequestIsMade()
    {
        var registered = Register(() => Loaded(Item(FirstId, "Pending Book")), authority: HouseholdAdministrator());

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("server administrators", cut.Markup));
        Assert.DoesNotContain("No items need review", cut.Markup);
        Assert.DoesNotContain("could not load", cut.Markup);
        Assert.Empty(cut.FindAll("tr.needs-review-table__row"));
        Assert.Empty(cut.FindAll(".review-pinned"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNeedsReviewListAsync), registered.EngineCalls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetPendingReviewsAsync), registered.EngineCalls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
    }

    [Fact]
    public void Dismiss_WhenTheEngineAccepts_CallsTheEngineAndTheRowLeavesTheList()
    {
        var registered = Register(() => Loaded(Item(FirstId, "Resolved Book"), Item(SecondId, "Other Book")));
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));

        OpenMenuAndClick(cut, "Dismiss from review");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Single(registered.Dismissed);
        Assert.Empty(Toasts);
    }

    [Fact]
    public void NeedsReviewPage_SearchNarrowsTheList_AndStartsFromTheQueryText()
    {
        Register(() => Loaded(Item(FirstId, "Dune"), Item(SecondId, "Emma")));

        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.InitialSearch, "dune"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Contains("Dune", cut.Markup);
        Assert.DoesNotContain("Emma", cut.Markup);
    }

    [Fact]
    public async Task ReviewCount_ForAServerAdministrator_IsFetchedFromTheEngine()
    {
        var registered = Register(() => Loaded(), authority: ServerAdministrator());

        var count = await Services.GetRequiredService<UIOrchestratorService>().RefreshReviewCountAsync();

        Assert.Equal(3, count);
        Assert.Contains(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
    }

    [Fact]
    public async Task ReviewCount_ForAHouseholdAdministrator_MakesNoEngineRequestAndStaysZero()
    {
        var registered = Register(() => Loaded(), authority: HouseholdAdministrator());

        var count = await Services.GetRequiredService<UIOrchestratorService>().RefreshReviewCountAsync();

        Assert.Equal(0, count);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
    }

    // ---- One list (universal editor v3 WP7 and D9) ---------------------------------------------------------------------------------

    private static readonly Guid FileAssetId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid FileWorkId = Guid.Parse("66666666-6666-4666-8666-666666666666");

    private static MediaEngine.Contracts.Review.NeedsReviewRowDto FileRow(Guid assetId, string name, Guid? workId = null, string reason = "no_match", string library = "movies", string folder = "C:/media/movies", DateTimeOffset? lastTried = null) => new()
    {
        Key = $"file:{assetId:D}",
        Kind = MediaEngine.Contracts.Review.NeedsReviewKinds.NotMatched,
        LibraryId = library,
        LibraryName = "Movies",
        LastTriedAt = lastTried ?? DateTimeOffset.UtcNow.AddHours(-3),
        Unmatched = new MediaEngine.Contracts.Library.UnmatchedItemDto
        {
            AssetId = assetId,
            LibraryId = library,
            FileName = name,
            Folder = folder,
            WorkId = workId,
            Reason = reason,
            LastTriedAt = DateTimeOffset.UtcNow.AddHours(-3),
        },
    };

    private static MediaEngine.Contracts.Review.NeedsReviewRowDto ReviewRow(Guid id, string title, string trigger, MediaEngine.Contracts.Review.NeedsReviewBestGuessDto? guess = null, double? confidence = null, string? year = null, string? cover = null) => new()
    {
        Key = $"review:{id:D}",
        Kind = trigger == "RetailMatchFailed" ? MediaEngine.Contracts.Review.NeedsReviewKinds.NotMatched : MediaEngine.Contracts.Review.NeedsReviewKinds.NotSure,
        LibraryId = "movies",
        LibraryName = "Movies",
        LastTriedAt = DateTimeOffset.UtcNow.AddHours(-2),
        BestGuess = guess,
        Review = new MediaEngine.Contracts.Review.ReviewItemDto
        {
            Id = id,
            EntityId = Guid.NewGuid(),
            EntityType = "MediaAsset",
            Trigger = trigger,
            Status = "Pending",
            EntityTitle = title,
            MediaType = "Movies",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LibraryId = "movies",
            ConfidenceScore = confidence,
            CoverUrl = cover,
            DetectedFacts = FactsOf(year),
        },
    };

    private static Dictionary<string, string> FactsOf(string? year)
    {
        var facts = new Dictionary<string, string>();
        if (year is not null)
        {
            facts["year"] = year;
        }

        return facts;
    }

    private static NeedsReviewListAttempt ListOf(params MediaEngine.Contracts.Review.NeedsReviewRowDto[] rows) => new(new MediaEngine.Contracts.Review.NeedsReviewListDto
    {
        Items = [.. rows],
        Total = rows.Length,
        NotMatchedTotal = rows.Count(row => row.Kind == MediaEngine.Contracts.Review.NeedsReviewKinds.NotMatched),
        NotSureTotal = rows.Count(row => row.Kind == MediaEngine.Contracts.Review.NeedsReviewKinds.NotSure),
        Take = 500,
    }, null);

    [Fact]
    public void TheListHoldsUnmatchedFilesAndReviewItemsTogether_EachSayingWhyInWords()
    {
        var list = ListOf(
            FileRow(FileAssetId, "Mystery.2019.mkv"),
            ReviewRow(FirstId, "Cibola Burn", "MissingQid"));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));
        var file = cut.Find($"tr[data-key='file:{FileAssetId:D}']");
        Assert.Contains("Not matched", file.TextContent, StringComparison.Ordinal);
        Assert.Contains("No match was found online", file.TextContent, StringComparison.Ordinal);
        Assert.Contains("Mystery.2019.mkv", file.TextContent, StringComparison.Ordinal);
        var review = cut.Find($"tr[data-key='review:{FirstId:D}']");
        Assert.Contains("Not sure", review.TextContent, StringComparison.Ordinal);
        Assert.Contains("No canonical identity found", review.TextContent, StringComparison.Ordinal);
        // The columns the product promises.
        var headings = cut.FindAll("thead th").Select(th => th.TextContent.Trim()).ToArray();
        Assert.Contains("Why", headings);
        Assert.Contains("Best guess", headings);
        Assert.Contains("Last tried", headings);
    }

    [Fact]
    public void TheFilterChipsShowTheTotals_AndAFilterAsksTheEngineForThatKindAndLibrary()
    {
        var asked = new List<(string? Kind, string? Library)>();
        var list = ListOf(FileRow(FileAssetId, "Mystery.2019.mkv"), ReviewRow(FirstId, "Cibola Burn", "MissingQid"));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), args =>
        {
            asked.Add(((string?)args![0], (string?)args[1]));
            return Task.FromResult(list);
        }));

        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.Show, "not-matched").Add(c => c.Library, "movies"));

        cut.WaitForAssertion(() => Assert.NotEmpty(asked));
        Assert.Equal(("not_matched", "movies"), asked[0]);
        var chips = cut.FindAll(".app-segmented-control__item").Select(chip => chip.TextContent.Trim()).ToArray();
        Assert.Equal(["All 2", "Not matched 1", "Not sure 1"], chips);
        Assert.Equal("true", cut.FindAll(".app-segmented-control__item").Single(chip => chip.TextContent.Contains("Not matched", StringComparison.Ordinal)).GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Fix_OnAnUnmatchedFile_OpensThePopOverOnMatch_AndAFileWithNoWorkOffersNoFix()
    {
        var list = ListOf(FileRow(FileAssetId, "Mystery.2019.mkv", FileWorkId), FileRow(Guid.NewGuid(), "Orphan.mkv", workId: null));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));
        await Services.GetRequiredService<DeviceContextService>()
            .InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var popover = Render<MediaEngine.Web.Components.Details.EditorPopover>();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));

        var fixes = cut.FindAll("tr.needs-review-table__row button").Where(button => button.TextContent.Trim() == "Fix").ToList();
        Assert.Single(fixes);
        fixes[0].Click();

        popover.WaitForAssertion(() => Assert.True(Services.GetRequiredService<EditorHost>().IsOpen));
        Assert.Equal(EditTool.Match, Services.GetRequiredService<EditorHost>().Session.ActiveTool);
    }

    [Fact]
    public void UseThis_RunsTheEnginesBestGuess_AndTheRowLeavesTheList()
    {
        var guess = new MediaEngine.Contracts.Review.NeedsReviewBestGuessDto { Action = "keep_provider_match", Name = "Cibola Burn", Year = "2014", Source = "Provider match" };
        var list = ListOf(ReviewRow(FirstId, "Cibola Burn", "MissingQid", guess), ReviewRow(SecondId, "Other", "MetadataConflict"));
        var registered = Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));
        Assert.Contains("Cibola Burn (2014)", cut.Find($"tr[data-key='review:{FirstId:D}'] .needs-review-table__guess").TextContent, StringComparison.Ordinal);
        Assert.Equal("None", cut.Find($"tr[data-key='review:{SecondId:D}'] .needs-review-table__guess").TextContent.Trim());

        cut.Find($"tr[data-key='review:{FirstId:D}'] .needs-review-table__guess button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        Assert.Equal([FirstId], registered.Kept);
    }

    [Fact]
    public void BulkTryAgainForAllAndKeepAllAsTheyAre_ActOnTheRowsTheFiltersLeave()
    {
        var retried = new List<Guid>();
        var kept = new List<Guid>();
        var list = ListOf(
            FileRow(FileAssetId, "A.mkv"),
            FileRow(Guid.Parse("77777777-7777-4777-8777-777777777777"), "B.mkv"),
            ReviewRow(FirstId, "Cibola Burn", "MetadataConflict"));
        var registered = Register(() => Loaded(), configure: stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list));
            stub.SetHandler(nameof(IEngineApiClient.RetryUnmatchedItemAsync), args =>
            {
                retried.Add((Guid)args![0]!);
                return Task.FromResult<MediaEngine.Contracts.Library.UnmatchedActionResultDto?>(new() { Affected = 1 });
            });
            stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args =>
            {
                kept.Add((Guid)args![0]!);
                return Task.FromResult<MediaEngine.Contracts.Library.UnmatchedActionResultDto?>(new() { Affected = 1 });
            });
        });
        var dialogs = Render<AppDialogHost>();
        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.InitialSearch, "mkv"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));

        // The two links sit in the filter row, not on a row of their own, and there is no tick step.
        Assert.Equal(2, cut.FindAll(".needs-review-page__filters .needs-review-page__bulk button").Count);
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
        Assert.DoesNotContain("Tick rows", cut.Markup, StringComparison.Ordinal);

        // The search leaves the two files, so Try again for all reaches both and never the hidden review item.
        BulkButton(cut, "Try again for all").Click();
        cut.WaitForAssertion(() => Assert.Equal([FileAssetId, Guid.Parse("77777777-7777-4777-8777-777777777777")], retried));
        Assert.Empty(registered.Dismissed);

        // Keep all as they are asks first and says how many rows it covers; Cancel changes nothing.
        BulkButton(cut, "Keep all as they are").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("2 items will be left as they are", dialogs.Find(".app-confirm__message").TextContent, StringComparison.Ordinal));
        dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        Assert.Empty(kept);
        Assert.Empty(registered.Dismissed);

        BulkButton(cut, "Keep all as they are").Click();
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll(".app-confirm")));
        dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep all").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, kept.Count));
        Assert.Empty(registered.Dismissed);
    }

    private static AngleSharp.Dom.IElement BulkButton(IRenderedComponent<NeedsReviewPageContent> cut, string label) =>
        cut.FindAll(".needs-review-page__bulk button").Single(button => button.TextContent.Trim() == label);

    [Fact]
    public void KeepAllAsTheyAre_WithNoSearch_AlsoDismissesTheItemsTheEngineWasUnsureAbout()
    {
        var list = ListOf(FileRow(FileAssetId, "A.mkv"), ReviewRow(FirstId, "Cibola Burn", "MetadataConflict"));
        var kept = new List<Guid>();
        var registered = Register(() => Loaded(), configure: stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list));
            stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args =>
            {
                kept.Add((Guid)args![0]!);
                return Task.FromResult<MediaEngine.Contracts.Library.UnmatchedActionResultDto?>(new() { Affected = 1 });
            });
        });
        var dialogs = Render<AppDialogHost>();
        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));

        BulkButton(cut, "Keep all as they are").Click();
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll(".app-confirm")));
        dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep all").Click();

        cut.WaitForAssertion(() => Assert.Equal([FileAssetId], kept));
        Assert.Equal([FirstId], registered.Dismissed);
    }

    [Fact]
    public void TryAgainForAll_IsOff_WhenNoRowLeftCanBeLookedUpAgain()
    {
        var list = ListOf(ReviewRow(FirstId, "Cibola Burn", "MetadataConflict"));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));

        Assert.True(BulkButton(cut, "Try again for all").HasAttribute("disabled"));
        Assert.False(BulkButton(cut, "Keep all as they are").HasAttribute("disabled"));
    }

    [Fact]
    public void AnEmptyFilter_SaysSoInWords()
    {
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(ListOf())));

        var cut = Render<NeedsReviewPageContent>(p => p.Add(c => c.Show, "not-matched"));

        cut.WaitForAssertion(() => Assert.Contains("Every file has been matched.", cut.Markup));
    }

    [Fact]
    public void AnUnmatchedFileRow_ShowsTitleKindAndOneShortenedFolderLine_WithTheFullPathInTheTooltip()
    {
        const string folder = @"Z:\Development\testing\media\real_media\books\Rick Riordan\The Percy Jackson and the Olympians, Book Two_ Sea of Monsters (112)";
        var list = ListOf(FileRow(FileAssetId, "Sea of Monsters.epub", folder: folder));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        var item = cut.Find("td.needs-review-table__item");
        Assert.Equal("Movies", item.QuerySelectorAll("small")[0].TextContent.Trim());
        var path = item.QuerySelectorAll("[data-testid='needs-review-path']").Single();
        Assert.Equal(@"…\Rick Riordan\The Percy Jackson and the Olympians, Book Two_ Sea of Monsters (112)", path.TextContent.Trim());
        Assert.DoesNotContain("real_media", path.TextContent, StringComparison.Ordinal);
        Assert.Equal(folder, item.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
        Assert.Null(item.QuerySelector("[title]"));
    }

    [Theory]
    [InlineData(@"Z:\a\b\c\d", @"…\c\d")]
    [InlineData("C:/media/movies", "…/media/movies")]
    [InlineData(@"Z:\movies", @"Z:\movies")]
    [InlineData("/movies", "/movies")]
    public void ShortFolder_KeepsTheLastTwoFolderNames_AndTheSeparatorTheFolderUses(string folder, string expected)
    {
        Assert.Equal(expected, NeedsReviewPageContent.ShortFolder(folder));
    }

    [Fact]
    public void TheLastTriedColumn_ReadsRelative_Today_Yesterday_AndDaysAgo()
    {
        var noon = DateTime.Today.AddHours(12);
        var yesterday = Guid.Parse("88888888-8888-4888-8888-888888888888");
        var older = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var list = ListOf(
            FileRow(FileAssetId, "Today.mkv", lastTried: new DateTimeOffset(noon)),
            FileRow(yesterday, "Yesterday.mkv", lastTried: new DateTimeOffset(noon.AddDays(-1))),
            FileRow(older, "Older.mkv", lastTried: new DateTimeOffset(noon.AddDays(-2))));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tr.needs-review-table__row").Count));
        string TriedOf(Guid id) => cut.Find($"tr[data-key='file:{id:D}'] td.needs-review-table__tried").TextContent.Trim();
        Assert.Equal("Today", TriedOf(FileAssetId));
        Assert.Equal("Yesterday", TriedOf(yesterday));
        Assert.Equal("2 days ago", TriedOf(older));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(30, 30)]
    public void WholeDaysAgo_CountsCalendarDays_AndNothingYetHasNone(int daysBack, int expected)
    {
        var now = new DateTimeOffset(2026, 11, 6, 0, 30, 0, TimeSpan.Zero);
        var tried = now.AddDays(-daysBack);

        Assert.Equal(expected, NeedsReviewPageContent.WholeDaysAgo(tried, now));
        Assert.Null(NeedsReviewPageContent.WholeDaysAgo(default, now));
    }

    [Fact]
    public void TheRow_HasAPictureBeforeTheName_ADarkSquareWhenThereIsNone_AndNoTickBox()
    {
        var list = ListOf(
            FileRow(FileAssetId, "Mystery.2019.mkv"),
            ReviewRow(FirstId, "Cibola Burn", "MetadataConflict", cover: "/engine-image/cibola.jpg"));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
        var file = cut.Find($"tr[data-key='file:{FileAssetId:D}']");
        Assert.Empty(file.QuerySelectorAll(".needs-review-table__thumb img"));
        Assert.Single(file.QuerySelectorAll(".needs-review-table__thumb"));
        var review = cut.Find($"tr[data-key='review:{FirstId:D}']");
        Assert.Single(review.QuerySelectorAll(".needs-review-table__thumb img.needs-review-table__thumb-img"));
        // The picture is the first cell, ahead of the name.
        Assert.Contains("needs-review-table__thumb-cell", review.Children[0].ClassName, StringComparison.Ordinal);
        Assert.Contains("needs-review-table__item", review.Children[1].ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void BestGuess_ShowsANamedGuessWithAPercentChipWhenTheEngineIsPartlySure_AndNoneWhenItHasNothing()
    {
        var list = ListOf(
            ReviewRow(FirstId, "Dune", "LowConfidence", confidence: 0.82, year: "2021"),
            FileRow(FileAssetId, "sample_clip.mp4"));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.needs-review-table__row").Count));
        var partial = cut.Find($"tr[data-key='review:{FirstId:D}'] .needs-review-table__guess");
        Assert.Contains("Dune (2021)", partial.TextContent, StringComparison.Ordinal);
        Assert.Contains("82", partial.QuerySelector(".needs-review-table__confidence")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(partial.QuerySelectorAll("button"));
        Assert.Equal("None", cut.Find($"tr[data-key='file:{FileAssetId:D}'] .needs-review-table__guess").TextContent.Trim());
    }

    [Fact]
    public void UseThis_IsAGreenShortcutOnTheClearCases_AndRunsTheEnginesAnswerInOneClick()
    {
        var guess = new MediaEngine.Contracts.Review.NeedsReviewBestGuessDto { Action = "keep_provider_match", Name = "Cibola Burn", Year = "2014", Source = "Provider match" };
        var list = ListOf(ReviewRow(FirstId, "Cibola Burn", "MissingQid", guess));
        var registered = Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));

        var cut = Render<NeedsReviewPageContent>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        var useThis = cut.Find(".needs-review-table__guess button.needs-review-table__use-this");
        Assert.Contains("success", useThis.ClassName, StringComparison.OrdinalIgnoreCase);

        useThis.Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tr.needs-review-table__row")));
        Assert.Equal([FirstId], registered.Kept);
    }

    [Fact]
    public async Task Fix_IsAPlainOutlinedButton_AndTheRowMenuKeepsWhatFixCannotCover()
    {
        var list = ListOf(FileRow(FileAssetId, "Mystery.2019.mkv", FileWorkId));
        Register(() => Loaded(), configure: stub => stub.SetHandler(nameof(IEngineApiClient.GetNeedsReviewListAsync), _ => Task.FromResult(list)));
        await Services.GetRequiredService<DeviceContextService>()
            .InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

        var cut = Render<NeedsReviewPageContent>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.needs-review-table__row")));
        var fix = cut.FindAll("tr.needs-review-table__row button").Single(button => button.TextContent.Trim() == "Fix");
        Assert.Contains("app-button--outlined", fix.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("app-button--filled", fix.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("app-tone--primary", fix.ClassName, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("td.needs-review-table__actions .needs-review-table__actions-row .app-overflow-menu__trigger"));
        Assert.Equal(6, cut.FindAll("thead th").Count);
        Assert.Equal(6, cut.FindAll("tr.needs-review-table__row > td").Count);

        cut.Find("tr.needs-review-table__row .app-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.app-menu-item")));
        var items = cut.FindAll("button.app-menu-item").Select(item => item.TextContent.Trim()).ToArray();
        Assert.Contains("Try again", items);
        Assert.Contains("Keep as it is", items);
        Assert.Contains("Remove from library", items);
        Assert.DoesNotContain("Fix", items);
    }


    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
