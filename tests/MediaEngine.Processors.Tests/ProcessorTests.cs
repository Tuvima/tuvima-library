using System.IO.Compression;
using System.Reflection;
using System.Text;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;
using MediaEngine.Processors.Processors;

namespace MediaEngine.Processors.Tests;

// ════════════════════════════════════════════════════════════════════════
//  GenericFileProcessor
// ════════════════════════════════════════════════════════════════════════

public class GenericFileProcessorTests
{
    private readonly GenericFileProcessor _processor = new();

    [Fact]
    public void SupportedType_IsUnknown()
    {
        Assert.Equal(MediaType.Unknown, _processor.SupportedType);
    }

    [Fact]
    public void Priority_IsMinValue()
    {
        Assert.Equal(int.MinValue, _processor.Priority);
    }

    [Fact]
    public void CanProcess_AlwaysReturnsTrue()
    {
        Assert.True(_processor.CanProcess("/any/file.xyz"));
        Assert.True(_processor.CanProcess("test.epub"));
    }

    [Fact]
    public async Task ProcessAsync_ExtractsTitleFromFilenameStem()
    {
        // Create a temp file so the processor can open it.
        // Must use a non-known extension (.xyz) so GenericFileProcessor does not
        // classify the file as corrupt (it marks .epub, .mp4, etc. as corrupt when
        // no format-specific processor could parse them).
        var tempFile = Path.Combine(Path.GetTempPath(), $"My Book Title_{Guid.NewGuid():N}.xyz");
        await File.WriteAllBytesAsync(tempFile, [0x00]);

        try
        {
            var result = await _processor.ProcessAsync(tempFile);

            Assert.Equal(tempFile, result.FilePath);
            Assert.Equal(MediaType.Unknown, result.DetectedType);
            Assert.Single(result.Claims);
            Assert.Equal("title", result.Claims[0].Key);
            Assert.StartsWith("My Book Title_", result.Claims[0].Value);
            Assert.Equal(0.5, result.Claims[0].Confidence);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ProcessAsync_ThrowsOnNullPath()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _processor.ProcessAsync(null!));
    }

    [Fact]
    public async Task ProcessAsync_ThrowsOnWhitespacePath()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _processor.ProcessAsync("   "));
    }
}

public class FFmpegServiceParsingTests
{
    [Fact]
    public void ResolveBinary_DoesNotHideInvalidExplicitConfigurationWithPathFallback()
    {
        var resolver = typeof(FFmpegService).GetMethod("ResolveBinary", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolver);

        var missing = Path.Combine(Path.GetTempPath(), $"missing-ffmpeg-{Guid.NewGuid():N}");
        var result = resolver!.Invoke(null, ["ffmpeg", missing]);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveBinary_ReturnsAbsoluteExplicitPath()
    {
        var resolver = typeof(FFmpegService).GetMethod("ResolveBinary", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(resolver);
        var configured = Path.GetTempFileName();
        try
        {
            var result = Assert.IsType<string>(resolver!.Invoke(null, ["ffmpeg", configured]));
            Assert.Equal(Path.GetFullPath(configured), result);
        }
        finally
        {
            File.Delete(configured);
        }
    }

    [Fact]
    public void ParseProbeJson_PreservesTimedAudiobookChapters()
    {
        const string json = """
        {
          "format": {
            "duration": "120.0",
            "size": "4096",
            "tags": { "title": "Dungeon Crawler Carl" }
          },
          "streams": [],
          "chapters": [
            {
              "id": 0,
              "start_time": "0.000000",
              "end_time": "60.500000",
              "tags": { "title": "Chapter One" }
            },
            {
              "id": 1,
              "start_time": "60.500000",
              "end_time": "120.000000",
              "tags": { "TITLE": "Chapter Two" }
            }
          ]
        }
        """;

        var parser = typeof(FFmpegService).GetMethod("ParseProbeJson", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parser);

        var result = Assert.IsType<MediaProbeResult>(parser!.Invoke(null, [json, "audiobook.mp3"]));

        Assert.Equal(2, result.ChapterCount);
        Assert.Collection(
            result.Chapters,
            chapter =>
            {
                Assert.Equal(0, chapter.Index);
                Assert.Equal("Chapter One", chapter.Title);
                Assert.Equal(0, chapter.StartSeconds);
                Assert.Equal(60.5, chapter.EndSeconds);
            },
            chapter =>
            {
                Assert.Equal(1, chapter.Index);
                Assert.Equal("Chapter Two", chapter.Title);
                Assert.Equal(60.5, chapter.StartSeconds);
                Assert.Equal(120, chapter.EndSeconds);
            });
    }

    [Fact]
    public void ParseProbeJson_ReadsAppleCaptureAndLivePhotoMetadata()
    {
        const string json = """
        {
          "format": {
            "duration": "1.5",
            "size": "2048",
            "tags": {
              "com.apple.quicktime.creationdate": "2022-09-11T01:31:34-0500",
              "com.apple.quicktime.make": "Apple",
              "com.apple.quicktime.model": "iPhone 13 Pro",
              "com.apple.quicktime.location.ISO6709": "+41.8818-087.6231+181.000/",
              "com.apple.quicktime.content.identifier": "edge-live-photo-id",
              "com.apple.quicktime.live-photo.auto": "1"
            }
          },
          "streams": []
        }
        """;

        var parser = typeof(FFmpegService).GetMethod("ParseProbeJson", BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsType<MediaProbeResult>(parser!.Invoke(null, [json, "photo.mov"]));

        Assert.Equal(2022, result.CapturedAt?.Year);
        Assert.Equal("Apple", result.DeviceMake);
        Assert.Equal("iPhone 13 Pro", result.DeviceModel);
        Assert.Equal(41.8818, result.Latitude);
        Assert.Equal(-87.6231, result.Longitude);
        Assert.Equal("edge-live-photo-id", result.ContentIdentifier);
        Assert.True(result.IsLivePhoto);
    }
}

// ════════════════════════════════════════════════════════════════════════
//  MediaProcessorRouter
// ════════════════════════════════════════════════════════════════════════

public class VideoProcessorTests
{
    [Theory]
    [InlineData("Arrival (2016) {imdb-tt2543164}.mp4", "Arrival", "2016")]
    [InlineData("Dune Part One (2021) {imdb-tt1160419} [tmdb-438631].mp4", "Dune Part One", "2021")]
    public async Task ProcessAsync_StripsOrganizerBridgeTokensBeforeTitleAndYearExtraction(
        string fileName,
        string expectedTitle,
        string expectedYear)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"video_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(file, MinimalMp4Header());

        try
        {
            var processor = new VideoProcessor(new StubVideoMetadataExtractor());

            var result = await processor.ProcessAsync(file);

            Assert.Contains(result.Claims, claim => claim.Key == "title" && claim.Value == expectedTitle);
            Assert.Contains(result.Claims, claim => claim.Key == "year" && claim.Value == expectedYear);
            Assert.DoesNotContain(result.Claims, claim => claim.Key == "title" && claim.Value.Contains("imdb-", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Dr. Horrible's Sing-Along Blog (2008) - x264 DTS.mp4", "Dr. Horrible's Sing-Along Blog", "2008")]
    [InlineData("Mr. Smith Goes to Washington (1939).mp4", "Mr. Smith Goes to Washington", "1939")]
    [InlineData("The.Matrix.1999.1080p.mp4", "The Matrix 1999", null)]
    public async Task ProcessAsync_KeepsAbbreviationFullStopsInSpacedFileNames(
        string fileName,
        string expectedTitle,
        string? expectedYear)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"video_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(file, MinimalMp4Header());

        try
        {
            var result = await new VideoProcessor(new StubVideoMetadataExtractor()).ProcessAsync(file);

            Assert.Contains(result.Claims, claim => claim.Key == "title" && claim.Value == expectedTitle);
            if (expectedYear is not null)
            {
                Assert.Contains(result.Claims, claim => claim.Key == "year" && claim.Value == expectedYear);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Dr. Horrible's Sing-Along Blog (2008)", "Dr. Horrible's Sing-Along Blog (2008)")]
    [InlineData("Mr. Smith Goes to Washington (1939)", "Mr. Smith Goes to Washington (1939)")]
    [InlineData("The.Matrix.1999.1080p", "The Matrix 1999 1080p")]
    [InlineData("Vol. 2", "Vol. 2")]
    [InlineData("Some_Title_Here", "Some Title Here")]
    [InlineData("Spaced  Out   Title", "Spaced Out Title")]
    [InlineData("Dr. Who_Special", "Dr. Who Special")]
    public void FileStemTitleCleaner_OnlyTurnsFullStopsIntoSpacesForSceneStyleNames(string stem, string expected)
    {
        Assert.Equal(expected, FileStemTitleCleaner.Clean(stem));
    }

    [Fact]
    public async Task ProcessAsync_StripsReleaseQualityAndVersionFromEpisodeTitle()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"video_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Solo Leveling - S02E08 - Looking Up Was Tiring Me Out WEBRip-1080p v2.mp4");
        await File.WriteAllBytesAsync(file, MinimalMp4Header());
        try
        {
            var result = await new VideoProcessor(new StubVideoMetadataExtractor()).ProcessAsync(file);
            Assert.Contains(result.Claims, claim => claim.Key == "episode_title" && claim.Value == "Looking Up Was Tiring Me Out");
            Assert.DoesNotContain(result.Claims, claim => claim.Value.Contains("WEBRip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_EmitsOneSubtitleLanguageClaimPerDistinctLanguage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"video_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Arrival (2016).mp4");
        await File.WriteAllBytesAsync(file, MinimalMp4Header());
        try
        {
            var extractor = new FixedVideoMetadataExtractor(new VideoMetadata
            {
                SubtitleLanguages = ["eng", "eng", "ara", "ger", "chi", "chi", "mov_text"],
            });

            var result = await new VideoProcessor(extractor).ProcessAsync(file);

            var languages = result.Claims
                .Where(claim => claim.Key == MetadataFieldConstants.SubtitleLanguages)
                .Select(claim => claim.Value)
                .ToList();
            Assert.Equal(["en", "ar", "de", "zh", "mov_text"], languages);
            Assert.DoesNotContain(languages, value => value.Contains('|'));
            Assert.True(MetadataFieldConstants.IsMultiValued(MetadataFieldConstants.SubtitleLanguages));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Show Name - S01E01E02 - Two Part Premiere.mp4", "1", "2", "Two Part Premiere")]
    [InlineData("Show Name - S01E01-E02.mp4", "1", "2", null)]
    [InlineData("Show Name - S01E01-02 - Pilot.mp4", "1", "2", "Pilot")]
    [InlineData("Show Name - 1x01-02.mp4", "1", "2", null)]
    [InlineData("Show Name - S01E03E04E05.mp4", "3", "5", null)]
    public async Task ProcessAsync_EmitsEpisodeEnd_ForUnbrokenRange(
        string fileName, string expectedFirst, string expectedEnd, string? expectedTitle)
    {
        var claims = await ProcessClaimsAsync(fileName);

        Assert.Contains(claims, c => c.Key == "episode_number" && c.Value == expectedFirst);
        Assert.Contains(claims, c => c.Key == "episode_end" && c.Value == expectedEnd);
        if (expectedTitle is not null)
        {
            Assert.Contains(claims, c => c.Key == "episode_title" && c.Value == expectedTitle);
        }
    }

    [Theory]
    [InlineData("Show Name - S01E01.mp4")]
    [InlineData("Show Name - S01E01 - Pilot.mp4")]
    [InlineData("Show Name - S01E01E03.mp4")]
    [InlineData("Show Name - S01E01E200.mp4")]
    [InlineData("Show Name - S01E01-E09.mp4")]
    public async Task ProcessAsync_OmitsEpisodeEnd_ForSingleOrBrokenRange(string fileName)
    {
        var claims = await ProcessClaimsAsync(fileName);

        Assert.Contains(claims, c => c.Key == "episode_number" && c.Value == "1");
        Assert.DoesNotContain(claims, c => c.Key == "episode_end");
    }

    private static async Task<IReadOnlyList<ExtractedClaim>> ProcessClaimsAsync(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"video_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(file, MinimalMp4Header());
        try
        {
            var result = await new VideoProcessor(new StubVideoMetadataExtractor()).ProcessAsync(file);
            return result.Claims;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FixedVideoMetadataExtractor(VideoMetadata metadata) : IVideoMetadataExtractor
    {
        public Task<VideoMetadata?> ExtractAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<VideoMetadata?>(metadata);
    }

    private static byte[] MinimalMp4Header() =>
    [
        0x00, 0x00, 0x00, 0x18,
        0x66, 0x74, 0x79, 0x70,
        0x69, 0x73, 0x6F, 0x6D,
        0x00, 0x00, 0x02, 0x00,
    ];
}

public class ComicProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ExtractsFirstPageAsIssueCover()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"comic_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var issueOne = Path.Combine(dir, "Saga 001.cbz");
        var issueTwo = Path.Combine(dir, "Saga 002.cbz");
        var coverOne = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0xFF, 0xD9 };
        var coverTwo = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x03, 0x04, 0xFF, 0xD9 };

        try
        {
            CreateComicArchive(issueOne, "Saga Chapter One", "Saga", 1, coverOne);
            CreateComicArchive(issueTwo, "Saga Chapter Two", "Saga", 2, coverTwo);

            var processor = new ComicProcessor();
            var resultOne = await processor.ProcessAsync(issueOne);
            var resultTwo = await processor.ProcessAsync(issueTwo);

            Assert.Equal("image/jpeg", resultOne.CoverImageMimeType);
            Assert.Equal("image/jpeg", resultTwo.CoverImageMimeType);
            Assert.Equal(coverOne, resultOne.CoverImage);
            Assert.Equal(coverTwo, resultTwo.CoverImage);
            Assert.NotEqual(resultOne.CoverImage, resultTwo.CoverImage);
        }
        finally
        {
            DeleteDirectoryWithRetry(dir);
        }
    }

    [Fact]
    public async Task ProcessAsync_ComicInfoWithSeriesAndNumber_EmitsIssueScopedTitleAndDescription()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"comic_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var issue = Path.Combine(dir, "Batman 405.cbz");
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0xFF, 0xD9 };

        try
        {
            CreateComicArchive(issue, "Year One, Chapter Two: War Is Declared", "Batman", 405, cover);

            var processor = new ComicProcessor();
            var result = await processor.ProcessAsync(issue);

            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.Title
                && claim.Value == "Year One, Chapter Two: War Is Declared");
            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.IssueTitle
                && claim.Value == "Year One, Chapter Two: War Is Declared");
            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.Description
                && claim.Value == "Summary for Year One, Chapter Two: War Is Declared");
            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.IssueDescription
                && claim.Value == "Summary for Year One, Chapter Two: War Is Declared");
        }
        finally
        {
            DeleteDirectoryWithRetry(dir);
        }
    }

    [Fact]
    public async Task ProcessAsync_ComicInfoWithoutIssueIdentity_KeepsGenericTitleAndDescription()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"comic_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var issue = Path.Combine(dir, "Standalone.cbz");
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0xFF, 0xD9 };

        try
        {
            CreateStandaloneComicArchive(issue, "Standalone Story", "Standalone summary", cover);

            var processor = new ComicProcessor();
            var result = await processor.ProcessAsync(issue);

            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.Title
                && claim.Value == "Standalone Story");
            Assert.Contains(result.Claims, claim => claim.Key == MetadataFieldConstants.Description
                && claim.Value == "Standalone summary");
            Assert.DoesNotContain(result.Claims, claim => claim.Key == MetadataFieldConstants.IssueTitle);
            Assert.DoesNotContain(result.Claims, claim => claim.Key == MetadataFieldConstants.IssueDescription);
        }
        finally
        {
            DeleteDirectoryWithRetry(dir);
        }
    }

    [Fact]
    public async Task ProcessAsync_ComicInfo_EmitsVolumeLanguageRolesAndScopedProviderIdentifiers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"comic_processor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var issue = Path.Combine(dir, "Batman 405.cbz");
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0xFF, 0xD9 };

        try
        {
            CreateRichComicArchive(issue, cover);
            var result = await new ComicProcessor().ProcessAsync(issue);

            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.Volume && c.Value == "2");
            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.Language && c.Value == "en");
            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.Inker && c.Value == "Inker Name");
            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.Colorist && c.Value == "Colorist Name");
            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.Translator && c.Value == "Translator Name");
            Assert.Contains(result.Claims, c => c.Key == MetadataFieldConstants.IssueSourceUrl && c.Value.StartsWith("https://comicvine.gamespot.com/batman-405/4000-123456/"));
            Assert.Contains(result.Claims, c => c.Key == BridgeIdKeys.ComicVineId && c.Value == "123456");
            Assert.Contains(result.Claims, c => c.Key == BridgeIdKeys.ComicVineVolumeId && c.Value == "654321");
        }
        finally
        {
            DeleteDirectoryWithRetry(dir);
        }
    }

    private static void CreateComicArchive(string path, string title, string series, int number, byte[] cover)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        var page = archive.CreateEntry("page_001.jpg", CompressionLevel.NoCompression);
        using (var pageStream = page.Open())
        {
            pageStream.Write(cover);
        }

        var info = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Fastest);
        using var writer = new StreamWriter(info.Open(), Encoding.UTF8);
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Title>{title}</Title>
              <Series>{series}</Series>
              <Number>{number}</Number>
              <Summary>Summary for {title}</Summary>
              <PageCount>1</PageCount>
            </ComicInfo>
            """);
    }

    private static void CreateStandaloneComicArchive(string path, string title, string summary, byte[] cover)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        var page = archive.CreateEntry("page_001.jpg", CompressionLevel.NoCompression);
        using (var pageStream = page.Open())
        {
            pageStream.Write(cover);
        }

        var info = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Fastest);
        using var writer = new StreamWriter(info.Open(), Encoding.UTF8);
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Title>{title}</Title>
              <Summary>{summary}</Summary>
              <PageCount>1</PageCount>
            </ComicInfo>
            """);
    }

    private static void CreateRichComicArchive(string path, byte[] cover)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        var page = archive.CreateEntry("page_001.jpg", CompressionLevel.NoCompression);
        using (var pageStream = page.Open())
        {
            pageStream.Write(cover);
        }

        var info = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Fastest);
        using var writer = new StreamWriter(info.Open(), Encoding.UTF8);
        writer.Write("""
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Title>Issue title</Title>
              <Series>Batman</Series>
              <Number>405</Number>
              <Volume>2</Volume>
              <LanguageISO>en</LanguageISO>
              <Inker>Inker Name</Inker>
              <Colorist>Colorist Name</Colorist>
              <Translator>Translator Name</Translator>
              <Web>https://comicvine.gamespot.com/batman-405/4000-123456/</Web>
              <Notes>https://comicvine.gamespot.com/batman/4050-654321/</Notes>
            </ComicInfo>
            """);
    }

    private static void DeleteDirectoryWithRetry(string dir)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }
}

public class MediaProcessorRouterTests : IDisposable
{
    private readonly MediaProcessorRouter _libraryItem = new(maxDegreeOfParallelism: 2);

    public void Dispose() => _libraryItem.Dispose();

    [Fact]
    public void Register_NullProcessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _libraryItem.Register(null!));
    }

    [Fact]
    public void Resolve_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _libraryItem.Resolve(null!));
    }

    [Fact]
    public void Resolve_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => _libraryItem.Resolve("  "));
    }

    [Fact]
    public void Resolve_FallbackProcessor_ReturnedWhenNoSpecificMatch()
    {
        var fallback = new GenericFileProcessor();
        _libraryItem.Register(fallback);

        var result = _libraryItem.Resolve("/some/unknown/file.xyz");

        Assert.Same(fallback, result);
    }

    [Fact]
    public void Resolve_HigherPriorityProcessor_SelectedFirst()
    {
        var lowPriority = new FakeProcessor("low", 10, canProcess: true);
        var highPriority = new FakeProcessor("high", 100, canProcess: true);

        _libraryItem.Register(lowPriority);
        _libraryItem.Register(highPriority);

        var result = _libraryItem.Resolve("/test/file.txt");

        Assert.Same(highPriority, result);
    }

    [Fact]
    public void Resolve_SkipsProcessorsThatCannotProcess()
    {
        var cannotProcess = new FakeProcessor("no", 100, canProcess: false);
        var canProcess = new FakeProcessor("yes", 50, canProcess: true);

        _libraryItem.Register(cannotProcess);
        _libraryItem.Register(canProcess);

        var result = _libraryItem.Resolve("/test/file.txt");

        Assert.Same(canProcess, result);
    }

    [Fact]
    public async Task ProcessAsync_NoProcessors_ThrowsInvalidOperation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _libraryItem.ProcessAsync("/some/file.txt"));
    }

    [Fact]
    public async Task ProcessAsync_DelegatesToResolvedProcessor()
    {
        var expectedResult = new ProcessorResult
        {
            FilePath = "/test.txt",
            DetectedType = MediaType.Books,
        };
        var processor = new FakeProcessor("test", 100, canProcess: true, result: expectedResult);
        _libraryItem.Register(processor);

        var result = await _libraryItem.ProcessAsync("/test.txt");

        Assert.Same(expectedResult, result);
    }

    [Fact]
    public async Task ProcessAsync_ConcurrencyLimitedBySemaphore()
    {
        // Register a slow processor
        var tcs = new TaskCompletionSource<ProcessorResult>();
        var slowResult = new ProcessorResult { FilePath = "/slow.txt", DetectedType = MediaType.Unknown };
        var slowProcessor = new SlowProcessor(tcs.Task);
        _libraryItem.Register(slowProcessor);

        // Start two tasks (fill the semaphore limit of 2)
        var task1 = _libraryItem.ProcessAsync("/slow.txt");
        var task2 = _libraryItem.ProcessAsync("/slow.txt");

        // Both should be in progress
        Assert.False(task1.IsCompleted);
        Assert.False(task2.IsCompleted);

        // Complete them
        tcs.SetResult(slowResult);
        var result1 = await task1;
        var result2 = await task2;

        Assert.Equal(MediaType.Unknown, result1.DetectedType);
    }

    [Fact]
    public async Task ProcessAsync_RoutesPdfToBooks()
    {
        var file = Path.Combine(Path.GetTempPath(), $"phase2_{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(file, "%PDF-1.7\n%%EOF"u8.ToArray());

        try
        {
            _libraryItem.Register(new PdfProcessor());
            _libraryItem.Register(new GenericFileProcessor());

            var result = await _libraryItem.ProcessAsync(file);

            Assert.Equal(MediaType.Books, result.DetectedType);
            Assert.Contains(result.Claims, c => c.Key == "container" && c.Value == "PDF");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProcessAsync_RoutesAacToMusicCandidate()
    {
        var file = Path.Combine(Path.GetTempPath(), $"phase2_{Guid.NewGuid():N}.aac");
        await File.WriteAllBytesAsync(file, [0xFF, 0xF1, 0x50, 0x80, 0x00, 0x1F, 0xFC]);

        try
        {
            _libraryItem.Register(new AudioProcessor());
            _libraryItem.Register(new GenericFileProcessor());

            var result = await _libraryItem.ProcessAsync(file);

            Assert.Equal(MediaType.Music, result.DetectedType);
            Assert.Contains(result.MediaTypeCandidates, c => c.Type == MediaType.Music);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProcessAsync_ExtractsAudiobookSeriesTags()
    {
        var file = Path.Combine(Path.GetTempPath(), $"foundation_{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(file, MinimalTaggedMp3(
            title: "Foundation",
            author: "Isaac Asimov",
            narrator: "Scott Brick",
            series: "Foundation",
            seriesIndex: "1"));

        try
        {
            var processor = new AudioProcessor();

            var result = await processor.ProcessAsync(file);

            Assert.Equal(MediaType.Audiobooks, result.DetectedType);
            Assert.Contains(result.Claims, c => c.Key == "series" && c.Value == "Foundation");
            Assert.Contains(result.Claims, c => c.Key == "series_position" && c.Value == "1");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static byte[] MinimalTaggedMp3(
        string title,
        string author,
        string narrator,
        string series,
        string seriesIndex)
    {
        using var stream = new MemoryStream();
        using var frames = new MemoryStream();

        WriteTextFrame(frames, "TIT2", title);
        WriteTextFrame(frames, "TPE1", author);
        WriteTextFrame(frames, "TPE2", narrator);
        WriteTextFrame(frames, "TALB", title);
        WriteTextFrame(frames, "TCON", "Audiobook");
        WriteTxxxFrame(frames, "NARRATOR", narrator);
        WriteTxxxFrame(frames, "AUTHOR", author);
        WriteTxxxFrame(frames, "SERIES", series);
        WriteTxxxFrame(frames, "SERIES_INDEX", seriesIndex);

        var frameData = frames.ToArray();
        stream.Write("ID3"u8);
        stream.WriteByte(3);
        stream.WriteByte(0);
        stream.WriteByte(0);
        WriteSyncSafe(stream, frameData.Length);
        stream.Write(frameData);
        stream.WriteByte(0xFF);
        stream.WriteByte(0xFB);
        stream.WriteByte(0x90);
        stream.WriteByte(0x00);
        stream.Write(new byte[413]);

        return stream.ToArray();
    }

    private static void WriteTextFrame(Stream stream, string frameId, string text)
    {
        var textBytes = Encoding.Latin1.GetBytes(text);
        stream.Write(Encoding.ASCII.GetBytes(frameId));
        WriteBigEndianInt(stream, 1 + textBytes.Length);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(textBytes);
    }

    private static void WriteTxxxFrame(Stream stream, string description, string value)
    {
        var descBytes = Encoding.Latin1.GetBytes(description);
        var valBytes = Encoding.Latin1.GetBytes(value);
        stream.Write("TXXX"u8);
        WriteBigEndianInt(stream, 1 + descBytes.Length + 1 + valBytes.Length);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(descBytes);
        stream.WriteByte(0);
        stream.Write(valBytes);
    }

    private static void WriteBigEndianInt(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteSyncSafe(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 21) & 0x7F));
        stream.WriteByte((byte)((value >> 14) & 0x7F));
        stream.WriteByte((byte)((value >> 7) & 0x7F));
        stream.WriteByte((byte)(value & 0x7F));
    }
}

// ════════════════════════════════════════════════════════════════════════
//  ProcessorResult model
// ════════════════════════════════════════════════════════════════════════

public class ProcessorResultTests
{
    [Fact]
    public void Default_IsNotCorrupt()
    {
        var result = new ProcessorResult
        {
            FilePath = "/test.epub",
            DetectedType = MediaType.Books,
        };

        Assert.False(result.IsCorrupt);
        Assert.Null(result.CorruptReason);
        Assert.Null(result.CoverImage);
        Assert.Null(result.CoverImageMimeType);
        Assert.Empty(result.Claims);
        Assert.Empty(result.MediaTypeCandidates);
    }

    [Fact]
    public void CorruptResult_HasReason()
    {
        var result = new ProcessorResult
        {
            FilePath = "/bad.epub",
            DetectedType = MediaType.Books,
            IsCorrupt = true,
            CorruptReason = "Truncated ZIP archive",
        };

        Assert.True(result.IsCorrupt);
        Assert.Equal("Truncated ZIP archive", result.CorruptReason);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────

file sealed class FakeProcessor : IMediaProcessor
{
    private readonly bool _canProcess;
    private readonly ProcessorResult? _result;

    public FakeProcessor(string name, int priority, bool canProcess, ProcessorResult? result = null)
    {
        _canProcess = canProcess;
        _result = result;
        Priority = priority;
    }

    public MediaType SupportedType => MediaType.Unknown;
    public int Priority { get; }
    public bool CanProcess(string filePath) => _canProcess;

    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        return Task.FromResult(_result ?? new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Unknown,
        });
    }
}

file sealed class SlowProcessor : IMediaProcessor
{
    private readonly Task<ProcessorResult> _blocker;

    public SlowProcessor(Task<ProcessorResult> blocker) => _blocker = blocker;

    public MediaType SupportedType => MediaType.Unknown;
    public int Priority => 100;
    public bool CanProcess(string filePath) => true;
    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default) => _blocker;
}
