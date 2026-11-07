using System.Text.Json;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class VideoTouchTests
{
    private static readonly Uri BaseUri = new("http://localhost:5016/");

    [Fact]
    public void EpisodeMetadataNamesTheEpisodeThenTheShowAndNumberAndUsesSizedCovers()
    {
        var workId = Guid.NewGuid();
        var item = new ListenQueueItem
        {
            WorkId = workId,
            MediaType = "TV",
            Title = "Pilot",
            EpisodeTitle = "Pilot",
            EpisodeNumber = "1",
            SeasonNumber = "2",
            Album = "Library Show",
        };

        var metadata = PlaybackSessionMetadata.Create(new PlaybackSessionContext(PlaybackBarMedia.Tv, item, BaseUri, ShowTitle: "The Show"))!;

        Assert.Equal("Pilot", metadata.Title);
        Assert.Equal("The Show · S2 E1", metadata.Artist);
        Assert.Equal(["512x512", "1024x1024"], metadata.Artwork.Select(artwork => artwork.Sizes));
        Assert.All(metadata.Artwork, artwork => Assert.StartsWith($"http://localhost:5016/engine-image/stream/entity/work/{workId:D}/cover?size=", artwork.Src));
    }

    [Fact]
    public void EpisodeMetadataFallsBackToTheQueueShowNameBeforeThePresentationContextLoads()
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), MediaType = "TV", Title = "Pilot", EpisodeNumber = "1", Album = "Library Show" };

        var metadata = PlaybackSessionMetadata.Create(new PlaybackSessionContext(PlaybackBarMedia.Tv, item, BaseUri))!;

        Assert.Equal("Library Show · E1", metadata.Artist);
    }

    [Fact]
    public void FilmMetadataUsesTheYearAndTheWordMovie()
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), MediaType = "Movie", Title = "Dune", Year = "2021", Album = "Dune Films" };

        var metadata = PlaybackSessionMetadata.Create(new PlaybackSessionContext(PlaybackBarMedia.Movie, item, BaseUri, ShowTitle: "ignored"))!;

        Assert.Equal("Dune", metadata.Title);
        Assert.Equal("2021 · Movie", metadata.Artist);
    }

    [Fact]
    public void MetadataSerializesToTheNamesTheBrowserMediaSessionExpects()
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), MediaType = "Movie", Title = "Dune", Year = "2021" };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(PlaybackSessionMetadata.Create(new PlaybackSessionContext(PlaybackBarMedia.Movie, item, BaseUri))));

        Assert.Equal(["actions", "album", "artist", "artwork", "skipBackSeconds", "skipForwardSeconds", "title"],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(json.RootElement.GetProperty("artwork")[0].TryGetProperty("src", out _));
    }

    [Fact]
    public void TouchGesturesAndMediaSessionAreWiredToTheExistingTransportPaths()
    {
        var root = FindRepoRoot();
        var razor = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var touch = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/video-touch.js"));
        var chrome = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/playback-chrome.js"));
        var css = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.css"));

        // Gestures call the same seek and play paths as the visible controls; no new Engine call is introduced.
        Assert.Contains("await SeekOnHostAsync(targetSeconds)", razor, StringComparison.Ordinal);
        Assert.Contains("await TogglePlayPauseAsync();", razor, StringComparison.Ordinal);
        Assert.Contains("\"attachVideo\"", razor, StringComparison.Ordinal);
        Assert.Contains("\"detachVideo\"", razor, StringComparison.Ordinal);
        Assert.Contains("HandleTouchSeek", touch, StringComparison.Ordinal);
        Assert.Contains("HandleTouchTogglePlay", touch, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", touch, StringComparison.Ordinal);
        Assert.Contains("data-video-touch-feedback", razor, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\" data-video-touch-announce", razor, StringComparison.Ordinal);
        Assert.Contains("Turn your phone for full screen", razor, StringComparison.Ordinal);
        // Touch never hides the controls with a tap, and a pressed button does not hold them open.
        Assert.Contains("[data-playback-chrome] :focus-visible", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("else if (!held()) { clearTimeout(state.timer); hidden(true); }", chrome, StringComparison.Ordinal);
        Assert.Contains("state.pressing", chrome, StringComparison.Ordinal);
        Assert.Contains("requestVersion != Playback.PlaybackRequestVersion", razor, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-height: 450px)", css, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
