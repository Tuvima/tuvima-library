using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Details;

namespace MediaEngine.Api.Tests;

/// <summary>Videos appear in Watch as one card per series and one per standalone video, and play like episodes.</summary>
public sealed class VideoDisplayTests
{
    private static DisplayWorkRow Video(Guid? root, string title, string? season = null, string? episode = null, string? show = null) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = root ?? Guid.Empty,
            MediaType = "Videos",
            CreatedAt = DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
            Title = title,
            ShowName = show,
            SeasonNumber = season,
            EpisodeNumber = episode,
        };

    [Fact]
    public void VideosAreAWatchKindAndMapBackToTheVideosTab()
    {
        Assert.True(DisplayMediaRules.IsWatchKind("Videos"));
        Assert.Equal("Video", DisplayMediaRules.NormalizeDisplayKind("Videos"));
        Assert.Equal("Videos", DisplayMediaRules.NormalizeMediaType("Videos"));
    }

    [Fact]
    public void BuildVideoCards_GroupsSeriesIntoOneCardAndKeepsStandaloneVideosApart()
    {
        var seriesRoot = Guid.NewGuid();
        var standalone = Video(null, "Sample Talk");
        var works = new[]
        {
            Video(seriesRoot, "Welcome", "1", "1", "Python Course"),
            Video(seriesRoot, "Variables", "1", "2", "Python Course"),
            standalone,
        };

        var cards = new DisplayCardBuilder().BuildVideoCards(works);

        Assert.Equal(2, cards.Count);
        var series = Assert.Single(cards, card => card.Title == "Python Course");
        Assert.Equal("Videos", series.MediaType);
        Assert.Equal($"/details/tvshow/{seriesRoot:D}?context=watch", series.Actions[0].WebUrl);
        Assert.Equal(2, series.PreviewTotalCount);
        var single = Assert.Single(cards, card => card.Title == "Sample Talk");
        Assert.Equal("Video", single.MediaType);
        Assert.Equal(standalone.WorkId, single.WorkId);
        Assert.Contains(single.Actions, action => action.WebUrl == $"/watch/player/{standalone.WorkId:D}");
    }

    [Fact]
    public void BuildTvShowCards_IgnoresVideos()
    {
        var works = new[] { Video(Guid.NewGuid(), "Welcome", "1", "1", "Python Course") };

        Assert.Empty(new DisplayCardBuilder().BuildTvShowCards(works));
    }

    [Fact]
    public void NextEpisodes_ListsTheRestOfAVideoSeriesInOrder()
    {
        var root = Guid.NewGuid();
        var first = Video(root, "One", "1", "1", "Course");
        var second = Video(root, "Two", "1", "2", "Course");
        var third = Video(root, "Ten", "2", "1", "Course");

        var plan = NextEpisodesPlanner.Plan(root, first.AssetId, 5, [third, second, first]);

        Assert.NotNull(plan);
        Assert.Equal([second.WorkId.ToString("D"), third.WorkId.ToString("D")], plan!.Episodes.Select(item => item.WorkId.ToString("D")));
    }
}
