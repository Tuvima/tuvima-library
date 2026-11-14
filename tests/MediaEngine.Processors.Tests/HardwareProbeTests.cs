using MediaEngine.Domain.Models;
using MediaEngine.Processors;

namespace MediaEngine.Processors.Tests;

public sealed class HardwareProbeTests
{
    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_vaapi")]
    public void ProbeArguments_UseAPictureSizeEveryEncoderAccepts(string encoder)
    {
        var arguments = FFmpegService.BuildProbeArguments(encoder);

        var source = arguments[arguments.ToList().IndexOf("-i") + 1];
        var size = source.Split("size=")[1].Split(':')[0].Split('x').Select(int.Parse).ToArray();
        // NVIDIA's encoder refuses pictures smaller than roughly 145 by 49 pixels; the old 64 by 64 test failed on every NVIDIA card.
        Assert.True(size[0] >= 145 && size[1] >= 49, $"The probe picture {size[0]}x{size[1]} is too small for NVENC.");
        Assert.Contains(encoder, arguments);
    }

    [Fact]
    public void ProbeArguments_UploadThePictureToTheCardForVaapiOnly()
    {
        Assert.Contains("format=nv12,hwupload", FFmpegService.BuildProbeArguments("h264_vaapi"));
        Assert.DoesNotContain("format=nv12,hwupload", FFmpegService.BuildProbeArguments("h264_nvenc"));
    }

    // ---- The graphics test runs again after FFmpeg is updated (playback-path spec D11) ----

    [Fact]
    public void TheEarlierHardwareAnswer_IsKeptWhileTheSameFFmpegFileIsInPlace()
    {
        var previous = Installation("tools/ffmpeg", "auto", "100:1");

        Assert.True(FFmpegService.KeepsHardwareAnswer(previous, "tools/ffmpeg", "auto", "100:1"));
    }

    [Theory]
    [InlineData("tools/other", "auto", "100:1")]
    [InlineData("tools/ffmpeg", "cpu", "100:1")]
    [InlineData("tools/ffmpeg", "auto", "200:2")]
    public void TheHardwareIsTestedAgain_WhenTheToolMovedTheChoiceChangedOrTheFileWasUpdatedInPlace(string path, string mode, string stamp)
    {
        var previous = Installation("tools/ffmpeg", "auto", "100:1");

        Assert.False(FFmpegService.KeepsHardwareAnswer(previous, path, mode, stamp));
    }

    [Fact]
    public void AnUpdatedFile_GetsADifferentStampFromTheOldOne()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tuvima_stamp_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(file, "old");
            var before = FFmpegService.BinaryStamp(file);
            File.WriteAllText(file, "a longer, newer copy");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
            var after = FFmpegService.BinaryStamp(file);

            Assert.NotNull(before);
            Assert.NotEqual(before, after);
            Assert.Null(FFmpegService.BinaryStamp(file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static FFmpegInstallation Installation(string path, string mode, string stamp) =>
        new(path, "tools/ffprobe", new HardwareCapabilities(), mode, FFmpegDetection.NotFound, stamp);
}
