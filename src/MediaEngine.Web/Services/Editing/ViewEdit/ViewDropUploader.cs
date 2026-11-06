using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What one drop or "Add media" pick came to: the items now in the library, files already there, and files turned away.</summary>
public sealed record ViewDropResult(
    IReadOnlyList<Guid> ItemIds,
    int Added,
    int Duplicates,
    IReadOnlyList<ViewDropRejection> Rejected,
    IReadOnlyList<string> Failed);

/// <summary>A file View did not take, with the plain-English reason ("It is a document.").</summary>
public sealed record ViewDropRejection(string FileName, string Reason);

/// <summary>
/// Adds the photos and videos dropped on a View page (spec view-section D9). Files that are not photos or videos are turned away before
/// anything is sent, valid files in the same drop still upload one after another, and progress ("Uploading 2 of 5") shows on the slim
/// ribbon, never in a toast. The Engine still checks every file it receives.
/// </summary>
public sealed class ViewDropUploader(IEngineApiClient api, ViewRibbonState ribbon, IStringLocalizer<SharedStrings> L)
{
    private const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".bmp", ".tif", ".tiff", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".3gp", ".mts", ".m2ts", ".mpg", ".mpeg", ".wmv",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".csv", ".md",
    };

    public static bool IsPhotoOrVideo(string fileName) =>
        PhotoExtensions.Contains(Path.GetExtension(fileName)) || VideoExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsPicture(string fileName) => PhotoExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsDocument(string fileName) => DocumentExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>The reason a file is not a photo or video, in words a person would use.</summary>
    public static string RejectionReason(string fileName) =>
        IsDocument(fileName) ? "It is a document." : "It is a " + (Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " " : string.Empty) + "file.";

    /// <summary>Sorts a drop into files to upload and files to turn away.</summary>
    public static (IReadOnlyList<IBrowserFile> Valid, IReadOnlyList<ViewDropRejection> Rejected) Sort(IEnumerable<IBrowserFile> files)
    {
        var valid = new List<IBrowserFile>();
        var rejected = new List<ViewDropRejection>();
        foreach (var file in files)
        {
            if (IsPhotoOrVideo(file.Name))
            {
                valid.Add(file);
            }
            else
            {
                rejected.Add(new ViewDropRejection(file.Name, RejectionReason(file.Name)));
            }
        }

        return (valid, rejected);
    }

    public async Task<ViewDropResult> UploadAsync(IReadOnlyList<IBrowserFile> files, CancellationToken ct = default)
    {
        var (valid, rejected) = Sort(files);
        var itemIds = new List<Guid>();
        var failed = new List<string>();
        var duplicates = 0;
        for (var index = 0; index < valid.Count; index++)
        {
            var file = valid[index];
            ribbon.ShowProgress(L["ViewDrop_Uploading", index + 1, valid.Count, file.Name].Value, index, valid.Count);
            try
            {
                await using var stream = file.OpenReadStream(Math.Min(file.Size, MaxFileBytes), ct);
                var result = await api.UploadViewMediaAsync(stream, file.Name, file.ContentType, ct);
                if (!result.Success || result.Upload is null)
                {
                    failed.Add($"{file.Name}: {result.ErrorMessage ?? "The Engine rejected this upload."}");
                }
                else if (result.Upload.Outcome == "duplicate")
                {
                    duplicates++;
                    if (result.Upload.ItemId != Guid.Empty)
                    {
                        itemIds.Add(result.Upload.ItemId);
                    }
                }
                else
                {
                    itemIds.Add(result.Upload.ItemId);
                }
            }
            catch (IOException exception)
            {
                failed.Add($"{file.Name}: {exception.Message}");
            }
        }

        return new ViewDropResult(itemIds, itemIds.Count - duplicates, duplicates, rejected, failed);
    }

    /// <summary>Says on the ribbon what a drop came to. A rejected or failed file shows as an error, and valid files in the same drop are still reported.</summary>
    public void Report(ViewDropResult result, string? destination = null)
    {
        var parts = new List<string>();
        if (result.Added > 0)
        {
            parts.Add(destination is null
                ? result.Added == 1 ? L["ViewDrop_AddedOne"].Value : L["ViewDrop_AddedMany", result.Added].Value
                : L["ViewDrop_AddedToGallery", result.Added, destination].Value);
        }

        if (result.Duplicates > 0)
        {
            parts.Add(L["ViewDrop_AlreadyThere", result.Duplicates].Value);
        }

        var problems = new List<string>();
        foreach (var rejection in result.Rejected.Take(2))
        {
            problems.Add(L["ViewDrop_NotAdded", rejection.FileName, rejection.Reason].Value);
        }

        problems.AddRange(result.Failed.Take(2));
        var more = result.Rejected.Count + result.Failed.Count - problems.Count;
        if (more > 0)
        {
            problems.Add($"+{more}");
        }

        ribbon.Show(string.Join(" ", parts.Concat(problems)), isError: problems.Count > 0);
    }
}
