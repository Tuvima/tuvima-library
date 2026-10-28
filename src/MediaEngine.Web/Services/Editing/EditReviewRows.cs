using System.Text;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The sections of Review changes, in the order they are shown (universal editor spec D12).</summary>
public enum EditReviewGroup
{
    Metadata,
    Identity,
    Artwork,
    Relationships,
    PhysicalFiles,
}

/// <summary>
/// One line in Review changes: the item, what changes, and the before and after the person can compare. A details change with several
/// fields becomes one row per field, so the count on Save matches the rows. <see cref="Before"/> is null when the Dashboard does not
/// know the old value (only the item being edited has its values loaded).
/// </summary>
public sealed record EditReviewRow(
    string ChangeId,
    string? FieldKey,
    Guid EntityId,
    string Kind,
    EditReviewGroup Group,
    string Item,
    string What,
    string? Before,
    string After,
    string? Warning)
{
    public string RowId => FieldKey is null ? ChangeId : $"{ChangeId}:{FieldKey}";

    /// <summary>The tool that holds this kind of change, so Edit can jump to it.</summary>
    public EditTool Tool => Kind switch
    {
        EditSessionChangeKinds.Details => EditTool.Details,
        EditSessionChangeKinds.Artwork => EditTool.Artwork,
        _ => EditTool.Match,
    };
}

/// <summary>Turns the pending list into the rows Review changes shows. Pure: no calls to the Engine, nothing is changed.</summary>
public static class EditReviewRows
{
    public const string UnknownItem = "This item";

    public const string FileMoveWarning = "Moves the files on disk. This cannot be undone.";

    public static string GroupTitle(EditReviewGroup group) => group switch
    {
        EditReviewGroup.Metadata => "Metadata",
        EditReviewGroup.Identity => "Identity",
        EditReviewGroup.Artwork => "Artwork",
        EditReviewGroup.Relationships => "Relationships",
        _ => "Physical files",
    };

    public static IReadOnlyList<EditReviewRow> Build(PendingEditList pending, EditSessionState session, ItemFieldEditSession fields)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fields);

        var rows = new List<EditReviewRow>();
        foreach (var change in pending.Changes)
        {
            var item = session.ItemName(change.EntityId) ?? UnknownItem;
            var label = pending.Label(change.Id);
            switch (change.Kind)
            {
                case EditSessionChangeKinds.Details:
                    foreach (var field in change.Fields)
                    {
                        var presented = fields.Find(field.Key);
                        var before = fields.EntityId == change.EntityId && presented is not null
                            ? FirstText(presented.DisplayValue, presented.Value)
                            : null;
                        var after = string.IsNullOrWhiteSpace(field.Value) ? "Provider value" : field.Value.Trim();
                        rows.Add(new EditReviewRow(change.Id, field.Key, change.EntityId, change.Kind, EditReviewGroup.Metadata, item,
                            presented?.Label ?? field.Key, before, after, null));
                    }

                    break;
                case EditSessionChangeKinds.Identity:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Identity, item,
                        "Match", null, label ?? IdentityText(change.TargetId), null));
                    break;
                case EditSessionChangeKinds.Artwork:
                    var removal = string.Equals(change.TargetId, EditSessionArtworkTargets.None, StringComparison.OrdinalIgnoreCase);
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Artwork, item,
                        SlotText(change.Slot), null, removal ? "Removed" : label ?? "New picture", null));
                    break;
                case EditSessionChangeKinds.Relationship:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Relationships, item,
                        "Moves to", null, label ?? "The chosen place", null));
                    break;
                case EditSessionChangeKinds.FileMove:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.PhysicalFiles, item,
                        "Files", "Where they are now", "Moved and renamed to match the new details", FileMoveWarning));
                    break;
            }
        }

        // Stable: groups in the spec's order, each keeping the order the changes were staged in.
        return rows.OrderBy(row => row.Group).ToList();
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string IdentityText(string? targetId)
    {
        if (!EditSessionIdentityTarget.TryParse(targetId, out var match) || match is null)
        {
            return "The chosen record";
        }

        var title = match.RequiredFields.TryGetValue("title", out var named) && !string.IsNullOrWhiteSpace(named) ? named.Trim() : null;
        return title is null ? $"A {match.ProviderName} record" : $"{title} ({match.ProviderName})";
    }

    // "CoverArt" reads "Cover art".
    private static string SlotText(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
        {
            return "Artwork";
        }

        var text = new StringBuilder();
        foreach (var character in slot.Trim())
        {
            if (char.IsUpper(character) && text.Length > 0)
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(character));
            }
            else
            {
                text.Append(text.Length == 0 ? char.ToUpperInvariant(character) : character);
            }
        }

        return text.ToString();
    }
}
