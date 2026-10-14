using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Reading;
using MediaEngine.Domain.Services;

namespace MediaEngine.Contracts.Tests;

public sealed class ReadingPlaybackContractTests
{
    [Fact]
    public void ReaderBookmarkContract_PreservesTheApiCompleteWireShape()
    {
        var bookmark = new ReaderBookmarkDto
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId = "local",
            AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ChapterIndex = 4,
            CfiPosition = "epubcfi(/6/8)",
            Label = "Return here",
            CreatedAt = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(bookmark, MediaEngineJson.Web);
        var roundTrip = JsonSerializer.Deserialize<ReaderBookmarkDto>(json, MediaEngineJson.Web);

        Assert.Contains("\"userId\":\"local\"", json, StringComparison.Ordinal);
        Assert.Equal(bookmark.Id, roundTrip?.Id);
        Assert.Equal(bookmark.UserId, roundTrip?.UserId);
        Assert.Equal(bookmark.CfiPosition, roundTrip?.CfiPosition);
    }

    [Fact]
    public void ReaderHighlightContract_CarriesTheCfiRangeAndStaysReadableWithoutOne()
    {
        var request = new CreateReaderHighlightRequestDto(0, 0, 0, "The first words", "#EAB308", null, "epubcfi(/6/6!/4/2/4,/1:0,/1:24)");
        var requestJson = JsonSerializer.Serialize(request, MediaEngineJson.Web);
        Assert.Contains("\"cfiRange\":\"epubcfi(/6/6!/4/2/4,/1:0,/1:24)\"", requestJson, StringComparison.Ordinal);

        // A request from before the book reader (no cfiRange) still reads, with the range absent.
        var legacy = JsonSerializer.Deserialize<CreateReaderHighlightRequestDto>(
            "{\"chapterIndex\":2,\"startOffset\":5,\"endOffset\":9,\"selectedText\":\"words\",\"color\":null,\"noteText\":null}",
            MediaEngineJson.Web);
        Assert.Null(legacy!.CfiRange);

        var highlight = new ReaderHighlightDto
        {
            Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            UserId = "local",
            AssetId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            CfiRange = "epubcfi(/6/6!/4/2/4,/1:0,/1:24)",
            SelectedText = "The first words",
        };
        var roundTrip = JsonSerializer.Deserialize<ReaderHighlightDto>(JsonSerializer.Serialize(highlight, MediaEngineJson.Web), MediaEngineJson.Web);
        Assert.Equal(highlight.CfiRange, roundTrip?.CfiRange);
    }

    [Fact]
    public void ReaderStatisticsContract_DoesNotDropPersistenceIdentity()
    {
        var statistics = new ReaderStatisticsDto
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            UserId = "local",
            AssetId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            ChaptersRead = 7,
            TotalReadingTimeSecs = 900,
            WordsRead = 4200,
            SessionsCount = 3,
            AvgWordsPerMinute = 280,
        };

        var json = JsonSerializer.Serialize(statistics, MediaEngineJson.Web);

        Assert.Contains("\"id\":\"33333333-3333-3333-3333-333333333333\"", json, StringComparison.Ordinal);
        Assert.Contains("\"userId\":\"local\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressContract_PreservesContentHashAndSnakeCaseNames()
    {
        var state = new UserStateResponse(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            "sha256",
            42.5,
            new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, string> { ["chapter_index"] = "2" });

        var json = JsonSerializer.Serialize(state, MediaEngineJson.Web);

        Assert.Contains("\"user_id\":\"55555555-5555-5555-5555-555555555555\"", json, StringComparison.Ordinal);
        Assert.Contains("\"content_hash\":\"sha256\"", json, StringComparison.Ordinal);
        Assert.Contains("\"progress_pct\":42.5", json, StringComparison.Ordinal);
        Assert.Contains("\"extended_properties\":{\"chapter_index\":\"2\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardReaderModelFile_ContainsOnlyDeviceLocalPresentationSettings()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MediaEngine.Web",
            "Models",
            "ViewDTOs",
            "EpubReaderDtos.cs"));

        Assert.Contains("ReaderSettingsDto", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReaderBookmarkDto", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReaderStatisticsDto", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressStateDto", source, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            root,
            "src",
            "MediaEngine.Web",
            "Models",
            "ViewDTOs",
            "TextTrackViewModel.cs")));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [Fact]
    public void ReadingSettingsContract_DefaultsAndWireNamesAreStable()
    {
        var reading = new ReadingSettingsDto();

        Assert.Equal("system", reading.Theme);
        Assert.Equal("book", reading.Font);
        Assert.Equal(18, reading.FontSizePx);
        Assert.Equal(1.5m, reading.LineHeight);
        Assert.Equal("standard", reading.PageWidth);
        Assert.Equal(PlaybackPreferenceValues.Paginated, reading.ReadingMode);

        var json = JsonSerializer.Serialize(reading, MediaEngineJson.Web);
        Assert.Contains("\"theme\":\"system\"", json, StringComparison.Ordinal);
        Assert.Contains("\"font\":\"book\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fontSizePx\":18", json, StringComparison.Ordinal);
        Assert.Contains("\"lineHeight\":1.5", json, StringComparison.Ordinal);
        Assert.Contains("\"pageWidth\":\"standard\"", json, StringComparison.Ordinal);
        // The older fields stay on the wire unchanged.
        Assert.Contains("\"readingMode\":\"Paginated\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fontSizePercent\":100", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingSettingsContract_FillsTheReaderDefaultsWhenOlderJsonLacksThem()
    {
        var older = JsonSerializer.Deserialize<ReadingSettingsDto>(
            "{\"readingMode\":\"Scroll\",\"fontSizePercent\":120}", MediaEngineJson.Web);

        Assert.NotNull(older);
        Assert.Equal(PlaybackPreferenceValues.Scroll, older!.ReadingMode);
        Assert.Equal(120, older.FontSizePercent);
        Assert.Equal("system", older.Theme);
        Assert.Equal("book", older.Font);
        Assert.Equal(18, older.FontSizePx);
        Assert.Equal(1.5m, older.LineHeight);
        Assert.Equal("standard", older.PageWidth);
    }
}
