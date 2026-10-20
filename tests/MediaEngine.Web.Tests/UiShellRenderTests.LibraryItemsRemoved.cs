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
    public async Task APlayerPlayingADeletedTitle_StopsAndTheLayoutToastsThatItIsNoLongerInTheLibrary()
    {
        var cut = RenderOpenAccountMenu(
            ShellAuthority(true, ["settings.administration"], ["access.manage"]), AdultProfile("Shy"));
        var playback = Services.GetRequiredService<PlaybackSessionController>();
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        var toasts = Services.GetRequiredService<AppToastService>();
        var song = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Playing now", StreamUrl = "stream://now" };
        await cut.InvokeAsync(() => playback.PlayQueueItemAsync(song));

        // The layout starts listening once its start-up finishes, so raise the event until it is heard.
        cut.WaitForAssertion(() =>
        {
            cut.InvokeAsync(() => orchestrator.RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([song.WorkId], [song.AssetId!.Value])));
            Assert.Contains(toasts.Toasts, toast => toast.Message == "This item is no longer in your library.");
        });
        Assert.True(playback.IsDismissed);
        Assert.Null(playback.CurrentItem);
        Assert.Single(toasts.Toasts, toast => toast.Message == "This item is no longer in your library.");
    }
}
