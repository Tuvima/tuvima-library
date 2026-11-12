using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Review;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// Picking a choice in Needs Review: POST /review/{id}/choose runs for real here over this test's data store, beside the other review routes
// the Vault tests map. The three writes the choice can run (retail match, Series, media type) are a hand-written spy.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly string MediaTypeChoices = JsonSerializer.Serialize(new object[]
    {
        new { type = "Music", confidence = 0.55, reason = "extension" },
        new { type = "Audiobooks", confidence = 0.5, reason = "folder" },
    });

    private async Task<Guid> InsertChoiceReviewAsync(Guid entityId, string status = ReviewStatus.Pending) =>
        await new ReviewQueueRepository(_database).InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.AmbiguousMediaType,
            Status = status,
            CandidatesJson = MediaTypeChoices,
            ReviewReadyAt = DateTimeOffset.UtcNow,
        });

    private Task<IReadOnlyDictionary<string, Microsoft.AspNetCore.Routing.RouteEndpoint>> MapChooseRoutesAsync(ChooseApplierSpy applier) =>
        MapRoutesAsync(
            builder => builder.MapReviewEndpoints(),
            configure: web =>
            {
                web.Services.AddSingleton<IEventPublisher>(new ChooseEventsSpy());
                web.Services.AddSingleton<ISystemActivityRepository>(new SystemActivityRepository(_database));
                web.Services.AddSingleton<ICanonicalValueRepository>(new CanonicalValueRepository(_database));
                web.Services.AddSingleton<IReviewChooseService>(_ => new ReviewChooseService(
                    new ReviewQueueReadService(_database), new ReviewQueueRepository(_database), applier,
                    new CanonicalValueRepository(_database), new SystemActivityRepository(_database), new ChooseEventsSpy(),
                    NullLogger<ReviewChooseService>.Instance));
            });

    [Fact]
    public async Task Choose_AnOfferedChoice_IsAppliedAndTheItemIsResolved()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var review = await InsertChoiceReviewAsync(shape.Normal.AssetId);
        var applier = new ChooseApplierSpy();
        var routes = await MapChooseRoutesAsync(applier);

        var result = await InvokeAsync(routes["ChooseReviewItem"], shape.Account, NewVaultSession(shape.Account),
            ("id", review.ToString("D")), body: """{"choice_id":"media_type:Audiobooks"}""");

        Assert.True(result.Status == StatusCodes.Status200OK, result.Body);
        using var json = JsonDocument.Parse(result.Body);
        Assert.True(json.RootElement.GetProperty("chosen").GetBoolean());
        Assert.Equal(review, json.RootElement.GetProperty("review_item_id").GetGuid());
        Assert.Equal("media_type:Audiobooks", json.RootElement.GetProperty("choice_id").GetString());
        Assert.Equal("media_type", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal((shape.Normal.AssetId, MediaType.Audiobooks), Assert.Single(applier.Types));
        Assert.Equal(ReviewStatus.Resolved, (await new ReviewQueueRepository(_database).GetByIdAsync(review))!.Status);
    }

    [Fact]
    public async Task Choose_AChoiceTheItemDoesNotOffer_AnswersBadRequest_AndAppliesNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var review = await InsertChoiceReviewAsync(shape.Normal.AssetId);
        var applier = new ChooseApplierSpy();
        var routes = await MapChooseRoutesAsync(applier);
        var session = NewVaultSession(shape.Account);

        foreach (var body in new[] { """{"choice_id":"media_type:TV"}""", """{"choice_id":"nonsense"}""", """{"choice_id":""}""", "{}" })
        {
            var result = await InvokeAsync(routes["ChooseReviewItem"], shape.Account, session, ("id", review.ToString("D")), body: body);
            Assert.True(result.Status == StatusCodes.Status400BadRequest, $"{body}: {result.Body}");
        }

        Assert.Empty(applier.Types);
        Assert.Equal(ReviewStatus.Pending, (await new ReviewQueueRepository(_database).GetByIdAsync(review))!.Status);
    }

    [Fact]
    public async Task Choose_AnItemThatIsNoLongerPending_AnswersConflict_AndAppliesNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var done = await InsertChoiceReviewAsync(shape.Normal.AssetId, ReviewStatus.Resolved);
        var applier = new ChooseApplierSpy();
        var routes = await MapChooseRoutesAsync(applier);

        var result = await InvokeAsync(routes["ChooseReviewItem"], shape.Account, NewVaultSession(shape.Account),
            ("id", done.ToString("D")), body: """{"choice_id":"media_type:Audiobooks"}""");

        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        Assert.Empty(applier.Types);
    }

    [Fact]
    public async Task Choose_WhenTheEditorsWriteRefusesTheChoice_AnswersWithThatReason_AndTheItemStaysPending()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var review = await InsertChoiceReviewAsync(shape.Normal.AssetId);
        var applier = new ChooseApplierSpy { Result = ReviewChoiceApplyResult.Failed(StatusCodes.Status409Conflict, "This item's library never looks anything up.") };
        var routes = await MapChooseRoutesAsync(applier);

        var result = await InvokeAsync(routes["ChooseReviewItem"], shape.Account, NewVaultSession(shape.Account),
            ("id", review.ToString("D")), body: """{"choice_id":"media_type:Music"}""");

        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        Assert.Contains("never looks anything up", result.Body, StringComparison.Ordinal);
        Assert.Equal(ReviewStatus.Pending, (await new ReviewQueueRepository(_database).GetByIdAsync(review))!.Status);
    }

    private sealed class ChooseApplierSpy : IReviewChoiceApplier
    {
        public ReviewChoiceApplyResult Result { get; set; } = ReviewChoiceApplyResult.Ok;
        public List<(Guid EntityId, MediaType Type)> Types { get; } = [];

        public Task<ReviewChoiceApplyResult> ApplyRetailMatchAsync(Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<ReviewChoiceApplyResult> PlaceOnSeriesAsync(Guid entityId, Guid collectionId, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<ReviewChoiceApplyResult> ReclassifyAsync(Guid entityId, MediaType mediaType, CancellationToken ct)
        {
            Types.Add((entityId, mediaType));
            return Task.FromResult(Result);
        }
    }

    private sealed class ChooseEventsSpy : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }
}
