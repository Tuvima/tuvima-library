namespace MediaEngine.Storage.Backup;

/// <summary>
/// The kept tables that store a position in a file as seconds, and what a re-attach may do with them (backup and restore spec D6). A
/// position only means the same thing on the same cut of a title. On a different cut such a row attaches only when its chapter name is
/// known and the live file has a chapter of that name (<c>BookmarkPackMatcher.AlignByChapter</c>); otherwise it keeps waiting. A row
/// in a table that is not listed here (a progress percent, a reaction, a saved item, a preference) attaches on a different cut too,
/// and so does a table whose only seconds are inside a JSON column (<c>user_states</c>): it attaches with its percent, and those
/// seconds are left out.
/// A guardrail test fails when a kept table gains a seconds column that is neither listed here nor named as not a position.
/// </summary>
internal static class WaitingStateTimePositions
{
    /// <summary>How a table stores positions in seconds.</summary>
    /// <param name="Positions">The columns that hold a position, the first being the point and a second (if any) the end of a range.</param>
    /// <param name="ChapterColumn">The column holding the name of the chapter the position is in; null when the table keeps none.</param>
    /// <param name="ExactCutOnly">True when the row can attach only to the exact same cut, because the positions cannot be moved or left out.</param>
    /// <param name="JsonColumn">A JSON text column that holds seconds under some keys; null when the table keeps none.</param>
    /// <param name="JsonSecondsKeys">The keys of <paramref name="JsonColumn"/> that hold seconds in the cut. They are removed on a different cut.</param>
    internal sealed record TimeColumns(
        IReadOnlyList<string> Positions,
        string? ChapterColumn,
        bool ExactCutOnly,
        string? JsonColumn = null,
        IReadOnlyList<string>? JsonSecondsKeys = null);

    /// <summary>The tables, by name.</summary>
    public static IReadOnlyDictionary<string, TimeColumns> Tables { get; } = new Dictionary<string, TimeColumns>(StringComparer.OrdinalIgnoreCase)
    {
        // A bookmark in an audiobook or video: a point, sometimes a range, with the chapter it sits in.
        ["audiobook_bookmarks"] = new(["position_seconds", "end_seconds"], "chapter_title", false),

        // Where a person stopped listening; the chapter it was in.
        ["audiobook_listen_history"] = new(["position_seconds"], "chapter_title", false),

        // Where a person stopped on each device; no chapter name is kept, so only the exact cut will do.
        ["player_device_states"] = new(["position_seconds"], null, false),

        // The saved playback timing is inside extended_properties: position_seconds (where playback stopped) and duration_seconds (the
        // length of the cut it was saved on), the two keys PlayerService writes in seconds. On a different cut the row attaches with its
        // percent and those two keys are removed (the other keys, such as the device, client and hide-from-Continue flag, stay).
        ["user_states"] = new([], null, false, "extended_properties", ["position_seconds", "duration_seconds"]),

        // The bookmarks inside a pack are positions in the cut the pack was made on.
        ["bookmark_packs"] = new([], null, true),
    };

    /// <summary>
    /// Seconds columns of kept tables that are a length or a total, not a position in a file, so a different cut does not change what
    /// they mean. Named here so the guardrail test knows they were considered.
    /// </summary>
    public static IReadOnlySet<string> NotPositions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audiobook_bookmarks.duration_seconds",
        "audiobook_listen_history.duration_seconds",
        "player_device_states.duration_seconds",
        "personal_activity.played_seconds",
        "reader_statistics.total_reading_time_secs",
        "bookmark_packs.source_running_time_seconds",
    };
}
