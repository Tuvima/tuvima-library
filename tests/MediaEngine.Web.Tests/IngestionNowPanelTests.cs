using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Now panel and the Added summary as the page renders them.
/// </summary>
public sealed class IngestionNowPanelTests : AsyncBunitContext
{
    public IngestionNowPanelTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public void AMovieAShowAnAlbumAndABook_RenderTheSameFiveColumnsInTheSameOrder()
    {
        var cut = Render<IngestionNowPanel>(parameters => parameters
            .Add(panel => panel.Active, true)
            .Add(panel => panel.Total, 4)
            .Add(panel => panel.Items, new[] { Row(1, "Movies"), Row(2, "TV"), Row(3, "Music"), Row(4, "Books") }));

        Assert.Equal(
            ["Title", "Type", "Step", "Status", "Details"],
            cut.FindAll(".ingestion-now__head [role='columnheader']").Select(header => header.TextContent).ToArray());

        var rows = cut.FindAll(".ingestion-media-list-row");
        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(
                ["ingestion-media-list-row__identity", "ingestion-media-list-row__format", "ingestion-media-list-row__step", "ingestion-media-list-row__status", "ingestion-media-list-row__details"],
                row.QuerySelectorAll("[role='cell']").Select(cell => cell.ClassList.First()).ToArray());
            Assert.Equal(5, row.QuerySelectorAll(".ingestion-facets__slot").Length);
        }
    }

    [Fact]
    public void TheHeadingRowAndEveryItemRow_ShareOneColumnTemplate()
    {
        var panelCss = ReadSource("Components/Settings/IngestionNowPanel.razor.css");
        var rowCss = ReadSource("Components/Settings/IngestionMediaCard.razor.css");

        Assert.Contains(".ingestion-now__head {\r\n    display: grid;\r\n    grid-template-columns: var(--ingestion-now-columns);", panelCss.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: var(--ingestion-now-columns,", rowCss, StringComparison.Ordinal);
        // The template is defined once on the panel, at each of the three widths.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(panelCss, @"--ingestion-now-columns:").Count);
    }

    [Fact]
    public void ThreeThousandPhotos_ShowOneCountLineAndNoFileRows()
    {
        var cut = Render<IngestionNowPanel>(parameters => parameters
            .Add(panel => panel.Active, true)
            .Add(panel => panel.PersonalMedia, new IngestionPersonalMediaProgressDto { Photos = 3000 }));

        Assert.Equal("3,000 photos in progress", cut.Find(".ingestion-now__personal").TextContent);
        Assert.Empty(cut.FindAll(".ingestion-media-list-row"));
        Assert.Empty(cut.FindAll("[role='row']"));
    }

    [Theory]
    [InlineData(2300, 0, "2,300 photos in progress")]
    [InlineData(0, 12, "12 personal videos in progress")]
    [InlineData(1, 1, "1 photo and 1 personal video in progress")]
    [InlineData(2300, 12, "2,300 photos and 12 personal videos in progress")]
    public void PhotosAndPersonalVideos_AreOneCountLineWorded(int photos, int videos, string line)
    {
        Assert.Equal(line, IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto { Photos = photos, Videos = videos }));
    }

    [Fact]
    public void WithNoPhotosOrVideos_TheCountLineIsHidden()
    {
        Assert.Null(IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto()));

        var cut = Render<IngestionNowPanel>(parameters => parameters
            .Add(panel => panel.Active, true)
            .Add(panel => panel.Total, 1)
            .Add(panel => panel.Items, new[] { Row(1, "Movies") }));

        Assert.Empty(cut.FindAll(".ingestion-now__personal"));
    }

    [Fact]
    public void TheCountUnderTheHeading_IsTheEnginesTotalNotTheNumberOfRows()
    {
        var cut = Render<IngestionNowPanel>(parameters => parameters
            .Add(panel => panel.Active, true)
            .Add(panel => panel.Total, 212)
            .Add(panel => panel.Items, Enumerable.Range(0, 12).Select(number => Row(number, "Movies")).ToArray()));

        Assert.Equal("Now", cut.Find("#ingestion-now-title").TextContent);
        Assert.Contains("212 items being added", cut.Find(".ingestion-now > header p").TextContent, StringComparison.Ordinal);
        Assert.Equal(10, cut.FindAll(".ingestion-media-list-row").Count);
    }

    [Fact]
    public void WhenNothingIsHappening_ThePanelIsNotShown()
    {
        var cut = Render<IngestionNowPanel>(parameters => parameters.Add(panel => panel.Active, false));

        Assert.Empty(cut.FindAll(".ingestion-now"));
    }

    [Fact]
    public void AddedSummary_ShowsEachDaysCountsByTypeAsLinksToTheActivityLogOnAdded()
    {
        var today = new DateOnly(2026, 10, 19);
        var cut = Render<IngestionAddedSummary>(parameters => parameters
            .Add(summary => summary.TodayOverride, today)
            .Add(summary => summary.Days, new[]
            {
                Day(today, ("Movies", 212), ("TV", 140), ("Books", 31)),
                Day(today.AddDays(-1), ("Music", 1)),
                Day(today.AddDays(-2), ("Audiobooks", 2)),
            }));

        var lines = cut.FindAll(".ingestion-added__list li").Select(item => string.Join(' ', item.TextContent.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal(
            ["Today: 212 movies, 140 episodes, 31 books", "Yesterday: 1 song", "Oct 17: 2 audiobooks"],
            lines);
        var links = cut.FindAll(".ingestion-added__list a");
        Assert.Equal(5, links.Count);
        Assert.All(links, link => Assert.Equal("/settings/system/logs?tab=events&filter=added", link.GetAttribute("href")));
        Assert.Equal("/settings/system/logs?tab=events&filter=added", cut.Find(".ingestion-added > header a").GetAttribute("href"));
    }

    [Fact]
    public void AddedSummary_WithNoDays_SaysNothingWasAddedInTheLastThreeDays()
    {
        var cut = Render<IngestionAddedSummary>(parameters => parameters.Add(summary => summary.Days, Array.Empty<IngestionRecentDayDto>()));

        Assert.Equal("Nothing added in the last 3 days", cut.Find(".ingestion-added__empty").TextContent);
    }

    private static IngestionRecentDayDto Day(DateOnly date, params (string Type, int Count)[] counts) => new()
    {
        Date = date,
        TotalCount = counts.Sum(count => count.Count),
        TypeCounts = counts.Select(count => new IngestionTypeCountDto { MediaType = count.Type, Count = count.Count }).ToList(),
    };

    private static IngestionMediaGroupDto Row(int number, string mediaType) => new()
    {
        GroupId = new Guid(number + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        Title = $"{mediaType} {number}",
        MediaType = mediaType,
        Availability = "finishing",
        StatusLabel = "Adding people",
        UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-number),
        TextTracks = mediaType is "Music" or "Movies" or "TV"
            ? new() { State = "pending", Label = "Subtitles pending" }
            : IngestionFacetStateDto.NotApplicable("Subtitles"),
    };

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", relativePath)));
}
