using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The video bookmark pane (player-controls-v3 D10) and the bubble markers on the bar (D8a).
public sealed class VideoBookmarkPaneTests : AsyncBunitContext
{
    public VideoBookmarkPaneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static PlaybackRailBookmark Mark(double seconds, string? note = null, bool global = false, string? label = null, string? owner = null) =>
        new(Guid.NewGuid(), seconds, global, note, null, label, owner);

    [Fact]
    public async Task TheAddRow_SavesAtTheTimeThePaneOpened_WithTheNoteTyped_AndClearsTheNote()
    {
        (double Seconds, string? Note)? saved = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Position, 822d)
            .Add(component => component.Add, (Func<double, string?, Task<bool>>)((seconds, note) => { saved = (seconds, note); return Task.FromResult(true); })));
        Assert.Contains("+ Bookmark at 13:42", cut.Markup);

        cut.Find("input.video-bookmark-pane__note").Change("The reveal");
        await cut.Find("button.video-bookmark-pane__add").ClickAsync();

        Assert.Equal((822d, "The reveal"), saved);
        Assert.Equal(string.Empty, cut.Find("input.video-bookmark-pane__note").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task AFailedSave_TurnsTheAddRowIntoATryAgain_InPlace()
    {
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Position, 60d)
            .Add(component => component.Add, (Func<double, string?, Task<bool>>)((_, _) => Task.FromResult(false))));

        await cut.Find("button.video-bookmark-pane__add").ClickAsync();

        Assert.Contains("Couldn’t save · Try again", cut.Find("button.video-bookmark-pane__add").TextContent);
    }

    [Fact]
    public void WithNoBookmarks_ThePaneSaysSoAndOffersNoFilter()
    {
        var cut = Render<VideoBookmarkPane>(parameters => parameters.Add(component => component.Position, 0d));

        Assert.Contains("No bookmarks yet.", cut.Find(".video-bookmark-pane__empty").TextContent);
        Assert.DoesNotContain("press B", cut.Markup);
        Assert.Empty(cut.FindAll(".video-bookmark-pane__filter"));
    }

    [Fact]
    public void TheFilter_AppearsOnlyWhenTheTitleHasBothKinds_AndNarrowsTheList()
    {
        var onlyMine = Render<VideoBookmarkPane>(parameters => parameters.Add(component => component.Bookmarks, new[] { Mark(10) }));
        Assert.Empty(onlyMine.FindAll(".video-bookmark-pane__filter"));

        var both = new[] { Mark(10, "Mine one"), Mark(20, global: true, label: "Famous scene") };
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, both)
            .Add(component => component.Filter, PlaybackPreferenceValues.Global));

        Assert.Equal(["Mine (1)", "Global (1)"], cut.FindAll(".video-bookmark-pane__filter button").Select(button => button.TextContent.Trim()).ToArray());
        var row = Assert.Single(cut.FindAll("li.video-bookmark-pane__row"));
        Assert.Contains("Famous scene", row.TextContent);
        Assert.Contains("is-global", row.QuerySelector(".video-bookmark-pane__time")!.ClassName);
    }

    [Fact]
    public async Task ChoosingAFilter_IsReportedSoTheHostCanRememberIt()
    {
        string? chosen = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, new[] { Mark(10), Mark(20, global: true) })
            .Add(component => component.FilterChanged, EventCallback.Factory.Create<string>(this, value => chosen = value)));

        await cut.FindAll(".video-bookmark-pane__filter button").First(button => button.TextContent.Trim().StartsWith("Mine", StringComparison.Ordinal)).ClickAsync();

        Assert.Equal(PlaybackPreferenceValues.Mine, chosen);
    }

    [Fact]
    public void TheList_ShowsOwners_AndOnlyPermittedDeletes()
    {
        var marks = new[] { Mark(100, "Mine one"), Mark(750, "For all", global: true) };
        var mine = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Current, 740d)
            .Add(component => component.Filter, PlaybackPreferenceValues.Mine)
            .Add(component => component.Bookmarks, marks)
            .Add(component => component.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));
        var mineRow = Assert.Single(mine.FindAll("li.video-bookmark-pane__row"));
        Assert.Contains("Mine", mineRow.TextContent);
        Assert.Contains("Delete", mineRow.TextContent);

        var global = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Current, 740d)
            .Add(component => component.Filter, PlaybackPreferenceValues.Global)
            .Add(component => component.Bookmarks, marks)
            .Add(component => component.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));
        var globalRow = Assert.Single(global.FindAll("li.video-bookmark-pane__row"));
        Assert.Contains("Global", globalRow.TextContent);
        Assert.Contains("is-near", globalRow.ClassName);
        Assert.DoesNotContain("Delete", globalRow.TextContent); // Global bookmarks are read-only for everyone but an administrator.
        Assert.DoesNotContain("Edit", globalRow.TextContent);
    }

    [Fact]
    public void ThePane_HasAHeaderWithTheCount_TheTimelineSwitch_TheSearch_AndTheFooterLine()
    {
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, new[] { Mark(10, label: "Opening aerial shot"), Mark(300, label: "Return to this explanation") }));

        Assert.Equal("2", cut.Find(".video-bookmark-pane__count").TextContent.Trim());
        Assert.Contains("Show on timeline", cut.Markup);
        Assert.NotEmpty(cut.FindAll("input.video-bookmark-pane__search"));
        Assert.Contains("Select a time to jump to that moment.", cut.Find(".video-bookmark-pane__hint").TextContent);
        Assert.Equal(2, cut.FindAll("li.video-bookmark-pane__row").Count);

        cut.Find("input.video-bookmark-pane__search").Change("aerial");

        var row = Assert.Single(cut.FindAll("li.video-bookmark-pane__row"));
        Assert.Contains("Opening aerial shot", row.TextContent);
    }

    [Fact]
    public async Task TheCloseButton_ClosesThePanel_WhenTheHostGivesAHandler()
    {
        var closed = false;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        await cut.Find("button.video-bookmark-pane__close").ClickAsync();

        Assert.True(closed);
    }

    [Fact]
    public async Task Delete_AsksInPlaceFirst_ThenDeletes()
    {
        var mark = Mark(100);
        PlaybackRailBookmark? deleted = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, new[] { mark })
            .Add(component => component.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(item => { deleted = item; return Task.FromResult(true); })));

        await cut.FindAll("button.video-bookmark-pane__action").First(button => button.TextContent == "Delete").ClickAsync();
        Assert.Null(deleted);
        await cut.FindAll("button.video-bookmark-pane__action").First(button => button.TextContent == "Delete?").ClickAsync();

        Assert.Equal(mark.Id, deleted!.Id);
    }

    [Fact]
    public void Everyones_IsOfferedToAdministratorsOnDesktopAndTabletOnly()
    {
        var mine = new[] { Mark(10) };
        var admin = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, mine).Add(component => component.IsAdministrator, true).Add(component => component.AllowEveryones, true));
        Assert.Contains(admin.FindAll(".video-bookmark-pane__filter button"), button => button.TextContent.Trim() == "Everyone’s");

        var vaultOrPhone = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, mine).Add(component => component.IsAdministrator, true).Add(component => component.AllowEveryones, false));
        Assert.DoesNotContain("Everyone’s", vaultOrPhone.Markup);

        var member = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, mine).Add(component => component.IsAdministrator, false).Add(component => component.AllowEveryones, true));
        Assert.DoesNotContain("Everyone’s", member.Markup);
    }

    [Fact]
    public async Task Promote_IsImmediate_WithNoConfirmation_AndTheRowGlows()
    {
        var theirs = Mark(300, "Their note", owner: "Mira");
        PlaybackRailBookmark? promoted = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, new[] { Mark(10) })
            .Add(component => component.Everyones, new[] { theirs })
            .Add(component => component.IsAdministrator, true)
            .Add(component => component.AllowEveryones, true)
            .Add(component => component.Filter, PlaybackPreferenceValues.Everyones)
            .Add(component => component.Promote, (Func<PlaybackRailBookmark, string?, Task<bool>>)((item, _) => { promoted = item; return Task.FromResult(true); })));

        var row = Assert.Single(cut.FindAll("li.video-bookmark-pane__row"));
        Assert.Contains("Mira", row.TextContent);
        Assert.DoesNotContain("Delete", row.TextContent); // Other people's personal bookmarks are theirs to delete.

        var click = cut.FindAll("button.video-bookmark-pane__action").First(button => button.TextContent == "Promote").ClickAsync();
        cut.WaitForAssertion(() => Assert.Contains("is-glow", cut.Find("li.video-bookmark-pane__row").ClassName));
        await click;

        Assert.Equal(theirs.Id, promoted!.Id);
    }

    [Fact]
    public async Task AnAdministrator_EditsAGlobalBookmarkInPlace_AndCanDeleteIt()
    {
        var global = Mark(500, "Old note", global: true, label: "Old name");
        (string? Label, string? Note, string? Description)? sent = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.Bookmarks, new[] { global })
            .Add(component => component.IsAdministrator, true)
            .Add(component => component.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true)))
            .Add(component => component.Update, (Func<PlaybackRailBookmark, string?, string?, string?, Task<bool>>)((_, label, note, description) =>
            {
                sent = (label, note, description);
                return Task.FromResult(true);
            })));
        Assert.Contains(cut.FindAll("button.video-bookmark-pane__action"), button => button.TextContent == "Delete");

        await cut.FindAll("button.video-bookmark-pane__action").First(button => button.TextContent == "Edit").ClickAsync();
        cut.FindAll(".video-bookmark-pane__edit input")[0].Change("The bridge");
        cut.FindAll(".video-bookmark-pane__edit input")[2].Change("Look left.");
        await cut.FindAll("button.video-bookmark-pane__action").First(button => button.TextContent == "Save").ClickAsync();

        Assert.Equal(("The bridge", "Old note", "Look left."), sent);
        Assert.Empty(cut.FindAll(".video-bookmark-pane__edit"));
    }

    [Fact]
    public async Task TheBarSwitch_ReportsItsChoice_AndFindBookmarksOpensTheHostsView_WithABackArrowToTheList()
    {
        bool? shown = null;
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.ShowOnBar, false)
            .Add(component => component.ShowOnBarChanged, EventCallback.Factory.Create<bool>(this, value => shown = value))
            .Add(component => component.AllowFind, true)
            .Add(component => component.FindView, (RenderFragment<EventCallback>)(close => builder =>
            {
                builder.AddMarkupContent(0, "<p class=\"find-slot\">Finding</p>");
                builder.OpenElement(1, "button");
                builder.AddAttribute(2, "class", "find-back");
                builder.AddAttribute(3, "onclick", close);
                builder.CloseElement();
            })));

        Assert.Contains("Show on timeline", cut.Markup);
        await cut.Find("input[role=switch]").ChangeAsync(true);
        Assert.True(shown);

        await cut.Find("button.video-bookmark-pane__find").ClickAsync();
        Assert.Equal("Finding", cut.Find(".find-slot").TextContent);
        Assert.Empty(cut.FindAll(".video-bookmark-pane__add"));

        await cut.Find(".find-back").ClickAsync();
        Assert.Empty(cut.FindAll(".find-slot"));
        Assert.NotEmpty(cut.FindAll(".video-bookmark-pane__add"));
    }

    [Fact]
    public void FindBookmarks_IsNotOffered_WhenTheHostDoesNotAllowIt()
    {
        var cut = Render<VideoBookmarkPane>(parameters => parameters
            .Add(component => component.AllowFind, false)
            .Add(component => component.FindView, (RenderFragment<EventCallback>)(_ => builder => builder.AddContent(0, "x"))));

        Assert.Empty(cut.FindAll("button.video-bookmark-pane__find"));
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
