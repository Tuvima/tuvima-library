using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The video Bookmarks panel quick list (video-player-controls-and-bookmarks D6, D7) and the bubble markers on the rail (D8).
public sealed class VideoBookmarkQuickListTests : AsyncBunitContext
{
    public VideoBookmarkQuickListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static PlaybackRailBookmark Mark(double seconds, string? note = null, bool global = false, string? pack = null) =>
        new(Guid.NewGuid(), seconds, global, note, null, pack);

    [Fact]
    public async Task TheAddRow_SavesAtTheTimeThePanelOpened_WithTheNoteTyped_AndClearsTheNote()
    {
        (double Seconds, string? Note)? saved = null;
        var cut = Render<VideoBookmarkQuickList>(parameters => parameters
            .Add(component => component.Position, 822d)
            .Add(component => component.Add, (Func<double, string?, Task<bool>>)((seconds, note) => { saved = (seconds, note); return Task.FromResult(true); })));
        Assert.Contains("Add bookmark here · 13:42", cut.Markup);

        cut.Find("input").Change("The reveal");
        await cut.Find("button.video-bookmark-quick__add").ClickAsync();

        Assert.Equal((822d, "The reveal"), saved);
        Assert.Equal(string.Empty, cut.Find("input").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task AFailedSave_TurnsTheAddRowIntoATryAgain_InPlace()
    {
        var cut = Render<VideoBookmarkQuickList>(parameters => parameters
            .Add(component => component.Position, 60d)
            .Add(component => component.Add, (Func<double, string?, Task<bool>>)((_, _) => Task.FromResult(false))));

        await cut.Find("button.video-bookmark-quick__add").ClickAsync();

        Assert.Contains("Couldn’t save · Try again", cut.Find("button.video-bookmark-quick__add").TextContent);
    }

    [Fact]
    public void TheList_ShowsTheEmptyHint_OrTheBookmarks_WithOwnersAndOnlyPermittedDeletes()
    {
        var empty = Render<VideoBookmarkQuickList>(parameters => parameters.Add(component => component.Position, 0d));
        Assert.Contains("press B while watching", empty.Markup);

        var cut = Render<VideoBookmarkQuickList>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Current, 740d)
            .Add(component => component.Bookmarks, new[] { Mark(100, "Mine one"), Mark(750, "For all", global: true), Mark(900, pack: "Fan pack") }));

        var rows = cut.FindAll("li.video-bookmark-quick__row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("Mine", rows[0].TextContent);
        Assert.Contains("Everyone", rows[1].TextContent);
        Assert.Contains("Fan pack", rows[2].TextContent);
        Assert.Contains("is-near", rows[1].ClassName);
        Assert.Contains("Delete", rows[0].TextContent);
        Assert.DoesNotContain("Delete", rows[1].TextContent); // Everyone bookmarks: administrators only.
        Assert.DoesNotContain("Delete", rows[2].TextContent); // Packs are read-only.
    }

    [Fact]
    public async Task Delete_AsksInPlaceFirst_ThenDeletes()
    {
        var mark = Mark(100);
        PlaybackRailBookmark? deleted = null;
        var cut = Render<VideoBookmarkQuickList>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Bookmarks, new[] { mark })
            .Add(component => component.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(item => { deleted = item; return Task.FromResult(true); })));

        await cut.FindAll("button.video-bookmark-quick__action").First(button => button.TextContent == "Delete").ClickAsync();
        Assert.Null(deleted);
        await cut.FindAll("button.video-bookmark-quick__action").First(button => button.TextContent == "Delete?").ClickAsync();

        Assert.Equal(mark.Id, deleted!.Id);
    }

    [Fact]
    public void TheRail_ShowsTheNoticeLine_AndOffersDeleteOnlyOnPersonalBookmarks()
    {
        JSInterop.SetupModule("./js/playback-seek-rail.js");
        var personal = Mark(100, "Mine");
        var cut = Render<PlaybackSeekRail>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Duration, 600d)
            .Add(component => component.Notice, "Bookmark added 1:40")
            .Add(component => component.Bookmarks, new[] { personal, Mark(200, global: true) })
            .Add(component => component.BookmarkDelete, EventCallback.Factory.Create<PlaybackRailBookmark>(this, _ => { })));
        Assert.Contains("Bookmark added 1:40", cut.Find(".playback-seek-rail__notice").TextContent);

        cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].MouseEnter();
        Assert.Contains(cut.FindAll(".playback-seek-rail__quicklook button"), button => button.TextContent == "Delete");
        cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].MouseLeave();
        cut.FindAll(".playback-seek-rail__bookmark-wrap")[1].MouseEnter();
        Assert.DoesNotContain(cut.FindAll(".playback-seek-rail__quicklook button"), button => button.TextContent.StartsWith("Delete"));
    }
}
