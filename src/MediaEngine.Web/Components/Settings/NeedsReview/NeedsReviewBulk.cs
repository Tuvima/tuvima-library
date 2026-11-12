using MediaEngine.Contracts.Review;

namespace MediaEngine.Web.Components.Settings.NeedsReview;

/// <summary>The ticked rows of one list entry: a single row, or some or all of a family's files.</summary>
/// <param name="Entry">The entry the rows belong to.</param>
/// <param name="Rows">The ticked rows still waiting for a person, in list order.</param>
/// <param name="WholeFamily">True when the entry is a family and every file still waiting in it is ticked, so the files act as one line named for the family.</param>
public sealed record NeedsReviewTicked(NeedsReviewEntry Entry, IReadOnlyList<NeedsReviewRowDto> Rows, bool WholeFamily);

/// <summary>What the action bar offers for the rows that are ticked (mock screen 2, marker 3). A count of 0 means the button is left out.</summary>
/// <param name="Selected">How many rows are ticked.</param>
/// <param name="Suggestions">Ticked rows that have exactly one suggestion (Use suggestions).</param>
/// <param name="TryAgain">Ticked rows an Engine action can try again.</param>
/// <param name="Keep">Ticked rows that can be kept as they are.</param>
/// <param name="Edit">Ticked rows the editor can open (Edit N items).</param>
/// <param name="Delete">Ticked rows that can be deleted (Delete).</param>
public sealed record NeedsReviewBarPlan(int Selected, int Suggestions, int TryAgain, int Keep, int Edit, int Delete)
{
    public static readonly NeedsReviewBarPlan Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Which ticked rows each action bar button acts on (spec needs-review-redesign WP5). Pure rules: a button is left out when it applies to none
/// of the ticked rows, and every button touches only the ticked rows it applies to, never the rest of the list.
/// </summary>
public static class NeedsReviewBulk
{
    /// <summary>The most titles one Delete from library asks the Engine about; the Delete button is left out above it.</summary>
    public const int MaxDeleteRows = MediaEngine.Web.Services.Editing.BulkEditRules.MaxDeleteItems;

    /// <summary>Ticked rows that have exactly one suggestion. A family's files and the rows that show choices instead have none to apply.</summary>
    public static IReadOnlyList<NeedsReviewRowDto> SuggestionRows(IEnumerable<NeedsReviewTicked> ticked) =>
        [.. ticked.Where(group => !group.Entry.IsFamily)
            .SelectMany(group => group.Rows)
            .Where(NeedsReviewRowInfo.HasSingleSuggestion)];

    /// <summary>Ticked rows an Engine action can try again, in the groups they act in (a whole family acts as one line).</summary>
    public static IReadOnlyList<IReadOnlyList<NeedsReviewRowDto>> TryAgainGroups(IEnumerable<NeedsReviewTicked> ticked) =>
        Groups(ticked, NeedsReviewRowInfo.CanTryAgain);

    /// <summary>Every ticked row can be kept as it is (a file stops being retried; an item the Engine was unsure about is dismissed).</summary>
    public static IReadOnlyList<IReadOnlyList<NeedsReviewRowDto>> KeepGroups(IEnumerable<NeedsReviewTicked> ticked) =>
        Groups(ticked, row => row.Unmatched is not null || row.Review is not null);

    /// <summary>
    /// Ticked rows the editor can open: the same target Fix opens for the row, on a device that may edit. One row per thing the editor opens, so
    /// two rows of one Work read as one item (the first of them stands for it).
    /// </summary>
    public static IReadOnlyList<NeedsReviewRowDto> EditRows(IEnumerable<NeedsReviewTicked> ticked, bool deviceMayEdit) =>
        deviceMayEdit
            ? [.. ticked.SelectMany(group => group.Rows).Where(row => NeedsReviewRowInfo.FixTargetOf(row) is not null).DistinctBy(NeedsReviewRowInfo.FixTargetOf)]
            : [];

    /// <summary>Ticked rows that can be deleted (the page's own Delete rule); none when more than the Engine takes in one go are ticked.</summary>
    public static IReadOnlyList<NeedsReviewRowDto> DeleteRows(IEnumerable<NeedsReviewTicked> ticked, Func<NeedsReviewRowDto, bool> canDelete)
    {
        var rows = ticked.SelectMany(group => group.Rows).Where(canDelete).ToList();
        return rows.Count > MaxDeleteRows ? [] : rows;
    }

    /// <summary>What the bar shows for the ticked rows.</summary>
    public static NeedsReviewBarPlan PlanFor(IReadOnlyList<NeedsReviewTicked> ticked, bool deviceMayEdit, Func<NeedsReviewRowDto, bool> canDelete) =>
        new(
            ticked.Sum(group => group.Rows.Count),
            SuggestionRows(ticked).Count,
            TryAgainGroups(ticked).Sum(group => group.Count),
            KeepGroups(ticked).Sum(group => group.Count),
            EditRows(ticked, deviceMayEdit).Count,
            DeleteRows(ticked, canDelete).Count);

    // The rows that pass the filter, in the groups they act in: a family whose every waiting file is ticked (and passes) acts as one line named
    // for the family; any other row acts on its own, so each keeps its own done or failure line.
    private static IReadOnlyList<IReadOnlyList<NeedsReviewRowDto>> Groups(IEnumerable<NeedsReviewTicked> ticked, Func<NeedsReviewRowDto, bool> applies)
    {
        var groups = new List<IReadOnlyList<NeedsReviewRowDto>>();
        foreach (var item in ticked)
        {
            var rows = item.Rows.Where(applies).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            if (item.WholeFamily && rows.Count == item.Rows.Count)
            {
                groups.Add(rows);
            }
            else
            {
                groups.AddRange(rows.Select(row => (IReadOnlyList<NeedsReviewRowDto>)[row]));
            }
        }

        return groups;
    }
}
