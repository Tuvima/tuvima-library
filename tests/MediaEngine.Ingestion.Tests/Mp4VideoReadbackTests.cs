using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class Mp4VideoReadbackTests
{
    [Fact]
    public async Task Mp4Video_ReopenedFileMatchesProvenFieldSet()
    {
        var path = CopyFixture();
        try
        {
            IMetadataTagger tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);
            var tags = new Dictionary<string, string>
            {
                ["title"] = "A Film",
                ["genre"] = "Drama",
                ["description"] = "A short description",
                ["year"] = "2026",
            };
            await tagger.WriteTagsAsync(path, tags);
            var readback = await tagger.VerifyTagsAsync(path, tags);
            Assert.True(readback.IsVerified, readback.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));

            var mismatch = await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string>(tags) { ["genre"] = "Comedy" });
            Assert.False(mismatch.IsVerified);
            Assert.Contains("genre", mismatch.Reason);
        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task TvAtomRequestRemainsUnverifiedUntilItsReaderIsProven()
    {
        IMetadataTagger tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);
        var result = await tagger.VerifyTagsAsync("unopened.mp4",
            new Dictionary<string, string> { ["season_number"] = "1" });
        Assert.False(result.IsVerified);
    }

    [Fact]
    public async Task Mp4ReadbackMismatchRestoresOriginalBytes()
    {
        var path = CopyFixture();
        try
        {
            var tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);
            await tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Original" });
            var original = await File.ReadAllBytesAsync(path);

            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "" }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { DeleteFixture(path); }
    }

    private static string CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "metadata-readback.mp4");
        var destination = Path.Combine(Path.GetTempPath(), $"tuvima_mp4_readback_{Guid.NewGuid():N}.mp4");
        File.Copy(source, destination);
        return destination;
    }

    private static void DeleteFixture(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        if (File.Exists(path + BackedUpMetadataTagger.BackupSuffix))
        {
            File.Delete(path + BackedUpMetadataTagger.BackupSuffix);
        }
    }
}
