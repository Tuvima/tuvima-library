using System.Globalization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Turns one text subtitle stream inside a video file into WebVTT the first time a player asks for it, and keeps the result in the
/// temporary folder so the next request (and every other viewer) reads the saved copy. The copy is tied to the file's size and
/// modified time, so a replaced file gets a fresh one.
/// </summary>
internal static class EmbeddedSubtitleCache
{
    private static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>The saved WebVTT file for one stream, or null when ffmpeg could not read it as text (a picture track, or a stream number that is not there).</summary>
    public static async Task<string?> GetOrExtractAsync(IFFmpegService ffmpeg, ILogger logger, Guid assetId, string filePath, int streamIndex, CancellationToken ct)
    {
        if (streamIndex < 0)
        {
            return null;
        }

        var info = new FileInfo(filePath);
        var folder = Path.Combine(Path.GetTempPath(), "tuvima-embedded-subtitles");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{assetId:N}-{streamIndex.ToString(CultureInfo.InvariantCulture)}-{info.Length.ToString(CultureInfo.InvariantCulture)}-{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}.vtt");
        if (File.Exists(target))
        {
            return target;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            var partial = target + "." + Guid.NewGuid().ToString("N") + ".tmp.vtt";
            var (exitCode, _, error) = await ffmpeg.RunAsync(
                ["-y", "-v", "error", "-i", filePath, "-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}", "-c:s", "webvtt", partial],
                ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(partial))
            {
                logger.LogInformation("Embedded subtitle stream {StreamIndex} of asset {AssetId} could not be read as text: {Error}", streamIndex, assetId, Tail(error));
                TryDelete(partial);
                return null;
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Tail(string? text) => string.IsNullOrWhiteSpace(text) ? "no detail" : text.Trim().Length > 300 ? text.Trim()[^300..] : text.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover partial file in the temporary folder is harmless and is overwritten or cleaned up by the system.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: the partial file stays until the system clears the temporary folder.
        }
    }
}
