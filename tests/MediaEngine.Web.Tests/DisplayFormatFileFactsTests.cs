using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Tests;

/// <summary>The size, bitrate and date wording on the Details tab file facts.</summary>
public sealed class DisplayFormatFileFactsTests
{
    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1_536L, "1.5 KB")]
    [InlineData(327_155_712L, "312 MB")]
    [InlineData(1_503_238_554L, "1.4 GB")]
    [InlineData(3_221_225_472L, "3 GB")]
    [InlineData(1_099_511_627_776L, "1 TB")]
    public void FileSizeReadsLikeAPersonWouldSayIt(long bytes, string expected) =>
        Assert.Equal(expected, DisplayFormat.FormatFileSize(bytes));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void NoSizeMeansNothingToShow(long bytes) => Assert.Null(DisplayFormat.FormatFileSize(bytes));

    [Theory]
    [InlineData(6_200_000L, "6.2 Mbps")]
    [InlineData(1_000_000L, "1 Mbps")]
    [InlineData(256_000L, "256 kbps")]
    [InlineData(64_400L, "64 kbps")]
    [InlineData(300L, "1 kbps")]
    public void BitrateUsesMbpsFromOneMegabitAndKbpsBelow(long bitsPerSecond, string expected) =>
        Assert.Equal(expected, DisplayFormat.FormatBitrate(bitsPerSecond));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void NoBitrateMeansNothingToShow(long bitsPerSecond) => Assert.Null(DisplayFormat.FormatBitrate(bitsPerSecond));

    [Fact]
    public void TheAddedDateIsAPlainUtcDateSoItNeverShiftsWithTheViewersClock()
    {
        var late = new DateTimeOffset(2026, 10, 16, 23, 30, 0, TimeSpan.Zero);
        var sameInstantElsewhere = new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.FromHours(10));

        Assert.Equal("2026-10-16", DisplayFormat.FormatPlainDate(late));
        Assert.Equal("2026-10-16", DisplayFormat.FormatPlainDate(sameInstantElsewhere));
    }

    [Theory]
    [InlineData("eng", "English")]
    [InlineData("en", "English")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("jpn", "Japanese")]
    [InlineData("fre", "French")]
    [InlineData("fra", "French")]
    [InlineData("ger", "German")]
    [InlineData("chi", "Chinese")]
    [InlineData("dut", "Dutch")]
    [InlineData("cze", "Czech")]
    [InlineData("gre", "Greek")]
    [InlineData("rum", "Romanian")]
    [InlineData("slo", "Slovak")]
    [InlineData("wel", "Welsh")]
    [InlineData("baq", "Basque")]
    [InlineData("per", "Persian")]
    [InlineData("mac", "Macedonian")]
    [InlineData("mao", "Maori")]
    [InlineData("tib", "Tibetan")]
    [InlineData("alb", "Albanian")]
    [InlineData("arm", "Armenian")]
    [InlineData("geo", "Georgian")]
    [InlineData("bur", "Burmese")]
    [InlineData("ice", "Icelandic")]
    [InlineData("GER", "German")]
    [InlineData("xyz", "xyz")]
    public void LanguageCodesReadAsNamesIncludingTheBibliographicOnesFfmpegWrites(string code, string expected)
    {
        var name = DisplayFormat.FormatLanguageName(code);

        // Portuguese and Japanese depend on the host knowing them; the explicit map must never depend on it.
        Assert.StartsWith(expected, name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("und")]
    [InlineData("UND")]
    [InlineData("zxx")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void UndeterminedOrMissingLanguageShowsNothing(string? code) => Assert.Null(DisplayFormat.FormatLanguageName(code));
}
