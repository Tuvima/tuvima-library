using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Projects recording-local playback state onto the book timeline when the source
/// provides a complete ordered set of recording chapter durations.
/// </summary>
public sealed record AudiobookPlaybackProjection(
    string Label,
    double Position,
    double Total,
    PlaybackChapterDto? ActiveChapter,
    int ChapterCount)
{
    public static AudiobookPlaybackProjection? Create(
        IReadOnlyList<PlaybackChapterDto>? chapters,
        Guid? currentAssetId,
        double currentTimeSeconds,
        double currentRecordingDurationSeconds)
    {
        if (!double.IsFinite(currentTimeSeconds) || currentTimeSeconds < 0)
        {
            currentTimeSeconds = 0;
        }

        if (chapters is null || chapters.Count == 0)
        {
            return CreateCurrentRecording(currentTimeSeconds, currentRecordingDurationSeconds, null, 0);
        }

        var assetParts = chapters
            .Where(chapter => chapter.AssetId.HasValue)
            .GroupBy(chapter => chapter.AssetId!.Value)
            .OrderBy(group => group.Min(chapter => chapter.Index))
            .ToList();

        var currentAssetChapters = currentAssetId is Guid assetId
            ? chapters.Where(chapter => chapter.AssetId == assetId).ToArray()
            : [];
        var chapterCount = chapters.Count;
        var activeChapter = currentAssetChapters.LastOrDefault(chapter =>
            chapter.StartSeconds <= currentTimeSeconds
            && (!chapter.EndSeconds.HasValue || currentTimeSeconds < chapter.EndSeconds.Value));

        if (assetParts.Count <= 1)
        {
            return CreateCurrentRecording(currentTimeSeconds, currentRecordingDurationSeconds, activeChapter,
                    chapterCount);
        }

        if (chapters.Any(chapter => !chapter.AssetId.HasValue)
            || assetParts.Any(group => group.Any(chapter =>
                !double.IsFinite(chapter.StartSeconds) || chapter.StartSeconds < 0
                || !chapter.EndSeconds.HasValue || !double.IsFinite(chapter.EndSeconds.Value)
                || chapter.EndSeconds.Value <= chapter.StartSeconds))
            || currentAssetId is not Guid currentId
            || !assetParts.Any(group => group.Key == currentId))
        {
            return CreateCurrentRecording(currentTimeSeconds, currentRecordingDurationSeconds, activeChapter,
                chapterCount);
        }

        var totalSeconds = assetParts.Sum(group => group.Max(chapter => chapter.EndSeconds!.Value));
        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0)
        {
            return CreateCurrentRecording(currentTimeSeconds, currentRecordingDurationSeconds, activeChapter,
                    chapterCount);
        }

        var elapsedBeforeCurrent = 0d;
        foreach (var part in assetParts)
        {
            if (part.Key == currentId)
            {
                break;
            }
            elapsedBeforeCurrent += part.Max(chapter => chapter.EndSeconds!.Value);
        }

        return new AudiobookPlaybackProjection(
            "Book progress",
            Math.Clamp(elapsedBeforeCurrent + currentTimeSeconds, 0, totalSeconds),
            totalSeconds,
            activeChapter,
            chapterCount);
    }

    private static AudiobookPlaybackProjection? CreateCurrentRecording(
        double currentTimeSeconds,
        double durationSeconds,
        PlaybackChapterDto? activeChapter,
        int activeChapterCount)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return null;
        }

        return new AudiobookPlaybackProjection(
            "Current recording",
            Math.Clamp(currentTimeSeconds, 0, durationSeconds),
            durationSeconds,
            activeChapter,
            activeChapterCount);
    }
}
