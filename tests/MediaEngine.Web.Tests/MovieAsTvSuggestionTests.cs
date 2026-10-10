using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Review;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Found as a TV title": a Movies item that TMDB lists as a short TV series shows a suggestion card in the
/// Review Queue with Move to TV, Search again and Keep as unmatched film.
/// </summary>
public sealed class MovieAsTvSuggestionTests : AsyncBunitContext
{
    private static readonly Guid ReviewId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid EntityId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static MovieTvSuggestionDto DrHorrible() => new()
    {
        TmdbTvId = "5739",
        Name = "Dr. Horrible's Sing-Along Blog",
        FirstAirYear = 2008,
        Type = "Miniseries",
        Seasons = 1,
        Episodes = 3,
        PosterUrl = "https://image.tmdb.org/t/p/w500/dr.jpg",
    };

    [Fact]
    public void Card_WordsTheSuggestionAndOffersTheThreeActions()
    {
        var cut = Render<MovieAsTvSuggestionCard>(p => p.Add(c => c.Suggestion, DrHorrible()));

        Assert.Contains(
            "TMDB lists this as the miniseries 'Dr. Horrible's Sing-Along Blog' (2008, 3 parts). Move it to TV?",
            System.Net.WebUtility.HtmlDecode(cut.Find(".movie-as-tv-card__text").TextContent));
        Assert.Equal(
            ["Move to TV", "Search again", "Keep as unmatched film"],
            cut.FindAll(".movie-as-tv-card__actions button").Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("The file stays where it is; Tuvima files it under TV as a special.", cut.Find(".movie-as-tv-card__primary small").TextContent);
        Assert.Equal("https://image.tmdb.org/t/p/w500/dr.jpg", cut.Find("img.movie-as-tv-card__poster").GetAttribute("src"));
    }

    [Fact]
    public void Card_ActionsRaiseTheirOwnCallbacks_AndAreDisabledWhileBusy()
    {
        var calls = new List<string>();
        var cut = Render<MovieAsTvSuggestionCard>(p => p
            .Add(c => c.Suggestion, DrHorrible())
            .Add(c => c.OnMoveToTv, () => calls.Add("move"))
            .Add(c => c.OnSearchAgain, () => calls.Add("search"))
            .Add(c => c.OnKeepAsFilm, () => calls.Add("keep")));

        var buttons = cut.FindAll(".movie-as-tv-card__actions button");
        buttons[0].Click();
        buttons[1].Click();
        buttons[2].Click();
        Assert.Equal(["move", "search", "keep"], calls);

        cut.Render(p => p.Add(c => c.Busy, true));
        Assert.All(cut.FindAll(".movie-as-tv-card__actions button"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public void ReviewQueueRow_ForTheTrigger_ShowsTheCardInsteadOfTheReviewButton_AndMoveToTvResolvesTheRow()
    {
        var moved = new List<Guid>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsAsync), _ =>
                Task.FromResult(new List<ReviewItemViewModel> { Item() }));
            stub.SetHandler(nameof(IEngineApiClient.MoveReviewItemToTvAsync), args =>
            {
                moved.Add((Guid)args![0]!);
                return Task.FromResult<ReviewMoveToTvResponse?>(
                    new ReviewMoveToTvResponse(true, ReviewId, EntityId, "Dr. Horrible's Sing-Along Blog", "tv-library"));
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

        var cut = Render<SettingsReviewQueueTab>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Found as a TV title", cut.Markup, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll(".movie-as-tv-card"));
        });
        // The suggestion card replaces the generic Review button for this reason.
        Assert.DoesNotContain(cut.FindAll(".settings-review-row__actions button"), b => b.TextContent.Trim() == "Review");

        cut.FindAll(".movie-as-tv-card__actions button")[0].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([ReviewId], moved);
            Assert.Empty(cut.FindAll(".movie-as-tv-card"));
        });
    }

    private void RegisterReviewServices(List<ReviewItemViewModel> reviews, List<Guid>? moved = null, List<Guid>? dismissed = null)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsAsync), _ => Task.FromResult(reviews));
            stub.SetHandler(nameof(IEngineApiClient.MoveReviewItemToTvAsync), args =>
            {
                moved?.Add((Guid)args![0]!);
                return Task.FromResult<ReviewMoveToTvResponse?>(
                    new ReviewMoveToTvResponse(true, ReviewId, EntityId, "Dr. Horrible's Sing-Along Blog", "tv-library"));
            });
            stub.SetHandler(nameof(IEngineApiClient.DismissReviewItemAsync), args =>
            {
                dismissed?.Add((Guid)args![0]!);
                return Task.FromResult(true);
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
    }

    [Fact]
    public void NeedsReviewRow_ForTheTrigger_ShowsTheCardWithThreeActions_AndOtherTriggersKeepTheReviewButton()
    {
        var moved = new List<Guid>();
        var dismissed = new List<Guid>();
        var other = Item();
        other.Id = Guid.Parse("33333333-3333-4333-8333-333333333333");
        other.Trigger = "RetailMatchFailed";
        other.CandidatesJson = null;
        other.EntityTitle = "Plain Film";
        RegisterReviewServices([Item(), other], moved, dismissed);

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".movie-as-tv-card")));
        Assert.Equal(
            ["Move to TV", "Search again", "Keep as unmatched film"],
            cut.FindAll(".movie-as-tv-card__actions button").Select(button => button.TextContent.Trim()).ToArray());

        var rows = cut.FindAll("article.review-row");
        Assert.Equal(2, rows.Count);
        var tvRow = rows.Single(row => row.QuerySelector(".movie-as-tv-card") is not null);
        var plainRow = rows.Single(row => row.QuerySelector(".movie-as-tv-card") is null);
        // The card replaces the generic Review button for the TV-title trigger only.
        Assert.DoesNotContain(tvRow.QuerySelectorAll(".review-row__actions button"), b => b.TextContent.Trim() == "Review");
        Assert.Contains(plainRow.QuerySelectorAll(".review-row__actions button"), b => b.TextContent.Trim() == "Review");

        cut.FindAll(".movie-as-tv-card__actions button")[0].Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal([ReviewId], moved);
            Assert.Empty(cut.FindAll(".movie-as-tv-card"));
        });
    }

    [Fact]
    public void NeedsReviewRow_KeepAsFilmDismissesTheReviewItem()
    {
        var dismissed = new List<Guid>();
        RegisterReviewServices([Item()], null, dismissed);

        var cut = Render<RecentlyAddedPageContent>(p => p.Add(c => c.ReviewState, "expanded"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".movie-as-tv-card")));

        cut.FindAll(".movie-as-tv-card__actions button")[2].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([ReviewId], dismissed);
            Assert.Empty(cut.FindAll(".movie-as-tv-card"));
        });
    }

    [Fact]
    public void Reader_ReadsTheEngineSuggestionAndIgnoresMissingOrBrokenCandidates()
    {
        var json = JsonSerializer.Serialize(new[] { DrHorrible() });

        var suggestion = MovieTvSuggestionReader.Read(json);

        Assert.NotNull(suggestion);
        Assert.Equal("5739", suggestion!.TmdbTvId);
        Assert.Equal(3, suggestion.Episodes);
        Assert.Null(MovieTvSuggestionReader.Read(null));
        Assert.Null(MovieTvSuggestionReader.Read("not json"));
        Assert.Null(MovieTvSuggestionReader.Read("[]"));
    }

    [Theory]
    [InlineData("Miniseries", 3, "TMDB lists this as the miniseries 'X' (2008, 3 parts). Move it to TV?")]
    [InlineData(null, 1, "TMDB lists this as the TV series 'X' (2008, 1 part). Move it to TV?")]
    public void Describe_UsesTheSeriesTypeAndPartCount(string? type, int episodes, string expected)
    {
        var suggestion = new MovieTvSuggestionDto { Name = "X", FirstAirYear = 2008, Type = type, Episodes = episodes };

        Assert.Equal(expected, MovieTvSuggestionReader.Describe(suggestion));
    }

    [Fact]
    public void TheTrigger_IsLabelledFoundAsATvTitle()
    {
        Assert.Equal("Found as a TV title", ReviewIssueClassifier.Classify("MovieMatchedAsTv").Label);
        Assert.Equal(ReviewIssueBucket.ManualReview, ReviewIssueClassifier.Classify("MovieMatchedAsTv").Bucket);
        Assert.Equal("Found as a TV title", MediaEngine.Web.Components.Library.LibraryHelpers.GetReviewTriggerLabel("MovieMatchedAsTv"));
    }

    private static ReviewItemViewModel Item() => new()
    {
        Id = ReviewId,
        EntityId = EntityId,
        EntityType = "MediaAsset",
        Trigger = "MovieMatchedAsTv",
        Status = "Pending",
        MediaType = "Movies",
        EntityTitle = "Dr. Horrible's Sing-Along Blog",
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        CandidatesJson = JsonSerializer.Serialize(new[] { DrHorrible() }),
    };
}
