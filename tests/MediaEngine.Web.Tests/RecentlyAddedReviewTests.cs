using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings → Recently Added: the pinned Needs Review list reports a failed load instead of an empty queue,
/// says so when Dismiss or Keep provider match is refused, shows the From merge / Import folder badges, and
/// sends the Added status filter to the Engine.
/// </summary>
public sealed class RecentlyAddedReviewTests : AsyncBunitContext
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private sealed record Registered(List<string?> HistoryStatuses, List<Guid> Dismissed, List<Guid> Kept);

    private Registered Register(
        Func<ReviewListAttempt> reviews,
        Func<Task<bool>>? dismiss = null,
        Func<Task<bool>>? skipUniverse = null)
    {
        var result = new Registered([], [], []);
        var api = EngineApiClientStub.Create(stub =>
        {
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
            new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
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
}
