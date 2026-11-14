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
}
