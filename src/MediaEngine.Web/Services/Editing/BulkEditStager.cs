using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What staging a bulk change did: how many items got an entry and how many were left alone, with the reason.</summary>
public sealed record BulkStageResult(int Staged, int Skipped, string? Note = null);

/// <summary>What the ticked rows have staged about where they sit: a new number, a move to another album or season, or a match in their own place.</summary>
public sealed record BulkPlaceSummary(int Numbered, int Moved, int Matched)
{
    public bool IsEmpty => Numbered + Moved + Matched == 0;
}

/// <summary>
/// Turns what the person did in the "Edit N items" panel into entries of the page's <see cref="PendingEditList"/> (spec D14): one entry per
/// item and field, staged exactly as the single-item Details tool stages one. A field the person never touched is never staged, and a
/// value typed back to what an item already holds takes that item's entry out again, so Save never counts a no-op. Nothing here talks to
/// the Engine: the one save checks and applies the whole list.
/// </summary>
public static class BulkEditStager
{
    /// <summary>
    /// A single-value field was typed into. <paramref name="typed"/> is what the box now holds. On a mixed field, an empty box means "leave
    /// unchanged" (nothing is staged); on a field every item agrees on, an empty box sends the items back to the provider's value, as the
    /// single Details tool does.
    /// </summary>
    public static BulkStageResult StageScalar(PendingEditList pending, BulkFieldView field, string? typed)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(field);
        var value = (typed ?? string.Empty).Trim();
        var staged = 0;
        foreach (var item in field.Eligible)
        {
            var current = item.Text.Trim();
            var leaveAlone = string.Equals(value, current, StringComparison.Ordinal) || (value.Length == 0 && (field.IsMixed || current.Length == 0));
            if (leaveAlone)
            {
                pending.ClearField(item.EntityId, field.Key);
                continue;
            }

            pending.SetField(item.EntityId, field.Key, value);
            staged++;
        }

        return new BulkStageResult(staged, field.Locked.Count, SkippedNote(field));
    }

    /// <summary>
    /// A list field (genres, tags) was changed with Add, Replace or Remove: each item's new list is worked out from its own current one.
    /// An item whose list would not change gets no entry, and a genre list is never emptied (an empty genre list means "use the provider's").
    /// </summary>
    public static BulkStageResult StageList(PendingEditList pending, BulkFieldView field, string operation, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);
        if (!BatchEditFieldCatalog.TryGet(field.Key, out var catalog))
        {
            return new BulkStageResult(0, field.Items.Count, $"{field.Label} can't be changed here.");
        }

        var chosen = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList();
        var staged = 0;
        var kept = 0;
        foreach (var item in field.Eligible)
        {
            if (chosen.Count == 0)
            {
                // Nothing chosen yet is "No change", for every operation.
                pending.ClearField(item.EntityId, field.Key);
                continue;
            }

            var next = BatchEditFieldCatalog.ApplyListOperation(item.Values, operation, chosen);
            if (BatchEditFieldCatalog.SameItems(next, item.Values))
            {
                pending.ClearField(item.EntityId, field.Key);
            }
            else if (next.Count == 0 && !BatchEditFieldCatalog.CanBeEmptied(catalog))
            {
                pending.ClearField(item.EntityId, field.Key);
                kept++;
            }
            else
            {
                pending.SetField(item.EntityId, field.Key, BatchEditFieldCatalog.ToOverrideValue(catalog, next));
                staged++;
            }
        }

        var note = kept > 0
            ? $"{kept} {(kept == 1 ? "item keeps" : "items keep")} its {field.Label.ToLowerInvariant()}: the list can't be left empty."
            : SkippedNote(field);
        return new BulkStageResult(staged, field.Locked.Count + kept, note);
    }

    /// <summary>Takes every staged value of one field back out for the ticked items (the person set the field back to "No change").</summary>
    public static void ClearField(PendingEditList pending, BulkFieldView field)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(field);
        foreach (var item in field.Items)
        {
            pending.ClearField(item.EntityId, field.Key);
        }
    }

    /// <summary>
    /// Renumber sequentially: the rows, in the order the list shows them, are asked to become 1 to N in the album they sit in. Each is a
    /// relationship entry whose target is its current album and whose token carries the number.
    /// </summary>
    public static BulkStageResult Renumber(PendingEditList pending, IReadOnlyList<BulkEditRow> orderedRows)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(orderedRows);
        var staged = 0;
        for (var index = 0; index < orderedRows.Count; index++)
        {
            var row = orderedRows[index];
            if (row.ParentId is not { } album || album == Guid.Empty)
            {
                continue;
            }

            pending.SetTarget(EditSessionChangeKinds.Relationship, row.Id, album.ToString("D"), EditSessionPosition.Token(index + 1));
            staged++;
        }

        return new BulkStageResult(staged, orderedRows.Count - staged);
    }

    /// <summary>
    /// Change album and Move to season: every row is asked to belong to <paramref name="destinationId"/>. A number staged for the row is
    /// replaced, because the row's place is what is being decided now.
    /// </summary>
    public static BulkStageResult MoveTo(PendingEditList pending, IReadOnlyList<BulkEditRow> rows, Guid destinationId)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            pending.SetTarget(EditSessionChangeKinds.Relationship, row.Id, destinationId.ToString("D"));
        }

        return new BulkStageResult(rows.Count, 0);
    }

    /// <summary>Match: stages each row against the album or season it already sits in, so the Engine pairs it with the entry it fits. Rows that do not know their place are left out.</summary>
    public static BulkStageResult MatchInPlace(PendingEditList pending, IReadOnlyList<BulkEditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(rows);
        var staged = 0;
        foreach (var row in rows)
        {
            if (row.ParentId is { } place && place != Guid.Empty)
            {
                pending.SetTarget(EditSessionChangeKinds.Relationship, row.Id, place.ToString("D"));
                staged++;
            }
        }

        return new BulkStageResult(staged, rows.Count - staged);
    }

    /// <summary>True when the row has a number or a move staged (a relationship entry), so a second one would replace it.</summary>
    public static bool HasStagedPlace(PendingEditList pending, Guid rowId) =>
        pending.Changes.Any(change => change.Kind == EditSessionChangeKinds.Relationship && change.EntityId == rowId);

    /// <summary>Takes the staged number, move or match back out for these rows.</summary>
    public static int ClearPlace(PendingEditList pending, IReadOnlyList<BulkEditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(rows);
        var ids = pending.Changes
            .Where(change => change.Kind == EditSessionChangeKinds.Relationship && rows.Any(row => row.Id == change.EntityId))
            .Select(change => change.Id)
            .ToList();
        return ids.Count(pending.Remove);
    }

    /// <summary>How many of the rows have a number, a move to another place, or a match against their own place staged.</summary>
    public static BulkPlaceSummary SummarisePlace(PendingEditList pending, IReadOnlyList<BulkEditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(rows);
        int numbered = 0, moved = 0, matched = 0;
        foreach (var row in rows)
        {
            var change = pending.Changes.FirstOrDefault(candidate => candidate.Kind == EditSessionChangeKinds.Relationship && candidate.EntityId == row.Id);
            if (change is null)
            {
                continue;
            }

            if (EditSessionPosition.IsPosition(change.Token, out _))
            {
                numbered++;
            }
            else if (row.ParentId is { } parent && string.Equals(change.TargetId, parent.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
            else
            {
                moved++;
            }
        }

        return new BulkPlaceSummary(numbered, moved, matched);
    }

    private static string? SkippedNote(BulkFieldView field)
    {
        var locked = field.Locked;
        if (locked.Count == 0)
        {
            return null;
        }

        var reason = locked.Select(item => item.LockedReason).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.TrimEnd('.');
        return $"{locked.Count} of {field.Items.Count} can't take this change{(reason is null ? string.Empty : $" ({reason})")} and will be left as they are.";
    }
}
