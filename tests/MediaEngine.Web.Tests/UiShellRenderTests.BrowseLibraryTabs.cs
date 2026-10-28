using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Browse tabs per kind and per named library (Library setup spec D54) and the Unmatched filter on each library tab (D64): Watch, Listen and
/// Read build their tabs from the Engine's browse library list, keep their fixed tabs when it cannot be read, and send the tab's library
/// (and, for the Unmatched chip, <c>unmatched=true</c>) with every browse call.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly Guid MoviesLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid TvLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid YouTubeLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid LecturesLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000004");
    private static readonly Guid EmptyLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000005");

    private static BrowseLibraryDto[] WatchLibraries() =>
    [
        new(MoviesLibrary, "Movies", "movies", "watch", "Movies", true),
        new(TvLibrary, "TV Shows", "tv", "watch", "TV", true),
        new(YouTubeLibrary, "YouTube", "youtube", "watch", "Videos", true),
        new(LecturesLibrary, "Lectures", "other_videos", "watch", "Videos", true),
        new(EmptyLibrary, "Empty", "other_videos", "watch", "Videos", false),
    ];

    private readonly List<object?[]> _browseCalls = [];
    private readonly List<object?[]> _groupCalls = [];
    private EngineApiClientStub? _browseStub;

    // Replaces the Engine stub: the browse library list is what the test gives (null = it could not be read), and every browse call is kept.
    private void UseBrowseLibraries(IReadOnlyList<BrowseLibraryDto>? libraries, TaskCompletionSource<IReadOnlyList<BrowseLibraryDto>?>? pending = null)
    {
        var client = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSystemViewGroupsAsync), args =>
            {
                _groupCalls.Add(args ?? []);
                return Task.FromResult(Enumerable.Range(0, 250).Select(index => new ContentGroupViewModel
                {
                    CollectionId = Guid.NewGuid(),
                    DisplayName = $"Series {index:000}",
                    PrimaryMediaType = "Books",
                    WorkCount = 1,
                }).ToList());
            });
            stub.SetHandler(nameof(IEngineApiClient.GetBrowseLibrariesAsync), _ =>
                pending?.Task ?? Task.FromResult<IReadOnlyList<BrowseLibraryDto>?>(libraries));
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayBrowseAsync), args =>
            {
                _browseCalls.Add(args ?? []);
                return Task.FromResult<DisplayPageDto?>(new DisplayPageDto("browse", "Browse", null, null, [], []));
            });
        });
        _browseStub = (EngineApiClientStub)(object)client;
        Services.AddSingleton(client);
    }

    // The positional arguments of IEngineApiClient.GetDisplayBrowseAsync.
    private const int BrowseMediaTypeArgument = 1;
    private const int BrowseLibraryIdArgument = 16;
    private const int BrowseUnmatchedArgument = 17;
    private const int BrowseGroupingArgument = 2;
    private const int BrowseLimitArgument = 5;

    // The positional arguments of IEngineApiClient.GetSystemViewGroupsAsync.
    private const int GroupLibraryIdArgument = 3;
    private const int GroupUnmatchedArgument = 4;

    private IRenderedComponent<IComponent> RenderLanePage<TPage>(string route, string? tab) where TPage : IComponent
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        return Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(3);
            if (tab is not null)
            {
                builder.AddAttribute(4, "Tab", tab);
            }

            builder.CloseComponent();
        });
    }

    private static string[] TabRoutes(IRenderedComponent<IComponent> cut, string routeBase) =>
        [.. cut.FindAll("a")
            .Select(link => link.GetAttribute("href") ?? string.Empty)
            .Where(href => href.StartsWith(routeBase + "/", StringComparison.Ordinal) && !href.Contains('?'))
            .Distinct(StringComparer.Ordinal)];

    [Fact]
    public void Watch_ShowsOneTabPerKindAndOneVideosTabGatheringTheVideoLibrariesAsChips()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>("/watch/videos", "videos");

        cut.WaitForAssertion(() => Assert.Equal(
            ["/watch/movies", "/watch/tv", "/watch/videos"],
            TabRoutes(cut, "/watch")));
        Assert.Contains(">TV Shows<", cut.Markup);
        Assert.Equal(["All", "YouTube", "Lectures"], ChipLabels(cut));
        Assert.DoesNotContain(">Empty<", cut.Markup);
    }

    private static string[] ChipLabels(IRenderedComponent<IComponent> cut) =>
        [.. cut.FindAll("[data-library-chips] .app-chip").Select(chip => chip.TextContent.Trim())];

    private static readonly Guid HomeVideosLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000011");
    private static readonly Guid TikTokLibrary = Guid.Parse("a0000000-0000-0000-0000-000000000012");

    private static BrowseLibraryDto[] ThreeOtherVideos() =>
    [
        new(HomeVideosLibrary, "Home videos", "other_videos", "watch", "Videos", true),
        new(TikTokLibrary, "TikTok edits", "other_videos", "watch", "Videos", true),
        new(LecturesLibrary, "Other videos", "other_videos", "watch", "Videos", true),
    ];

    [Fact]
    public void Watch_ThreeOtherVideosLibrariesShowThreeChips()
    {
        UseBrowseLibraries(ThreeOtherVideos());

        var cut = RenderLanePage<WatchPage>("/watch/videos", "videos");

        cut.WaitForAssertion(() => Assert.Equal(["All", "Home videos", "TikTok edits", "Other videos"], ChipLabels(cut)));
    }

    [Fact]
    public void Watch_AProfileThatCannotSeeOneOfThreeLibrariesGetsNeitherItsChipNorItsVideos()
    {
        // The Engine leaves a library the profile cannot see out of the list, so the page has no chip for it, and asking for it by address does nothing.
        var all = ThreeOtherVideos();
        UseBrowseLibraries([all[0], all[2]]);

        var cut = RenderLanePage<WatchPage>($"/watch/videos?library={TikTokLibrary:D}", "videos");

        cut.WaitForAssertion(() => Assert.Equal(["All", "Home videos", "Other videos"], ChipLabels(cut)));
        Assert.DoesNotContain("TikTok edits", cut.Markup);
        cut.WaitForAssertion(() => Assert.NotEmpty(_browseCalls));
        Assert.Null(_browseCalls[0][BrowseLibraryIdArgument]);
    }

    [Fact]
    public void Watch_ChoosingALibraryChipBrowsesWithThatLibraryAndKeepsItInTheAddress()
    {
        UseBrowseLibraries(WatchLibraries());
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderLanePage<WatchPage>("/watch/videos", "videos");
        cut.WaitForAssertion(() => Assert.NotEmpty(_browseCalls));
        Assert.Null(_browseCalls[0][BrowseLibraryIdArgument]);

        cut.FindAll("[data-library-chips] .app-chip").Single(chip => chip.TextContent.Trim() == "Lectures").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(LecturesLibrary, _browseCalls[^1][BrowseLibraryIdArgument]);
            Assert.Contains($"library={LecturesLibrary:D}", navigation.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Watch_OpeningTheVideosTabWithALibraryChipBrowsesWithThatLibrary()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>($"/watch/videos?library={LecturesLibrary:D}", "videos");

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(_browseCalls.Take(1));
            Assert.Equal(LecturesLibrary, call[BrowseLibraryIdArgument]);
            Assert.Equal("Videos", call[BrowseMediaTypeArgument]);
        });
    }

    [Fact]
    public void Watch_AProfileThatCanOnlySeeOneLibraryGetsOnlyThatTab()
    {
        UseBrowseLibraries([WatchLibraries()[3]]);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Equal(["/watch/videos"], TabRoutes(cut, "/watch")));
        Assert.Empty(ChipLabels(cut));
    }

    [Fact]
    public void Watch_KeepsItsFixedTabsWhenTheLibraryListCannotBeRead()
    {
        UseBrowseLibraries(null);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Equal(["/watch/movies", "/watch/tv", "/watch/videos"], TabRoutes(cut, "/watch")));
    }

    [Fact]
    public void Watch_AnOldYouTubeLinkOpensTheVideosTabOnAll()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>("/watch/youtube", "youtube");

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(_browseCalls.Take(1));
            Assert.Null(call[BrowseLibraryIdArgument]);
            Assert.Equal("Videos", call[BrowseMediaTypeArgument]);
        });
    }

    [Fact]
    public void Watch_TheUnmatchedChipSendsUnmatchedWithTheTabsLibraryAndKeepsItInTheAddress()
    {
        UseBrowseLibraries(WatchLibraries());
        var route = $"/watch/videos?library={LecturesLibrary:D}";
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderLanePage<WatchPage>(route, "videos");
        cut.WaitForAssertion(() => Assert.NotEmpty(_browseCalls));
        Assert.Null(_browseCalls[0][BrowseUnmatchedArgument]);

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Unmatched").Click();

        cut.WaitForAssertion(() =>
        {
            var call = _browseCalls[^1];
            Assert.Equal(true, call[BrowseUnmatchedArgument]);
            Assert.Equal(LecturesLibrary, call[BrowseLibraryIdArgument]);
            Assert.EndsWith("unmatched=true", navigation.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Watch_TheUnmatchedFilterSurvivesAReloadThroughTheAddressAndShowsOneLineWhenThereIsNothing()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>($"/watch/videos?library={LecturesLibrary:D}&unmatched=true", "videos");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(true, _browseCalls[0][BrowseUnmatchedArgument]);
            Assert.Equal(LecturesLibrary, _browseCalls[0][BrowseLibraryIdArgument]);
            Assert.Contains("Nothing unmatched here.", cut.Markup);
        });
        Assert.Equal("true", cut.FindAll("button[aria-pressed]").Single(button => button.TextContent.Trim() == "Unmatched").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Watch_AFixedTabHasNoUnmatchedChip()
    {
        UseBrowseLibraries(null);

        var cut = RenderLanePage<WatchPage>("/watch/movies?unmatched=true", "movies");

        cut.WaitForAssertion(() => Assert.NotEmpty(_browseCalls));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Unmatched");
        Assert.Null(_browseCalls[0][BrowseUnmatchedArgument]);
    }

    [Fact]
    public void Listen_ShowsATabForEachLibraryThenKeepsPlaylists()
    {
        UseBrowseLibraries(
        [
            new(Guid.Parse("b0000000-0000-0000-0000-000000000001"), "Music", "music", "listen", "Music", true),
            new(Guid.Parse("b0000000-0000-0000-0000-000000000002"), "Audiobooks", "audiobooks", "listen", "Audiobooks", false),
            new(Guid.Parse("b0000000-0000-0000-0000-000000000003"), "Family recordings", "other_audio", "listen", "Music", true),
        ]);

        var cut = RenderLanePage<ListenBrowsePage>("/listen", null);

        cut.WaitForAssertion(() => Assert.Equal(
            ["/listen/music", "/listen/lib-b0000000000000000000000000000003", "/listen/playlists"],
            TabRoutes(cut, "/listen").Where(route => !route.StartsWith("/listen/music/", StringComparison.Ordinal)).ToArray()));
    }

    [Fact]
    public void Watch_AProfileWithOnlyListenLibrariesSeesTheEmptyStateNotEmptyTabs()
    {
        UseBrowseLibraries([new(Guid.Parse("b0000000-0000-0000-0000-000000000001"), "Music", "music", "listen", "Music", true)]);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Contains("No movies or shows yet", cut.Markup));
        Assert.Empty(TabRoutes(cut, "/watch"));
        Assert.Empty(cut.FindAll(".media-section-shell__rail-item"));
        Assert.Empty(cut.FindAll(".browse-shell"));
    }

    [Fact]
    public void Watch_ASeeingNothingProfileOpeningATabRouteSeesTheEmptyStateToo()
    {
        UseBrowseLibraries([]);

        var cut = RenderLanePage<WatchPage>("/watch/movies", "movies");

        cut.WaitForAssertion(() => Assert.Contains("No movies or shows yet", cut.Markup));
        Assert.Empty(cut.FindAll(".browse-shell"));
        Assert.Empty(_browseCalls);
    }

    [Fact]
    public void Watch_DrawsTheHeaderAndATabSkeletonAtOnce_AndLoadsTheLandingWhileTheLibraryListIsStillBeingRead()
    {
        var list = new TaskCompletionSource<IReadOnlyList<BrowseLibraryDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UseBrowseLibraries(null, list);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".library-section-header"));
            Assert.Single(cut.FindAll("[aria-label='Loading tabs']"));
            Assert.Contains(_browseCalls, call => Equals(call[0], "watch"));
        });
        Assert.Empty(TabRoutes(cut, "/watch"));

        list.SetResult(WatchLibraries());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[aria-label='Loading tabs']"));
            Assert.Contains("/watch/videos", TabRoutes(cut, "/watch"));
        });
    }

    [Fact]
    public void Watch_ShortcutsOpenTheFirstTabWhenThereIsNoMoviesTab()
    {
        UseBrowseLibraries([WatchLibraries()[3]]);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a"), link =>
            link.GetAttribute("href") == "/watch/videos?status=in-progress"));
        Assert.DoesNotContain(cut.FindAll("a"), link => link.GetAttribute("href")?.StartsWith("/watch/movies", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Listen_ShortcutsFollowTheMusicTabAndAreLeftOutWithoutOne()
    {
        UseBrowseLibraries(
        [
            new(Guid.Parse("b0000000-0000-0000-0000-000000000002"), "Audiobooks", "audiobooks", "listen", "Audiobooks", true),
        ]);

        var cut = RenderLanePage<ListenBrowsePage>("/listen", null);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == "/listen/audiobooks?status=in-progress"));
        Assert.DoesNotContain(">Recently Played<", cut.Markup);
        Assert.DoesNotContain(">Recently Added<", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("a"), link => link.GetAttribute("href") is "/listen/music" || link.GetAttribute("href")?.StartsWith("/listen/music?", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Listen_RecentlyPlayedAndAddedOpenTheFirstMusicTypeLibraryTab()
    {
        UseBrowseLibraries(
        [
            new(Guid.Parse("b0000000-0000-0000-0000-000000000003"), "Family recordings", "other_audio", "listen", "Music", true),
        ]);

        var cut = RenderLanePage<ListenBrowsePage>("/listen", null);

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll("a").Select(link => link.GetAttribute("href")).ToList();
            Assert.Contains("/listen/lib-b0000000000000000000000000000003?sort=recently-played", hrefs);
            Assert.Contains("/listen/lib-b0000000000000000000000000000003?sort=newest", hrefs);
        });
    }

    [Fact]
    public void Watch_TakingTheUnmatchedFilterOffWithItsChipOrClearAllReplacesTheAddress()
    {
        UseBrowseLibraries(WatchLibraries());
        var navigation = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        var cut = RenderLanePage<WatchPage>($"/watch/videos?library={LecturesLibrary:D}&unmatched=true", "videos");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Remove Unmatched filter']")));

        cut.Find("button[aria-label='Remove Unmatched filter']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("unmatched", navigation.Uri);
            Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);
        });

        navigation.NavigateTo($"/watch/videos?library={LecturesLibrary:D}&unmatched=true");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-active-filter-summary__clear")));
        cut.Find(".app-active-filter-summary__clear").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("unmatched", navigation.Uri);
            Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);
        });
    }

    private static readonly Guid EssaysLibrary = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private static BrowseLibraryDto[] ReadLibraries() =>
    [
        new(Guid.Parse("c0000000-0000-0000-0000-000000000001"), "Books", "books", "read", "Books", true),
        new(EssaysLibrary, "Essays", "other_reading", "read", "Books", true),
    ];

    [Fact]
    public void Read_AGroupedLibraryTabAsksForThatLibrarysGroupsAndKeepsEveryOneOfThemPastTwoHundred()
    {
        UseBrowseLibraries(ReadLibraries());

        var cut = RenderLanePage<ReadPage>($"/read/lib-{EssaysLibrary:N}?browse=series", $"lib-{EssaysLibrary:N}");

        cut.WaitForAssertion(() => Assert.Contains("250 items", cut.Markup));
        Assert.Equal(EssaysLibrary, _groupCalls[0][GroupLibraryIdArgument]);
        Assert.Null(_groupCalls[0][GroupUnmatchedArgument]);
        // No 200-card page is read to cut the groups down, and the filter options come from the same library.
        Assert.DoesNotContain(_browseCalls, call => Equals(call[BrowseLimitArgument], 200));
        Assert.All(_browseCalls.Where(call => Equals(call[BrowseLimitArgument], 1)), call => Assert.Equal(EssaysLibrary, call[BrowseLibraryIdArgument]));
    }

    [Fact]
    public void Read_TheUnmatchedFilterListsFilesFlatEvenWhenTheViewIsGrouped()
    {
        UseBrowseLibraries(ReadLibraries());

        var cut = RenderLanePage<ReadPage>($"/read/lib-{EssaysLibrary:N}?browse=series&unmatched=true", $"lib-{EssaysLibrary:N}");

        cut.WaitForAssertion(() => Assert.NotEmpty(_browseCalls));
        Assert.All(_browseCalls, call =>
        {
            Assert.Equal("all", call[BrowseGroupingArgument]);
            Assert.Equal(true, call[BrowseUnmatchedArgument]);
            Assert.Equal(EssaysLibrary, call[BrowseLibraryIdArgument]);
        });
        Assert.Empty(_groupCalls);
        Assert.DoesNotContain("GetContentGroupsAsync", _browseStub!.Calls);
    }

    [Fact]
    public void Read_ShowsATabForEachLibraryThatHasFolders()
    {
        UseBrowseLibraries(
        [
            new(Guid.Parse("c0000000-0000-0000-0000-000000000001"), "Books", "books", "read", "Books", true),
            new(Guid.Parse("c0000000-0000-0000-0000-000000000002"), "Comics", "comics", "read", "Comics", false),
        ]);

        var cut = RenderLanePage<ReadPage>("/read", null);

        cut.WaitForAssertion(() => Assert.Equal(["/read/books"], TabRoutes(cut, "/read")));
    }
}
