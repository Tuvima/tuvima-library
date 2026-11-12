using System.Text.Json;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Review;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// Choices in the row (Needs Review redesign D11): up to three answers per row, and the one action that applies the pick through the
// editor's own writes. Real temp SQLite; the three writes (retail match, Series placement, media type change) are a hand-written spy,
// because they are the editor's existing writes and have their own tests.
public sealed partial class NeedsReviewListReadServiceTests
{
    private static readonly Guid TmdbProviderId = Guid.Parse("b7000007-0000-4000-8000-000000000008");

    private static string RetailJson(params (string Title, string Year, string ItemId, double Score)[] candidates) =>
        JsonSerializer.Serialize(candidates.Select(c => new RetailChoiceCandidate
        {
            ProviderName = "tmdb",
            ProviderId = TmdbProviderId,
            ProviderItemId = c.ItemId,
            Title = c.Title,
            Creator = "Denis Villeneuve",
            Year = c.Year,
            ImageUrl = $"https://image.tmdb.org/t/p/w500/{c.ItemId}.jpg",
            Score = c.Score,
            BridgeIdsJson = $$"""{"tmdb_id":"{{c.ItemId}}"}""",
        }));

    private (Guid Review, Guid Asset, Guid Work) SeedChoiceItem(string mediaType, string trigger, string? candidatesJson, string title = "dune-2021.mkv")
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedWork(workId, mediaType, "standalone", null);
        SeedEditionAndAsset(workId, editionId, assetId, "movies", $"C:/media/movies/{assetId:N}.mkv");
        SeedCanonical(assetId, "title", title);
        var id = Guid.NewGuid();
        _reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = id,
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = trigger,
            Status = ReviewStatus.Pending,
            Detail = "needs a look",
            CandidatesJson = candidatesJson,
            CreatedAt = At(1),
            ReviewReadyAt = At(1),
            AutomationCompletedAt = At(1),
        }).GetAwaiter().GetResult();
        return (id, assetId, workId);
    }

    private async Task<NeedsReviewRowDto> RowOfAsync(Guid review, NeedsReviewListReadService? service = null) =>
        (await (service ?? _service).ListAsync(null, true, 0, 100)).Items.Single(row => row.Key == $"review:{review:D}");

    [Fact]
    public async Task AnAmbiguousFilmWithTwoCandidates_OffersTwoChoices_AndIsInChooseAMatch()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dune", "2021", "438631", 0.78), ("Dune", "1984-12-14", "841", 0.71)));

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Null(row.BestGuess);
        Assert.Equal(["Dune (2021)", "Dune (1984)"], row.Choices.Select(choice => choice.Title));
        Assert.All(row.Choices, choice =>
        {
            Assert.Equal(NeedsReviewChoiceKinds.Retail, choice.Kind);
            Assert.Equal("Denis Villeneuve", choice.Subtitle);
            Assert.False(string.IsNullOrWhiteSpace(choice.Id));
        });
        Assert.Equal(2, row.Choices.Select(choice => choice.Id).Distinct().Count());
    }

    [Fact]
    public async Task AnAmbiguousFilmWithOneCandidate_OffersItAsTheOneAnswerToConfirm_AndIsReadyToConfirm()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous, RetailJson(("Dune", "2021", "438631", 0.78)));

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Ready, row.Group);
        Assert.Empty(row.Choices);
        Assert.NotNull(row.BestGuess);
        Assert.Equal("use_retail_match", row.BestGuess!.Action);
        Assert.Equal("Dune", row.BestGuess.Name);
        Assert.Equal("2021", row.BestGuess.Year);
        Assert.Equal("TMDB", row.BestGuess.Source);
        Assert.False(string.IsNullOrWhiteSpace(row.BestGuess.ChoiceId));
    }

    [Fact]
    public async Task AnAmbiguousFilmStoredBeforeChoicesExisted_StaysInChooseAMatch_WithNoChoices()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous, candidatesJson: null);

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Empty(row.Choices);
        Assert.Null(row.BestGuess);
    }

    [Fact]
    public async Task ARowNeverOffersMoreThanThreeChoices()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("A", "2001", "1", 0.9), ("B", "2002", "2", 0.8), ("C", "2003", "3", 0.7), ("D", "2004", "4", 0.6)));

        var row = await RowOfAsync(review);

        Assert.Equal(["A (2001)", "B (2002)", "C (2003)"], row.Choices.Select(choice => choice.Title));
    }

    [Fact]
    public async Task AChoiceWithNoYear_ShowsJustItsTitle()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new RetailChoiceCandidate { ProviderName = "tmdb", ProviderId = TmdbProviderId, ProviderItemId = "1", Title = "Untitled One" },
            new RetailChoiceCandidate { ProviderName = "tmdb", ProviderId = TmdbProviderId, ProviderItemId = "2", Title = "Untitled Two", Creator = "  " },
        });
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous, json);

        var row = await RowOfAsync(review);

        Assert.Equal(["Untitled One", "Untitled Two"], row.Choices.Select(choice => choice.Title));
        Assert.All(row.Choices, choice => Assert.Null(choice.Subtitle));
    }

    [Fact]
    public async Task AnAmbiguousTelevisionMatch_OffersNoChoices_BecauseAShowIsMatchedInTheEditorAgainstTheTvdb()
    {
        var (review, _, _) = SeedChoiceItem("TV", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dark", "2017", "1", 0.8), ("Dark Matter", "2015", "2", 0.7)));

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Empty(row.Choices);
        Assert.Null(row.BestGuess);
    }

    [Fact]
    public async Task ABookListedInTwoSeries_OffersEachSeriesItCanBePlacedOn()
    {
        var dune = Guid.NewGuid();
        var chronicles = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new object[]
        {
            new { series_qid = "Q1", series_label = "Dune Novels", media_type = "Books", membership_scope = "MainSequence", collection_id = dune },
            new { series_qid = "Q2", series_label = "Dune Chronicles", media_type = "Books", membership_scope = "MainSequence", collection_id = chronicles },
            new { series_qid = "Q3", series_label = "Not shelved yet", media_type = "Books", membership_scope = "MainSequence", collection_id = (Guid?)null },
        });
        var (review, _, _) = SeedChoiceItem("Books", ReviewTrigger.AmbiguousSeriesMembership, json);

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Equal(["Dune Novels", "Dune Chronicles"], row.Choices.Select(choice => choice.Title));
        Assert.All(row.Choices, choice =>
        {
            Assert.Equal(NeedsReviewChoiceKinds.Series, choice.Kind);
            Assert.Null(choice.Subtitle);
        });
    }

    [Fact]
    public async Task AFileThatCouldBeSeveralKindsOfMedia_OffersEachKindByItsPlainName()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { type = "Music", confidence = 0.55, reason = "extension" },
            new { type = "Audiobooks", confidence = 0.5, reason = "folder" },
            new { type = "TV", confidence = 0.2, reason = "name" },
            new { type = "Unknown", confidence = 0.1, reason = "none" },
        });
        var (review, _, _) = SeedChoiceItem("Music", ReviewTrigger.AmbiguousMediaType, json);

        var row = await RowOfAsync(review);

        Assert.Equal(["Music", "Audiobooks", "TV Shows"], row.Choices.Select(choice => choice.Title));
        Assert.All(row.Choices, choice => Assert.Equal(NeedsReviewChoiceKinds.MediaType, choice.Kind));
    }

    [Theory]
    [InlineData(ReviewTrigger.RetailMatchAmbiguous)]
    [InlineData(ReviewTrigger.AmbiguousSeriesMembership)]
    [InlineData(ReviewTrigger.AmbiguousMediaType)]
    public async Task CandidatesThatCannotBeRead_OfferNoChoices_AndTheRowStillLists(string trigger)
    {
        var (review, _, _) = SeedChoiceItem("Movies", trigger, "{ this is not json ]");

        var row = await RowOfAsync(review);

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Empty(row.Choices);
        Assert.Null(row.BestGuess);
    }

    [Fact]
    public async Task AProvidersPictureLinkIsNeverHandedToTheBrowser_AndAnUnlistedHostIsNotEvenFetched()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new RetailChoiceCandidate { ProviderName = "tmdb", ProviderId = TmdbProviderId, ProviderItemId = "1", Title = "One", ImageUrl = "https://evil.example/poster.jpg" },
            new RetailChoiceCandidate { ProviderName = "tmdb", ProviderId = TmdbProviderId, ProviderItemId = "2", Title = "Two", ImageUrl = "https://image.tmdb.org/t/p/w500/2.jpg" },
        });
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous, json);
        var http = new CountingHttpFactory();
        var withPictures = new NeedsReviewListReadService(
            new ReviewQueueReadService(_db), _unmatched, new ListLoader(new MediaEngine.Domain.Configuration.LibraryFolderConfig { Id = "movies", Name = "Movies" }),
            new VaultLibraryRepository(_db), new NeedsReviewRowContextReadService(_db), http, new MemoryCache(new MemoryCacheOptions()));

        var withFetch = await RowOfAsync(review, withPictures);
        var withoutFetch = await RowOfAsync(review);

        Assert.All(withFetch.Choices.Concat(withoutFetch.Choices), choice => Assert.Null(choice.ImageUrl));
        // Only the allow-listed picture host was asked; the other was never contacted.
        Assert.Equal(["image.tmdb.org"], http.Hosts);
    }

    // ── POST /review/{id}/choose ─────────────────────────────────────────────────────────────

    private (ReviewChooseService Service, SpyApplier Applier, ChoicePublisher Publisher) NewChooseService(ReviewChoiceApplyResult? result = null)
    {
        var applier = new SpyApplier { Result = result ?? ReviewChoiceApplyResult.Ok };
        var publisher = new ChoicePublisher();
        var service = new ReviewChooseService(
            new ReviewQueueReadService(_db), _reviews, applier, new CanonicalValueRepository(_db), new SystemActivityRepository(_db),
            publisher, NullLogger<ReviewChooseService>.Instance);
        return (service, applier, publisher);
    }

    [Fact]
    public async Task PickingAFilmChoice_AppliesThatMatchLikeTheEditorsMovieMatch_AndTheItemLeavesTheList()
    {
        var (review, asset, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dune", "2021", "438631", 0.78), ("Dune", "1984-12-14", "841", 0.71)));
        var (service, applier, publisher) = NewChooseService();
        var second = (await RowOfAsync(review)).Choices[1];

        var result = await service.ChooseAsync(review, second.Id, "user");

        Assert.Equal(ReviewChooseOutcome.Chosen, result.Outcome);
        Assert.Equal(new ReviewChooseResponse(true, review, second.Id, NeedsReviewChoiceKinds.Retail), result.Response);
        var applied = Assert.Single(applier.Retail);
        Assert.Equal(asset, applied.EntityId);
        Assert.Equal("item", applied.Request.TargetKind);
        Assert.Equal("movie_identity", applied.Request.TargetFieldGroup);
        Assert.Equal("tmdb", applied.Request.ProviderName);
        Assert.Equal(TmdbProviderId.ToString("D"), applied.Request.ProviderId);
        Assert.Equal("841", applied.Request.ProviderItemId);
        Assert.Equal(review, applied.Request.ReviewItemId);
        Assert.Equal("Dune", applied.Request.RequiredFields["title"]);
        Assert.Equal("1984", applied.Request.RequiredFields["year"]);
        Assert.Equal("Denis Villeneuve", applied.Request.RequiredFields["director"]);
        Assert.Equal("841", applied.Request.BridgeIds["tmdb_id"]);
        Assert.Equal("https://image.tmdb.org/t/p/w500/841.jpg", applied.Request.CoverUrl);

        // The row has left the list, whether or not the write closed it itself.
        var list = await _service.ListAsync(null, true, 0, 100);
        Assert.DoesNotContain(list.Items, row => row.Key == $"review:{review:D}");
        Assert.Equal(ReviewStatus.Resolved, (await _reviews.GetByIdAsync(review))!.Status);
        Assert.Contains(publisher.Events, e => e == SignalREvents.ReviewItemResolved);
    }

    [Fact]
    public async Task PickingTheOneCandidateOfAReadyRow_AppliesItThroughTheSameAction()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous, RetailJson(("Dune", "2021", "438631", 0.78)));
        var (service, applier, _) = NewChooseService();
        var choiceId = (await RowOfAsync(review)).BestGuess!.ChoiceId!;

        var result = await service.ChooseAsync(review, choiceId, "user");

        Assert.Equal(ReviewChooseOutcome.Chosen, result.Outcome);
        Assert.Equal("438631", Assert.Single(applier.Retail).Request.ProviderItemId);
        Assert.Equal(ReviewStatus.Resolved, (await _reviews.GetByIdAsync(review))!.Status);
    }

    [Theory]
    [InlineData("Books", "book_identity", "author")]
    [InlineData("Audiobooks", "audiobook_identity", "author")]
    [InlineData("Comics", "issue", "author")]
    [InlineData("Music", "track", "artist")]
    public async Task APickedRetailChoice_UsesTheEditorsItemTargetForThatKindOfItem(string mediaType, string fieldGroup, string creatorKey)
    {
        var (review, _, _) = SeedChoiceItem(mediaType, ReviewTrigger.RetailMatchAmbiguous, RetailJson(("One", "2001", "1", 0.8), ("Two", "2002", "2", 0.7)));
        var (service, applier, _) = NewChooseService();

        await service.ChooseAsync(review, (await RowOfAsync(review)).Choices[0].Id, "user");

        var request = Assert.Single(applier.Retail).Request;
        Assert.Equal("item", request.TargetKind);
        Assert.Equal(fieldGroup, request.TargetFieldGroup);
        Assert.Equal("Denis Villeneuve", request.RequiredFields[creatorKey]);
    }

    [Fact]
    public async Task PickingASeries_PlacesTheItemOnThatSeries_AndResolvesTheItem()
    {
        var dune = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new object[]
        {
            new { series_qid = "Q1", series_label = "Dune Novels", media_type = "Books", membership_scope = "MainSequence", collection_id = dune },
            new { series_qid = "Q2", series_label = "Dune Chronicles", media_type = "Books", membership_scope = "MainSequence", collection_id = Guid.NewGuid() },
        });
        var (review, asset, _) = SeedChoiceItem("Books", ReviewTrigger.AmbiguousSeriesMembership, json);
        var (service, applier, _) = NewChooseService();
        var choice = (await RowOfAsync(review)).Choices.Single(c => c.Title == "Dune Novels");

        var result = await service.ChooseAsync(review, choice.Id, "user");

        Assert.Equal(ReviewChooseOutcome.Chosen, result.Outcome);
        Assert.Equal((asset, dune), Assert.Single(applier.Series));
        Assert.Equal(ReviewStatus.Resolved, (await _reviews.GetByIdAsync(review))!.Status);
    }

    [Fact]
    public async Task PickingALibraryType_ReclassifiesTheItem_AndResolvesTheItem()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { type = "Music", confidence = 0.55, reason = "extension" },
            new { type = "Audiobooks", confidence = 0.5, reason = "folder" },
        });
        var (review, asset, _) = SeedChoiceItem("Music", ReviewTrigger.AmbiguousMediaType, json);
        var (service, applier, _) = NewChooseService();
        var choice = (await RowOfAsync(review)).Choices.Single(c => c.Title == "Audiobooks");

        var result = await service.ChooseAsync(review, choice.Id, "user");

        Assert.Equal(ReviewChooseOutcome.Chosen, result.Outcome);
        Assert.Equal((asset, MediaType.Audiobooks), Assert.Single(applier.Types));
        Assert.Equal(ReviewStatus.Resolved, (await _reviews.GetByIdAsync(review))!.Status);
    }

    [Fact]
    public async Task AChoiceTheRowDoesNotOffer_IsRefused_AndNothingIsApplied()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dune", "2021", "1", 0.78), ("Dune", "1984", "2", 0.71)));
        var (service, applier, publisher) = NewChooseService();

        var unknown = await service.ChooseAsync(review, "retail:tmdb:999", "user");
        var other = await service.ChooseAsync(review, $"series:{Guid.NewGuid():N}", "user");

        Assert.Equal(ReviewChooseOutcome.UnknownChoice, unknown.Outcome);
        Assert.Equal(ReviewChooseOutcome.UnknownChoice, other.Outcome);
        Assert.Empty(applier.Retail);
        Assert.Empty(applier.Series);
        Assert.Empty(applier.Types);
        Assert.Equal(ReviewStatus.Pending, (await _reviews.GetByIdAsync(review))!.Status);
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task AnItemThatIsNoLongerPending_CannotBeChosenFor()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dune", "2021", "1", 0.78), ("Dune", "1984", "2", 0.71)));
        var choiceId = (await RowOfAsync(review)).Choices[0].Id;
        await _reviews.UpdateStatusAsync(review, ReviewStatus.Resolved, "user");
        var (service, applier, _) = NewChooseService();

        var result = await service.ChooseAsync(review, choiceId, "user");

        Assert.Equal(ReviewChooseOutcome.NotPending, result.Outcome);
        Assert.Empty(applier.Retail);
    }

    [Fact]
    public async Task AnUnknownReviewItem_IsReportedMissing()
    {
        var (service, _, _) = NewChooseService();

        var result = await service.ChooseAsync(Guid.NewGuid(), "retail:tmdb:1", "user");

        Assert.Equal(ReviewChooseOutcome.ReviewItemNotFound, result.Outcome);
    }

    [Fact]
    public async Task WhenTheEditorsWriteRefusesTheChoice_TheReasonIsReported_AndTheItemStaysInTheList()
    {
        var (review, _, _) = SeedChoiceItem("Movies", ReviewTrigger.RetailMatchAmbiguous,
            RetailJson(("Dune", "2021", "1", 0.78), ("Dune", "1984", "2", 0.71)));
        var (service, _, publisher) = NewChooseService(ReviewChoiceApplyResult.Failed(409, "This item's library never looks anything up."));

        var result = await service.ChooseAsync(review, (await RowOfAsync(review)).Choices[0].Id, "user");

        Assert.Equal(ReviewChooseOutcome.ApplyFailed, result.Outcome);
        Assert.Equal(409, result.FailureStatus);
        Assert.Equal("This item's library never looks anything up.", result.Message);
        Assert.Equal(ReviewStatus.Pending, (await _reviews.GetByIdAsync(review))!.Status);
        Assert.Empty(publisher.Events);
        var stillListed = await _service.ListAsync(null, true, 0, 100);
        Assert.Contains(stillListed.Items, row => row.Key == $"review:{review:D}");
    }

    private sealed class SpyApplier : IReviewChoiceApplier
    {
        public ReviewChoiceApplyResult Result { get; set; } = ReviewChoiceApplyResult.Ok;
        public List<(Guid EntityId, ReplaceRetailMatchRequestDto Request)> Retail { get; } = [];
        public List<(Guid EntityId, Guid CollectionId)> Series { get; } = [];
        public List<(Guid EntityId, MediaType Type)> Types { get; } = [];

        public Task<ReviewChoiceApplyResult> ApplyRetailMatchAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct)
        {
            Retail.Add((entityId, request));
            return Task.FromResult(Result);
        }

        public Task<ReviewChoiceApplyResult> PlaceOnSeriesAsync(Guid entityId, Guid collectionId, CancellationToken ct)
        {
            Series.Add((entityId, collectionId));
            return Task.FromResult(Result);
        }

        public Task<ReviewChoiceApplyResult> ReclassifyAsync(Guid entityId, MediaType mediaType, CancellationToken ct)
        {
            Types.Add((entityId, mediaType));
            return Task.FromResult(Result);
        }
    }

    private sealed class ChoicePublisher : IEventPublisher
    {
        public List<string> Events { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }
    }

    // Answers every request with a refusal so no picture is ever produced, and remembers which hosts were asked.
    private sealed class CountingHttpFactory : IHttpClientFactory
    {
        public List<string> Hosts { get; } = [];

        public HttpClient CreateClient(string name) => new(new RefusingHandler(Hosts));

        private sealed class RefusingHandler(List<string> hosts) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                hosts.Add(request.RequestUri!.Host);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
        }
    }
}
