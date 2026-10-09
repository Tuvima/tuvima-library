using MediaInfo;

namespace MediaEngine.Api.Tests;

public sealed class NativeMediaRuntimeTests
{
    [Fact]
    public void SelectedNativeRuntime_ParsesWaveAudio()
    {
        if (OperatingSystem.IsLinux())
        {
            // MediaInfo.Core.Native has no generic linux-x64 asset: it ships only distro-named builds
            // (ubuntu.22.04-x64, debian-x64, ...), which the .NET host loads only when its own RID names
            // that exact distro. Portable Linux runtimes report linux-x64, so parsing cannot be exercised
            // here; instead prove the build output still carries those libraries (the runtime filter in
            // Directory.Build.targets once dropped them, leaving nothing to load on any distro).
            var libraries = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "runtimes"), "libmediainfo.so", SearchOption.AllDirectories);
            Assert.NotEmpty(libraries);
            Assert.Contains(libraries, path => path.Contains("debian-x64", StringComparison.Ordinal));
            return;
        }

        // Real parsing catches missing native libraries and their dependencies;
        // the wrapper can otherwise return empty metadata without throwing.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            const int samples = 8000;
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(samples);
            writer.Write(samples * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            writer.Write(new byte[samples * 2]);
        }
        stream.Position = 0;
        var media = new MediaInfoWrapper(stream);
        Assert.Single(media.AudioStreams);
        Assert.InRange(media.Duration, 990, 1010);
    }
}
