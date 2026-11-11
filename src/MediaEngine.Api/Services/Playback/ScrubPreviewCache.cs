using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The Library's own cache of scrub previews (Unified player v2 D9a): one folder per media asset holding <c>index.json</c> and the
/// sprite sheets it names. A set is complete only when its index is there and every sheet the index names exists; the index is
/// written last, so a half-made set is never offered. Everything here is a cache that can be rebuilt, so removing it is always safe.
/// </summary>
public sealed partial class ScrubPreviewCache(AssetPathService assetPaths)
{
    public const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    [GeneratedRegex(@"^sheet-\d{3}\.jpg$", RegexOptions.CultureInvariant)]
    private static partial Regex SheetNamePattern();

    public string Root => assetPaths.ScrubPreviewsRoot;

    public string FolderFor(Guid assetId) => Path.Combine(Root, assetId.ToString("D"));

    /// <summary>A fresh folder next to the final one, so a run is moved into place in one step and a crash leaves nothing half-made.</summary>
    public string NewStagingFolder(Guid assetId) =>
        Path.Combine(Root, $"{assetId:D}.tmp-{Guid.NewGuid():N}");

    /// <summary>The index of a complete set, or null when the asset has none (or it is half-made or unreadable).</summary>
    public ScrubPreviewIndexDto? TryReadComplete(Guid assetId)
    {
        var folder = FolderFor(assetId);
        var indexPath = Path.Combine(folder, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            var index = JsonSerializer.Deserialize<ScrubPreviewIndexDto>(File.ReadAllBytes(indexPath), Json);
            return index is { Sheets.Count: > 0 } && index.Sheets.All(sheet => SheetNamePattern().IsMatch(sheet) && File.Exists(Path.Combine(folder, sheet)))
                ? index
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A cache file that cannot be read is the same as no cache: it is rebuilt.
            return null;
        }
    }

    /// <summary>The path of the index file of a complete set, or null.</summary>
    public string? ResolveIndexFile(Guid assetId) =>
        TryReadComplete(assetId) is null ? null : Path.Combine(FolderFor(assetId), IndexFileName);

    /// <summary>
    /// The path of one sprite sheet of a complete set, or null. Only names the index lists are honoured, so a request can never reach
    /// any other file.
    /// </summary>
    public string? ResolveSheetFile(Guid assetId, string name)
    {
        if (!SheetNamePattern().IsMatch(name) || TryReadComplete(assetId) is not { } index || !index.Sheets.Contains(name))
        {
            return null;
        }

        return Path.Combine(FolderFor(assetId), name);
    }

    /// <summary>Writes the index into a staging folder, then moves the whole folder into place, replacing any earlier set.</summary>
    public void Publish(Guid assetId, string stagingFolder, ScrubPreviewIndexDto index)
    {
        File.WriteAllBytes(Path.Combine(stagingFolder, IndexFileName), JsonSerializer.SerializeToUtf8Bytes(index, Json));
        var final = FolderFor(assetId);
        Delete(assetId);
        Directory.Move(stagingFolder, final);
    }

    public void DeleteStaging(string stagingFolder) => DeleteFolder(stagingFolder);

    /// <summary>Removes one asset's previews (used when the file is deleted).</summary>
    public void Delete(Guid assetId) => DeleteFolder(FolderFor(assetId));

    /// <summary>Removes every preview (used when the switch is turned off). Returns how many bytes were freed.</summary>
    public long DeleteAll()
    {
        var freed = UsedBytes();
        if (Directory.Exists(Root))
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                DeleteFolder(folder);
            }
        }

        return freed;
    }

    /// <summary>The disk space the whole cache uses now.</summary>
    public long UsedBytes()
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished while counting is simply not counted.
            }
        }

        return total;
    }

    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        // A reparse point is never followed: only the link itself would go, never what it points at.
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        Directory.Delete(folder, recursive: true);
    }
}
