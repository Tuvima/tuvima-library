using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Events part of System > Logs: the flat, paged, searchable Activity log.</summary>
public sealed class ActivityLogPanelTests : AsyncBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly List<(string Category, string? Search, int Offset, int Limit)> _requests = [];
    private Func<string, string?, int, int, PagedResponse<ActivityLogEntryDto>?> _onLog =
        (_, _, offset, limit) => new PagedResponse<ActivityLogEntryDto>([], offset, limit, false, 0);

    public ActivityLogPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetActivityLogAsync), args =>
            {
                var request = ((string)args![0]!, (string?)args[1], (int)args[2]!, (int)args[3]!);
                _requests.Add(request);
                return Task.FromResult(_onLog(request.Item1, request.Item2, request.Item3, request.Item4));
            });
        }));
    }

    [Fact]
    public void Panel_OffersTheFiveFilters_InOrder()
    {
        var cut = Render<ActivityLogPanel>();

        var chips = cut.FindAll(".app-segmented-control__item").Select(chip => chip.TextContent.Trim()).ToArray();
        Assert.Equal(["Added", "Needs Review", "Failed", "System", "All"], chips);
    }

    [Fact]
    public void Panel_WithNoFilter_AsksForEverythingFromTheFirstPage()
    {
        Render<ActivityLogPanel>();

        var request = Assert.Single(_requests);
        Assert.Equal(("all", (string?)null, 0, 50), request);
    }

    [Fact]
    public void Panel_OpenedOnAFilter_AsksTheEngineForThatCategory()
    {
        var cut = Render<ActivityLogPanel>(parameters => parameters.Add(p => p.Filter, "added"));

        Assert.Equal("added", Assert.Single(_requests).Category);
        Assert.Equal("true", Chip(cut, "Added").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Panel_ChangingTheFilter_AsksAgainFromTheFirstPage()
    {
        _onLog = (_, _, offset, limit) => Page(Entries(50), offset, limit, hasMore: true, total: 120);
        var cut = Render<ActivityLogPanel>();
        Button(cut, "Next").Click();
        Assert.Equal(50, _requests[^1].Offset);

        Chip(cut, "Failed").Click();

        cut.WaitForAssertion(() => Assert.Equal(("failed", (string?)null, 0, 50), _requests[^1]));
    }

    [Fact]
    public void Panel_Searching_AsksWithTheTermFromTheFirstPage()
    {
        _onLog = (_, _, offset, limit) => Page(Entries(50), offset, limit, hasMore: true, total: 120);
        var cut = Render<ActivityLogPanel>();
        Button(cut, "Next").Click();

        cut.Find("input").Input("Dune");

        cut.WaitForAssertion(() => Assert.Equal(("all", (string?)"Dune", 0, 50), _requests[^1]), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Rows_ShowTheEnginesTitleDetailAndMediaType_Verbatim()
    {
        _onLog = (_, _, offset, limit) => Page(
            [
                Entry(1, "added", "Dune: Part Two", "Added to Movies from /films/dune2.mkv", mediaType: "Movies"),
                Entry(2, "failed", "Couldn't read dune.epub", "The file is damaged"),
            ],
            offset, limit, hasMore: false, total: 2);

        var cut = Render<ActivityLogPanel>();

        var rows = cut.FindAll("[data-testid=activity-log-row]");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Dune: Part Two", rows[0].QuerySelector(".activity-log__title")!.TextContent);
        Assert.Equal("Added to Movies from /films/dune2.mkv", rows[0].QuerySelector(".activity-log__detail")!.TextContent);
        Assert.Equal("Movies", rows[0].QuerySelector(".activity-log__media-type")!.TextContent);
        Assert.Equal("failed", rows[1].GetAttribute("data-category"));
        Assert.Null(rows[1].QuerySelector(".activity-log__media-type"));
        Assert.NotNull(rows[0].QuerySelector("time[title]"));
    }

    [Fact]
    public void ACountRow_ReadsLikeAnyOtherRow()
    {
        _onLog = (_, _, offset, limit) => Page(
            [Entry(1, "added", "3,000 photos", "Added to Photos", mediaType: "Photos", count: 3000)],
            offset, limit, hasMore: false, total: 1);

        var cut = Render<ActivityLogPanel>();

        var row = Assert.Single(cut.FindAll("[data-testid=activity-log-row]"));
        Assert.Equal("3,000 photos", row.QuerySelector(".activity-log__title")!.TextContent);
        Assert.Equal("Photos", row.QuerySelector(".activity-log__media-type")!.TextContent);
    }

    [Fact]
    public void ARowWithARoute_HasALinkedTitle()
    {
        _onLog = (_, _, offset, limit) => Page(
            [
                Entry(1, "added", "Dune", null, route: "/details/work/00000000-0000-0000-0000-000000000001"),
                Entry(2, "system", "Couldn't find FFmpeg", null),
            ],
            offset, limit, hasMore: false, total: 2);

        var cut = Render<ActivityLogPanel>();

        var rows = cut.FindAll("[data-testid=activity-log-row]");
        Assert.Equal("/details/work/00000000-0000-0000-0000-000000000001", rows[0].QuerySelector("a.activity-log__title")!.GetAttribute("href"));
        Assert.Null(rows[1].QuerySelector("a.activity-log__title"));
        Assert.Null(rows[1].QuerySelector(".activity-log__detail"));
    }

    [Fact]
    public void Pager_ShowsTheRangeAndTotal_AndMovesThroughThePages()
    {
        _onLog = (_, _, offset, limit) => offset == 0
            ? Page(Entries(50), offset, limit, hasMore: true, total: 1234)
            : Page(Entries(50), offset, limit, hasMore: false, total: 1234);
        var cut = Render<ActivityLogPanel>();

        Assert.Equal("1–50 of 1,234", cut.Find(".activity-log__pager-summary").TextContent);
        Assert.True(Button(cut, "Previous").HasAttribute("disabled"));
        Assert.False(Button(cut, "Next").HasAttribute("disabled"));

        Button(cut, "Next").Click();

        cut.WaitForAssertion(() => Assert.Equal("51–100 of 1,234", cut.Find(".activity-log__pager-summary").TextContent));
        Assert.Equal(50, _requests[^1].Offset);
        Assert.True(Button(cut, "Next").HasAttribute("disabled"));
        Assert.False(Button(cut, "Previous").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("added", "Nothing added yet")]
    [InlineData("review", "Nothing needs review")]
    [InlineData("failed", "No failures")]
    [InlineData("system", "No system events")]
    [InlineData("all", "No activity yet")]
    public void AnEmptyFilter_SaysSoInPlainWords(string filter, string expected)
    {
        var cut = Render<ActivityLogPanel>(parameters => parameters.Add(p => p.Filter, filter));

        Assert.Contains(expected, cut.Find("[data-testid=activity-log-empty]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchWithNoMatches_NamesTheTerm()
    {
        var cut = Render<ActivityLogPanel>();

        cut.Find("input").Input("zzz");

        cut.WaitForAssertion(
            () => Assert.Contains("Nothing matches “zzz”", cut.Find("[data-testid=activity-log-empty]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void WhenTheEngineDoesNotAnswer_ShowsTheNotRespondingMessage_AndRetryAsksAgain()
    {
        _onLog = (_, _, _, _) => null;
        var cut = Render<ActivityLogPanel>();

        Assert.Contains("The Library's engine isn't responding", cut.Find("[data-testid=activity-log-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=activity-log-row]"));

        _onLog = (_, _, offset, limit) => Page([Entry(1, "added", "Dune", null)], offset, limit, hasMore: false, total: 1);
        cut.Find("[data-testid=activity-log-error] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=activity-log-row]")));
        Assert.Equal(2, _requests.Count);
    }

    private static AngleSharp.Dom.IElement Chip(IRenderedComponent<ActivityLogPanel> cut, string label) =>
        cut.FindAll(".app-segmented-control__item").Single(chip => chip.TextContent.Trim() == label);

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<ActivityLogPanel> cut, string label) =>
        cut.FindAll(".activity-log__pager button").Single(button => button.TextContent.Trim() == label);

    private static PagedResponse<ActivityLogEntryDto> Page(
        IReadOnlyList<ActivityLogEntryDto> items, int offset, int limit, bool hasMore, int total) =>
        new(items, offset, limit, hasMore, total);

    private static List<ActivityLogEntryDto> Entries(int count) =>
        Enumerable.Range(1, count).Select(index => Entry(index, "added", $"Title {index}", null)).ToList();

    private static ActivityLogEntryDto Entry(
        long id, string category, string title, string? detail,
        string? mediaType = null, int? count = null, string? route = null) =>
        new()
        {
            Id = id,
            OccurredAt = Now.AddMinutes(-id),
            Category = category,
            Kind = "MediaAdded",
            Title = title,
            Detail = detail,
            MediaType = mediaType,
            Count = count,
            Route = route,
        };
}
