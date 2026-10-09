using Bunit;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SongActionsMenuTests : AsyncBunitContext
{
    public SongActionsMenuTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        ComponentFactories.AddStub<PersonalStatusMenuItems>();
        Render<AppPopoverHost>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverflowRatingIsAvailableOnlyWhenDirectRatingIsAbsent(bool showRating)
    {
        var cut = Render<SongActions>(p => p
            .Add(c => c.Item, new ListenQueueItem { WorkId = Guid.NewGuid(), Title = "Song" })
            .Add(c => c.ShowRating, showRating));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPopover>().Instance.OpenAsync(true));

        Assert.Equal(showRating ? 0 : 1, cut.FindAll("button[role=menuitemcheckbox]")
            .Count(e => e.TextContent.Contains("I like this song")));
        Assert.Equal(showRating ? 0 : 1, cut.FindAll("button[role=menuitemcheckbox]")
            .Count(e => e.TextContent.Contains("I don't like this song")));
        Assert.Contains(cut.FindAll("button[role=menuitem]"), e => e.TextContent.Contains("Add to playlist"));
        Assert.Contains(cut.FindAll("button[role=menuitem]"), e => e.TextContent.Contains("Add to Favorites"));
        Assert.Equal(showRating ? 1 : 0, cut.FindAll("button[aria-label=Rate]").Count);
    }
}
