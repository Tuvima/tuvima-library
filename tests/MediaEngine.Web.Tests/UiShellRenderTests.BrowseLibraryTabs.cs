using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
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

    // Replaces the Engine stub: the browse library list is what the test gives (null = it could not be read), and every browse call is kept.
    private void UseBrowseLibraries(IReadOnlyList<BrowseLibraryDto>? libraries)
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBrowseLibrariesAsync), _ =>
                Task.FromResult<IReadOnlyList<BrowseLibraryDto>?>(libraries));
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayBrowseAsync), args =>
            {
                _browseCalls.Add(args ?? []);
                return Task.FromResult<DisplayPageDto?>(new DisplayPageDto("browse", "Browse", null, null, [], []));
            });
        }));
    }

    // The positional arguments of IEngineApiClient.GetDisplayBrowseAsync.
    private const int BrowseMediaTypeArgument = 1;
    private const int BrowseLibraryIdArgument = 16;
    private const int BrowseUnmatchedArgument = 17;

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
    public void Watch_ShowsOneTabPerKindAndPerNamedLibraryThatHasFolders()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Equal(
            ["/watch/movies", "/watch/tv", "/watch/youtube", $"/watch/lib-{LecturesLibrary:N}"],
            TabRoutes(cut, "/watch")));
        Assert.Contains(">TV Shows<", cut.Markup);
        Assert.Contains(">YouTube<", cut.Markup);
        Assert.Contains(">Lectures<", cut.Markup);
        Assert.DoesNotContain(">Empty<", cut.Markup);
    }

    [Fact]
    public void Watch_OpeningANamedLibraryTabBrowsesWithThatLibrary()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>($"/watch/lib-{LecturesLibrary:N}", $"lib-{LecturesLibrary:N}");

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

        cut.WaitForAssertion(() => Assert.Equal([$"/watch/lib-{LecturesLibrary:N}"], TabRoutes(cut, "/watch")));
    }

    [Fact]
    public void Watch_KeepsItsFixedTabsWhenTheLibraryListCannotBeRead()
    {
        UseBrowseLibraries(null);

        var cut = RenderLanePage<WatchPage>("/watch", null);

        cut.WaitForAssertion(() => Assert.Equal(["/watch/movies", "/watch/tv", "/watch/videos"], TabRoutes(cut, "/watch")));
    }

    [Fact]
    public void Watch_AnOldVideosLinkOpensTheFirstVideosTab()
    {
        UseBrowseLibraries(WatchLibraries());

        var cut = RenderLanePage<WatchPage>("/watch/videos", "videos");

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(_browseCalls.Take(1));
            Assert.Equal(YouTubeLibrary, call[BrowseLibraryIdArgument]);
        });
    }

    [Fact]
    public void Watch_TheUnmatchedChipSendsUnmatchedWithTheTabsLibraryAndKeepsItInTheAddress()
    {
        UseBrowseLibraries(WatchLibraries());
        var route = $"/watch/lib-{LecturesLibrary:N}";
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderLanePage<WatchPage>(route, $"lib-{LecturesLibrary:N}");
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

        var cut = RenderLanePage<WatchPage>($"/watch/lib-{LecturesLibrary:N}?unmatched=true", $"lib-{LecturesLibrary:N}");

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
