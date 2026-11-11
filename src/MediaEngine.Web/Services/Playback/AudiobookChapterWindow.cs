using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The chapter the audiobook is in right now, as a window on the timeline (unified player D5): "Chapter 4 of 21",
/// where it starts and ends, and how far into it the listener is. The Chapter view of the bar uses it so the bar
/// shows the chapter instead of the whole book.
/// </summary>
public sealed record AudiobookChapterWindow(int Number, int Count, string Title, double Start, double End)
{
    public double Length => Math.Max(0d, End - Start);

    public string Label => $"Chapter {Number} of {Count}";

    /// <summary>Time into the chapter for an absolute position, clamped to the chapter.</summary>
    public double Elapsed(double position) => Math.Clamp(position - Start, 0d, Length);

    /// <summary>The absolute position for a time inside the chapter.</summary>
    public double ToAbsolute(double elapsedInChapter) => Start + Math.Clamp(elapsedInChapter, 0d, Length);

    /// <summary>Finds the chapter holding <paramref name="position"/>. Null when the book has fewer than two usable chapters.</summary>
    public static AudiobookChapterWindow? Find(IReadOnlyList<PlaybackChapterDto>? chapters, double position, double duration)
    {
        if (chapters is null || chapters.Count < 2 || !double.IsFinite(duration) || duration <= 0d)
        {
            return null;
        }

        var ordered = chapters.OrderBy(chapter => chapter.StartSeconds).ToList();
        var index = ordered.FindLastIndex(chapter => chapter.StartSeconds <= position);
        if (index < 0)
        {
            index = 0;
        }

        var chapter = ordered[index];
        var end = chapter.EndSeconds ?? (index + 1 < ordered.Count ? ordered[index + 1].StartSeconds : duration);
        if (!double.IsFinite(end) || end <= chapter.StartSeconds)
        {
            end = duration;
        }

        return new AudiobookChapterWindow(index + 1, ordered.Count, chapter.Title, chapter.StartSeconds, Math.Min(end, duration));
    }
}
