using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class XiphAudioReadbackTests
{
    [Theory]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    public async Task ReopenedXiphCommentsMatchProvenFields(string extension)
    {
        var path = CopyFixture(extension);
        try
        {
            IMetadataTagger tagger = NewTagger();
            var tags = new Dictionary<string, string>
            {
                ["title"] = "A title", ["artist"] = "An artist", ["album"] = "An album",
                ["track_number"] = "02", ["genre"] = "Rock",
                ["description"] = "A description", ["year"] = "2026",
            };
            await tagger.WriteTagsAsync(path, tags);
            var result = await tagger.VerifyTagsAsync(path, tags);
            Assert.True(result.IsVerified, result.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));

            var mismatch = await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string>(tags) { ["title"] = "Different" });
            Assert.False(mismatch.IsVerified);
            Assert.Contains("title", mismatch.Reason);
        }
        finally { DeleteFixture(path); }
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    public async Task UnsupportedXiphFieldRemainsUnverified(string extension)
    {
        var result = await NewTagger().VerifyTagsAsync("unopened" + extension,
            new Dictionary<string, string> { ["musicbrainz_id"] = Guid.NewGuid().ToString("D") });
        Assert.False(result.IsVerified);
    }

    [Fact]
    public async Task OggOpusDoesNotInheritVorbisVerification()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "metadata-readback-opus.ogg");
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_ogg_opus_{Guid.NewGuid():N}.ogg");
        File.Copy(source, path);
        try
        {
            var tagger = NewTagger();
            await tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Attempted" });
            var result = await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "Attempted" });
            Assert.False(result.IsVerified);
        }
        finally { DeleteFixture(path); }
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    public async Task ReadbackMismatchRestoresOriginalBytes(string extension)
    {
        var path = CopyFixture(extension);
        try
        {
            var tagger = NewTagger();
            await tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Original" });
            var original = await File.ReadAllBytesAsync(path);

            // An empty field is omitted by Xiph serialization. Verification
            // happens before backup cleanup and must restore the old bytes.
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "" }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { DeleteFixture(path); }
    }

    private static IMetadataTagger NewTagger() => new AudioMetadataTagger(
        NullLogger<AudioMetadataTagger>.Instance, new MediaTypeExtensionCatalog());

    private static string CopyFixture(string extension)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "metadata-readback" + extension);
        var destination = Path.Combine(Path.GetTempPath(), $"tuvima_xiph_readback_{Guid.NewGuid():N}{extension}");
        File.Copy(source, destination);
        return destination;
    }

    private static void DeleteFixture(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + BackedUpMetadataTagger.BackupSuffix))
            File.Delete(path + BackedUpMetadataTagger.BackupSuffix);
    }
}
