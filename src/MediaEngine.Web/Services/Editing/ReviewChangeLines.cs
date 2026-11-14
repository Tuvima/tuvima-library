using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One file that will move or be renamed on save: the single sentence Review changes shows for it inside the amber block.</summary>
public sealed record ReviewFileEntry(EditReviewRow Row, string Text);

/// <summary>What Review changes draws (editor v7 D11): one line per change, then at most one amber block for the files that will move or be renamed.</summary>
public sealed record ReviewChangeLayout(IReadOnlyList<EditReviewRow> Lines, IReadOnlyList<ReviewFileEntry> Files, bool MultipleItems)
{
    public int Count => Lines.Count + Files.Count;

    /// <summary>The amber block's heading, or null when no file will move.</summary>
    public string? FileHeading => Files.Count == 0
        ? null
        : Files.All(entry => entry.Row.Kind == EditSessionChangeKinds.VideoRename)
            ? "Files will be renamed when you save."
            : Files.Count == 1 ? "This file will move when you save." : "These files will move when you save.";
}

/// <summary>
/// Turns the review rows into the compact list (editor v7 D11). Pure: the same fact is shown once. A row that repeats an earlier one is dropped,
/// the rows about files join one amber block instead of each carrying its own warning, and a file's line does not name a series again when the
/// Series line already does. Pictures are not special-cased: nothing the Artwork tab does is staged any more, so a picture line only appears when
/// a bulk edit staged one, and that change is still saved on Save, so it stays visible.
/// </summary>
public static class ReviewChangeLines
{
    // Before values that say "not recorded" rather than name something; the line then shows only what it changes to.
    private static readonly string[] Placeholders = [EditReviewRows.AsItIsNow, "Where it is now", "Where they are now", "As they are now"];

    public static ReviewChangeLayout Build(IReadOnlyList<EditReviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<EditReviewRow>();
        var fileRows = new List<EditReviewRow>();
        foreach (var row in rows)
        {
            if (!seen.Add($"{row.EntityId:N}|{row.Kind}|{row.What}|{row.After}"))
            {
                continue;
            }

            (row.Group == EditReviewGroup.PhysicalFiles ? fileRows : lines).Add(row);
        }

        var files = fileRows.Select(row => new ReviewFileEntry(row, FileText(row, lines))).ToList();
        var multiple = rows.Select(row => row.Item).Distinct(StringComparer.Ordinal).Count() > 1;
        return new ReviewChangeLayout(lines, files, multiple);
    }

    /// <summary>True when the row has a recorded old value worth showing struck through.</summary>
    public static bool HasBefore(EditReviewRow row) =>
        !string.IsNullOrWhiteSpace(row.Before) && !Placeholders.Contains(row.Before, StringComparer.Ordinal);

    /// <summary>The note under a line, if any. The "Library only" reassurance is left out: with no amber block, no file moves.</summary>
    public static string? NoteFor(EditReviewRow row) =>
        row.Group == EditReviewGroup.PhysicalFiles || string.IsNullOrWhiteSpace(row.Warning) || row.Warning == EditReviewRows.VideoSeriesNote ? null : row.Warning;

    private static string FileText(EditReviewRow row, List<EditReviewRow> lines)
    {
        // A video's file that follows its series says "Moved to the series folder: <where>"; the phrase is the Series line's business, so the
        // two paths are what this line adds. Without recorded paths the phrase stays, but not the name the Series line already carries.
        var seriesFolder = EditReviewRows.MovedToSeriesFolder + ":";
        var where = row.After.StartsWith(seriesFolder, StringComparison.Ordinal) ? row.After[seriesFolder.Length..].Trim() : null;
        if (HasBefore(row))
        {
            return $"{row.Before} → {where ?? row.After}";
        }

        if (where is not null && lines.Any(line => line.Kind == EditSessionChangeKinds.VideoSeries && line.EntityId == row.EntityId))
        {
            return EditReviewRows.MovedToSeriesFolder;
        }

        return row.After;
    }
}
