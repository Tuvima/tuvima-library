using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Review mock-ups D7: the one "pick a result" window subtitles and lyrics share. A search box that starts filled in, results with the best match first
/// and selected, a Search button that asks again with the box's text, and Cancel / Use this.
/// </summary>
public sealed class AppPickResultDialogTests : AsyncBunitContext
{
    private readonly List<string> _searches = [];
    private readonly List<AppPickResult> _used = [];
    private int _cancels;

    public AppPickResultDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Render<AppPopoverHost>();
    }

    private static readonly IReadOnlyList<AppPickResult> Three =
    [
        new("a", "Ferrari (2023)", "Same release", "/cover/a"),
        new("b", "Ferrari (1998)", "Different release", "/cover/b"),
        new("c", "Le Mans '66", "Different release", "/cover/c", "In use now", true),
    ];

    private IRenderedComponent<AppPickResultDialog> Open(IReadOnlyList<AppPickResult>? results = null, string search = "Ferrari 2023", AppPickCoverShape shape = AppPickCoverShape.Portrait) =>
        Render<AppPickResultDialog>(parameters => parameters
            .Add(p => p.Title, "Find subtitles")
            .Add(p => p.SearchText, search)
            .Add(p => p.Results, results ?? Three)
            .Add(p => p.CoverShape, shape)
            .Add(p => p.OnSearch, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, text => _searches.Add(text)))
            .Add(p => p.OnUse, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<AppPickResult>(this, pick => _used.Add(pick)))
            .Add(p => p.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => _cancels++)));

    [Fact]
    public void TheTitleAndTheSearchBoxStartFilledIn_WithASearchButton()
    {
        var cut = Open();

        Assert.Equal("Find subtitles", cut.Find("dialog h2").TextContent);
        Assert.Equal("Ferrari 2023", cut.Find("[data-pick-box]").GetAttribute("value"));
        Assert.Equal("Search", cut.Find("[data-pick-search]").TextContent.Trim());
        Assert.Equal(["Cancel", "Use this"], cut.FindAll("[data-pick-action]").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public void TheFirstResultIsTheBestMatch_WithThePill_AndStartsSelectedWithTheAccentOutline()
    {
        var cut = Open();

        var rows = cut.FindAll("[data-pick-result]");
        Assert.Equal(["a", "b", "c"], rows.Select(row => row.GetAttribute("data-pick-result")));
        Assert.Equal(["true", "false", "false"], rows.Select(row => row.GetAttribute("aria-checked")));
        Assert.Contains("is-selected", rows[0].ClassList);
        Assert.DoesNotContain("is-selected", rows[1].ClassList);
        Assert.Equal("Best match", Assert.Single(cut.FindAll("[data-pick-best]")).TextContent);
        Assert.NotNull(rows[0].QuerySelector("[data-pick-best]"));
        Assert.Equal("Ferrari (2023)", rows[0].QuerySelector(".app-pick-result__title")!.TextContent);
        Assert.Equal("Same release", rows[0].QuerySelector("[data-pick-sub]")!.TextContent);
        Assert.All(rows, row => Assert.Single(row.QuerySelectorAll("img")));
    }

    [Theory]
    [InlineData(AppPickCoverShape.Portrait, "portrait")]
    [InlineData(AppPickCoverShape.Square, "square")]
    public void TheCoverIsPortraitForVideoAndSquareForSongs(AppPickCoverShape shape, string expected)
    {
        var cut = Open(shape: shape);

        Assert.Equal(expected, cut.Find("[data-pick-dialog]").GetAttribute("data-pick-cover"));
    }

    [Fact]
    public async Task ChoosingAnotherRow_MovesTheOutline_AndUseThisHandsOverThatResult()
    {
        var cut = Open();

        await cut.Find("[data-pick-result='b']").ClickAsync();
        Assert.Equal("true", cut.Find("[data-pick-result='b']").GetAttribute("aria-checked"));
        Assert.Equal("false", cut.Find("[data-pick-result='a']").GetAttribute("aria-checked"));
        await cut.Find("[data-pick-action='use']").ClickAsync();

        Assert.Equal("b", Assert.Single(_used).Key);
    }

    [Fact]
    public async Task UseThisOnTheBestMatchWithNoClick_HandsOverTheFirstResult()
    {
        var cut = Open();

        await cut.Find("[data-pick-action='use']").ClickAsync();

        Assert.Equal("a", Assert.Single(_used).Key);
    }

    [Fact]
    public async Task TheResultInUse_ShowsItsNote_AndUseThisIsOffForIt()
    {
        var cut = Open();

        await cut.Find("[data-pick-result='c']").ClickAsync();

        Assert.Equal("In use now", cut.Find("[data-pick-result='c'] [data-pick-note]").TextContent);
        Assert.True(cut.Find("[data-pick-action='use']").HasAttribute("disabled"));
        Assert.Empty(_used);
    }

    [Fact]
    public async Task EditingTheBoxAndPressingSearch_SendsTheNewTextTrimmed()
    {
        var cut = Open();

        cut.Find("[data-pick-box]").Input("  Ferrari 1998  ");
        await cut.Find("[data-pick-search]").ClickAsync();

        Assert.Equal(["Ferrari 1998"], _searches);
        Assert.Equal("  Ferrari 1998  ", cut.Find("[data-pick-box]").GetAttribute("value"));
    }

    [Fact]
    public async Task PressingEnterInTheBox_SearchesToo()
    {
        var cut = Open();

        cut.Find("[data-pick-box]").Input("Ferrari 1998");
        await cut.Find("[data-pick-box]").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(["Ferrari 1998"], _searches);
    }

    [Fact]
    public async Task AnEmptyBox_DoesNotSearch()
    {
        var cut = Open();

        cut.Find("[data-pick-box]").Input("   ");

        Assert.True(cut.Find("[data-pick-search]").HasAttribute("disabled"));
        Assert.Empty(_searches);
        await Task.CompletedTask;
    }

    [Fact]
    public void ANewListSelectsItsBestMatch_ButTheSameListKeepsThePersonsChoice()
    {
        var cut = Open();
        cut.Find("[data-pick-result='b']").Click();

        cut.Render(parameters => parameters.Add(p => p.Results, [.. Three]));
        Assert.Equal("true", cut.Find("[data-pick-result='b']").GetAttribute("aria-checked"));

        cut.Render(parameters => parameters.Add(p => p.Results, [new AppPickResult("x", "Other"), new AppPickResult("y", "Another")]));
        Assert.Equal("true", cut.Find("[data-pick-result='x']").GetAttribute("aria-checked"));
    }

    [Fact]
    public void WhileSearching_ASingleWaitingLineReplacesTheList_AndSearchWaits()
    {
        var cut = Render<AppPickResultDialog>(parameters => parameters
            .Add(p => p.Title, "Find lyrics").Add(p => p.SearchText, "Song Artist").Add(p => p.Loading, true).Add(p => p.Results, Three));

        Assert.Equal("Searching…", cut.Find("[data-pick-state='searching']").TextContent);
        Assert.Empty(cut.FindAll("[data-pick-result]"));
        Assert.True(cut.Find("[data-pick-search]").HasAttribute("disabled"));
    }

    [Fact]
    public void WithNoResults_TheMessageIsShown_AndUseThisIsOff()
    {
        var cut = Render<AppPickResultDialog>(parameters => parameters
            .Add(p => p.Title, "Find lyrics").Add(p => p.SearchText, "Song Artist").Add(p => p.Message, "No other results were found."));

        Assert.Equal("No other results were found.", cut.Find("[data-pick-state='empty']").TextContent);
        Assert.True(cut.Find("[data-pick-action='use']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Cancel_RaisesTheCancelCallback()
    {
        var cut = Open();

        await cut.Find("[data-pick-action='cancel']").ClickAsync();

        Assert.Equal(1, _cancels);
    }

    [Fact]
    public void TheSharedWindowStylesheetKeepsToThe44PixelTouchTargetAndHasNoInlineStyles()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }
        var css = File.ReadAllText(Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Shared", "AppPickResultDialog.razor.css"));
        var razor = File.ReadAllText(Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Shared", "AppPickResultDialog.razor"));

        Assert.Contains("min-height: var(--tl-tap-target-min, 44px)", css);
        Assert.DoesNotContain("style=", razor);
    }
}

/// <summary>What the search box starts with (review mock-ups D7): a video's title and year, a song's title and artist (the album artist when there is none).</summary>
public sealed class FindDialogSearchTextTests
{
    [Fact]
    public void AVideoStartsWithItsTitleAndYear() => Assert.Equal("Ferrari 2023", FindDialogSearchText.ForVideo("Ferrari", "2023"));

    [Fact]
    public void AVideoWithNoYearIsJustItsTitle() => Assert.Equal("Ferrari", FindDialogSearchText.ForVideo(" Ferrari ", null));

    [Fact]
    public void ASongStartsWithItsArtistThenItsTitle() => Assert.Equal("The Midnight Coast Stay a Little Longer", FindDialogSearchText.ForSong("Stay a Little Longer", "The Midnight Coast", "Someone Else"));

    [Fact]
    public void ASongWithNoArtistFallsBackToTheAlbumArtist() => Assert.Equal("Album Band Stay a Little Longer", FindDialogSearchText.ForSong("Stay a Little Longer", null, "Album Band"));

    [Fact]
    public void ABoxLeftAsFilledIn_IsNotAnEdit() => Assert.Null(FindDialogSearchText.EditedOrNull("Ferrari 2023", " ferrari   2023 "));

    [Fact]
    public void ABoxThatWasChanged_IsAnEdit() => Assert.Equal("Ferrari 1998", FindDialogSearchText.EditedOrNull("Ferrari 2023", "Ferrari  1998"));
}
