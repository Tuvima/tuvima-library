using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Details;

/// <summary>One label/value row in a file block.</summary>
public sealed record DetailFileFactRow(string Label, string Value);

/// <summary>The facts for one file (or one owned format when no file list is known).</summary>
public sealed record DetailFileBlock(string Title, IReadOnlyList<DetailFileFactRow> Rows);

/// <summary>
/// Builds the file facts shown on the Details tab from data the Engine already stores:
/// the page model, the work's asset list, and the library item detail. Nothing here asks
/// the Engine for new fields; facts that are not stored are simply left out.
/// </summary>
public static class DetailFileFacts
{
    /// <summary>
    /// Entity types whose detail page is one playable or readable item, so the file facts
    /// of the item can be read for it. Groupings (shows, seasons, albums, series) and
    /// non-media pages are not file-bearing.
    /// </summary>
    public static bool IsFileBearing(DetailEntityType entityType) => entityType
        is DetailEntityType.Work
        or DetailEntityType.Movie
        or DetailEntityType.TvEpisode
        or DetailEntityType.Book
        or DetailEntityType.Audiobook
        or DetailEntityType.ComicIssue;

    /// <summary>True for groupings that own no file themselves (the files live on the items inside).</summary>
    public static bool IsGrouping(DetailEntityType entityType) => entityType
        is DetailEntityType.TvShow
        or DetailEntityType.TvSeason
        or DetailEntityType.MusicAlbum
        or DetailEntityType.MovieSeries
        or DetailEntityType.BookSeries
        or DetailEntityType.ComicSeries
        or DetailEntityType.Collection;

    /// <param name="includePath">The file path is only shown to people who may already see it.</param>
    public static IReadOnlyList<DetailFileBlock> Build(
        DetailPageViewModel model,
        MediaEditorWorkVersionSelectorDto? versions,
        LibraryItemDetailViewModel? detail,
        bool includePath)
    {
        if (!IsFileBearing(model.EntityType))
        {
            return [];
        }

        var files = versions?.Editions
            .SelectMany(edition => edition.Assets.Select(asset => (Edition: edition, Asset: asset)))
            .Where(file => !string.IsNullOrWhiteSpace(file.Asset.FileName))
            .ToList() ?? [];

        var itemRows = BuildItemRows(model, detail);
        var path = includePath ? NullIfBlank(detail?.FilePath) : null;

        if (files.Count == 0)
        {
            return BuildFromDetailOrFormats(model, detail, itemRows, path);
        }

        var primaryIndex = files.FindIndex(file => string.Equals(
            file.Asset.FileName, detail?.FileName, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex < 0)
        {
            primaryIndex = 0;
        }

        var blocks = new List<DetailFileBlock>();
        for (var index = 0; index < files.Count; index++)
        {
            var (edition, asset) = files[index];
            var rows = new List<DetailFileFactRow>();
            Add(rows, "Format", asset.TechnicalLabel);
            if (files.Count > 1)
            {
                Add(rows, "Edition", edition.Label);
            }

            if (index == primaryIndex)
            {
                rows.AddRange(itemRows);
                Add(rows, "Path", path);
            }

            blocks.Add(new DetailFileBlock(asset.FileName, rows));
        }

        return blocks;
    }

    private static IReadOnlyList<DetailFileBlock> BuildFromDetailOrFormats(
        DetailPageViewModel model,
        LibraryItemDetailViewModel? detail,
        List<DetailFileFactRow> itemRows,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(detail?.FileName))
        {
            var rows = new List<DetailFileFactRow>();
            Add(rows, "Format", FormatFromFileName(detail!.FileName));
            rows.AddRange(itemRows);
            Add(rows, "Path", path);
            return [new DetailFileBlock(detail.FileName!, rows)];
        }

        // No file list: fall back to the owned formats the page already carries (one block each).
        var blocks = new List<DetailFileBlock>();
        foreach (var format in model.OwnedFormats)
        {
            var rows = new List<DetailFileFactRow>();
            Add(rows, "Format", format.FileFormat);
            Add(rows, "Edition", format.EditionTitle);
            Add(rows, "Length", format.Runtime);
            if (format.PageCount is > 0)
            {
                Add(rows, "Pages", format.PageCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (format.ChapterCount is > 0)
            {
                Add(rows, "Chapters", format.ChapterCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (rows.Count > 0)
            {
                blocks.Add(new DetailFileBlock(string.IsNullOrWhiteSpace(format.DisplayName) ? "File" : format.DisplayName, rows));
            }
        }

        if (blocks.Count == 0 && itemRows.Count > 0)
        {
            blocks.Add(new DetailFileBlock("File", itemRows));
        }

        return blocks;
    }

    private static List<DetailFileFactRow> BuildItemRows(DetailPageViewModel model, LibraryItemDetailViewModel? detail)
    {
        var rows = new List<DetailFileFactRow>();
        var facts = model.Facts;
        Add(rows, "Length", facts?.Runtime ?? facts?.Duration ?? detail?.Runtime);
        Add(rows, "Video", PlaybackTechnicalSummaryDisplay.VideoChip(detail?.PlaybackSummary));
        Add(rows, "Audio", PlaybackTechnicalSummaryDisplay.AudioChip(detail?.PlaybackSummary));
        Add(rows, "Subtitles", PlaybackTechnicalSummaryDisplay.SubtitleChip(detail?.PlaybackSummary));
        Add(rows, "Pages", facts?.PageCount);
        Add(rows, "Language", facts?.Language ?? detail?.Language);
        if (detail?.FileSizeBytes is > 0)
        {
            Add(rows, "Size", FormatSize(detail.FileSizeBytes.Value));
        }

        return rows;
    }

    private static string? FormatFromFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToUpperInvariant();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(List<DetailFileFactRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new DetailFileFactRow(label, value.Trim()));
        }
    }
}
