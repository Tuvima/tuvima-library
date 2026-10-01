using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class MetadataTaggerCapabilityTests
{
    [Fact]
    public void VideoCapabilities_AdmitRichTvFieldsOnlyForMp4()
    {
        var tagger = new VideoMetadataTagger(NullLogger<VideoMetadataTagger>.Instance);
        var mp4 = tagger.GetCapabilities("episode.mp4");
        var mkv = tagger.GetCapabilities("episode.mkv");

        Assert.True(mp4.CanWriteField("tvdb_id"));
        Assert.True(mp4.CanWriteField("season_number"));
        Assert.True(mp4.CanWriteArtwork);
        Assert.False(mkv.CanWriteField("tvdb_id"));
        Assert.True(mkv.CanWriteField("title"));
        Assert.False(mkv.CanWriteArtwork);
        Assert.Throws<NotSupportedException>(() => mkv.ValidateTags(
            new Dictionary<string, string> { ["tvdb_id"] = "123" }));
        Assert.Throws<FormatException>(() => mp4.ValidateTags(
            new Dictionary<string, string> { ["episode_number"] = "two" }));
    }

    [Fact]
    public void ComicCapabilities_RejectNoOpFieldsAndCoverArt()
    {
        var tagger = new ComicMetadataTagger(NullLogger<ComicMetadataTagger>.Instance);
        var capabilities = tagger.GetCapabilities("issue.cbz");

        Assert.True(capabilities.CanWriteField("series_position"));
        Assert.False(capabilities.CanWriteField("isbn"));
        Assert.False(capabilities.CanWriteArtwork);
        Assert.Throws<NotSupportedException>(() => capabilities.ValidateTags(
            new Dictionary<string, string> { ["isbn"] = "123" }));
    }

    [Fact]
    public async Task ComicUnsupportedField_FailsBeforeArchiveMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-capability-{Guid.NewGuid():N}.cbz");
        await File.WriteAllTextAsync(path, "unchanged");
        try
        {
            var tagger = new ComicMetadataTagger(NullLogger<ComicMetadataTagger>.Instance);
            await Assert.ThrowsAsync<NotSupportedException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["isbn"] = "123" }));
            await Assert.ThrowsAsync<NotSupportedException>(() => tagger.WriteCoverArtAsync(path, [1, 2, 3]));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AudioCapabilities_RestrictCustomIdentifiersToSupportedTagFamilies()
    {
        var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
            new MediaTypeExtensionCatalog());
        var mp3 = tagger.GetCapabilities("song.mp3");
        var wav = tagger.GetCapabilities("song.wav");

        Assert.True(mp3.CanWriteField("musicbrainz_id"));
        Assert.True(mp3.CanWriteArtwork);
        Assert.False(wav.CanWriteField("musicbrainz_id"));
        Assert.False(wav.CanWriteArtwork);
        Assert.Throws<FormatException>(() => mp3.ValidateTags(
            new Dictionary<string, string> { ["track_number"] = "A" }));
    }

    [Fact]
    public void EpubCapabilities_DescribeCustomOpfFields()
    {
        var tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
        var capabilities = tagger.GetCapabilities("book.epub");

        Assert.True(capabilities.CanWriteField("isbn"));
        Assert.True(capabilities.AcceptsCustomOpfFields);
        Assert.True(capabilities.CanWriteArtwork);
        capabilities.ValidateTags(new Dictionary<string, string> { ["isbn"] = "978123" });
    }
}
