using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A library's Activity tab (Library pages WP7, D16): the Engine's plain sentences with a time and an icon per kind, Needs review lines that
// link to Needs Review, an empty state, and loading and error states with Try again.
public sealed class LibraryActivityTabTests : AsyncBunitContext
{
    private const string LibraryId = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly List<(string LibraryId, int Take)> _asked = [];
    private LibraryActivityDto? _activity;
    private TaskCompletionSource<LibraryActivityDto?>? _pending;

    public LibraryActivityTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryActivityAsync), args =>
            {
                _asked.Add(((string)args![0]!, (int)args[1]!));
                return _pending is { } pending ? pending.Task : Task.FromResult(_activity);
            });
        }));
    }

    private static LibraryFolderDto Library() => new() { Id = LibraryId, Name = "Movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"] };

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static LibraryActivityEntryDto Entry(string kind, string text, int minutesAgo = 0, bool needsReview = false, string? route = null) =>
        new($"e-{kind}-{minutesAgo}", Now.AddMinutes(-minutesAgo), kind, text, needsReview, route);

    private IRenderedComponent<LibraryActivityTab> Open() => Render<LibraryActivityTab>(parameters => parameters.Add(x => x.Library, Library()));

    [Fact]
    public void TheTabListsTheEnginesSentences_NewestFirst_WithAnIconAndATimeOnEachLine()
    {
        _activity = new LibraryActivityDto(Guid.Parse(LibraryId),
        [
            Entry(LibraryActivityKinds.Added, "Added Dune.", 1),
            Entry(LibraryActivityKinds.Failed, "Couldn't take in broken.mkv: Corrupt file.", 5),
            Entry(LibraryActivityKinds.Scan, "Checked films: 126 files looked at, 3 not matched.", 9),
        ]);

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=library-activity-row]").Count));
        var rows = cut.FindAll("[data-testid=library-activity-row]");
        Assert.Contains("Added Dune.", Text(rows[0]), StringComparison.Ordinal);
        Assert.Contains("Couldn't take in broken.mkv", Text(rows[1]), StringComparison.Ordinal);
        Assert.Equal(["added", "failed", "scan"], rows.Select(row => row.GetAttribute("data-kind") ?? string.Empty).ToArray());
        Assert.All(rows, row =>
        {
            Assert.NotEmpty(row.QuerySelectorAll("time[datetime]"));
            Assert.NotEmpty(row.QuerySelectorAll(".library-activity__icon svg"));
        });
        Assert.Equal([(LibraryId, 100)], _asked);
    }

    [Fact]
    public void ANeedsReviewLineLinksToNeedsReview_AndOtherLinesHaveNoLink()
    {
        _activity = new LibraryActivityDto(Guid.Parse(LibraryId),
        [
            Entry(LibraryActivityKinds.Review, "Arrival needs review.", 1, needsReview: true, route: "/settings/review"),
            Entry(LibraryActivityKinds.Added, "Added Dune.", 2),
        ]);

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=library-activity-row]").Count));
        var link = Assert.Single(cut.FindAll("[data-testid=library-activity-row] a"));
        Assert.Equal("Needs review", Text(link));
        Assert.Equal("/settings/review", link.GetAttribute("href"));
    }

    [Fact]
    public void WithNoEvents_TheTabSaysNothingHasHappenedYet()
    {
        _activity = new LibraryActivityDto(Guid.Parse(LibraryId), []);

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Contains("Nothing has happened in this library yet", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=library-activity-row]"));
    }

    [Fact]
    public void WhileLoading_TheTabShowsALoadingState()
    {
        _pending = new TaskCompletionSource<LibraryActivityDto?>();

        var cut = Open();

        Assert.Contains("Loading activity", cut.Markup, StringComparison.Ordinal);
        _pending.SetResult(new LibraryActivityDto(Guid.Parse(LibraryId), []));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading activity", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheEngineCannotAnswer_TheTabShowsAnErrorWithTryAgain_AndTryAgainReloads()
    {
        _activity = null;
        var cut = Open();
        cut.WaitForAssertion(() => Assert.Contains("Activity could not be loaded", cut.Markup, StringComparison.Ordinal));

        _activity = new LibraryActivityDto(Guid.Parse(LibraryId), [Entry(LibraryActivityKinds.Added, "Added Dune.")]);
        cut.FindAll("button").Single(button => Text(button) == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Contains("Added Dune.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(2, _asked.Count);
    }
}
