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
/// know the old value; staging records it when it can, and a row without one reads <see cref="AsItIsNow"/> so every row has a Before.
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

    public const string ExtraToExtrasNote = "The file stays where it is. The episode leaves your Library and its progress is lost.";

    public const string ExtraToEpisodeNote = "The file stays where it is and is added to your Library as that episode.";

    public const string SiblingMatchNote = "Each other episode in this season takes the episode with its own number. Matching cannot be undone from here.";

    /// <summary>The Before of a row whose old value was not recorded when it was staged (a list holds no loaded values); never blank, so every row can be compared.</summary>
    public const string AsItIsNow = "As it is now";

    public const string NotSet = "Not set";

    public const string NoPictureYet = "No picture chosen";

    public const string ChildRematchNote = "Each of the show's episodes takes the episode with its own season and number. Any the new show does not have go to Needs Review. Matching cannot be undone from here.";

    public const string VideoSeriesNote = "Library only. No file is moved or renamed.";

    public const string VideoOrderNote = "Library only. No file is moved or renamed.";

    public const string VideoRenameWarning = "Renames the files on disk to match the order. This cannot be undone.";

    public const string NotInASeries = "Not in a series";

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
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fields);
        return Build(pending, session.ItemName, fields);
    }

    /// <summary>
    /// The rows for a pending list whose items are named by <paramref name="itemName"/>. <paramref name="fields"/> is the loaded item's
    /// values (a detail page); a list has none, so its rows have no "before" and a field reads as its key made readable.
    /// </summary>
    public static IReadOnlyList<EditReviewRow> Build(PendingEditList pending, Func<Guid, string?> itemName, ItemFieldEditSession? fields)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(itemName);

        var rows = new List<EditReviewRow>();
        foreach (var change in pending.Changes)
        {
            var item = itemName(change.EntityId) ?? UnknownItem;
            var label = pending.Label(change.Id);
            switch (change.Kind)
            {
                case EditSessionChangeKinds.Details:
                    foreach (var field in change.Fields)
                    {
                        var presented = fields?.Find(field.Key);
                        var before = pending.Before(change.Id + ":" + field.Key.Trim())
                                     ?? (fields is not null && fields.EntityId == change.EntityId && presented is not null
                                         ? FirstText(presented.DisplayValue, presented.Value) ?? NotSet
                                         : AsItIsNow);
                        var after = string.IsNullOrWhiteSpace(field.Value) ? "Provider value" : field.Value.Trim();
                        var isExplicit = string.Equals(field.Key.Trim(), ItemFieldEditSession.ExplicitFieldKey, StringComparison.OrdinalIgnoreCase);
                        if (isExplicit)
                        {
                            after = ExplicitText(string.Equals(after, "true", StringComparison.OrdinalIgnoreCase));
                            before = pending.Before(change.Id + ":" + field.Key.Trim())
                                     ?? (fields is { ExplicitMark: { } mark } && fields.EntityId == change.EntityId ? ExplicitText(mark.IsExplicit) : AsItIsNow);
                        }

                        rows.Add(new EditReviewRow(change.Id, field.Key, change.EntityId, change.Kind, EditReviewGroup.Metadata, item,
                            isExplicit ? "Explicit" : presented?.Label ?? ReadableKey(field.Key), before, after, null));
                    }

                    break;
                case EditSessionChangeKinds.Identity:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Identity, item,
                        "Match", pending.Before(change.Id) ?? CurrentMatchText(fields, change.EntityId) ?? AsItIsNow, label ?? IdentityText(change.TargetId), null));
                    break;
                case EditSessionChangeKinds.SiblingMatch:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Identity, item,
                        "Other episodes", "Matched as they are now", label ?? "The other episodes in this season, by number", SiblingMatchNote));
                    break;
                case EditSessionChangeKinds.ChildRematch:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Identity, item,
                        "Seasons and episodes", "Matched as they are now", label ?? "The show's episodes, by season and number", ChildRematchNote));
                    break;
                case EditSessionChangeKinds.Artwork:
                    var removal = string.Equals(change.TargetId, EditSessionArtworkTargets.None, StringComparison.OrdinalIgnoreCase);
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Artwork, item,
                        SlotText(change.Slot), pending.Before(change.Id) ?? AsItIsNow, removal ? "Removed" : label ?? "New picture", null));
                    break;
                case EditSessionChangeKinds.Relationship:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Relationships, item,
                        "Moves to", pending.Before(change.Id) ?? AsItIsNow, label ?? "The chosen place", pending.Note(change.Id)));
                    break;
                case EditSessionChangeKinds.ExtraMove:
                    var toExtras = EditSessionExtraMove.IsToExtras(change.Token);
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Relationships, item,
                        "Moves to", toExtras ? "An episode of your Library" : "Extras", label ?? (toExtras ? "Extras" : "An episode"), toExtras ? ExtraToExtrasNote : ExtraToEpisodeNote));
                    break;
                case EditSessionChangeKinds.VideoSeries:
                    var leaves = EditSessionVideo.IsNoSeries(change.Token);
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Relationships, item,
                        "Series", pending.Before(change.Id) ?? AsItIsNow,
                        leaves ? NotInASeries : label ?? (EditSessionVideo.TryParseNewSeries(change.Token, out var started) ? started : "The chosen series"),
                        VideoSeriesNote));
                    break;
                case EditSessionChangeKinds.VideoOrder:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.Relationships, label ?? item,
                        "Order", pending.Before(change.Id) ?? AsItIsNow, $"{change.OrderedIds.Count} videos in a new order", VideoOrderNote));
                    break;
                case EditSessionChangeKinds.VideoRename:
                    rows.Add(new EditReviewRow(change.Id, null, change.EntityId, change.Kind, EditReviewGroup.PhysicalFiles, label ?? item,
                        "File names", "As they are now", "Numbered to match the order", VideoRenameWarning));
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

    // What the loaded item is matched as now (show, season and episode, title), so a re-match shows its before and after.
    private static string? CurrentMatchText(ItemFieldEditSession? fields, Guid entityId)
    {
        if (fields is null || fields.EntityId != entityId)
        {
            return null;
        }

        string? Read(string key) => fields.Find(key) is { } field ? FirstText(field.DisplayValue, field.Value) : null;

        var parts = new List<string>();
        if (Read("show_name") is { } show)
        {
            parts.Add(show);
        }

        if (int.TryParse(Read("season_number"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var season)
            && int.TryParse(Read("episode_number"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var episode))
        {
            parts.Add(EditorTvMatchCandidates.SlotLabel(season, episode));
        }

        if (Read("title") is { } title)
        {
            parts.Add(title);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // "release_year" reads "Release year" when no loaded item gives the field its own label.
    /// <summary>How the Explicit mark reads in Review changes, before and after.</summary>
    public static string ExplicitText(bool isExplicit) => isExplicit ? "On" : "Off";

    private static string ReadableKey(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
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
