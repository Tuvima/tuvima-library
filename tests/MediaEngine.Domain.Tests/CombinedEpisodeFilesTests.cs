using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class CombinedEpisodeFilesTests
{
    [Fact]
    public void Label_OfTwoEpisodesInOneFile_ReadsAsOneEntryWithBothTitles()
    {
        var label = CombinedEpisodeFiles.Label([(19, "Title A"), (20, "Title B")]);

        Assert.Equal("E19-20: Title A + Title B", label);
    }

    [Fact]
    public void Label_ListsEpisodesInNumberOrderWhateverOrderTheyArriveIn()
    {
        var label = CombinedEpisodeFiles.Label([(20, "Title B"), (19, "Title A")]);

        Assert.Equal("E19-20: Title A + Title B", label);
    }

    [Fact]
    public void Label_OfOneEpisode_HasNoRange()
    {
        Assert.Equal("E19: Title A", CombinedEpisodeFiles.Label([(19, "Title A")]));
    }

    [Fact]
    public void Label_LeavesOutBlankTitles()
    {
        Assert.Equal("E19-20: Title A", CombinedEpisodeFiles.Label([(19, "Title A"), (20, " ")]));
    }

    [Theory]
    [InlineData("E19-20", 19, 20)]
    [InlineData("E1-3", 1, 2, 3)]
    [InlineData("E5", 5)]
    [InlineData("E19, E21", 19, 21)]
    [InlineData("E1-2, E5", 1, 2, 5)]
    public void NumberText_JoinsAnUnbrokenRunAndNamesGapsApart(string expected, params int[] numbers)
    {
        Assert.Equal(expected, CombinedEpisodeFiles.NumberText(numbers));
    }

    [Fact]
    public void Count_OfTwentyEpisodesWhereOneFileCoversTwo_IsTwentyEpisodesInNineteenFiles()
    {
        var episodes = Enumerable.Range(1, 20)
            .Select(number => (EpisodeId: $"work-{number}", FileId: (string?)(number == 20 ? "file-19" : $"file-{number}")))
            .ToList();

        var (episodeCount, fileCount) = CombinedEpisodeFiles.Count(episodes);

        Assert.Equal(20, episodeCount);
        Assert.Equal(19, fileCount);
    }

    [Fact]
    public void Count_CountsAnEpisodeSeenTwiceOnce()
    {
        var (episodes, files) = CombinedEpisodeFiles.Count([("work-1", "file-1"), ("work-1", "file-1")]);

        Assert.Equal(1, episodes);
        Assert.Equal(1, files);
    }

    [Fact]
    public void Count_GivesAnEpisodeWithNoFileKeyItsOwnFile()
    {
        var (episodes, files) = CombinedEpisodeFiles.Count([("work-1", null), ("work-2", " ")]);

        Assert.Equal(2, episodes);
        Assert.Equal(2, files);
    }

    [Fact]
    public void CountText_WhenAFileCoversTwoEpisodes_SaysEpisodesAndFiles()
    {
        Assert.Equal("20 episodes · 19 files", CombinedEpisodeFiles.CountText(20, 19));
    }

    [Fact]
    public void CountText_WhenEveryEpisodeIsItsOwnFile_SaysEpisodesOnly()
    {
        Assert.Equal("20 episodes", CombinedEpisodeFiles.CountText(20, 20));
        Assert.Equal("1 episode", CombinedEpisodeFiles.CountText(1, 1));
        Assert.Null(CombinedEpisodeFiles.CountText(0, 0));
    }
}
