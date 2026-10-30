using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PhoneContinueShelfTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public PhoneContinueShelfTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    // The seed: one book, one audiobook, one episode, one album, plus a movie that was never started and an
    // episode that is next up (in progress state but no percent), each with its own last-touched time.
    private static MediaTileViewModel Item(string kind, string title, double? pct, int minutesAgo, MediaTileShape shape,
        double? remainingSeconds = null, string? progressLabel = null, DisplayContinuationState state = DisplayContinuationState.InProgress,
        DisplayEpisodeContextDto? episode = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Title = title,
            MediaKind = kind,
            Shape = shape,
            TileImageUrl = "/art.jpg",
            ProgressPct = pct,
            ProgressLabel = progressLabel,
            RemainingSeconds = remainingSeconds,
            ContinuationState = state,
            SortTimestamp = Now.AddMinutes(-minutesAgo),
            DetailsNavigationUrl = "/details/work/x",
            HoverMode = MediaTileHoverMode.GlowOnly,
            EpisodeContext = episode,
        };

    private static MediaTileViewModel[] Seed() =>
    [
        Item("Book", "Leviathan Wakes", 42, 300, MediaTileShape.Portrait, progressLabel: "Page 112"),
        Item("Audiobook", "The Martian", 25, 10, MediaTileShape.Square, progressLabel: "Chapter 4"),
        Item("TV", "The Continental", 60, 60, MediaTileShape.Landscape, remainingSeconds: 23 * 60,
            episode: new DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "The Continental", "Night 1", 1, 2, DisplayContinuationState.InProgress, null, null)),
        Item("Music", "Jagged Little Pill", 10, 120, MediaTileShape.Square),
        Item("Movie", "Never Started", null, 5, MediaTileShape.Landscape, state: DisplayContinuationState.Unstarted),
        Item("TV", "Next Up", 0, 200, MediaTileShape.Landscape, remainingSeconds: 40 * 60,
            episode: new DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Next Up", "Pilot", 1, 1, DisplayContinuationState.InProgress, null, null)),
    ];

    [Fact]
    public async Task PickUpRowIsOneRowOfStartedItemsAndRecentAlbumsNewestFirstOnEveryDevice()
    {
        foreach (var device in new[] { "phone", "desktop" })
        {
            await UseAsync(device);
            var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = Seed() }));
            var cards = cut.FindAll(".pickup-card");
            // The never-started movie is out; the rest run newest activity first: audiobook, episode, album, next-up episode, book.
            Assert.Equal(5, cards.Count);
            string Text(int index) => cards[index].TextContent;
            Assert.Contains("The Martian", Text(0));
            Assert.Contains("Listen", Text(0));
            Assert.Contains("The Continental", Text(1));
            Assert.Contains("S1 E2 · Night 1", Text(1));
            Assert.Contains("23 min left", Text(1));
            Assert.Contains("Jagged Little Pill", Text(2));
            Assert.Contains("Recently played", Text(2));
            Assert.Contains("S1 E1 · Pilot", Text(3));
            Assert.Contains("Leviathan Wakes", Text(4));
            Assert.Contains("42% complete", Text(4));
            Assert.DoesNotContain("Never Started", cut.Markup);
        }
    }

    [Fact]
    public async Task NothingStartedHidesTheWholeSectionWithNoEmptyHeader()
    {
        await UseAsync("phone");
        var cut = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf,
            new MediaTileShelfViewModel { Items = [Item("Movie", "Fresh", null, 1, MediaTileShape.Landscape, state: DisplayContinuationState.Unstarted)] }));
        Assert.Empty(cut.FindAll("section"));
        Assert.Empty(cut.FindAll("h2"));
    }

    [Fact]
    public async Task NoProgressBarAtZeroPercentAndABarWhenThereIsProgress()
    {
        await UseAsync("phone");
        var zero = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [Seed()[5]] }));
        Assert.Empty(zero.FindAll("[role='progressbar']"));
        var some = Render<ContinueAcrossMediaSection>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel { Items = [Seed()[0]] }));
        Assert.NotEmpty(some.FindAll("[role='progressbar']"));
    }

    [Fact]
    public async Task EveryHomeShelfHeaderHasSeeAllOnTheRightAsA48pxTargetOnPhones()
    {
        await UseAsync("phone");
        foreach (var title in new[] { "Watch", "Read", "Listen" })
        {
            var cut = Render<MediaTileShelf>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel
            {
                Title = title,
                SeeAllRoute = $"/{title.ToLowerInvariant()}",
                Items = [Item("Movie", "X", 10, 1, MediaTileShape.Landscape)],
            }));
            var link = cut.Find("a.media-tile-shelf-link");
            Assert.Equal("See All", link.TextContent.Trim());
            Assert.Equal($"/{title.ToLowerInvariant()}", link.GetAttribute("href"));
            // The header is title then link, so the link sits on the right of the title.
            var header = cut.Find(".media-tile-shelf-header");
            Assert.Equal("media-tile-shelf-copy", header.Children[0].ClassList.First());
            Assert.Equal("media-tile-shelf-link", header.Children[1].ClassList.First());
        }

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaTiles", "MediaTileShelf.razor.css"));
        Assert.Contains("data-form-factor=\"phone\"] .media-tile-shelf-link", css);
        Assert.Contains("min-height:48px", css.Replace(" ", string.Empty));

        // Recently Added: its See All link has the same phone rule, and stays right of the heading while the chips drop to their own row.
        var feedCss = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaTiles", "RecentMediaFeed.razor.css"));
        var linkRule = feedCss[feedCss.IndexOf(":root[data-form-factor=\"phone\"] .recent-view-all {", StringComparison.Ordinal)..];
        linkRule = linkRule[..linkRule.IndexOf('}')].Replace(" ", string.Empty);
        Assert.Contains("min-height:48px", linkRule);
        Assert.Contains("min-width:48px", linkRule);
        Assert.Contains("justify-content:flex-end", linkRule);
        Assert.Contains("margin-left:auto", linkRule);
        Assert.Contains(".recent-feed-heading::deep.recent-filters{order:3;flex-basis:100%;}", feedCss.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task DesktopKeepsViewAll()
    {
        await UseAsync("desktop");
        var cut = Render<MediaTileShelf>(p => p.Add(c => c.Shelf, new MediaTileShelfViewModel
        {
            Title = "Watch",
            SeeAllRoute = "/watch",
            Items = [Item("Movie", "X", 10, 1, MediaTileShape.Landscape)],
        }));
        Assert.Equal("View all", cut.Find("a.media-tile-shelf-link").TextContent.Trim());
    }

    [Fact]
    public void RecentFilterChipsStayInOneScrollingRowOnPhones()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaTiles", "RecentMediaFilters.razor.css"));
        var rule = css[css.IndexOf(":root[data-form-factor=\"phone\"] .recent-filters {", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}')];
        Assert.Contains("flex-wrap:nowrap", rule);
        Assert.Contains("overflow-x:auto", rule);
    }

    [Fact]
    public void SubLabelsReadNaturallyForEachType()
    {
        Assert.Equal("1 hr 5 min left", PhoneContinueLogic.SubLabel(Item("Movie", "M", 30, 1, MediaTileShape.Landscape, remainingSeconds: 65 * 60)));
        Assert.Equal("2 hr left", PhoneContinueLogic.SubLabel(Item("Movie", "M", 30, 1, MediaTileShape.Landscape, remainingSeconds: 120 * 60)));
        Assert.Equal("30% watched", PhoneContinueLogic.SubLabel(Item("Movie", "M", 30, 1, MediaTileShape.Landscape)));
        Assert.Equal("42% read", PhoneContinueLogic.SubLabel(Item("Book", "B", 42, 1, MediaTileShape.Portrait)));
        Assert.Equal("Chapter 4", PhoneContinueLogic.SubLabel(Item("Audiobook", "A", 25, 1, MediaTileShape.Square, progressLabel: "Chapter 4")));
        Assert.Equal("In progress", PhoneContinueLogic.SubLabel(Item("Music", "A", null, 1, MediaTileShape.Square)));
    }
}
