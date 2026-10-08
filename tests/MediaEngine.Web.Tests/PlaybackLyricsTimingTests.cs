using MediaEngine.Web.Services.Playback;
namespace MediaEngine.Web.Tests;

public sealed class PlaybackLyricsTimingTests
{
    [Fact]
    public void EnhancedTimingStripsTagsAndKeepsExplicitEnd()
    {
        var line = Assert.Single(PlaybackLyricsParser.Parse("[offset:250]\n[00:06]<00:06>Hello <00:07>world<00:08>"));
        Assert.Equal("Hello world", line.Text); Assert.Equal(5.75, line.StartSeconds);
        Assert.Equal(2, line.Words!.Count); Assert.Equal(6.75, line.Words[0].EndSeconds);
        Assert.True(line.Words[1].ExplicitEnd); Assert.Equal(7.75, line.Words[1].EndSeconds);
    }
    [Theory]
    [InlineData("[00:01]<bad>plain text")]
    [InlineData("[00:01]<00:03>plain <00:02>text")]
    [InlineData("[00:01]<00:02>plain <00:02>text")]
    public void BadWordTimingDegradesToLineTiming(string source)
    {
        var line = Assert.Single(PlaybackLyricsParser.Parse(source)); Assert.DoesNotContain("<", line.Text); Assert.Null(line.Words);
    }
    [Fact]
    public void TimelineUsesOnlyExplicitMidSongEvidence()
    {
        var lines = PlaybackLyricsParser.Parse("[00:06]<00:06>Hello<00:08>\n[00:14]Next\n[00:20]\n[00:26]Last\n[00:30]");
        var timeline = PlaybackLyricsTimeline.Build(lines, 40, new());
        var gaps = timeline.Where(line => line.IsInstrumental).ToArray();
        Assert.Equal(new double?[] { 0, 8, 20, 30 }, gaps.Select(gap => gap.StartSeconds));
        Assert.Equal(new double?[] { 6, 14, 26, 40 }, gaps.Select(gap => gap.EndSeconds));
        Assert.DoesNotContain(PlaybackLyricsTimeline.Build(PlaybackLyricsParser.Parse("[00:00]Long line\n[01:00]Next"), 90, new()), line => line.IsInstrumental);
    }
    [Fact] public void EmptySongHasNoInferredGaps() => Assert.Empty(PlaybackLyricsTimeline.Build([], 60, new()));
}
