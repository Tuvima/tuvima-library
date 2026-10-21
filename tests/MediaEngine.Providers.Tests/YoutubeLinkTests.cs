using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

/// <summary>Finding the one thing that leaves the machine, the 11-character video ID, in whatever a person pastes.</summary>
public sealed class YoutubeLinkTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("  dQw4w9WgXcQ  ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?list=PL1&v=dQw4w9WgXcQ&t=42s")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    public void TryGetVideoId_FindsTheId(string pasted)
    {
        Assert.True(YoutubeLink.TryGetVideoId(pasted, out var id));
        Assert.Equal("dQw4w9WgXcQ", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a link")]
    [InlineData("too-short")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghijk")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%0A")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ%0A")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ%0A")]
    public void TryGetVideoId_RefusesAnythingElse(string? pasted)
    {
        Assert.False(YoutubeLink.TryGetVideoId(pasted, out var id));
        Assert.Equal(string.Empty, id);
    }
}
