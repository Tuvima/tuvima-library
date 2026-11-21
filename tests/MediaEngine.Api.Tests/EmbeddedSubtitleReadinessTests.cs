using System.Diagnostics;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 1166: a text subtitle is read out of the film once, together with its siblings, and is offered to a player only once its saved copy
/// exists, so every listed subtitle shows something when chosen.
/// </summary>
public sealed class EmbeddedSubtitleReadinessTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuvima-subtitle-readiness-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _asset = Guid.NewGuid();

    public EmbeddedSubtitleReadinessTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* The temporary folder is cleaned up by the system later. */ }
    }

    [RealFfmpegFact]
    public async Task AllTextStreamsOfAFilmAreSavedByOneReadOfTheFile()
    {
        var film = MakeFilmWithTwoTextSubtitles();
        var counting = new CountingFfmpeg(RealFfmpeg.Find()!);
        var streams = FindSubtitleStreams(film);
        Assert.Equal(2, streams.Count);
        Assert.All(streams, index => Assert.Null(EmbeddedSubtitleCache.TryGetReady(_asset, film, index)));

        await EmbeddedSubtitleCache.EnsureStarted(counting, NullLogger.Instance, _asset, film, streams);

        Assert.Equal(1, counting.Runs);
        foreach (var index in streams)
        {
            var saved = EmbeddedSubtitleCache.TryGetReady(_asset, film, index);
            Assert.NotNull(saved);
            Assert.StartsWith("WEBVTT", await File.ReadAllTextAsync(saved!));
        }
    }

    [RealFfmpegFact]
    public async Task AskingAgainWhenEverythingIsSavedReadsNothing()
    {
        var film = MakeFilmWithTwoTextSubtitles();
        var counting = new CountingFfmpeg(RealFfmpeg.Find()!);
        var streams = FindSubtitleStreams(film);
        await EmbeddedSubtitleCache.EnsureStarted(counting, NullLogger.Instance, _asset, film, streams);

        await EmbeddedSubtitleCache.EnsureStarted(counting, NullLogger.Instance, _asset, film, streams);
        var again = await EmbeddedSubtitleCache.GetOrExtractAsync(counting, NullLogger.Instance, _asset, film, streams[0], CancellationToken.None);

        Assert.Equal(1, counting.Runs);
        Assert.NotNull(again);
    }

    [RealFfmpegFact]
    public async Task AStreamThatIsNotThereIsRememberedAsUnreadableAndNeverOffered()
    {
        var film = MakeFilmWithTwoTextSubtitles();
        var counting = new CountingFfmpeg(RealFfmpeg.Find()!);

        var path = await EmbeddedSubtitleCache.GetOrExtractAsync(counting, NullLogger.Instance, _asset, film, 99, CancellationToken.None);

        Assert.Null(path);
        Assert.True(EmbeddedSubtitleCache.IsUnreadable(_asset, film, 99));
        Assert.Null(EmbeddedSubtitleCache.TryGetReady(_asset, film, 99));
    }

    [Fact]
    public void TheAnnouncedPictureLevelMatchesTheSizeOfThePicture()
    {
        Assert.Equal("avc1.4d401f", AdaptiveHlsService.AvcCodecFor(720));
        Assert.Equal("avc1.4d402a", AdaptiveHlsService.AvcCodecFor(1080));
        Assert.Equal("avc1.4d4033", AdaptiveHlsService.AvcCodecFor(2160));
        Assert.Equal("4.2", AdaptiveHlsService.AvcLevelFor(1080));
        Assert.Equal("3.0", AdaptiveHlsService.AvcLevelFor(480));
    }

    private string MakeFilmWithTwoTextSubtitles()
    {
        var first = Path.Combine(_folder, "en.srt");
        var second = Path.Combine(_folder, "sdh.srt");
        File.WriteAllText(first, "1\n00:00:00,000 --> 00:00:01,000\nHello\n\n");
        File.WriteAllText(second, "1\n00:00:00,000 --> 00:00:01,000\n[music] Hello\n\n");
        var film = Path.Combine(_folder, "film.mkv");
        Run(RealFfmpeg.Find()!.FfmpegPath!, "-y", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=160x90:d=2", "-i", first, "-i", second,
            "-map", "0:v", "-map", "1:0", "-map", "2:0", "-c:v", "libx264", "-c:s", "srt", film);
        return film;
    }

    private static IReadOnlyList<int> FindSubtitleStreams(string film)
    {
        var output = Run(RealFfmpeg.Find()!.FfprobePath!, "-v", "error", "-select_streams", "s", "-show_entries", "stream=index", "-of", "csv=p=0", film);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
    }

    private static string Run(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private sealed class CountingFfmpeg(IFFmpegService inner) : IFFmpegService
    {
        public int Runs;

        public string? FfmpegPath => inner.FfmpegPath;
        public string? FfprobePath => inner.FfprobePath;
        public bool IsAvailable => inner.IsAvailable;
        public MediaEngine.Domain.Models.HardwareCapabilities HardwareCapabilities => inner.HardwareCapabilities;

        public Task<MediaEngine.Domain.Models.MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => inner.ProbeAsync(filePath, ct);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Runs);
            return inner.RunAsync(arguments, ct);
        }

        public Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Runs);
            return inner.RunAsync(arguments, ct);
        }
    }
}
