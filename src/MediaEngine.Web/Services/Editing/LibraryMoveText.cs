using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The words of a move: the entry point label, the confirmation (details overlay spec D17) and the result line.</summary>
public static class LibraryMoveText
{
    public const string ForbiddenMessage = "You can't change this item's identity.";
    public const string ChangedLine = "Something changed while this was open. Check again.";
    public const string ExpiredMessage = "This review expired. Review the move again.";
    public const string UnavailableMessage = "The move could not be checked. Try again in a moment.";
    public const string NoDestinationsMessage = "There is nowhere this item can be moved to.";
    public const string GenericRefusal = "This move could not be done.";

    /// <summary>The "Move" entry's label for this kind of item (Details overlay spec section 4).</summary>
    public static string MoveLabel(string? mediaType) => (mediaType ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "music" => "Move to another album",
        "tv" => "Move to another season or show",
        "books" or "book" or "comics" or "comic" => "Move to another series",
        "audiobooks" or "audiobook" => "Move to another audiobook",
        _ => "Move to a Series",
    };

    /// <summary>The label for a Not matched item: it is placed through the same Match step.</summary>
    public const string MatchLabel = "Match";

    /// <summary>"track", "episode", "position" or "part": what an unnumbered item does not get (D13).</summary>
    public static string NumberWord(string listKind) => listKind switch
    {
        LibraryMoveListKinds.Tracks => "track",
        LibraryMoveListKinds.Episodes => "episode",
        LibraryMoveListKinds.Parts => "part",
        _ => "position",
    };

    /// <summary>How an entry reads in a sentence: "Track 2", "Part 3", "Position 1.5" or an episode's own "S2 E5".</summary>
    public static string EntryLabel(LibraryMovePreview preview, LibraryMoveEntry entry) => preview.ListKind switch
    {
        LibraryMoveListKinds.Tracks => $"Track {entry.Number}",
        LibraryMoveListKinds.Parts => $"Part {entry.Number}",
        LibraryMoveListKinds.Positions => $"Position {entry.Number}",
        _ => entry.Number,
    };

    /// <summary>The destination as the person reads it: "OK Computer", or for a season "Season 2 of Severance" (a season alone is ambiguous).</summary>
    public static string DestinationLabel(LibraryMoveDestination destination) =>
        string.Equals(destination.Kind, LibraryMoveDestinationKinds.Season, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(destination.ParentLabel)
            ? $"{destination.Label} of {destination.ParentLabel}"
            : destination.Label;

    /// <summary>"Track 2 already has a file. Add as another version, or pick another track." (D16).</summary>
    public static string TakenMessage(LibraryMovePreview preview, LibraryMoveEntry entry) =>
        $"{EntryLabel(preview, entry)} already has a file. Add as another version, or pick another {NumberWord(preview.ListKind)}.";

    /// <summary>The same without the version choice, when the Engine can't add a version here.</summary>
    public static string TakenWithoutVersionMessage(LibraryMovePreview preview, LibraryMoveEntry entry) =>
        $"{EntryLabel(preview, entry)} already has a file. Pick another {NumberWord(preview.ListKind)}.";

    /// <summary>What the confirmation dialog shows.</summary>
    public sealed record Confirmation(
        string Title,
        string Message,
        string PrimaryLabel,
        string SecondaryLabel,
        IReadOnlyList<string> FileLines,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// The one confirmation for a move (D17): what moves, where it goes, what it leaves and what happens to the files. A move with
    /// an item added anyway says plainly it gets no number, and offers "Match instead".
    /// </summary>
    public static Confirmation Confirm(LibraryMoveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var preview = selection.Preview;
        var destination = DestinationLabel(preview.Destination);
        var leaves = preview.Warnings
            .Where(warning => warning.StartsWith("Leaves ", StringComparison.OrdinalIgnoreCase))
            .Select(warning => warning["Leaves ".Length..].Trim().TrimEnd('.'))
            .ToList();
        var notes = preview.Warnings
            .Where(warning => !warning.StartsWith("Leaves ", StringComparison.OrdinalIgnoreCase))
            .Select(EndSentence)
            .ToList();
        var leavesClause = leaves.Count == 0 ? string.Empty : $" It leaves {JoinNames(leaves)}.";

        var rows = selection.Rows;
        string title;
        string message;
        string primary;
        string secondary;
        if (selection.HasAddAnyway)
        {
            var numberWord = NumberWord(preview.ListKind);
            var anyway = rows.Where(row => row.Choice == LibraryMoveRowChoice.AddAnyway).ToList();
            title = anyway.Count == 1 ? $"Add {anyway[0].Title} anyway?" : $"Add {anyway.Count} items anyway?";
            message = $"This won't get a {numberWord} number or details from {destination}. You can match it later.";
            if (rows.Count > anyway.Count)
            {
                message = $"{message} The other {(rows.Count - anyway.Count == 1 ? "item is" : "items are")} matched as chosen.";
            }

            message += leavesClause;
            primary = "Add anyway";
            secondary = "Match instead";
        }
        else
        {
            title = rows.Count == 1 ? $"Move {rows[0].Title}?" : $"Move {rows.Count} items?";
            message = rows.Count == 1
                ? SingleMoveSentence(selection, rows[0], destination) + leavesClause
                : $"Move {rows.Count} items to {destination}?{leavesClause}";
            primary = "Move";
            secondary = "Cancel";
        }

        return new Confirmation(title, message, primary, secondary, FileLines(selection), notes);
    }

    /// <summary>Which of the two lines the dialog shows for each item: what happens to the file, or that files stay put.</summary>
    public static IReadOnlyList<string> FileLines(LibraryMoveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var several = selection.Rows.Count > 1;
        var lines = new List<string>();
        foreach (var row in selection.Rows)
        {
            var line = FileLine(row.Source);
            lines.Add(several ? $"{row.Title}: {line}" : line);
        }

        return lines;
    }

    private static string FileLine(LibraryMovePreviewRow row)
    {
        if (row.FileChange is { } change && !string.IsNullOrWhiteSpace(change.To))
        {
            return RenameLine(change.To);
        }

        return !string.IsNullOrWhiteSpace(row.FileNote) ? EndSentence(row.FileNote) : "Files stay where they are.";
    }

    // "The file will be renamed to 02 - Paranoid Android.flac and moved to OK Computer": the new name is the last part of
    // the Engine's "to" and the place is the folder it sits in. A bare name only renames.
    private static string RenameLine(string to)
    {
        var parts = to.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "The file will be renamed and moved to fit.";
        }

        return parts.Length == 1
            ? $"The file will be renamed to {parts[0]}."
            : $"The file will be renamed to {parts[^1]} and moved to {parts[^2]}.";
    }

    private static string SingleMoveSentence(LibraryMoveSelection selection, LibraryMoveRowState row, string destination)
    {
        if (!selection.HasList)
        {
            return $"Move {row.Title} to {destination}? It goes to the end of {destination}.";
        }

        var entry = selection.Entry(row.EntryKey);
        var label = entry is null ? string.Empty : EntryLabel(selection.Preview, entry);
        return row.Choice == LibraryMoveRowChoice.AddAsVersion
            ? $"Add {row.Title} to {destination} as another version of {label}?"
            : $"Move {row.Title} to {destination} as {label}?";
    }

    /// <summary>The result line after a move: what happened, and anything the move removed.</summary>
    public static string ResultLine(LibraryMoveSelection selection, LibraryMoveCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(result);
        var destination = DestinationLabel(selection.Preview.Destination);
        string line;
        if (selection.Rows.Count == 1)
        {
            var row = selection.Rows[0];
            var entry = selection.Entry(row.EntryKey);
            var label = entry is null ? string.Empty : EntryLabel(selection.Preview, entry);
            line = row.Choice switch
            {
                LibraryMoveRowChoice.AddAnyway => $"Added {row.Title} to {destination} without a number. It shows as Not matched.",
                LibraryMoveRowChoice.AddAsVersion => $"Added {row.Title} to {destination} as another version of {label}.",
                _ when selection.HasList => $"Moved {row.Title} to {destination} as {label}.",
                _ => $"Moved {row.Title} to {destination}.",
            };
        }
        else
        {
            var anyway = selection.Rows.Count(row => row.Choice == LibraryMoveRowChoice.AddAnyway);
            line = $"Moved {result.MovedCount} items to {destination}."
                + (anyway > 0 ? $" {anyway} added without numbers and shown as Not matched." : string.Empty);
        }

        return result.RemovedContainers.Count == 0
            ? line
            : $"{line} {JoinNames(result.RemovedContainers)} {(result.RemovedContainers.Count == 1 ? "was" : "were")} left empty and removed.";
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    private static string EndSentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ? trimmed : trimmed + ".";
    }
}
