using MediaEngine.Processors;

namespace MediaEngine.Processors.Tests;

public sealed class VideoFolderCoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-vcover-{Guid.NewGuid():N}");

    public VideoFolderCoverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Put(string relative, bool image = false)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[2048];
        if (image)
        {
            new byte[] { 0x89, 0x50, 0x4E, 0x47 }.CopyTo(bytes, 0);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PictureNamedAfterTheVideo_IsUsedEvenInTheLibraryFolder()
    {
        var video = Put("Sample Talk [dQw4w9WgXcQ].mp4");
        var picture = Put("Sample Talk [dQw4w9WgXcQ].png", image: true);

        var cover = FolderCoverImageReader.TryReadForVideo(video, _root);

        Assert.Equal(picture, cover?.SourcePath);
    }

    [Fact]
    public void LooseFolderCoverInTheLibraryFolder_IsNeverGivenToAStandaloneVideo()
    {
        var video = Put("Loose.mp4");
        Put("cover.png", image: true);

        Assert.Null(FolderCoverImageReader.TryReadForVideo(video, _root));
    }

    [Fact]
    public void SeriesFolderCover_IsUsedForItsVideos()
    {
        var video = Put(Path.Combine("Python Course", "01 - Welcome.mp4"));
        var picture = Put(Path.Combine("Python Course", "poster.png"), image: true);

        Assert.Equal(picture, FolderCoverImageReader.TryReadForVideo(video, _root)?.SourcePath);
    }
}
