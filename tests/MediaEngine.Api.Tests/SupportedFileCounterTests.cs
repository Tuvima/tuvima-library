using MediaEngine.Api.Services.Settings;

namespace MediaEngine.Api.Tests;

public sealed class SupportedFileCounterTests : IDisposable
{
    private static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-file-count-tests", Guid.NewGuid().ToString("N"));

    public SupportedFileCounterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void MakeFiles(int count, string extension = ".mkv", string? folder = null)
    {
        var target = Directory.CreateDirectory(folder ?? _root).FullName;
        for (var index = 0; index < count; index++)
        {
            File.WriteAllBytes(Path.Combine(target, $"file-{Guid.NewGuid():N}{extension}"), []);
        }
    }

    [Fact]
    public void Count_CountsSupportedFilesInSubfoldersToo()
    {
        MakeFiles(3);
        MakeFiles(2, ".mp4", Path.Combine(_root, "Show", "Season 1"));
        MakeFiles(4, ".txt");

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video");

        Assert.Equal(5, result.Count);
        Assert.False(result.Capped);
        Assert.False(result.Unavailable);
        Assert.Equal("video", result.KindLabel);
    }

    [Fact]
    public void Count_StopsAtTheCapAndSaysItWasCapped()
    {
        MakeFiles(12);

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video", cap: 10);

        Assert.Equal(10, result.Count);
        Assert.True(result.Capped);
        Assert.False(result.Unavailable);
    }

    [Fact]
    public void Count_ExactlyAtTheCapIsNotCapped()
    {
        MakeFiles(10);

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video", cap: 10);

        Assert.Equal(10, result.Count);
        Assert.False(result.Capped);
    }

    [Fact]
    public void Count_ACancelledRequestAnswersUnavailableInsteadOfThrowing()
    {
        MakeFiles(5);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video", cancelled.Token);

        Assert.True(result.Unavailable);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Count_RunningOutOfTimeAnswersUnavailable()
    {
        MakeFiles(5);

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video", timeLimit: TimeSpan.Zero);

        Assert.True(result.Unavailable);
        Assert.Null(result.Count);
        Assert.False(result.Capped);
    }

    [Fact]
    public void Count_LeavesOutHiddenFolders()
    {
        MakeFiles(2);
        MakeFiles(3, ".mkv", Path.Combine(_root, ".tuvima-work"));

        var result = SupportedFileCounter.Count(_root, VideoExtensions, "video");

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("Movies", "video")]
    [InlineData("TV", "video")]
    [InlineData("Videos", "video")]
    [InlineData("Music", "audio")]
    [InlineData("Audiobooks", "audio")]
    [InlineData("Books", "book")]
    [InlineData("Comics", "comic")]
    [InlineData(null, null)]
    public void KindLabelFor_NamesWhatEachFormatCounts(string? format, string? expected) =>
        Assert.Equal(expected, SupportedFileCounter.KindLabelFor(format));
}
