using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class AppleAudioReadbackTests
{
    [Theory]
    [InlineData(".m4a")]
    [InlineData(".m4b")]
    public async Task AppleAudio_ReopenedFileMatchesProvenFieldSet(string extension)
    {
        var path = CopyFixture(extension);
        try
        {
            IMetadataTagger tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            var tags = new Dictionary<string, string>
            {
                ["title"] = "Song or chapter",
                ["artist"] = "The Artist",
                ["album"] = "The Album",
                ["track_number"] = "02",
                ["genre"] = "Rock",
                ["description"] = "A description",
                ["year"] = "2026",
            };

            await tagger.WriteTagsAsync(path, tags);
            var readback = await tagger.VerifyTagsAsync(path, tags);
            Assert.True(readback.IsVerified, readback.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));

            var mismatched = await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string>(tags) { ["title"] = "Different" });
            Assert.False(mismatched.IsVerified);
            Assert.Contains("title", mismatched.Reason);
        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task UnsupportedAppleAudioFieldCannotProduceVerifiedResult()
    {
        IMetadataTagger tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
            new MediaTypeExtensionCatalog());
        var result = await tagger.VerifyTagsAsync("unopened.m4a",
            new Dictionary<string, string> { ["narrator"] = "Reader" });
        Assert.False(result.IsVerified);
    }

    [Fact]
    public async Task AppleAudioReadbackMismatchRestoresOriginalBytes()
    {
        var path = CopyFixture(".m4a");
        try
        {
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            await tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Original" });
            var original = await File.ReadAllBytesAsync(path);

            // TagLib omits an empty title atom, so read-back must reject it
            // after Save and restore the backup made before the attempt.
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "" }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { DeleteFixture(path); }
    }

    private static string CopyFixture(string extension)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "metadata-readback.m4a");
        var destination = Path.Combine(Path.GetTempPath(), $"tuvima_apple_readback_{Guid.NewGuid():N}{extension}");
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
