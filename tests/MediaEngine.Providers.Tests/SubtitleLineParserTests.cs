using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class SubtitleLineParserTests
{
    [Fact]
    public void Srt_ReadsTimesAndPlainText()
    {
        const string srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Frodo</i> sets out.\r\n\r\n2\r\n01:02:03,004 --> 01:02:05,000\r\nOne ring\r\nto rule them all.\r\n";

        var lines = SubtitleLineParser.Parse(srt, "srt");

        Assert.Equal(2, lines.Count);
        Assert.Equal(1_500, lines[0].StartMs);
        Assert.Equal(3_000, lines[0].EndMs);
        Assert.Equal("Frodo sets out.", lines[0].Text);
        Assert.Equal(3_723_004, lines[1].StartMs);
        Assert.Equal("One ring to rule them all.", lines[1].Text);
        Assert.Equal([0, 1], lines.Select(line => line.Index));
    }

    [Fact]
    public void WebVtt_SkipsHeaderNotesStylesAndCueSettings()
    {
        const string vtt = """
            WEBVTT - a film

            NOTE written by hand
            00:00:00.000 --> 00:00:01.000

            STYLE
            ::cue { color: red }

            intro
            00:05.250 --> 00:07.5 align:start position:10%
            <c.yellow>Welcome</c> to &amp; Mordor.
            """;

        var lines = SubtitleLineParser.Parse(vtt);

        var line = Assert.Single(lines);
        Assert.Equal(5_250, line.StartMs);
        Assert.Equal(7_500, line.EndMs);
        Assert.Equal("Welcome to & Mordor.", line.Text);
    }

    [Fact]
    public void Ass_ReadsDialogueLinesWithoutStyleOverrides()
    {
        const string ass = """
            [Script Info]
            Title: x

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:10.00,0:00:12.50,Default,,0,0,0,,{\i1}Gandalf{\i0}, wait.\NWait!
            """;

        var lines = SubtitleLineParser.Parse(ass, "ass");

        var line = Assert.Single(lines);
        Assert.Equal(10_000, line.StartMs);
        Assert.Equal(12_500, line.EndMs);
        Assert.Contains("Gandalf", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OutOfOrderCuesAreSortedAndEmptyCuesDropped()
    {
        const string srt = "1\n00:00:09,000 --> 00:00:10,000\nLater\n\n2\n00:00:01,000 --> 00:00:02,000\nEarlier\n\n3\n00:00:03,000 --> 00:00:04,000\n<i></i>\n";

        var lines = SubtitleLineParser.Parse(srt);

        Assert.Equal(["Earlier", "Later"], lines.Select(line => line.Text));
        Assert.Equal([0, 1], lines.Select(line => line.Index));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a subtitle file at all")]
    public void NothingReadable_GivesNoLines(string content)
    {
        Assert.Empty(SubtitleLineParser.Parse(content));
    }

    [Theory]
    [InlineData("00:00:01,500", 1_500)]
    [InlineData("1:02:03.4", 3_723_400)]
    [InlineData("02:03.45", 123_450)]
    public void Timestamps_ReadIntoMilliseconds(string value, long expected)
    {
        Assert.True(SubtitleLineParser.TryReadTimestamp(value, out var ms));
        Assert.Equal(expected, ms);
    }
}
