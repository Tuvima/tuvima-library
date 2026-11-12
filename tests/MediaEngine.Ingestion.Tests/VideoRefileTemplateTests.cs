using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Documents what a re-file (the existing naming step behind "move and rename to match the details") does to a video that sits in a series
/// folder, under the Videos template the Engine ships (<c>{Title}{Ext}</c>, config/core.json). It is why a match's move and a series move
/// never share one save (universal editor v6, D24): the template has no folder in it.
/// </summary>
public class VideoRefileTemplateTests
{
    [Fact]
    public void TheVideosTemplate_ForAVideoInASeriesFolder_NamesTheTopOfTheLibraryFolder_NotTheSeriesFolder()
    {
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
        var candidate = new IngestionCandidate
        {
            Path = Path.Combine(Path.GetTempPath(), "library", "Course", "01 Intro.mp4"),
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            DetectedMediaType = MediaType.Videos,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Intro, the long cut" },
        };

        var relative = LibraryOrganizationPath.Calculate(organizer, candidate, null, "{Title}{Ext}");

        Assert.Equal("Intro, the long cut.mp4", relative.Replace('\\', '/'));
    }
}
