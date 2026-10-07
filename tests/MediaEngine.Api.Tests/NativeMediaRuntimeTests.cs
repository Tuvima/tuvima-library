using MediaInfo;

namespace MediaEngine.Api.Tests;

public sealed class NativeMediaRuntimeTests
{
    [Fact]
    public void SelectedNativeRuntime_ParsesWaveAudio()
    {
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
