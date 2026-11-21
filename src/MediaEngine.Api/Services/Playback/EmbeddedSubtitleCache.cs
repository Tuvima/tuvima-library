using System.Collections.Concurrent;
using System.Globalization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Turns the text subtitle streams inside a video file into WebVTT, once, and keeps the results in the temporary folder so every later
/// request (and every other viewer) reads the saved copy. A copy is tied to the file's size and modified time, so a replaced file gets
/// fresh ones.
/// </summary>
/// <remarks>
/// Reading a subtitle out of a large film means reading the whole file (the subtitle pieces are spread through it), which can take
/// minutes, especially while the same film is being converted. So all of a film's text streams are read in ONE pass, in the background,
/// as soon as a player asks about the film, and a stream is offered to players only once its copy exists (issue 1166). Reading each
/// stream separately, on request, doubled the reading and left players waiting on a request that timed out.
/// </remarks>
internal static class EmbeddedSubtitleCache
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly TimeSpan ExtractionLimit = TimeSpan.FromMinutes(20);

    // One background job per film at a time, shared by everyone who needs any of its streams.
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlight = new(StringComparer.Ordinal);

    // Streams ffmpeg could not read as text (so they are not offered and not tried again until the file changes or the Engine restarts).
    private static readonly ConcurrentDictionary<string, bool> Unreadable = new(StringComparer.Ordinal);

    /// <summary>Where the saved WebVTT copy of one stream lives (it may not exist yet).</summary>
    public static string TargetFor(Guid assetId, string filePath, int streamIndex)
    {
        var info = new FileInfo(filePath);
        var folder = Path.Combine(Path.GetTempPath(), "tuvima-embedded-subtitles");
        return Path.Combine(folder, $"{assetId:N}-{streamIndex.ToString(CultureInfo.InvariantCulture)}-{info.Length.ToString(CultureInfo.InvariantCulture)}-{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}.vtt");
    }

    /// <summary>The saved copy of one stream, or null when it has not been read yet.</summary>
    public static string? TryGetReady(Guid assetId, string filePath, int streamIndex)
    {
        if (streamIndex < 0 || !File.Exists(filePath))
        {
            return null;
        }

        var target = TargetFor(assetId, filePath, streamIndex);
        return File.Exists(target) ? target : null;
    }

    /// <summary>True when ffmpeg already tried this stream and could not read it as text.</summary>
    public static bool IsUnreadable(Guid assetId, string filePath, int streamIndex) =>
        File.Exists(filePath) && Unreadable.ContainsKey(TargetFor(assetId, filePath, streamIndex));

    /// <summary>
    /// Starts reading every listed text stream that has no saved copy yet, in one pass, without waiting for it. Does nothing when
    /// everything is saved or a read of this film is already running. The returned task finishes when the job does.
    /// </summary>
    public static Task EnsureStarted(IFFmpegService ffmpeg, ILogger logger, Guid assetId, string filePath, IReadOnlyList<int> streamIndexes)
    {
        if (streamIndexes.Count == 0 || !File.Exists(filePath))
        {
            return Task.CompletedTask;
        }

        var missing = streamIndexes
            .Where(index => index >= 0 && TryGetReady(assetId, filePath, index) is null && !IsUnreadable(assetId, filePath, index))
            .Distinct()
            .ToList();
        if (missing.Count == 0)
        {
            return Task.CompletedTask;
        }

        var key = JobKey(assetId, filePath);
        return InFlight.GetOrAdd(key, _ => new Lazy<Task>(() => Task.Run(async () =>
        {
            try
            {
                using var limit = new CancellationTokenSource(ExtractionLimit);
                await ExtractAllAsync(ffmpeg, logger, assetId, filePath, missing, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Too slow to read this film: remember it so every later play does not read the whole file again from the start.
                logger.LogInformation("Reading the embedded subtitles of asset {AssetId} took longer than {Minutes} minutes and was stopped", assetId, ExtractionLimit.TotalMinutes);
                MarkUnreadable(assetId, filePath, missing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // No ffmpeg, or the temporary folder cannot be written: the subtitles are left out and playback carries on without them.
                logger.LogWarning(ex, "Reading the embedded subtitles of asset {AssetId} failed", assetId);
                MarkUnreadable(assetId, filePath, missing);
            }
            finally
            {
                InFlight.TryRemove(key, out Lazy<Task>? _);
            }
        }))).Value;
    }

    /// <summary>
    /// The saved WebVTT file for one stream, waiting for the background read when it is not saved yet. Null when ffmpeg could not read it
    /// as text (a picture track, or a stream number that is not there). A caller that stops waiting does not stop the read.
    /// </summary>
    public static async Task<string?> GetOrExtractAsync(IFFmpegService ffmpeg, ILogger logger, Guid assetId, string filePath, int streamIndex, CancellationToken ct)
    {
        if (streamIndex < 0)
        {
            return null;
        }

        if (TryGetReady(assetId, filePath, streamIndex) is { } ready)
        {
            return ready;
        }

        // A read of this film may already be running for its other streams: wait for it, then check again.
        if (InFlight.TryGetValue(JobKey(assetId, filePath), out var running))
        {
            await running.Value.WaitAsync(ct).ConfigureAwait(false);
        }

        if (TryGetReady(assetId, filePath, streamIndex) is { } afterWait)
        {
            return afterWait;
        }

        if (IsUnreadable(assetId, filePath, streamIndex))
        {
            return null;
        }

        await EnsureStarted(ffmpeg, logger, assetId, filePath, [streamIndex]).WaitAsync(ct).ConfigureAwait(false);
        return TryGetReady(assetId, filePath, streamIndex);
    }

    private static void MarkUnreadable(Guid assetId, string filePath, IEnumerable<int> streamIndexes)
    {
        foreach (var index in streamIndexes.Where(index => TryGetReady(assetId, filePath, index) is null))
        {
            Unreadable[TargetFor(assetId, filePath, index)] = true;
        }
    }

    private static string JobKey(Guid assetId, string filePath)
    {
        var info = new FileInfo(filePath);
        return $"{assetId:N}-{info.Length.ToString(CultureInfo.InvariantCulture)}-{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task ExtractAllAsync(IFFmpegService ffmpeg, ILogger logger, Guid assetId, string filePath, IReadOnlyList<int> streamIndexes, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TargetFor(assetId, filePath, 0))!);

            // One pass over the file for all the streams. When a single stream cannot be read the whole pass fails, so each stream is then
            // tried on its own to find the readable ones and to remember the others.
            if (streamIndexes.Count > 1 && await RunPassAsync(ffmpeg, logger, assetId, filePath, streamIndexes, ct).ConfigureAwait(false))
            {
                return;
            }

            if (streamIndexes.Count == 1)
            {
                await RunPassAsync(ffmpeg, logger, assetId, filePath, streamIndexes, ct).ConfigureAwait(false);
                return;
            }

            foreach (var streamIndex in streamIndexes.Where(index => TryGetReady(assetId, filePath, index) is null))
            {
                await RunPassAsync(ffmpeg, logger, assetId, filePath, [streamIndex], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> RunPassAsync(IFFmpegService ffmpeg, ILogger logger, Guid assetId, string filePath, IReadOnlyList<int> streamIndexes, CancellationToken ct)
    {
        var arguments = new List<string> { "-y", "-v", "error", "-nostdin", "-i", filePath };
        var outputs = new List<(int StreamIndex, string Partial, string Target)>();
        foreach (var streamIndex in streamIndexes)
        {
            var target = TargetFor(assetId, filePath, streamIndex);
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".tmp.vtt";
            outputs.Add((streamIndex, partial, target));
            arguments.AddRange(["-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}", "-c:s", "webvtt", partial]);
        }

        var (exitCode, _, error) = await ffmpeg.RunAsync(arguments, ct).ConfigureAwait(false);
        var allSaved = exitCode == 0;
        foreach (var output in outputs)
        {
            if (exitCode == 0 && File.Exists(output.Partial))
            {
                File.Move(output.Partial, output.Target, overwrite: true);
            }
            else
            {
                allSaved = false;
                TryDelete(output.Partial);
            }
        }

        if (!allSaved)
        {
            logger.LogInformation("Embedded subtitle stream(s) {Streams} of asset {AssetId} could not be read as text: {Error}",
                string.Join(", ", streamIndexes), assetId, Tail(error));
            if (streamIndexes.Count == 1)
            {
                Unreadable[outputs[0].Target] = true;
            }
        }

        return allSaved;
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
