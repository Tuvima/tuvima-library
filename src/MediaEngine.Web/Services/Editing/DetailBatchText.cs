using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Who may tick, move and delete rows together, and the plain words the selection bar and the batch section use (details overlay spec D8).</summary>
public static class DetailBatchRules
{
    /// <summary>A row can be ticked when it is one title (a track, episode, audiobook part, book, issue or movie), never a season, album or series.</summary>
    public static bool IsSelectableKind(DetailEntityType kind) => LibraryItemDeletionFlow.IsSingleTitle(kind);

    /// <summary>
    /// Move is offered when every ticked row is the same kind of single title: the destination picker only offers places that accept
    /// that kind (D15), so a mixed selection has no one right answer.
    /// </summary>
    public static bool CanMove(IReadOnlyList<DetailBatchRow> rows) =>
        rows.Count > 0
        && rows.All(row => IsSelectableKind(row.Kind))
        && rows.Select(row => row.Kind).Distinct().Count() == 1;

    /// <summary>
    /// Every ticked row must be one title (a whole show or album is never deleted in one go). Who may delete is the single Delete's own gate
    /// (<see cref="LibraryItemDeletionFlow.CanDelete"/>), which the caller checks as well.
    /// </summary>
    public static bool AreDeletable(IReadOnlyList<DetailBatchRow> rows) =>
        rows.Count > 0 && rows.All(row => IsSelectableKind(row.Kind));

    /// <summary>The most titles the Engine deletes in one request (its own cap, <c>LibraryItemDeletionService.MaxWorks</c>); more than this asks to delete fewer.</summary>
    public const int MaxDeleteItems = 200;

    /// <summary>The key of the bar's "Move…" entry. It is the bar's own and never an Engine action, so it is not in <c>DetailActionKeys.Handled</c>.</summary>
    public const string MoveActionKey = "batch-move";

    /// <summary>
    /// What the selection bar's "..." menu holds for these ticked rows, under the gates the bar always had: Move only where the rows can move,
    /// Delete only for the person who may delete (and rows that are single titles), and Delete off, with its reason, above the Engine's cap.
    /// <paramref name="busy"/> greys every entry while a save or a question is in flight, so nothing starts twice.
    /// </summary>
    public static IReadOnlyList<DetailAction> BarMenuActions(IReadOnlyList<DetailBatchRow> rows, bool canMove, bool canDelete, bool busy)
    {
        var actions = new List<DetailAction>();
        if (canMove && CanMove(rows))
        {
            // A book, comic or movie only takes a Series without a number (spec D20), and the entry says so.
            var label = rows.All(row => row.Kind is DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Movie)
                ? LibraryMoveText.AddToSeriesLabel + "…"
                : "Move…";
            actions.Add(new DetailAction { Key = MoveActionKey, Label = label, Icon = "account_tree", IsDisabled = busy });
        }

        if (canDelete && AreDeletable(rows))
        {
            var overLimit = rows.Count > MaxDeleteItems;
            actions.Add(new DetailAction
            {
                Key = DetailActionKeys.DeleteFromLibrary,
                Label = DetailMenuGrouping.DeleteFromLibraryAction.Label,
                Icon = DetailMenuGrouping.DeleteFromLibraryAction.Icon,
                IsDestructive = true,
                IsDisabled = busy || overLimit,
                Tooltip = overLimit ? DeleteLimitReason : null,
            });
        }

        return actions;
    }

    /// <summary>Why Delete is off above the cap.</summary>
    public const string DeleteLimitReason = "Delete up to 200 at a time";
}

public static class DetailBatchText
{
    public const string NoChange = "No change";
    public const string Mixed = "Mixed";
    public const string EditTogether = "Edit together";

    /// <summary>"episodes", "tracks" or "items": the plain noun for what is ticked.</summary>
    public static string Noun(IEnumerable<DetailEntityType> kinds, int count)
    {
        var distinct = kinds.Distinct().ToList();
        var plural = count != 1;
        if (distinct.Count == 1)
        {
            switch (distinct[0])
            {
                case DetailEntityType.TvEpisode:
                    return plural ? "episodes" : "episode";
                case DetailEntityType.Work or DetailEntityType.Audiobook:
                    return plural ? "tracks" : "track";
            }
        }

        return plural ? "items" : "item";
    }

    public static string SelectedLabel(int count) => $"{count} selected";

    public static string ApplyLabel(int count, IEnumerable<DetailEntityType> kinds) => $"Apply to {count} {Noun(kinds, count)}";

    public static string SelectLabel(string title) => $"Select {title}";

    /// <summary>"Drama (2), Comedy (1), and 3 more": the values the ticked items disagree on.</summary>
    public static string DistinctHint(LibraryBatchFieldState field)
    {
        var shown = field.DistinctValues.Select(value => $"{value.Value} ({value.Count})").ToList();
        if (shown.Count == 0)
        {
            return string.Empty;
        }

        var more = field.DistinctCount - field.DistinctValues.Count;
        return more > 0 ? $"{string.Join(", ", shown)}, and {more} more" : string.Join(", ", shown);
    }

    /// <summary>"3 updated", "2 updated, 1 sent to Needs Review", "1 unchanged", "1 not changed: reason", and a note naming each row that did not apply.</summary>
    public static DetailBatchResultView Result(LibraryBatchEditResult result, IReadOnlyList<DetailBatchRow> rows)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<string>();
        if (result.UpdatedCount > 0)
        {
            parts.Add($"{result.UpdatedCount} updated");
        }

        if (result.SentToReviewCount > 0)
        {
            parts.Add($"{result.SentToReviewCount} sent to Needs Review");
        }

        if (result.UnchangedCount > 0)
        {
            parts.Add($"{result.UnchangedCount} unchanged");
        }

        var refused = result.Items
            .Where(item => string.Equals(item.Outcome, LibraryBatchItemOutcomes.Refused, StringComparison.Ordinal))
            .ToList();
        if (result.RefusedCount > 0)
        {
            var sharedReason = refused
                .Select(item => item.Reason)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            parts.Add(sharedReason.Count == 1 && refused.All(item => string.Equals(item.Reason, sharedReason[0], StringComparison.Ordinal))
                ? $"{result.RefusedCount} not changed: {TrimEnd(sharedReason[0]!)}"
                : $"{result.RefusedCount} not changed");
        }

        var notes = new List<string>();
        foreach (var item in result.Items)
        {
            var title = rows.FirstOrDefault(row => row.Id == item.EntityId)?.Title ?? "An item";
            if (string.Equals(item.Outcome, LibraryBatchItemOutcomes.SentToReview, StringComparison.Ordinal))
            {
                notes.Add(Note(title, "sent to Needs Review", item.Reason));
            }
            else if (string.Equals(item.Outcome, LibraryBatchItemOutcomes.Refused, StringComparison.Ordinal))
            {
                notes.Add(Note(title, "not changed", item.Reason));
            }
        }

        var line = parts.Count == 0 ? "Nothing changed" : string.Join(", ", parts);
        return new DetailBatchResultView(line, notes, result.SentToReviewCount > 0 || result.RefusedCount > 0);
    }

    private static string Note(string title, string what, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? $"{title}: {what}." : $"{title}: {what}. {reason.Trim()}";

    private static string TrimEnd(string reason) => reason.Trim().TrimEnd('.');
}
