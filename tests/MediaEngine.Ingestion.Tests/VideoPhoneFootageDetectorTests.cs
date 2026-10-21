using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Videos;

namespace MediaEngine.Ingestion.Tests;

public sealed class VideoPhoneFootageDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-phone-{Guid.NewGuid():N}");
    private readonly VideoPhoneFootageConfiguration _rules = new();

    public VideoPhoneFootageDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    [Fact]
    public void CameraFolderWithCameraNames_LooksLikePhoneFootage()
    {
        Touch(Path.Combine("DCIM", "100APPLE", "IMG_1234.mov"));
        Touch(Path.Combine("DCIM", "100APPLE", "IMG_1235.mov"));

        Assert.True(VideoPhoneFootageDetector.Looks(_root, _rules));
    }

    [Fact]
    public void MostlyCameraStyleNames_LooksLikePhoneFootage()
    {
        Touch("MOV_0042.mp4");
        Touch("MOV_0043.mp4");
        Touch("Birthday.mp4");

        Assert.True(VideoPhoneFootageDetector.Looks(_root, _rules));
    }

    [Fact]
    public void CourseAndDownloads_DoNotLookLikePhoneFootage()
    {
        Touch(Path.Combine("Python Course", "01 - Welcome.mp4"));
        Touch(Path.Combine("Python Course", "02 - Basics.mp4"));
        Touch("Sample Talk [dQw4w9WgXcQ].mp4");
        Touch("IMG_0001.mp4");

        Assert.False(VideoPhoneFootageDetector.Looks(_root, _rules));
    }

    [Fact]
    public void EmptyOrMissingFolder_GetsNoHint()
    {
        Assert.False(VideoPhoneFootageDetector.Looks(_root, _rules));
        Assert.False(VideoPhoneFootageDetector.Looks(Path.Combine(_root, "missing"), _rules));
    }
}
