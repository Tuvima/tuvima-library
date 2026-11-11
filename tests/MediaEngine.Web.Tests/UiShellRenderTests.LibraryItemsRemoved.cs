using Bunit;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>When a title is deleted from the library, the real layout stops a player that is playing it and says why.</summary>
public sealed partial class UiShellRenderTests
{
    [Fact]
    public async Task APlayerPlayingADeletedTitle_StopsAndTheLayoutSaysInPlaceThatItIsNoLongerInTheLibrary()
    {
        var cut = RenderOpenAccountMenu(
            ShellAuthority(true, ["settings.administration"], ["access.manage"]), AdultProfile("Shy"));
        var playback = Services.GetRequiredService<PlaybackSessionController>();
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        var song = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Playing now", StreamUrl = "stream://now" };
        await cut.InvokeAsync(() => playback.PlayQueueItemAsync(song));

        // The layout starts listening once its start-up finishes, so raise the event until it is heard.
        cut.WaitForAssertion(() =>
        {
            cut.InvokeAsync(() => orchestrator.RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([song.WorkId], [song.AssetId!.Value])));
            Assert.Equal("This item is no longer in your library.", cut.Find("[data-layout-notice] span").TextContent.Trim());
        });
        Assert.True(playback.IsDismissed);
        Assert.Null(playback.CurrentItem);

        // The notice stays until it is closed.
        cut.Find("[data-layout-notice] button").Click();
        Assert.Empty(cut.FindAll("[data-layout-notice]"));
    }
}
