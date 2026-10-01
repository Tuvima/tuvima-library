using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorNavigationGuardTests
{
    [Fact]
    public void DirtyEditor_HoldsLatestRouteUntilExplicitDecision()
    {
        var guard = new MediaEditorNavigationGuard();

        Assert.True(guard.Intercept("http://localhost/watch", hasUnsavedChanges: true));
        Assert.True(guard.Intercept("http://localhost/read", hasUnsavedChanges: true));
        Assert.Equal("http://localhost/read", guard.PendingLocation);

        guard.Stay();
        Assert.Null(guard.PendingLocation);
        Assert.True(guard.Intercept("http://localhost/watch", hasUnsavedChanges: true));
    }

    [Fact]
    public void ApprovedRoute_IsAllowedExactlyOnce_WithoutAllowingAnotherRoute()
    {
        var guard = new MediaEditorNavigationGuard();
        const string destination = "http://localhost/watch";

        Assert.True(guard.Intercept(destination, hasUnsavedChanges: true));
        Assert.Equal(destination, guard.Approve());
        Assert.False(guard.Intercept(destination, hasUnsavedChanges: true));
        Assert.True(guard.Intercept(destination, hasUnsavedChanges: true));
        Assert.True(guard.Intercept("http://localhost/read", hasUnsavedChanges: true));
    }

    [Fact]
    public void CleanEditor_DoesNotHoldNavigation()
    {
        var guard = new MediaEditorNavigationGuard();

        Assert.False(guard.Intercept("http://localhost/watch", hasUnsavedChanges: false));
        Assert.Null(guard.PendingLocation);
    }
}
