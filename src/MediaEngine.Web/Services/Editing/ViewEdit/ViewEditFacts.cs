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

        // The Files tab is gone (universal editor v5 D25): the file's name and its parts (a RAW and its JPEG, for example) are facts here.
        if (!string.IsNullOrWhiteSpace(item.FileName))
        {
            facts.Add(new ViewQuickFact("file", item.FileName, "ViewEdit_FactFile", "Folder"));
        }

        if (item.Files.Count > 1)
        {
            facts.Add(new ViewQuickFact("parts", item.Files.Count.ToString(CultureInfo.InvariantCulture), "ViewEdit_FactParts", "Storage"));
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

    /// <summary>
    /// What follows the kind word on the header's one line (screen L): when it was taken or dated, and where. A document adds its pages. File facts
    /// (size, dimensions, camera) are in Quick facts, not here.
    /// </summary>
    public static string HeaderLine(LocalAssetDto item)
    {
        var parts = new List<string>();
        var when = ViewEditStage.IsDocument(item) ? item.DocumentInfo?.DocumentDate?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
            : item.CapturedAt?.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        if (when is not null) { parts.Add(when); }
        if (ViewEditStage.IsDocument(item) && item.PageCount is { } pages) { parts.Add($"{pages} p."); }
        if (!ViewEditStage.IsDocument(item) && PlaceText(item) is { } place) { parts.Add(place); }
        return string.Join(" · ", parts);
    }

    /// <summary>The place as the person reads it: "Lisbon, Portugal" when the city and country are known, else the place name the item carries.</summary>
    public static string? PlaceText(LocalAssetDto item)
    {
        var named = string.Join(", ", new[] { item.LocationCity, item.LocationCountry }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return named.Length > 0 ? named : string.IsNullOrWhiteSpace(item.LocationName) ? null : item.LocationName;
    }

    /// <summary>
    /// The formats of a selection for the header's line ("36 JPEG · 4 RAW + JPEG"): a photo made of several files (a RAW and its JPEG) counts as that
    /// pair, any other photo by its own file type.
    /// </summary>
    public static string FormatSummary(IReadOnlyList<LocalAssetDto> items) => string.Join(" · ", items
        .Select(item => item.Files.Count > 1
            ? string.Join(" + ", item.Files.Select(file => Plain(FileKind(file))).Distinct().OrderBy(kind => kind == "RAW" ? 0 : 1).ThenBy(kind => kind, StringComparer.Ordinal))
            : Plain(TypeText(item) ?? string.Empty))
        .Where(kind => kind.Length > 0)
        .GroupBy(kind => kind, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .Select(group => $"{group.Count()} {group.Key}"));

    // One name for one format: a photo named .jpg and its twin typed image/jpeg are both JPEG.
    private static string Plain(string kind) => kind switch
    {
        "JPG" => "JPEG",
        "TIF" => "TIFF",
        _ => kind,
    };

    private static string FileKind(LocalAssetFileDto file)
    {
        var extension = Path.GetExtension(file.FileName ?? string.Empty).TrimStart('.');
        if (file.Role.Contains("raw", StringComparison.OrdinalIgnoreCase) || file.MimeType.Contains("raw", StringComparison.OrdinalIgnoreCase))
        {
            return "RAW";
        }

        if (extension.Length > 0)
        {
            return extension.ToUpperInvariant();
        }

        return file.MimeType.Contains('/') ? file.MimeType[(file.MimeType.IndexOf('/') + 1)..].ToUpperInvariant() : file.MimeType.ToUpperInvariant();
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
