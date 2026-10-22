using System.Globalization;

namespace MediaEngine.Domain.Services;

/// <summary>How to recognise a title: its canonical IDs plus the running time and edition of the copy a pack was made on (Videos spec D46).</summary>
public sealed record BookmarkPackTitle(
    IReadOnlyDictionary<string, string> Ids,
    double? RunningTimeSeconds,
    string? Edition);

/// <summary>One bookmark as a pack carries it. Nothing here names a file, a profile or what anyone watched.</summary>
public sealed record BookmarkPackBookmark(
    double Time,
    double? EndTime,
    string? Chapter,
    double? ChapterStart,
    string? Note,
    string? Description);

/// <summary>A chapter of the copy the pack is being imported on.</summary>
public sealed record BookmarkPackChapter(string Title, double StartSeconds);

public enum BookmarkPackMatchKind
{
    /// <summary>Same title, same running time and edition: the times can be used as they are.</summary>
    Match,

    /// <summary>Same title but a different cut (another running time or edition): the person chooses to align by chapter or skip.</summary>
    DifferentCut,

    /// <summary>Not this title.</summary>
    WrongTitle,
}

public sealed record BookmarkPackMatch(BookmarkPackMatchKind Kind, string Reason);

public sealed record BookmarkPackAlignment(IReadOnlyList<BookmarkPackBookmark> Aligned, int Skipped);

/// <summary>
/// The rules that decide whether a bookmark pack is for the title it is being imported on, and how its times move when it was made
/// on a different cut (Videos spec D46). Pure, so the rules are tested without a data store.
/// </summary>
public static class BookmarkPackMatcher
{
    /// <summary>The IDs a pack may carry to recognise a title. Anything else (internal IDs, person IDs) is never written to a pack.</summary>
    public static readonly IReadOnlySet<string> RecognitionIdKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "imdb_id", "tmdb_id", "tvdb_id", "wikidata_qid", "tmdb_episode_id", "tvdb_episode_id", "youtube_id",
    };

    private const double MinimumRunningTimeToleranceSeconds = 2.0;
    private const double RunningTimeToleranceFraction = 0.002;

    /// <summary>
    /// The extra ID a restored Vault bookmark may use to recognise a matched Vault video: the Stash-box server and record, written
    /// <c>server|record</c>. It is only ever read by <see cref="VaultBookmarkRelinkPlanner"/>; a pack never writes it.
    /// </summary>
    public const string VaultStashBoxIdKey = "stash_box_id";

    public static BookmarkPackMatch Compare(BookmarkPackTitle pack, BookmarkPackTitle local) =>
        Compare(pack, local, extraIdKeys: null);

    /// <summary>
    /// Same rules as <see cref="Compare(BookmarkPackTitle, BookmarkPackTitle)"/>, also recognising the given extra ID keys
    /// (the Vault re-link uses this to add the Stash-box ID without changing what a pack may carry).
    /// </summary>
    public static BookmarkPackMatch Compare(BookmarkPackTitle pack, BookmarkPackTitle local, IReadOnlySet<string>? extraIdKeys)
    {
        var packIds = Normalize(pack.Ids, extraIdKeys);
        var localIds = Normalize(local.Ids, extraIdKeys);
        var shared = packIds.Keys.Where(localIds.ContainsKey).ToList();
        if (shared.Count == 0)
        {
            return new BookmarkPackMatch(BookmarkPackMatchKind.WrongTitle, "This pack does not name any ID that this title has, so it is not for this title.");
        }

        if (shared.Any(key => !string.Equals(packIds[key], localIds[key], StringComparison.OrdinalIgnoreCase)))
        {
            return new BookmarkPackMatch(BookmarkPackMatchKind.WrongTitle, "This pack is for a different title.");
        }

        if (pack.RunningTimeSeconds is double packTime && local.RunningTimeSeconds is double localTime
            && double.IsFinite(packTime) && double.IsFinite(localTime))
        {
            var tolerance = Math.Max(MinimumRunningTimeToleranceSeconds, Math.Max(packTime, localTime) * RunningTimeToleranceFraction);
            return Math.Abs(packTime - localTime) > tolerance
                ? new BookmarkPackMatch(BookmarkPackMatchKind.DifferentCut,
                    $"This pack was made on a cut that runs {Format(packTime)}; your copy runs {Format(localTime)}.")
                : new BookmarkPackMatch(BookmarkPackMatchKind.Match, "This pack is for this title and cut.");
        }

        if (!string.IsNullOrWhiteSpace(pack.Edition) && !string.IsNullOrWhiteSpace(local.Edition)
            && !string.Equals(pack.Edition.Trim(), local.Edition.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new BookmarkPackMatch(BookmarkPackMatchKind.DifferentCut,
                $"This pack was made on the {pack.Edition.Trim()} edition; your copy is {local.Edition.Trim()}.");
        }

        return new BookmarkPackMatch(BookmarkPackMatchKind.Match, "This pack is for this title.");
    }

    /// <summary>
    /// Moves a pack made on another cut onto this copy by chapter name: a bookmark keeps its distance into the chapter and lands in the
    /// chapter of the same name. A bookmark with no chapter name, or whose chapter this copy does not have, is skipped.
    /// </summary>
    public static BookmarkPackAlignment AlignByChapter(
        IReadOnlyList<BookmarkPackBookmark> bookmarks,
        IReadOnlyList<BookmarkPackChapter> chapters,
        double? runningTimeSeconds)
    {
        var aligned = new List<BookmarkPackBookmark>();
        var skipped = 0;
        foreach (var bookmark in bookmarks)
        {
            var key = NormalizeChapter(bookmark.Chapter);
            BookmarkPackChapter? target = null;
            if (key is not null)
            {
                target = chapters.Where(chapter => NormalizeChapter(chapter.Title) == key)
                    .OrderBy(chapter => Math.Abs(chapter.StartSeconds - (bookmark.ChapterStart ?? 0)))
                    .FirstOrDefault();
            }

            if (target is null)
            {
                skipped++;
                continue;
            }

            var shift = target.StartSeconds - (bookmark.ChapterStart ?? bookmark.Time);
            var time = bookmark.Time + shift;
            double? end = bookmark.EndTime is double original ? original + shift : null;
            if (!double.IsFinite(time) || time < 0 || (runningTimeSeconds is double total && total > 0 && time > total))
            {
                skipped++;
                continue;
            }

            if (end is double endTime
                && (!double.IsFinite(endTime) || endTime <= time || (runningTimeSeconds is double all && all > 0 && endTime > all)))
            {
                end = null;
            }

            aligned.Add(bookmark with { Time = time, EndTime = end, ChapterStart = target.StartSeconds });
        }

        return new BookmarkPackAlignment(aligned, skipped);
    }

    /// <summary>True when both sides carry at least one recognition ID of the same kind (whether or not the values agree).</summary>
    public static bool ShareAnIdKind(
        IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second, IReadOnlySet<string>? extraIdKeys)
    {
        var secondIds = Normalize(second, extraIdKeys);
        return Normalize(first, extraIdKeys).Keys.Any(secondIds.ContainsKey);
    }

    private static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string> ids, IReadOnlySet<string>? extraIdKeys) => ids
        .Where(pair => (RecognitionIdKeys.Contains(pair.Key) || (extraIdKeys?.Contains(pair.Key) ?? false)) && !string.IsNullOrWhiteSpace(pair.Value))
        .GroupBy(pair => pair.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.Ordinal);

    private static string? NormalizeChapter(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : title.Trim().ToLowerInvariant();

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }
}
