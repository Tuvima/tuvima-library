using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class UnifiedPlayerTests
{
    [Theory]
    [InlineData(false, 0d, false, false)] // initial zero before the player reported: sound on
    [InlineData(false, 0d, true, true)]   // the player reported a real zero level
    [InlineData(true, 0.8d, true, true)]  // the person muted it
    [InlineData(false, 0.8d, true, false)]
    public void MuteDisplay_StartsWithSoundOn_AndOnlyShowsMutedAfterMuteOrZero(bool muted, double volume, bool reported, bool expected)
    {
        Assert.Equal(expected, PlaybackMuteDisplay.ShowsMuted(muted, volume, reported));
    }

    [Fact]
    public void NewPlaybackSession_HasNotReportedAVolumeYet()
    {
        var controller = new PlaybackSessionController(null!, EngineApiClientStub.CreateDefault());
        Assert.False(controller.VolumeReported);
    }

    private static IReadOnlyList<MediaEngine.Contracts.Playback.PlaybackChapterDto> Chapters() =>
        Enumerable.Range(0, 21).Select(i => new MediaEngine.Contracts.Playback.PlaybackChapterDto
        {
            Index = i,
            Title = $"Chapter title {i + 1}",
            StartSeconds = i * 600d,
            EndSeconds = (i + 1) * 600d,
        }).ToList();

    [Fact]
    public void ChapterWindow_NamesTheCurrentChapter_AndScopesTheBarToIt()
    {
        var window = AudiobookChapterWindow.Find(Chapters(), 3 * 600d + 120d, 21 * 600d);

        Assert.NotNull(window);
        Assert.Equal("Chapter 4 of 21", window!.Label);
        Assert.Equal(600d, window.Length);
        Assert.Equal(120d, window.Elapsed(3 * 600d + 120d));
        Assert.Equal(3 * 600d + 300d, window.ToAbsolute(300d));
    }

    [Fact]
    public void ChapterWindow_IsNullWithoutAtLeastTwoChapters()
    {
        Assert.Null(AudiobookChapterWindow.Find(Chapters().Take(1).ToList(), 10d, 600d));
        Assert.Null(AudiobookChapterWindow.Find(null, 10d, 600d));
    }

    [Fact]
    public void ChapterWindow_LastChapterEndsAtTheBookEnd()
    {
        var chapters = Chapters().Select(c => c with { EndSeconds = null }).ToList();
        var window = AudiobookChapterWindow.Find(chapters, 21 * 600d - 5d, 21 * 600d);

        Assert.Equal(21, window!.Number);
        Assert.Equal(21 * 600d, window.End);
    }
}
