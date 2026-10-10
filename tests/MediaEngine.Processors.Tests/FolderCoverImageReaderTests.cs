using MediaEngine.Domain.Enums;
using MediaEngine.Processors;

namespace MediaEngine.Processors.Tests;

public sealed class FolderCoverImageReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"folder_cover_{Guid.NewGuid():N}");

    public FolderCoverImageReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Theory]
    [InlineData(MediaType.Books, true)]
    [InlineData(MediaType.Audiobooks, true)]
    [InlineData(MediaType.Music, true)]
    [InlineData(MediaType.Comics, false)]
    [InlineData(MediaType.Movies, false)]
    [InlineData(MediaType.TV, false)]
    public void AppliesTo_OnlyBooksAudiobooksAndMusic(MediaType mediaType, bool expected)
    {
        Assert.Equal(expected, FolderCoverImageReader.AppliesTo(mediaType));
    }

    [Fact]
    public void TryRead_ValidJpeg_ReturnsBytesAndMimeType()
    {
        var media = CreateMediaFile("Track 01.mp3");
        var image = JpegBytes(2048);
        File.WriteAllBytes(Path.Combine(_root, "cover.jpg"), image);

        var cover = FolderCoverImageReader.TryRead(media);

        Assert.NotNull(cover);
        Assert.Equal("image/jpeg", cover!.MimeType);
        Assert.Equal(image, cover.Bytes);
        Assert.Equal(Path.Combine(_root, "cover.jpg"), cover.SourcePath, ignoreCase: true);
    }

    [Fact]
    public void TryRead_ValidPng_ReturnsPngMimeType()
    {
        var media = CreateMediaFile("Track 01.mp3");
        File.WriteAllBytes(Path.Combine(_root, "folder.png"), PngBytes(4096));

        var cover = FolderCoverImageReader.TryRead(media);

        Assert.NotNull(cover);
        Assert.Equal("image/png", cover!.MimeType);
    }

    [Fact]
    public void TryRead_MatchesFileNamesCaseInsensitively()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(Path.Combine(_root, "COVER.JPG"), JpegBytes(2048));

        Assert.NotNull(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_PrefersCoverOverFolderOverFront()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(Path.Combine(_root, "front.jpg"), JpegBytes(1500));
        File.WriteAllBytes(Path.Combine(_root, "folder.jpg"), JpegBytes(1600));
        File.WriteAllBytes(Path.Combine(_root, "cover.png"), PngBytes(1700));

        var cover = FolderCoverImageReader.TryRead(media);

        Assert.NotNull(cover);
        Assert.Equal("cover.png", Path.GetFileName(cover!.SourcePath), ignoreCase: true);
    }

    [Fact]
    public void TryRead_NoCandidate_ReturnsNull()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(Path.Combine(_root, "poster.jpg"), JpegBytes(2048));

        Assert.Null(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_DoesNotLookInParentFolders()
    {
        File.WriteAllBytes(Path.Combine(_root, "cover.jpg"), JpegBytes(2048));
        var child = Directory.CreateDirectory(Path.Combine(_root, "Disc 1")).FullName;
        var media = Path.Combine(child, "Track 01.mp3");
        File.WriteAllText(media, "audio");

        Assert.Null(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_InvalidSignature_ReturnsNull()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(Path.Combine(_root, "cover.jpg"), new byte[2048]);

        Assert.Null(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_TooSmall_ReturnsNull()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(Path.Combine(_root, "cover.jpg"), JpegBytes(512));

        Assert.Null(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_TooLarge_ReturnsNull()
    {
        var media = CreateMediaFile("book.m4b");
        File.WriteAllBytes(
            Path.Combine(_root, "cover.jpg"),
            JpegBytes((int)FolderCoverImageReader.MaximumImageBytes + 1));

        Assert.Null(FolderCoverImageReader.TryRead(media));
    }

    [Fact]
    public void TryRead_SourceImageHeldOpenByAnotherReader_StillReadsAndLeavesFileUntouched()
    {
        var media = CreateMediaFile("book.m4b");
        var imagePath = Path.Combine(_root, "cover.jpg");
        var image = JpegBytes(2048);
        File.WriteAllBytes(imagePath, image);
        var before = File.GetLastWriteTimeUtc(imagePath);

        using (new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.NotNull(FolderCoverImageReader.TryRead(media));
        }

        Assert.Equal(image, File.ReadAllBytes(imagePath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(imagePath));
    }

    private string CreateMediaFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "audio");
        return path;
    }

    private static byte[] JpegBytes(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private static byte[] PngBytes(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        return bytes;
    }
}
