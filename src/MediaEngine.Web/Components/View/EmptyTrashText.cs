using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.View;

/// <summary>
/// What Empty Trash says: the Trash header, the confirmation dialog, the toasts and the note on a tile that could not be deleted.
/// One place so Photos, Documents and the dialog agree word for word (spec view-personal-file-deletion, section 4).
/// </summary>
public static class EmptyTrashText
{
    // The Trash header.
    public const string HeaderLead = "Items in Trash stay on your disk until you empty Trash.";
    public const string TrashIsEmpty = "Trash is empty.";
    public const string EmptyOnAComputer = "Empty Trash on a computer.";
    public const string EmptyButton = "Empty Trash…";
    public const string CheckingTrash = "Checking Trash…";

    // The dialog.
    public const string Checking = "Checking what's in Trash…";
    public const string Heading = "Empty Trash?";
    public const string Changed = "Something changed since you opened this. Here's the updated list.";
    public const string Unavailable = "Couldn't reach the Engine. Nothing was deleted.";
    public const string Unconfirmed = "The Engine didn't answer in time. Check your Trash before trying again.";
    public const string Forbidden = "You can't empty this Trash.";
    public const string Deleting = "Deleting…";
    public const string NothingToDelete = "Nothing in Trash can be deleted right now.";
    public const string CompoundLine = "Live Photos and RAW sets count as one item and all their files go together.";

    // The app name is not allowed in user-facing strings, so the spec's "Tuvima has no other copy" reads "Library has no other copy".
    public const string NoOtherCopy = "Photos and documents deleted from your disk can't be recovered. Library has no other copy.";

    /// <summary>How many galleries or skipped groups are named before "and N more".</summary>
    public const int ShownBeforeMore = 5;

    /// <summary>"1 item" or "128 items".</summary>
    public static string Items(int count) => count == 1 ? "1 item" : $"{DisplayFormat.FormatCount(count)} items";

    /// <summary>The header's size line: "128 items · 4.2 GB". The size is left out when it is not known.</summary>
    public static string SizeLine(ViewTrashSummaryDto summary) =>
        DisplayFormat.FormatFileSize(summary.Bytes) is { } size ? $"{Items(summary.ItemCount)} · {size}" : Items(summary.ItemCount);

    /// <summary>"128 items will be deleted from your disk for good:".</summary>
    public static string DeleteLead(int count) => $"{Items(count)} will be deleted from your disk for good:";

    /// <summary>"112 photos, 9 videos, 7 documents · 4.2 GB". Kinds with nothing in them are left out.</summary>
    public static string KindsLine(ViewTrashEmptyPreviewDto preview)
    {
        var kinds = new List<string>(4);
        AddKind(kinds, preview.PhotoCount, "photo", "photos");
        AddKind(kinds, preview.VideoCount, "video", "videos");
        AddKind(kinds, preview.DocumentCount, "document", "documents");
        AddKind(kinds, preview.OtherCount, "other file", "other files");
        var line = string.Join(", ", kinds);
        return DisplayFormat.FormatFileSize(preview.BytesFreed) is { } size
            ? (line.Length == 0 ? size : $"{line} · {size}")
            : line;
    }

    private static void AddKind(List<string> kinds, int count, string singular, string plural)
    {
        if (count > 0)
        {
            kinds.Add($"{DisplayFormat.FormatCount(count)} {(count == 1 ? singular : plural)}");
        }
    }

    /// <summary>The red button: "Delete 128 items from disk".</summary>
    public static string ConfirmLabel(int count) => $"Delete {Items(count)} from disk";

    /// <summary>One line for items that stay in Trash for one reason.</summary>
    public static string SkippedLine(ViewTrashPreviewSkippedDto skipped)
    {
        var count = skipped.Count;
        var they = count == 1 ? "It stays" : "They stay";
        switch (skipped.ReasonCode)
        {
            case ViewTrashOutcomes.SkipContributionPending:
                return $"{Items(count)} {(count == 1 ? "is" : "are")} waiting to be added to the Shared Library. {they} in Trash.";
            case ViewTrashOutcomes.SkipReadOnly:
                return $"{Items(count)} {(count == 1 ? "is" : "are")} in a read-only folder. {they} in Trash.";
            default:
                var reason = string.IsNullOrWhiteSpace(skipped.Reason) ? string.Empty : $" {EndWithStop(skipped.Reason.Trim())}";
                return $"{Items(count)} can't be deleted.{reason} {they} in Trash.";
        }
    }

    private static string EndWithStop(string sentence) =>
        sentence.EndsWith('.') || sentence.EndsWith('!') || sentence.EndsWith('?') ? sentence : sentence + ".";

    /// <summary>The start of the Galleries sentence: "14 of these were in 3 of your Galleries: ", or "1 of these was in your Gallery ".</summary>
    public static string GalleriesLead(int items, int galleries) =>
        $"{DisplayFormat.FormatCount(items)} of these {(items == 1 ? "was" : "were")} in "
        + (galleries == 1 ? "your Gallery " : $"{DisplayFormat.FormatCount(galleries)} of your Galleries: ");

    /// <summary>The galleries the deleted items leave: "Beach 2025 (9 of these)". A–Z, whatever order the Engine sent.</summary>
    public static IReadOnlyList<ViewTrashPreviewGalleryDto> SortedGalleries(ViewTrashEmptyPreviewDto preview) =>
        preview.Galleries.OrderBy(gallery => gallery.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The toast after Empty Trash: what went, how much space came back, and what could not be deleted.</summary>
    public static (string Message, bool IsWarning) Outcome(ViewTrashEmptyResultDto result)
    {
        var failed = result.Failed.Count;
        if (failed == 0)
        {
            if (result.DeletedCount == 0)
            {
                return ("Nothing was deleted.", false);
            }

            var message = $"Deleted {Items(result.DeletedCount)}.";
            return (DisplayFormat.FormatFileSize(result.BytesFreed) is { } freed ? $"{message} {freed} freed." : message, false);
        }

        var stillInTrash = failed == 1 ? "1 couldn't be deleted and is still in Trash." : $"{DisplayFormat.FormatCount(failed)} couldn't be deleted and are still in Trash.";
        return (result.DeletedCount == 0
            ? $"{Items(failed)} couldn't be deleted and {(failed == 1 ? "is" : "are")} still in Trash."
            : $"{DisplayFormat.FormatCount(result.DeletedCount)} deleted, {stillInTrash}", true);
    }

    /// <summary>The note on a tile that stayed in Trash: "Couldn't delete: the file is in use."</summary>
    public static string FailureNote(string reason)
    {
        var text = reason.Trim();
        if (text.StartsWith("Couldn't delete", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        // "This file changed…" reads as "Couldn't delete: this file changed…"; a name or acronym keeps its capital.
        if (text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1]))
        {
            text = char.ToLowerInvariant(text[0]) + text[1..];
        }

        return $"Couldn't delete: {text}";
    }

    /// <summary>"3 items are in a read-only folder and weren't moved to Trash." Null when there are none.</summary>
    public static string? ReadOnlyNote(int count) => count switch
    {
        <= 0 => null,
        1 => "1 item is in a read-only folder and wasn't moved to Trash.",
        _ => $"{DisplayFormat.FormatCount(count)} items are in a read-only folder and weren't moved to Trash.",
    };
}
