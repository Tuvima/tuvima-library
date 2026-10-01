using MediaEngine.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class VideoMetadataTaggerSafetyTests
{
    [Fact]
    public async Task MissingVideo_DoesNotReportSuccessfulMetadataWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-tuvima-{Guid.NewGuid():N}.mp4");
        var tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Episode" }));
    }

    [Fact]
    public async Task MissingVideo_DoesNotReportSuccessfulArtworkWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-tuvima-{Guid.NewGuid():N}.mp4");
        var tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            tagger.WriteCoverArtAsync(path, [0xFF, 0xD8, 0xFF]));
    }

    [Fact]
    public async Task MatroskaScopedIdentity_IsRejectedBeforeAnyFileMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-scoped-id-{Guid.NewGuid():N}.mkv");
        await File.WriteAllTextAsync(path, "PRESERVE ORIGINAL BYTES");
        try
        {
            var tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["tvdb_id"] = "123" }));

            Assert.Equal("PRESERVE ORIGINAL BYTES", await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
