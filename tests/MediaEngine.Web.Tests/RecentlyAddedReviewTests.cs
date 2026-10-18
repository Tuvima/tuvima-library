using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings → Recently Added: the pinned Needs Review list reports a failed load instead of an empty queue,
/// says so when Dismiss or Keep provider match is refused, shows the From merge / Import folder badges, and
/// sends the Added status filter to the Engine. Only server administrators get the section at all.
/// </summary>
public sealed class RecentlyAddedReviewTests : AsyncBunitContext
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private sealed record Registered(List<string?> HistoryStatuses, List<Guid> Dismissed, List<Guid> Kept)
    {
        /// <summary>Every Engine client method the Dashboard called, in order.</summary>
        public List<string> EngineCalls { get; set; } = [];
    }

    private static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static AccountProfileGrantDto[] Grants(bool adminEnabled) =>
    [
        new(AccountId, ProfileId, "Test User", null, true, true, adminEnabled,
            new GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow),
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
        DashboardAuthorityResponse? authority = null)
    {
        var result = new Registered([], [], []);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", AccountId, ProfileId, Guid.NewGuid(), authority ?? ServerAdministrator());
        Services.AddSingleton(session);
        var api = EngineApiClientStub.Create(stub =>
        {
            result.EngineCalls = stub.Calls;
            stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsAttemptAsync), _ => Task.FromResult(reviews()));
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
        Services.AddScoped<MediaEditorLauncherService>();
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

    private static IEnumerable<string> StatusFilterValues(IRenderedComponent<RecentlyAddedPageContent> cut) =>
        (cut.FindComponents<AppSelect>().Single(select => select.Instance.Label == "Status").Instance.Options ?? [])
            .Select(option => option.Value);

    private static void OpenMenuAndClick(IRenderedComponent<RecentlyAddedPageContent> cut, string label)
    {
        cut.Find("article.review-row .app-overflow-menu__trigger").Click();
        cut.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();
    }

    [Fact]
    public void ReviewLoadFailure_ShowsTheErrorAndRetry_NeverTheEmptyMessage_AndRetryShowsTheRows()
    {
        var failing = true;
        Register(() => failing ? new ReviewListAttempt(null, "The Engine answered 500.") : Loaded(Item(FirstId, "Recovered Book")));

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));

        cut.WaitForAssertion(() => Assert.Contains("Needs Review could not load. Nothing was changed.", cut.Markup));
        Assert.DoesNotContain("No items need review", cut.Markup);
        Assert.Empty(cut.FindAll("article.review-row"));

        failing = false;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.review-row")));
        Assert.DoesNotContain("could not load", cut.Markup);
        Assert.Contains("Recovered Book", cut.Markup);
    }

    [Fact]
    public void ReviewLoad_WithNothingPending_StillSaysNoItemsNeedReview()
    {
        Register(() => Loaded());

        var cut = Render<RecentlyAddedPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("No items need review", cut.Markup));
        Assert.DoesNotContain("could not load", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dismiss_WhenTheEngineRefusesOrFails_SaysSoAndKeepsTheRow(bool throws)
    {
        Register(
            () => Loaded(Item(FirstId, "Stubborn Book"), Item(SecondId, "Other Book")),
            dismiss: () => throws ? Task.FromException<bool>(new InvalidOperationException("boom")) : Task.FromResult(false));
        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("article.review-row").Count));

        OpenMenuAndClick(cut, "Dismiss from review");

        cut.WaitForAssertion(() => Assert.Single(Toasts));
        var toast = Toasts[0];
        Assert.Contains("could not be dismissed", toast.Message);
        if (!throws)
        {
            Assert.Equal(AppSeverity.Warning, toast.Severity);
            Assert.Contains("It is still in Needs Review.", toast.Message);
        }
        else
        {
            Assert.Equal(AppSeverity.Error, toast.Severity);
        }

        Assert.Equal(2, cut.FindAll("article.review-row").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepProviderMatch_WhenTheEngineRefusesOrFails_SaysSoAndKeepsTheRow(bool throws)
    {
        var registered = Register(
            () => Loaded(Item(FirstId, "Stubborn Book", "MissingQid")),
            skipUniverse: () => throws ? Task.FromException<bool>(new InvalidOperationException("boom")) : Task.FromResult(false));
        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.review-row")));

        OpenMenuAndClick(cut, "Keep provider match");

        cut.WaitForAssertion(() => Assert.Single(Toasts));
        Assert.Equal([FirstId], registered.Kept);
        Assert.Contains("could not keep the provider match", Toasts[0].Message);
        Assert.Equal(throws ? AppSeverity.Error : AppSeverity.Warning, Toasts[0].Severity);
        Assert.Single(cut.FindAll("article.review-row"));
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

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("article.review-row").Count));

        string[] BadgesOf(string title) => cut.FindAll("article.review-row")
            .Single(row => row.TextContent.Contains(title, StringComparison.Ordinal))
            .QuerySelectorAll(".review-row__identity .app-status-badge")
            .Select(badge => badge.TextContent.Trim())
            .ToArray();

        Assert.Equal(["From merge"], BadgesOf("Merged Book"));
        Assert.Equal(["From the Import folder"], BadgesOf("Imported Book"));
        Assert.Empty(BadgesOf("Plain Book"));
    }

    [Fact]
    public void StatusFilter_AllAsksForHistoryWithoutAStatus_AddedAsksForAdded_AndNeedsReviewHidesHistory()
    {
        var registered = Register(() => Loaded());
        var cut = Render<RecentlyAddedPageContent>();
        cut.WaitForAssertion(() => Assert.Equal([null], registered.HistoryStatuses));
        Assert.NotEmpty(cut.FindAll("section.recent-history"));

        var status = cut.FindComponents<AppSelect>().Single(select => select.Instance.Label == "Status");
        cut.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync("added"));

        cut.WaitForAssertion(() => Assert.Equal([null, "added"], registered.HistoryStatuses));
        Assert.NotEmpty(cut.FindAll("section.recent-history"));

        cut.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync("review"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("section.recent-history")));
        Assert.Equal([null, "added", null], registered.HistoryStatuses);

        cut.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync("all"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section.recent-history")));
        Assert.Equal([null, "added", null, null], registered.HistoryStatuses);
    }

    [Fact]
    public void ServerAdministrator_SeesTheNeedsReviewSection_AndTheCountIsFetched()
    {
        var registered = Register(() => Loaded(Item(FirstId, "Pending Book")));

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article.review-row")));
        Assert.Contains("Needs Review", cut.Markup);
        Assert.Contains(nameof(IEngineApiClient.GetPendingReviewsAttemptAsync), registered.EngineCalls);
        Assert.Contains(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
        Assert.Contains(StatusFilterValues(cut), value => value == "review");
    }

    [Fact]
    public void HouseholdAdministrator_GetsNoNeedsReviewSection_AndNoReviewRequestIsMade()
    {
        var registered = Register(() => Loaded(Item(FirstId, "Pending Book")), authority: HouseholdAdministrator());

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section.recent-history")));
        Assert.DoesNotContain("Needs Review", cut.Markup);
        Assert.DoesNotContain("No items need review", cut.Markup);
        Assert.DoesNotContain("could not load", cut.Markup);
        Assert.Empty(cut.FindAll("article.review-row"));
        Assert.Empty(cut.FindAll(".review-pinned"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetPendingReviewsAttemptAsync), registered.EngineCalls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetPendingReviewsAsync), registered.EngineCalls);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetReviewCountAsync), registered.EngineCalls);
        Assert.DoesNotContain(StatusFilterValues(cut), value => value == "review");
        Assert.Contains(nameof(IEngineApiClient.GetRecentIngestionAdditionsAsync), registered.EngineCalls);
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

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
