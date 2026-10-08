using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Services;
using MediaEngine.Processors.Models;
using Xunit;

namespace MediaEngine.Ingestion.Tests;

public sealed class AudiobookFolderHintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-recording-test-" + Guid.NewGuid());

    [Fact]
    public void DiscFoldersShareRecordingAndNaturalOrderWithoutRenamingTracks()
    {
        var first = Create("Author/Book/CD 1/2.mp3");
        Create("Author/Book/CD 1/10.mp3");
        var last = Create("Author/Book/CD 2/1.mp3");
        var a = Apply(first);
        var b = Apply(last);
        Assert.Equal(MediaType.Audiobooks, a.DetectedType);
        Assert.Equal(Value(a, "audiobook_recording_key"), Value(b, "audiobook_recording_key"));
        Assert.Equal("1", Value(a, "audiobook_part_number"));
        Assert.Equal("3", Value(b, "audiobook_part_number"));
        Assert.Equal("Source track title", Value(a, "track_title"));
        Assert.Equal("Book", Value(a, "title"));
        Assert.Equal("Book series", Value(a, "series"));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void SameNamedBooksInDifferentFoldersHaveDifferentIdentities()
    {
        Assert.NotEqual(Value(Apply(Create("One/Book/1.mp3")), "audiobook_recording_key"),
            Value(Apply(Create("Two/Book/1.mp3")), "audiobook_recording_key"));
    }

    [Fact]
    public void LooseFilesDoNotBecomeOneRecording()
    {
        Assert.Null(Value(Apply(Create("one.mp3")), "audiobook_recording_key"));
    }

    private ProcessorResult Apply(string path) => AudiobookFolderHints.Apply(new ProcessorResult {
        FilePath = path, DetectedType = MediaType.Music,
        Claims = [new() { Key = "title", Value = "Source track title", Confidence = 1 },
                  new() { Key = "series", Value = "Book series", Confidence = 1 }]
    }, _root);
    private static string? Value(ProcessorResult result, string key) => result.Claims.FirstOrDefault(c => c.Key == key)?.Value;
    private string Create(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }
    public void Dispose() { if (Directory.Exists(_root))
    {
        Directory.Delete(_root, true);
    } }
}
