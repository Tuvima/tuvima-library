using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed class ContextSidebarCoordinatorTests
{
    [Fact]
    public async Task ExplicitReplacementPublishesImmediatelyAndDisplacesOnlyPriorOwnerLease()
    {
        var coordinator = new ContextSidebarCoordinator();
        var displaced = 0;
        Guid prior = Guid.Empty;
        prior = await coordinator.OpenExplicitAsync(ContextSidebarOwner.Ingestion, "Ingestion", 340, Body,
            displaced: () =>
            {
                displaced++;
                Assert.Equal(ContextSidebarOwner.Playback, coordinator.Current?.Owner);
                Assert.NotEqual(prior, coordinator.Current?.LeaseId);
                return Task.CompletedTask;
            });
        var changed = 0;
        coordinator.Changed += () => changed++;
        var current = await coordinator.OpenExplicitAsync(ContextSidebarOwner.Playback, "Playback", 420, Body);

        Assert.NotEqual(prior, current);
        Assert.Equal(ContextSidebarOwner.Playback, coordinator.Current?.Owner);
        Assert.False(coordinator.Release(prior));
        Assert.Equal(1, displaced);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task UpdateAndReleaseRequireTheExactLiveLease()
    {
        var coordinator = new ContextSidebarCoordinator();
        var id = await coordinator.OpenExplicitAsync(ContextSidebarOwner.Playback, "Queue", 600, Body);

        Assert.False(coordinator.Update(Guid.NewGuid(), "Wrong", 400, Body));
        Assert.True(coordinator.Update(id, "History", 600, Body));
        Assert.Equal("History", coordinator.Current?.Title);
        Assert.Equal(480, coordinator.Current?.Width);
        Assert.True(coordinator.Release(id));
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task LateInitialRestoreCannotReplaceAnExplicitOwner()
    {
        var coordinator = new ContextSidebarCoordinator();
        var explicitId = await coordinator.OpenExplicitAsync(ContextSidebarOwner.Playback, "Queue", 340, Body);

        Assert.False(coordinator.TryInitialRestore(ContextSidebarOwner.Ingestion, "Ingestion", 340, Body));
        Assert.Equal(explicitId, coordinator.Current?.LeaseId);
    }

    [Fact]
    public async Task RestoredOwnerKeepsItsDisplacementCallback()
    {
        var coordinator = new ContextSidebarCoordinator();
        var cleared = false;
        Assert.True(coordinator.TryInitialRestore(ContextSidebarOwner.Ingestion, "Ingestion", 340, Body,
            displaced: () => { cleared = true; return Task.CompletedTask; }));

        await coordinator.OpenExplicitAsync(ContextSidebarOwner.Playback, "Queue", 340, Body);

        Assert.True(cleared);
        Assert.Equal(ContextSidebarOwner.Playback, coordinator.Current?.Owner);
    }

    private static readonly RenderFragment Body = builder => builder.AddContent(0, "body");
}
