using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class ArtworkEmbeddingSupportTests
{
    [Theory]
    [InlineData("track.mp3")]
    [InlineData("album.m4a")]
    [InlineData("chapter.m4b")]
    [InlineData("record.flac")]
    [InlineData("record.ogg")]
    [InlineData("record.opus")]
    public void OnlyVerifiedAudioFormatsAreEligible(string path) =>
        Assert.True(ArtworkEmbeddingSupport.CanEmbed(path));

    [Theory]
    [InlineData("episode.mp4")]
    [InlineData("episode.mkv")]
    [InlineData("issue.cbz")]
    [InlineData("book.epub")]
    [InlineData("book.pdf")]
    [InlineData("")]
    public void OtherFormatsStayUnsupportedUntilPhysicalReadbackIsProven(string path) =>
        Assert.False(ArtworkEmbeddingSupport.CanEmbed(path));

    [Fact]
    public async Task Mp3CoverIsPresentInPhysicalFileAfterWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_artwork_{Guid.NewGuid():N}.mp3");
        // A short MPEG-1 Layer III stream in a disposable file. No library asset is touched.
        var frame = new byte[417];
        frame[0] = 0xff;
        frame[1] = 0xfb;
        frame[2] = 0x90;
        frame[3] = 0x64;
        var stream = Enumerable.Range(0, 8).SelectMany(_ => frame).ToArray();
        var cover = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lB8AAAAASUVORK5CYII=");
        try
        {
            await File.WriteAllBytesAsync(path, stream);
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            await tagger.WriteCoverArtAsync(path, cover);

            Assert.True(ArtworkEmbeddingSupport.VerifyFrontCover(path, cover));
            using (var tagged = TagLib.File.Create(path))
            {
                tagged.Tag.Pictures = tagged.Tag.Pictures.Append(new TagLib.Picture(
                    new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.BackCover }).ToArray();
                tagged.Save();
            }
            await tagger.WriteCoverArtAsync(path, cover);
            using var readBack = TagLib.File.Create(path);
            Assert.Contains(readBack.Tag.Pictures, picture => picture.Type == TagLib.PictureType.BackCover);
        }
        finally
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
}
