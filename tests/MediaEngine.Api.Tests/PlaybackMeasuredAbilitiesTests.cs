using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>Players are measured, not assumed, and a failed start is learned (playback-path spec D12, D13).</summary>
public sealed class PlaybackMeasuredAbilitiesTests
{
    private static readonly Guid Profile = Guid.NewGuid();

    private static ClientCapabilitiesDto Measured(
        string[] containers, string[] video, string[]? tenBit = null, string[]? hardware = null, int schema = 2)
    {
        return new ClientCapabilitiesDto
        {
            SchemaVersion = schema,
            Containers = containers,
            VideoCodecs = video,
            AudioCodecs = ["aac", "mp3", "opus"],
            TenBitVideoCodecs = tenBit ?? [],
            HardwareDecodeVideoCodecs = hardware ?? [],
        };
    }

    private static PlaybackDecision Decide(
        ClientCapabilitiesDto capabilities, string extension, string video, string audio,
        string? pixelFormat = null, string? minimumRung = null)
    {
        var profile = PlaybackCapabilitiesService.ResolveProfile("web", capabilities);
        return PlaybackLadder.Decide(new PlaybackLadderInput(
            extension,
            profile,
            new MediaProbeResult { VideoCodec = video, AudioCodec = audio, Height = 1080, PixelFormat = pixelFormat },
            MinimumRung: minimumRung,
            TenBitVideoCodecs: capabilities.SchemaVersion >= 2 ? capabilities.TenBitVideoCodecs : null));
    }

    private static MediaProbeResult Probe(string video, string? pixelFormat = null) =>
        new() { VideoCodec = video, AudioCodec = "aac", Height = 1080, PixelFormat = pixelFormat };

    [Fact]
    public void ReportLackingHevc_MakesAnHevcTitleEncodeThePicture()
    {
        var report = Measured(["mp4", "webm"], ["h264", "vp9"]);

        var decision = Decide(report, ".mkv", "hevc", "aac");

        Assert.Equal(PlaybackRungs.EncodePicture, decision.Rung);
        Assert.False(decision.CopyVideo);
    }

    [Fact]
    public void ReportWithHevc_LetsAnHevcMkvBeRepackagedWithoutEncoding()
    {
        var report = Measured(["mp4"], ["h264", "hevc"], hardware: ["hevc"]);

        var decision = Decide(report, ".mkv", "hevc", "aac");

        Assert.Equal(PlaybackRungs.Repackage, decision.Rung);
        Assert.True(decision.CopyVideo);
    }

    [Fact]
    public void MkvIsDirectPlayedOnlyWhenTheBrowserReportsMkv()
    {
        var withMkv = Measured(["mp4", "mkv"], ["h264"]);
        var withoutMkv = Measured(["mp4"], ["h264"]);

        Assert.Equal(PlaybackRungs.Direct, Decide(withMkv, ".mkv", "h264", "aac").Rung);
        Assert.Equal(PlaybackRungs.Repackage, Decide(withoutMkv, ".mkv", "h264", "aac").Rung);
    }

    [Fact]
    public void TenBitHevc_EncodesWhenTheReportListsHevcButNotAtTenBits()
    {
        var eightBitOnly = Measured(["mp4"], ["h264", "hevc"], tenBit: []);
        var tenBitToo = Measured(["mp4"], ["h264", "hevc"], tenBit: ["hevc"]);

        Assert.Equal(PlaybackRungs.EncodePicture, Decide(eightBitOnly, ".mp4", "hevc", "aac", "yuv420p10le").Rung);
        Assert.Equal(PlaybackRungs.Direct, Decide(tenBitToo, ".mp4", "hevc", "aac", "yuv420p10le").Rung);
    }

    [Fact]
    public void AVersionOneList_NeverAssumesWhatItDidNotMeasure_SoTenBitHevcIsStillCopied()
    {
        var fixedList = Measured(["mp4"], ["h264", "hevc"], schema: 1);

        var decision = Decide(fixedList, ".mkv", "hevc", "aac", "yuv420p10le");

        Assert.Equal(PlaybackRungs.Repackage, decision.Rung);
    }

    [Theory]
    [InlineData(PlaybackRungs.Direct, PlaybackRungs.Repackage)]
    [InlineData(PlaybackRungs.Repackage, PlaybackRungs.ConvertSound)]
    [InlineData(PlaybackRungs.ConvertSound, PlaybackRungs.EncodePicture)]
    [InlineData(PlaybackRungs.EncodePicture, PlaybackRungs.EncodePicture)]
    public void AFailedRung_MovesTheNextStartDownOneRung(string failed, string expectedMinimum)
    {
        var limits = LearnedPlaybackLimits.Learn([], failed, "h264", "mp4", tenBit: false, DateTimeOffset.UtcNow);

        Assert.Equal(expectedMinimum, LearnedPlaybackLimits.MinimumRung(limits, ".mp4", Probe("h264")));
    }

    [Fact]
    public void AFailedDirectPlay_MakesTheNextStartOfThatKindOfFileUseTheNextRung()
    {
        var report = Measured(["mp4", "mkv"], ["h264", "hevc"]);
        var first = Decide(report, ".mkv", "hevc", "aac");
        Assert.Equal(PlaybackRungs.Direct, first.Rung);

        var learned = LearnedPlaybackLimits.Learn([], first.Rung, "hevc", "mkv", tenBit: false, DateTimeOffset.UtcNow);
        var minimum = LearnedPlaybackLimits.MinimumRung(learned, ".mkv", Probe("hevc"));
        var next = Decide(report, ".mkv", "hevc", "aac", minimumRung: minimum);

        Assert.Equal(PlaybackRungs.Repackage, next.Rung);
        Assert.True(next.CopyVideo);
        Assert.Contains("would not play as it is", next.Reason);
    }

    [Fact]
    public void AFailedStreamCopy_OfAPictureThatCannotBeCopied_EncodesTheNextStart()
    {
        var report = Measured(["mp4"], ["h264", "vp9"]);

        var decision = Decide(report, ".webm", "vp9", "opus", minimumRung: PlaybackRungs.Repackage);

        // WebM VP9 plays directly, so the learned minimum applies; a VP9 picture cannot be copied into a stream, so it is encoded.
        Assert.Equal(PlaybackRungs.EncodePicture, decision.Rung);
    }

    [Fact]
    public void ALimitOnOneKindOfFile_DoesNotChangeAnotherKind()
    {
        var limits = LearnedPlaybackLimits.Learn([], PlaybackRungs.Direct, "hevc", "mkv", tenBit: true, DateTimeOffset.UtcNow);

        Assert.Null(LearnedPlaybackLimits.MinimumRung(limits, ".mkv", Probe("hevc", "yuv420p")));
        Assert.Null(LearnedPlaybackLimits.MinimumRung(limits, ".mp4", Probe("hevc", "yuv420p10le")));
        Assert.Null(LearnedPlaybackLimits.MinimumRung(limits, ".mkv", Probe("h264", "yuv420p10le")));
        Assert.Equal(PlaybackRungs.Repackage, LearnedPlaybackLimits.MinimumRung(limits, ".mkv", Probe("hevc", "yuv420p10le")));
    }

    [Fact]
    public void LearningTheSameKindAgain_KeepsTheHeavierRungOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var once = LearnedPlaybackLimits.Learn([], PlaybackRungs.Repackage, "hevc", "mkv", tenBit: false, now);

        var lighter = LearnedPlaybackLimits.Learn(once, PlaybackRungs.Direct, "hevc", "mkv", tenBit: false, now);
        var heavier = LearnedPlaybackLimits.Learn(once, PlaybackRungs.ConvertSound, "hevc", "mkv", tenBit: false, now);

        Assert.Same(once, lighter);
        Assert.Equal(PlaybackRungs.ConvertSound, Assert.Single(heavier).FailedRung);
    }

    [Fact]
    public void TheSummaryReadsInPlainWords()
    {
        Assert.Equal("cannot play HEVC 10-bit MKV as it is", LearnedPlaybackLimits.SummaryFor(PlaybackRungs.Direct, "hevc", "mkv", true));
        Assert.Equal("cannot play H.264 from a copied stream", LearnedPlaybackLimits.SummaryFor(PlaybackRungs.Repackage, "h264", "mkv", false));
    }

    [Fact]
    public void Normalizing_IgnoresTheLearnedListAnAppSends_AndKeepsTheEnginesOwn()
    {
        var fromApp = new ClientCapabilitiesDto
        {
            SchemaVersion = 9,
            VideoCodecs = [" HEVC ", "hevc"],
            LearnedLimits = [new LearnedPlaybackLimitDto { VideoCodec = "av1" }],
        };
        var engines = LearnedPlaybackLimits.Learn([], PlaybackRungs.Direct, "hevc", "mkv", tenBit: false, DateTimeOffset.UtcNow);

        var normalized = ClientCapabilitiesNormalizer.Normalize(fromApp, engines);

        Assert.Equal(2, normalized.SchemaVersion);
        Assert.Equal(["hevc"], normalized.VideoCodecs);
        Assert.Equal("hevc", Assert.Single(normalized.LearnedLimits).VideoCodec);
    }

    [Fact]
    public void ABrowser_UsesItsOwnReport_AndLearnsPerBrowserSession()
    {
        var store = new BrowserPlaybackAbilityStore(TimeProvider.System);
        Assert.Null(store.TryGet(Profile, "abc123"));

        Assert.True(store.Set(Profile, "abc123", Measured(["mp4", "mkv"], ["h264", "hevc"], hardware: ["hevc"])));
        Assert.True(store.RecordLearnedLimit(Profile, "abc123", PlaybackRungs.Direct, "hevc", "mkv", tenBit: false));
        // A new report keeps what was learned.
        Assert.True(store.Set(Profile, "abc123", Measured(["mp4", "mkv"], ["h264", "hevc"])));

        var mine = store.TryGet(Profile, "abc123")!;
        Assert.Contains("mkv", mine.Containers);
        Assert.Single(mine.LearnedLimits);
        Assert.Null(store.TryGet(Guid.NewGuid(), "abc123"));
        Assert.Null(store.TryGet(Profile, "other"));
    }

    [Fact]
    public void ABrowserKeyThatIsNotPlain_IsRefused()
    {
        var store = new BrowserPlaybackAbilityStore(TimeProvider.System);

        Assert.False(store.Set(Profile, "../etc", Measured(["mp4"], ["h264"])));
        Assert.False(store.Set(Profile, string.Empty, Measured(["mp4"], ["h264"])));
        Assert.False(store.RecordLearnedLimit(Profile, "never-reported", PlaybackRungs.Direct, "hevc", "mkv", tenBit: false));
    }

    [Fact]
    public void ABrowserEntryNobodyUsesForHours_IsForgotten()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var store = new BrowserPlaybackAbilityStore(clock);
        store.Set(Profile, "old", Measured(["mp4"], ["h264"]));

        clock.Now += TimeSpan.FromHours(13);
        store.Set(Profile, "new", Measured(["mp4"], ["h264"]));

        Assert.Null(store.TryGet(Profile, "old"));
        Assert.NotNull(store.TryGet(Profile, "new"));
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
