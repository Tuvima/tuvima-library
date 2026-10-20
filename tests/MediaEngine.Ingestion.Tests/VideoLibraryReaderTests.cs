using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Videos;
using MediaEngine.Processors.Models;

namespace MediaEngine.Ingestion.Tests;

/// <summary>Reading a Videos folder with real files in a temp folder: no network, no providers.</summary>
public sealed class VideoLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-videos-{Guid.NewGuid():N}");
    private readonly VideoLibraryReader _reader;

    public VideoLibraryReaderTests()
    {
        Directory.CreateDirectory(_root);
        _reader = new VideoLibraryReader(new VideoNamingConfigurationProvider(() => null));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ProcessorResult Read(string path)
    {
        var processed = new ProcessorResult
        {
            FilePath = path,
            DetectedType = MediaType.Unknown,
            Claims =
            [
                // What the generic video processor guesses from a name like this; a Videos library ignores it.
                new() { Key = "show_name", Value = "Guessed Show", Confidence = 0.55 },
                new() { Key = "season_number", Value = "1", Confidence = 0.55 },
                new() { Key = "duration_sec", Value = "90", Confidence = 0.8 },
            ],
        };
        return _reader.Apply(processed, _root);
    }

    private static string? Claim(ProcessorResult result, string key) => result.Claims.FirstOrDefault(c => c.Key == key)?.Value;

    [Fact]
    public void ALooseFileIsAStandaloneVideoAndKeepsOnlyTechnicalClaims()
    {
        var result = Read(Touch("My trip.mp4"));

        Assert.Equal(MediaType.Videos, result.DetectedType);
        Assert.Equal("My trip", Claim(result, MetadataFieldConstants.Title));
        Assert.Null(Claim(result, MetadataFieldConstants.VideoSeries));
        Assert.Null(Claim(result, "show_name"));
        Assert.Equal("90", Claim(result, "duration_sec"));
        Assert.Equal(VideoIdentificationMethods.FileName, Claim(result, MetadataFieldConstants.VideoIdentifiedBy));
    }

    [Fact]
    public void ANumberedCourseWithSectionsBecomesASeriesOfSectionsAndEpisodes()
    {
        Touch("Python Course/01 - Basics/01 - Hello.mp4");
        Touch("Python Course/01 - Basics/02 - Variables.mp4");
        Touch("Python Course/02 - Advanced/01 - Classes.mp4");
        Touch("Python Course/Welcome.mp4");

        var variables = Read(Path.Combine(_root, "Python Course", "01 - Basics", "02 - Variables.mp4"));
        var classes = Read(Path.Combine(_root, "Python Course", "02 - Advanced", "01 - Classes.mp4"));
        var welcome = Read(Path.Combine(_root, "Python Course", "Welcome.mp4"));

        Assert.Equal("Python Course", Claim(variables, MetadataFieldConstants.VideoSeries));
        Assert.Equal("Basics", Claim(variables, MetadataFieldConstants.VideoSection));
        Assert.Equal("1", Claim(variables, MetadataFieldConstants.VideoSectionIndex));
        Assert.Equal("2", Claim(variables, MetadataFieldConstants.VideoEpisodeIndex));
        Assert.Equal("Variables", Claim(variables, MetadataFieldConstants.Title));
        Assert.Equal("Advanced", Claim(classes, MetadataFieldConstants.VideoSection));
        Assert.Equal("2", Claim(classes, MetadataFieldConstants.VideoSectionIndex));
        Assert.Equal("Python Course", Claim(welcome, MetadataFieldConstants.VideoSeries));
        Assert.Null(Claim(welcome, MetadataFieldConstants.VideoSection));
    }

    [Fact]
    public void AFolderHoldingOneVideoStaysStandalone()
    {
        var result = Read(Touch("Holiday/Beach day.mp4"));

        Assert.Null(Claim(result, MetadataFieldConstants.VideoSeries));
        Assert.Equal("Beach day", Claim(result, MetadataFieldConstants.Title));
    }

    [Fact]
    public void EpisodesWithoutNumbersFollowNaturalOrderSoTwoComesBeforeTen()
    {
        Touch("Lectures/Lecture 10.mp4");
        Touch("Lectures/Lecture 2.mp4");
        Touch("Lectures/Lecture 1 intro.mp4");

        var two = Read(Path.Combine(_root, "Lectures", "Lecture 2.mp4"));
        var ten = Read(Path.Combine(_root, "Lectures", "Lecture 10.mp4"));

        // These carry numbers, so those numbers are the order.
        Assert.Equal("2", Claim(two, MetadataFieldConstants.VideoEpisodeIndex));
        Assert.Equal("10", Claim(ten, MetadataFieldConstants.VideoEpisodeIndex));
    }

    [Fact]
    public void WithoutNumbersTheNaturalOrderIsUsed()
    {
        Touch("Talks/clip 10.mp4");
        Touch("Talks/clip 9.mp4");
        Touch("Talks/intro.mp4");

        Assert.Equal("1", Claim(Read(Path.Combine(_root, "Talks", "clip 9.mp4")), MetadataFieldConstants.VideoEpisodeIndex));
        Assert.Equal("2", Claim(Read(Path.Combine(_root, "Talks", "clip 10.mp4")), MetadataFieldConstants.VideoEpisodeIndex));
        Assert.Equal("3", Claim(Read(Path.Combine(_root, "Talks", "intro.mp4")), MetadataFieldConstants.VideoEpisodeIndex));
    }

    [Fact]
    public void AYoutubeIdInTheNameIsFoundFirst()
    {
        var result = Read(Touch("Learn Rust [dQw4w9WgXcQ].mkv"));

        Assert.Equal(VideoIdentificationMethods.YoutubeId, Claim(result, MetadataFieldConstants.VideoIdentifiedBy));
        Assert.Equal("dQw4w9WgXcQ", Claim(result, MetadataFieldConstants.YoutubeVideoId));
        Assert.Equal("Learn Rust", Claim(result, MetadataFieldConstants.Title));
    }

    [Fact]
    public void AnInfoJsonSuppliesTheIdAndDetailsWithoutAnyNetworkCall()
    {
        var video = Touch("clip.mp4");
        File.WriteAllText(VideoSidecarReader.InfoJsonPath(video),
            """{"id":"abc123XYZ_-","title":"A better title","description":"What it is about","channel":"Some Channel","upload_date":"20240315"}""");

        var result = Read(video);

        Assert.Equal(VideoIdentificationMethods.YoutubeId, Claim(result, MetadataFieldConstants.VideoIdentifiedBy));
        Assert.Equal("abc123XYZ_-", Claim(result, MetadataFieldConstants.YoutubeVideoId));
        Assert.Equal("A better title", Claim(result, MetadataFieldConstants.Title));
        Assert.Equal("What it is about", Claim(result, MetadataFieldConstants.Description));
        Assert.Equal("2024-03-15", Claim(result, MetadataFieldConstants.OriginalReleaseDate));
        Assert.Equal("Some Channel", Claim(result, MetadataFieldConstants.Network));
    }

    [Fact]
    public void AnNfoIsUsedWhenThereIsNoYoutubeId()
    {
        var video = Touch("scene.mp4");
        File.WriteAllText(VideoSidecarReader.NfoPath(video),
            "<movie><title>From the nfo</title><plot>Plot text</plot><premiered>2020-05-06</premiered></movie>");

        var result = Read(video);

        Assert.Equal(VideoIdentificationMethods.Nfo, Claim(result, MetadataFieldConstants.VideoIdentifiedBy));
        Assert.Equal("From the nfo", Claim(result, MetadataFieldConstants.Title));
        Assert.Equal("2020", Claim(result, MetadataFieldConstants.Year));
    }

    [Fact]
    public void BrokenSidecarsAreIgnoredAndTheFileNameIsUsed()
    {
        var video = Touch("plain clip.mp4");
        File.WriteAllText(VideoSidecarReader.InfoJsonPath(video), "{ not json");
        File.WriteAllText(VideoSidecarReader.NfoPath(video), "<!DOCTYPE x [<!ENTITY a SYSTEM \"file:///etc/passwd\">]><x>&a;</x>");

        var result = Read(video);

        Assert.Equal(VideoIdentificationMethods.FileName, Claim(result, MetadataFieldConstants.VideoIdentifiedBy));
        Assert.Equal("plain clip", Claim(result, MetadataFieldConstants.Title));
    }

    [Fact]
    public void RenamingAFileKeepsItsPlacementKeyBecauseTheKeyIsTheFolder()
    {
        Touch("Series A/one.mp4");
        Touch("Series A/two.mp4");
        var before = Read(Path.Combine(_root, "Series A", "two.mp4"));
        File.Move(Path.Combine(_root, "Series A", "two.mp4"), Path.Combine(_root, "Series A", "second.mp4"));
        var after = Read(Path.Combine(_root, "Series A", "second.mp4"));

        Assert.Equal(Claim(before, MetadataFieldConstants.VideoSeriesKey), Claim(after, MetadataFieldConstants.VideoSeriesKey));
    }
}
