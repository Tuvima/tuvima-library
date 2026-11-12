using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// Put back (Needs Review redesign): POST /review/{id}/reopen runs for real here over this test's data store, beside the review routes the
// Vault tests map. Only the live-update publisher is a hand-written spy; the activity log and canonical values are the real repositories.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    [Fact]
    public async Task Reopen_AKeptVaultItem_AnswersNotFound_AndStaysKept()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var kept = await InsertKeptReviewAsync(shape.Secret.AssetId);
        var spy = new SpyPublisher();
        var routes = await MapReopenRoutesAsync(spy);

        var result = await InvokeAsync(routes["ReopenReviewItem"], shape.Account, NewVaultSession(shape.Account), ("id", kept.ToString("D")));

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
        Assert.Equal(ReviewStatus.Dismissed, (await new ReviewQueueRepository(_database).GetByIdAsync(kept))!.Status);
        Assert.Empty(spy.Published);
    }

    [Fact]
    public async Task Reopen_AnItemThatWasNotKeptByAPerson_AnswersConflict()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var repository = new ReviewQueueRepository(_database);
        var pending = await InsertReviewAsync(shape.Normal.AssetId, ReviewStatus.Pending);
        var byEngine = await InsertReviewAsync(shape.Normal.WorkId, ReviewStatus.Pending, "Work");
        await repository.UpdateStatusAsync(byEngine, ReviewStatus.Dismissed, "reconciliation");
        var spy = new SpyPublisher();
        var routes = await MapReopenRoutesAsync(spy);
        var session = NewVaultSession(shape.Account);

        foreach (var id in new[] { pending, byEngine })
        {
            var result = await InvokeAsync(routes["ReopenReviewItem"], shape.Account, session, ("id", id.ToString("D")));
            Assert.True(result.Status == StatusCodes.Status409Conflict, result.Body);
        }

        Assert.Empty(spy.Published);
    }

    [Fact]
    public async Task Reopen_WhenTheSameQuestionIsAlreadyPending_AnswersConflict_AndKeepsTheKeptRow()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kept = await InsertKeptReviewAsync(shape.Normal.AssetId);
        // The Engine raised the same question again after the person kept the first one.
        await InsertReviewAsync(shape.Normal.AssetId, ReviewStatus.Pending);
        var spy = new SpyPublisher();
        var routes = await MapReopenRoutesAsync(spy);

        var result = await InvokeAsync(routes["ReopenReviewItem"], shape.Account, NewVaultSession(shape.Account), ("id", kept.ToString("D")));

        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        var row = (await new ReviewQueueRepository(_database).GetByIdAsync(kept))!;
        Assert.Equal(ReviewStatus.Dismissed, row.Status);
        Assert.Equal(ReviewStatus.KeptByUser, row.ResolvedBy);
        Assert.Empty(spy.Published);
    }

    [Fact]
    public async Task Reopen_AKeptItemWhoseProblemWasSolved_AnswersConflict()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var repository = new ReviewQueueRepository(_database);
        var kept = await InsertKeptReviewAsync(shape.Normal.AssetId);
        // A match saved in the editor closes the item's review rows, kept ones included.
        await repository.ResolveAllByEntityAsync(shape.Normal.AssetId, "user:editor");
        var routes = await MapReopenRoutesAsync(new SpyPublisher());

        var result = await InvokeAsync(routes["ReopenReviewItem"], shape.Account, NewVaultSession(shape.Account), ("id", kept.ToString("D")));

        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        Assert.Equal(ReviewStatus.Resolved, (await repository.GetByIdAsync(kept))!.Status);
    }

    [Fact]
    public async Task Reopen_AKeptItem_IsBackInTheNormalList_AndOpenDashboardsAreToldAndTheActivityLogRecordsIt()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kept = await InsertKeptReviewAsync(shape.Normal.AssetId);
        var readService = new ReviewQueueReadService(_database);
        Assert.DoesNotContain(await readService.GetPendingAsync(50), item => item.Id == kept);
        Assert.Equal(1, await readService.GetKeptCountAsync(null));
        var spy = new SpyPublisher();
        var routes = await MapReopenRoutesAsync(spy);

        var result = await InvokeAsync(routes["ReopenReviewItem"], shape.Account, NewVaultSession(shape.Account), ("id", kept.ToString("D")));

        Assert.True(result.Status == StatusCodes.Status200OK, result.Body);
        Assert.Contains("reopened", result.Body, StringComparison.OrdinalIgnoreCase);
        var row = (await new ReviewQueueRepository(_database).GetByIdAsync(kept))!;
        Assert.Equal(ReviewStatus.Pending, row.Status);
        Assert.Null(row.ResolvedBy);
        Assert.Contains(await readService.GetPendingAsync(50), item => item.Id == kept);
        Assert.Equal(0, await readService.GetKeptCountAsync(null));

        var (eventName, payload) = Assert.Single(spy.Published);
        Assert.Equal(SignalREvents.ReviewItemCreated, eventName);
        var created = Assert.IsType<ReviewItemCreatedEvent>(payload);
        Assert.Equal(kept, created.ReviewItemId);
        Assert.Equal(shape.Normal.AssetId, created.EntityId);

        var activity = await new SystemActivityRepository(_database).GetRecentAsync(10);
        Assert.Contains(activity, entry => entry.ActionType == SystemActionType.ReviewItemCreated && entry.EntityId == shape.Normal.AssetId);
    }

    private async Task<Guid> InsertKeptReviewAsync(Guid entityId)
    {
        var id = await InsertReviewAsync(entityId, ReviewStatus.Pending);
        await new ReviewQueueRepository(_database).UpdateStatusAsync(id, ReviewStatus.Dismissed, ReviewStatus.KeptByUser);
        return id;
    }

    // The review routes with the real activity log and canonical values, and the spy in place of the live-update publisher.
    private Task<IReadOnlyDictionary<string, Microsoft.AspNetCore.Routing.RouteEndpoint>> MapReopenRoutesAsync(SpyPublisher spy) =>
        MapRoutesAsync(
            builder => builder.MapReviewEndpoints(),
            configure: web =>
            {
                web.Services.AddSingleton<IEventPublisher>(spy);
                web.Services.AddSingleton<ISystemActivityRepository>(new SystemActivityRepository(_database));
                web.Services.AddSingleton<ICanonicalValueRepository>(new CanonicalValueRepository(_database));
            });

    private sealed class SpyPublisher : IEventPublisher
    {
        public List<(string EventName, object Payload)> Published { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            Published.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }
}
