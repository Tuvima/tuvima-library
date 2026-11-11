using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D21, D21a, D22 and acceptance A11 (mockup screen 11): one Bookmarks panel for every medium. Everyone is the default list; the
/// maker is on Everyone rows, which have no actions; Mine rows have Edit and a two-click Delete; the markers follow the list shown. Which list a
/// bookmark belongs to comes from the Engine (IsYours, InEveryone); the panel never compares profile ids.
/// </summary>
public sealed class BookmarksPanelTests : AsyncBunitContext
{
    private readonly PlaybackTransientToolCoordinator _tools = new();

    public BookmarksPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_tools);
        Render<AppPopoverHost>();
    }

    private static PlaybackRailBookmark Everyone(double seconds, string? note = null, string? maker = "Alex", string? frame = null) =>
        new(Guid.NewGuid(), seconds, false, note, null, null, maker, IsYours: false, InEveryone: true, FrameUrl: frame);

    private static PlaybackRailBookmark Mine(double seconds, string? note = null, bool inEveryone = false) =>
        new(Guid.NewGuid(), seconds, false, note, null, null, "Me", IsYours: true, InEveryone: inEveryone);

    private IRenderedComponent<BookmarksPanel> RenderPanel(IReadOnlyList<PlaybackRailBookmark> bookmarks, Action<ComponentParameterCollectionBuilder<BookmarksPanel>>? configure = null) =>
        Render<BookmarksPanel>(parameters =>
        {
            parameters.Add(p => p.Bookmarks, bookmarks).Add(p => p.Position, 2052d);
            configure?.Invoke(parameters);
        });

    [Fact]
    public void Everyone_IsTheDefaultList_AndTheChoiceShowsTheCounts()
    {
        var cut = RenderPanel([Everyone(100, "The first clue"), Everyone(200, "The hidden room"), Mine(300, "My own")]);

        var choice = cut.Find("[data-bookmarks-choice]");
        Assert.Contains("Everyone · 2", choice.TextContent);
        Assert.Contains("Mine · 1", choice.TextContent);
        var rows = cut.FindAll("[data-bookmark-id]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("The first clue", rows[0].TextContent);
        Assert.DoesNotContain("My own", cut.Markup);
    }

    [Fact]
    public void TheAddButton_ShowsTheTimeItWillSave_WithAPlusAndNoMaterialIcon()
    {
        var cut = RenderPanel([]);

        var add = cut.Find("[data-bookmarks-add]");
        Assert.Contains("Bookmark at 34:12", add.TextContent);
        Assert.Empty(cut.FindAll(".material-icons, .material-symbols-outlined"));
    }

    [Fact]
    public async Task Pressing_Add_SavesAtOnceAtTheShownTime_WithNoNote()
    {
        var saved = new List<(double Seconds, string? Note)>();
        var cut = RenderPanel([], p => p.Add(x => x.Add, (Func<double, string?, Task<bool>>)((seconds, note) => { saved.Add((seconds, note)); return Task.FromResult(true); })));

        await cut.Find("[data-bookmarks-add]").ClickAsync();

        Assert.Equal([(2052d, (string?)null)], saved);
    }

    [Fact]
    public async Task TheNoteField_ExpandsOnDemand_AndItsTextIsSavedWithTheBookmark()
    {
        (double Seconds, string? Note)? saved = null;
        var cut = RenderPanel([], p => p.Add(x => x.Add, (Func<double, string?, Task<bool>>)((seconds, note) => { saved = (seconds, note); return Task.FromResult(true); })));
        Assert.Empty(cut.FindAll("[data-bookmarks-note]"));

        await cut.Find("[data-bookmarks-note-toggle]").ClickAsync();
        Assert.Single(cut.FindAll("[data-bookmarks-note]"));
        cut.Find("[data-bookmarks-note] textarea").Input("The reveal");
        await cut.Find("[data-bookmarks-add]").ClickAsync();

        Assert.Equal((2052d, "The reveal"), saved);
        Assert.Empty(cut.FindAll("[data-bookmarks-note]"));
    }

    [Fact]
    public async Task AFailedSave_TurnsTheButtonIntoATryAgain()
    {
        var cut = RenderPanel([], p => p.Add(x => x.Add, (Func<double, string?, Task<bool>>)((_, _) => Task.FromResult(false))));

        await cut.Find("[data-bookmarks-add]").ClickAsync();

        Assert.Contains("Couldn’t save · Try again", cut.Find("[data-bookmarks-add]").TextContent);
    }

    [Fact]
    public void AnEveryoneRow_ShowsTheMaker_AndHasNoActions()
    {
        var cut = RenderPanel([Everyone(100, "The first clue", maker: "Morgan"), Mine(150, "Mine", inEveryone: true)]);

        var rows = cut.FindAll("[data-bookmark-id]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Morgan", rows[0].TextContent);
        Assert.Empty(cut.FindAll("[data-bookmark-edit]"));
        Assert.Empty(cut.FindAll("[data-bookmark-delete]"));
    }

    [Fact]
    public void AMineRow_HasEditAndDelete_AndNoMaker()
    {
        var cut = RenderPanel([Everyone(100, "Theirs"), Mine(300, "Mine")], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Edit, (Func<PlaybackRailBookmark, string?, string?, Task<bool>>)((_, _, _) => Task.FromResult(true)))
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));

        var row = Assert.Single(cut.FindAll("[data-bookmark-id]"));
        Assert.Contains("Mine", row.TextContent);
        Assert.Single(cut.FindAll("[data-bookmark-edit]"));
        Assert.Single(cut.FindAll("[data-bookmark-delete]"));
    }

    [Fact]
    public async Task Delete_NeedsASecondClick_AndKeepLeavesTheBookmark()
    {
        var deleted = new List<Guid>();
        var mine = Mine(300, "Mine");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(bookmark => { deleted.Add(bookmark.Id); return Task.FromResult(true); })));

        await cut.Find("[data-bookmark-delete]").ClickAsync();
        Assert.Empty(deleted);
        Assert.Single(cut.FindAll("[data-bookmark-delete-confirm]"));

        await cut.FindAll(".bmk__action").Last().ClickAsync(); // Keep
        Assert.Empty(deleted);
        Assert.Empty(cut.FindAll("[data-bookmark-delete-confirm]"));

        await cut.Find("[data-bookmark-delete]").ClickAsync();
        await cut.Find("[data-bookmark-delete-confirm]").ClickAsync();
        Assert.Equal([mine.Id], deleted);
    }

    [Fact]
    public async Task Edit_RenamesAndChangesTheNote_OfAMineRow()
    {
        (Guid Id, string? Name, string? Note)? edited = null;
        var mine = Mine(300, "Old note");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Edit, (Func<PlaybackRailBookmark, string?, string?, Task<bool>>)((bookmark, name, note) => { edited = (bookmark.Id, name, note); return Task.FromResult(true); })));

        await cut.Find("[data-bookmark-edit]").ClickAsync();
        var form = cut.Find("[data-bookmark-edit-form]");
        form.QuerySelector("input[type=text]")!.Input("The reveal");
        form.QuerySelector("textarea")!.Input("Watch the window");
        await cut.Find("[data-bookmark-edit-save]").ClickAsync();

        Assert.Equal((mine.Id, "The reveal", "Watch the window"), edited);
        Assert.Empty(cut.FindAll("[data-bookmark-edit-form]"));
    }

    // bUnit gives an element a new reference on every render, so the focus calls are counted rather than matched to one element; the note box is
    // the only other focus target and none of these flows opens it.
    private int FocusCalls() => JSInterop.Invocations.Count(invocation => invocation.Identifier == "Blazor._internal.domWrapper.focus");

    [Fact]
    public async Task AFailedDelete_SaysSoOnThatRow_AndKeepsIt()
    {
        var mine = Mine(300, "Mine");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(false))));

        await cut.Find("[data-bookmark-delete]").ClickAsync();
        await cut.Find("[data-bookmark-delete-confirm]").ClickAsync();

        Assert.Contains("Couldn’t delete", cut.Find("[data-bookmark-delete-failed]").TextContent);
        Assert.Single(cut.FindAll("[data-bookmark-id]"));
    }

    [Fact]
    public async Task ADeleteThatThrows_IsLogged_AndShownAsFailed()
    {
        var mine = Mine(300, "Mine");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => throw new HttpRequestException("down"))));

        await cut.Find("[data-bookmark-delete]").ClickAsync();
        await cut.Find("[data-bookmark-delete-confirm]").ClickAsync();

        Assert.Single(cut.FindAll("[data-bookmark-delete-failed]"));
    }

    [Fact]
    public async Task AnAddThatThrows_IsShownAsFailed_InsteadOfLeavingTheButtonSilent()
    {
        var cut = RenderPanel([], p => p.Add(x => x.Add, (Func<double, string?, Task<bool>>)((_, _) => throw new HttpRequestException("down"))));

        await cut.Find("[data-bookmarks-add]").ClickAsync();

        Assert.Contains("Couldn’t", cut.Find("[data-bookmarks-add]").TextContent);
        Assert.False(cut.Find("[data-bookmarks-add]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AfterADeleteIsConfirmed_FocusMovesToTheNextRowsEditButton()
    {
        var first = Mine(100, "First");
        var second = Mine(200, "Second");
        var bookmarks = new List<PlaybackRailBookmark> { first, second };
        var cut = RenderPanel(bookmarks, p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));

        Assert.Equal(0, FocusCalls());
        await cut.FindAll("[data-bookmark-delete]")[0].ClickAsync();
        await cut.Find("[data-bookmark-delete-confirm]").ClickAsync();

        Assert.Equal(1, FocusCalls());
        Assert.Equal(2, cut.FindAll("[data-bookmark-edit]").Count); // the next row's Edit button is still there to receive it
    }

    [Fact]
    public async Task AfterTheLastRowIsDeleted_FocusMovesToTheList()
    {
        var only = Mine(100, "Only");
        var cut = RenderPanel([only], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));

        await cut.Find("[data-bookmark-delete]").ClickAsync();
        await cut.Find("[data-bookmark-delete-confirm]").ClickAsync();
        Assert.Equal(0, FocusCalls()); // the row is still listed until the host refreshes the list
        cut.Render(parameters => parameters.Add(p => p.Bookmarks, (IReadOnlyList<PlaybackRailBookmark>)[]));

        Assert.Equal(1, FocusCalls());
        Assert.Single(cut.FindAll("[data-bookmarks-empty]"));
    }

    [Fact]
    public async Task AfterEditSaveOrCancel_FocusReturnsToThatRowsEditButton()
    {
        var mine = Mine(300, "Old note");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Edit, (Func<PlaybackRailBookmark, string?, string?, Task<bool>>)((_, _, _) => Task.FromResult(true))));
        var edit = $"[data-bookmark-id='{mine.Id}'] [data-bookmark-edit]";

        await cut.Find(edit).ClickAsync();
        Assert.Equal(0, FocusCalls());
        await cut.Find("[data-bookmark-edit-form] .bmk__action").ClickAsync(); // Cancel
        Assert.Equal(1, FocusCalls());

        await cut.Find(edit).ClickAsync();
        await cut.Find("[data-bookmark-edit-save]").ClickAsync();
        Assert.Equal(2, FocusCalls());
    }

    [Fact]
    public void TheNameFieldAllowsTheSameLengthAsTheNote()
    {
        var mine = Mine(300, "Mine");
        var cut = RenderPanel([mine], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Edit, (Func<PlaybackRailBookmark, string?, string?, Task<bool>>)((_, _, _) => Task.FromResult(true))));

        cut.Find("[data-bookmark-edit]").Click();

        var limit = MediaEngine.Domain.Services.AudiobookBookmarkNotePolicy.MaximumLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(limit, cut.Find("[data-bookmark-edit-form] input[type=text]").GetAttribute("maxlength"));
        Assert.Equal(limit, cut.Find("[data-bookmark-edit-form] textarea").GetAttribute("maxlength"));
    }

    [Fact]
    public void AnOwnRowThatIsGlobal_OffersNoEditOrDeleteInThePlayer()
    {
        var curated = Mine(100, "Curated") with { IsGlobal = true };
        var cut = RenderPanel([curated], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine)
            .Add(x => x.Delete, (Func<PlaybackRailBookmark, Task<bool>>)(_ => Task.FromResult(true))));

        Assert.Empty(cut.FindAll("[data-bookmark-delete]"));
    }

    [Fact]
    public void TheSearchBox_AppearsOnlyAboveEightRows()
    {
        var eight = Enumerable.Range(1, BookmarksPanel.SearchThreshold).Select(index => Everyone(index * 10, $"Note {index}")).ToList();
        Assert.Empty(RenderPanel(eight).FindAll("[data-bookmarks-search]"));

        eight.Add(Everyone(900, "Note nine"));
        var nine = RenderPanel(eight);
        Assert.Single(nine.FindAll("[data-bookmarks-search]"));
        nine.Find("[data-bookmarks-search] input").Input("nine");
        Assert.Single(nine.FindAll("[data-bookmark-id]"));
    }

    [Fact]
    public void TheEmptyStates_NameTheListThatIsEmpty()
    {
        Assert.Contains("No bookmarks for this title yet", RenderPanel([]).Find("[data-bookmarks-empty]").TextContent);
        var mine = RenderPanel([Everyone(10, "x")], p => p.Add(x => x.Choice, PlaybackPreferenceValues.Mine));
        Assert.Contains("No bookmarks of yours yet", mine.Find("[data-bookmarks-empty]").TextContent);
    }

    [Fact]
    public async Task SelectingARow_SeeksToThatMoment()
    {
        var sought = new List<double>();
        var cut = RenderPanel([Everyone(100, "One"), Everyone(200, "Two")],
            p => p.Add(x => x.Seek, EventCallback.Factory.Create<PlaybackRailBookmark>(this, bookmark => sought.Add(bookmark.Seconds))));

        await cut.FindAll("[data-bookmark-id] button").First(button => button.TextContent.Contains("Two")).ClickAsync();

        Assert.Equal([200d], sought);
    }

    [Fact]
    public async Task ShowOnTimeline_ReportsTheSwitch_AndChoosingMineIsReported()
    {
        bool? shown = null;
        string? chosen = null;
        var cut = RenderPanel([Everyone(100, "x"), Mine(200, "y")], p => p
            .Add(x => x.ShowOnTimelineChanged, EventCallback.Factory.Create<bool>(this, value => shown = value))
            .Add(x => x.ChoiceChanged, EventCallback.Factory.Create<string>(this, value => chosen = value)));

        Assert.Contains("Show on timeline", cut.Find("[data-bookmarks-footer]").TextContent);
        await cut.Find("[data-bookmarks-footer] input[role=switch]").ChangeAsync(new ChangeEventArgs { Value = true });
        Assert.True(shown);

        await cut.FindAll("[data-bookmarks-choice] button, [data-bookmarks-choice] [role=radio], [data-bookmarks-choice] [role=tab]")
            .First(button => button.TextContent.Contains("Mine")).ClickAsync();
        Assert.Equal(PlaybackPreferenceValues.Mine, chosen);
    }

    [Fact]
    public void TheMarkers_FollowTheChoiceShown_AndStayHiddenWhileTheSwitchIsOff()
    {
        var all = new[] { Everyone(100), Mine(200), Mine(300, inEveryone: true) };

        Assert.Equal([100d, 300d], PlaybackRailBookmark.ForChoice(all, null).Select(marker => marker.Seconds));
        Assert.Equal([100d, 300d], PlaybackRailBookmark.ForChoice(all, PlaybackPreferenceValues.All).Select(marker => marker.Seconds));
        Assert.Equal([200d, 300d], PlaybackRailBookmark.ForChoice(all, PlaybackPreferenceValues.Mine).Select(marker => marker.Seconds));
    }

    [Fact]
    public void AVideoRowDrawsTheFrameThroughTheDashboardProxy_AndAnAudioRowDrawsTheCover()
    {
        var id = Guid.NewGuid();
        var withFrame = new PlaybackRailBookmark(id, 100, false, "Frame", null, null, "Alex", false, true, $"/api/v1/player/bookmarks/{id}/frame?v=abc");
        Assert.Equal($"/engine-bookmark-frame/{id:D}?v=abc", BookmarksPanel.FrameAddress(withFrame));
        Assert.Null(BookmarksPanel.FrameAddress(Everyone(100)));

        var video = RenderPanel([withFrame]);
        Assert.Contains("engine-bookmark-frame", video.Markup);
        var audio = RenderPanel([withFrame], p => p.Add(x => x.UseFrames, false).Add(x => x.CoverUrl, "/cover.jpg"));
        Assert.DoesNotContain("engine-bookmark-frame", audio.Markup);
        Assert.Contains("/cover.jpg", audio.Markup);
    }

    [Fact]
    public async Task OnAPhone_ThePanelIsABottomSheet_WithTheBookmarksHeading()
    {
        var host = Render<PlaybackPanel>(parameters => parameters
            .Add(p => p.Title, "Bookmarks").Add(p => p.ToolId, "video-bookmarks").Add(p => p.Kind, "bookmarks")
            .Add(p => p.TriggerContent, (RenderFragment)(builder => builder.AddContent(0, "B")))
            .Add(p => p.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<BookmarksPanel>(0);
                builder.AddAttribute(1, nameof(BookmarksPanel.Bookmarks), (IReadOnlyList<PlaybackRailBookmark>)[Everyone(100, "Sheet row")]);
                builder.CloseComponent();
            })));
        await host.InvokeAsync(() => host.Instance.SetViewport(true));
        await host.InvokeAsync(() => host.Instance.OpenAsync(true));

        var panel = host.WaitForElement("[data-playback-popover-panel]");
        Assert.Contains("playback-panel--sheet", panel.ClassList);
        Assert.NotNull(panel.QuerySelector("button[aria-label='Close Bookmarks']"));
        Assert.Contains("Sheet row", panel.TextContent);
        Assert.NotNull(panel.QuerySelector("[data-bookmarks-add]"));
    }

    [Fact]
    public void ThePlayerWording_NeverUsesGlobalPromoteFindOrImport()
    {
        var cut = RenderPanel([Everyone(100, "x"), Mine(200, "y")], p => p.Add(x => x.ShowOnTimeline, true));
        var text = cut.Markup;
        foreach (var word in new[] { "Global", "Promote", "Find", "Import", "Everyone's", "Everyone’s" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }
}
