using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Tests;

/// <summary>Videos appear in Watch as one card per series and one per standalone video, and play like episodes.</summary>
public sealed class VideoDisplayTests
{
    private static DisplayWorkRow Video(Guid? root, string title, string? season = null, string? episode = null, string? show = null, string mediaType = "Videos", string? cover = null, string? rootCover = null, DateTimeOffset? createdAt = null) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = root ?? Guid.Empty,
            MediaType = mediaType,
            CreatedAt = createdAt ?? DateTimeOffset.Parse("2026-10-01T12:00:00Z"),
            CoverUrl = cover,
            RootCoverUrl = rootCover,
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
    public void ACourseFolderCard_OpensAsAFolderAndCountsSectionsAndVideos_NeverSeasonsOrEpisodes()
    {
        var root = Guid.NewGuid();
        var works = new[]
        {
            Video(root, "Welcome", "1", "1", "Python Course"),
            Video(root, "Variables", "1", "2", "Python Course"),
            Video(root, "Loops", "2", "1", "Python Course"),
        };

        var card = Assert.Single(new DisplayCardBuilder().BuildVideoCards(works));

        Assert.Equal("Open folder", Assert.Single(card.Actions).Label);
        Assert.Equal("2 sections", card.Subtitle);
        Assert.Contains("3 videos", card.Facts);
        Assert.Equal("Video folder", card.GroupSummary!.RelationshipLabel);
        Assert.All(card.PreviewItems, item => Assert.Null(item.Position));
        var said = card.Facts.Append(card.Subtitle).Append(card.Actions[0].Label).Append(card.GroupSummary.RelationshipLabel);
        Assert.All(said, text => Assert.False(VideoFolderWording.UsesTvWording(text), $"The card says \"{text}\"."));
    }

    [Fact]
    public void AVideoFolderWithNoCoverOfItsOwn_BorrowsTheFirstVideosFrame()
    {
        var root = Guid.NewGuid();
        var first = Video(root, "Welcome", "1", "1", "Python Course", cover: "/stream/artwork/first-frame");
        var later = Video(root, "Variables", "1", "2", "Python Course", cover: "/stream/artwork/later-frame", createdAt: first.CreatedAt.AddDays(1));

        var card = Assert.Single(new DisplayCardBuilder().BuildVideoCards([later, first]));

        Assert.Equal("/stream/artwork/first-frame", card.Artwork.CoverUrl);
    }

    [Fact]
    public void AVideoFolderWithItsOwnCover_KeepsIt()
    {
        var root = Guid.NewGuid();
        var works = new[]
        {
            Video(root, "Welcome", "1", "1", "Python Course", cover: "/stream/artwork/frame", rootCover: "/stream/artwork/chosen"),
            Video(root, "Variables", "1", "2", "Python Course", rootCover: "/stream/artwork/chosen"),
        };

        var card = Assert.Single(new DisplayCardBuilder().BuildVideoCards(works));

        Assert.Equal("/stream/artwork/chosen", card.Artwork.CoverUrl);
    }

    [Fact]
    public void ACourseWithOneSectionCountsOnlyItsVideos()
    {
        var root = Guid.NewGuid();
        var works = new[] { Video(root, "Welcome", "1", "1", "Python Course"), Video(root, "Variables", "1", "2", "Python Course") };

        var card = Assert.Single(new DisplayCardBuilder().BuildVideoCards(works));

        Assert.Equal("2 videos", card.Subtitle);
    }

    [Fact]
    public void ATvShowCard_KeepsOpenShowSeasonsAndEpisodes()
    {
        var root = Guid.NewGuid();
        var works = new[]
        {
            Video(root, "Pilot", "1", "1", "Le Continental", "TV"),
            Video(root, "Second", "2", "1", "Le Continental", "TV"),
        };

        var card = Assert.Single(new DisplayCardBuilder().BuildTvShowCards(works));

        Assert.Equal("Open Show", Assert.Single(card.Actions).Label);
        Assert.Equal("2 seasons", card.Subtitle);
        Assert.Contains("2 episodes", card.Facts);
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
