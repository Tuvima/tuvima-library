using System.Globalization;
using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One read-only fact in the window's Quick Facts card: a key tests and styles can name, the value, and the wording key for its label.</summary>
public sealed record ViewQuickFact(string Key, string Value, string LabelKey, string Icon);

/// <summary>The facts for the header line and the Quick Facts card of the View editor (spec photos-documents-editor point 5). Pure; wording keys only.</summary>
public static class ViewEditFacts
{
    public const string ReadOnlyFolderNote = "ViewEdit_NoteReadOnlyFolder";
    public const string StaysInLibraryNote = "ViewEdit_NoteStaysInLibrary";

    /// <summary>"3.2 MB", "840 KB": the size of an item's files, or null when the Engine did not say.</summary>
    public static string? SizeText(LocalAssetDto item)
    {
        var bytes = item.Files.Sum(file => file.ByteSize);
        return bytes <= 0 ? null : SizeText(bytes);
    }

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>The facts shown for a single item. A fact the Engine did not give is left out, never guessed.</summary>
    public static IReadOnlyList<ViewQuickFact> ForItem(LocalAssetDto item)
    {
        var facts = new List<ViewQuickFact>();
        var type = TypeText(item);
        if (type is not null)
        {
            facts.Add(new ViewQuickFact("type", type, "ViewEdit_FactType", "Description"));
        }

        if (SizeText(item) is { } size)
        {
            facts.Add(new ViewQuickFact("size", size, "ViewEdit_FactSize", "Storage"));
        }

        if (ViewEditStage.IsDocument(item))
        {
            if (item.PageCount is { } pages)
            {
                facts.Add(new ViewQuickFact("pages", pages.ToString(CultureInfo.InvariantCulture), "ViewEdit_FactPages", "Article"));
            }

            facts.Add(new ViewQuickFact("space", item.ScopeKind == "shared" ? "Shared" : "Own", "ViewEdit_FactWhere", "Folder"));
        }
        else
        {
            if (item.Width is { } w && item.Height is { } h)
            {
                facts.Add(new ViewQuickFact("dimensions", $"{w} × {h}", "ViewEdit_FactDimensions", "AspectRatio"));
            }

            var camera = string.Join(' ', new[] { item.DeviceMake, item.DeviceModel }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (camera.Length > 0)
            {
                facts.Add(new ViewQuickFact("camera", camera, "ViewEdit_FactCamera", "PhotoCamera"));
            }

            if (item.DurationSeconds is { } seconds && item.MediaKind == ViewMediaKinds.Video)
            {
                facts.Add(new ViewQuickFact("duration", TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture), "ViewEdit_FactLength", "Schedule"));
            }
        }

        return facts;
    }

    /// <summary>The notes under the facts: "Read-only folder" when Library may not move the file, and that edits stay in Library.</summary>
    public static IReadOnlyList<string> NotesFor(IReadOnlyList<LocalAssetDto> items)
    {
        var notes = new List<string>();
        if (items.Any(item => !item.CanTrash))
        {
            notes.Add(ReadOnlyFolderNote);
        }

        notes.Add(StaysInLibraryNote);
        return notes;
    }

    /// <summary>One line of facts for the header: when it was taken or dated, how big, how many pages or how long.</summary>
    public static string HeaderLine(LocalAssetDto item)
    {
        var parts = new List<string>();
        var when = ViewEditStage.IsDocument(item) ? item.DocumentInfo?.DocumentDate?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
            : item.CapturedAt?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        if (when is not null) { parts.Add(when); }
        if (ViewEditStage.IsDocument(item) && item.PageCount is { } pages) { parts.Add($"{pages} p."); }
        if (!ViewEditStage.IsDocument(item) && item.Width is { } w && item.Height is { } h) { parts.Add($"{w} × {h}"); }
        if (SizeText(item) is { } size) { parts.Add(size); }
        return string.Join(" · ", parts);
    }

    private static string? TypeText(LocalAssetDto item)
    {
        var extension = Path.GetExtension(item.FileName).TrimStart('.');
        if (extension.Length == 0)
        {
            return item.MimeType.Contains('/') ? item.MimeType[(item.MimeType.IndexOf('/') + 1)..].ToUpperInvariant() : null;
        }

        return extension.ToUpperInvariant();
    }

    /// <summary>For a gallery: the date range of its photos.</summary>
    public static string? DateRange(IReadOnlyList<LocalAssetDto> members)
    {
        var dates = members.Select(member => member.CapturedAt).Where(date => date is not null).Select(date => date!.Value).ToList();
        if (dates.Count == 0)
        {
            return null;
        }

        var first = dates.Min();
        var last = dates.Max();
        return first.Year == last.Year && first.Month == last.Month
            ? first.ToString("MMM yyyy", CultureInfo.CurrentCulture)
            : $"{first.ToString("MMM yyyy", CultureInfo.CurrentCulture)} – {last.ToString("MMM yyyy", CultureInfo.CurrentCulture)}";
    }
}
