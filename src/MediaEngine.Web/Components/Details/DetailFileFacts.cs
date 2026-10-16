using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Details;

/// <summary>One label/value row in a file block.</summary>
public sealed record DetailFileFactRow(string Label, string Value);

/// <summary>The facts for one file (or one owned format when no file list is known).</summary>
public sealed record DetailFileBlock(string Title, IReadOnlyList<DetailFileFactRow> Rows);

/// <summary>
/// Builds the file facts shown on the Details tab from data the Engine already stores:
/// the page model, the work's asset list (which carries each file's stored size, added date,
/// bitrate and tracks), and the library item detail. Nothing here probes a file or asks the Engine
/// to; facts that are not stored are simply left out.
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

        var itemRows = BuildItemRows(model, detail, includeFileLevel: files.Count == 0);
        var path = includePath ? NullIfBlank(detail?.FilePath) : null;

        if (files.Count == 0)
        {
            return BuildFromDetailOrFormats(model, detail, itemRows, path);
        }

        // The item detail describes one file. Match it by name so its facts (and path) never land on another file.
        var pathFileName = NullIfBlank(Path.GetFileName((detail?.FilePath ?? string.Empty).Replace('\\', '/')));
        var primaryName = NullIfBlank(detail?.FileName) ?? pathFileName;
        var primaryIndex = files.FindIndex(file => string.Equals(
            file.Asset.FileName, primaryName, StringComparison.OrdinalIgnoreCase));
        if (primaryIndex < 0)
        {
            primaryIndex = 0;
        }

        var blocks = new List<DetailFileBlock>();
        for (var index = 0; index < files.Count; index++)
        {
            var (edition, asset) = files[index];
            var rows = new List<DetailFileFactRow>();
            var isPrimary = index == primaryIndex;
            Add(rows, "Format", asset.TechnicalLabel);
            if (files.Count > 1)
            {
                Add(rows, "Edition", edition.Label);
            }

            // Each file shows its own stored container; only the file the item detail names borrows the item's.
            Add(rows, "Container", NullIfBlank(asset.Container) ?? (isPrimary ? ItemContainer(detail) : null));
            if (isPrimary)
            {
                rows.AddRange(itemRows);
            }

            var size = asset.FileSizeBytes is > 0 ? asset.FileSizeBytes : isPrimary ? detail?.FileSizeBytes : null;
            Add(rows, "Size", size is > 0 ? DisplayFormat.FormatFileSize(size.Value) : null);
            Add(rows, "Bitrate", asset.BitrateBps is > 0 ? DisplayFormat.FormatBitrate(asset.BitrateBps.Value) : null);
            Add(rows, "Added", asset.AddedAt is { } added ? DisplayFormat.FormatPlainDate(added) : null);
            AddTracks(rows, asset, isPrimary);

            if (path is not null && (files.Count == 1
                || string.Equals(asset.FileName, pathFileName, StringComparison.OrdinalIgnoreCase)))
            {
                Add(rows, "Path", path);
            }

            blocks.Add(new DetailFileBlock(asset.FileName, DropRepeatedContainer(rows)));
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
            return [new DetailFileBlock(detail.FileName!, DropRepeatedContainer(rows))];
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

    private static string? ItemContainer(LibraryItemDetailViewModel? detail) => detail?.CanonicalValues
        .FirstOrDefault(value => string.Equals(value.Key, "container", StringComparison.OrdinalIgnoreCase))?.Value;

    /// <param name="includeFileLevel">
    /// True when there is no file list, so the item detail also supplies the container and size
    /// that the per-file path takes from each file's own stored facts.
    /// </param>
    private static List<DetailFileFactRow> BuildItemRows(DetailPageViewModel model, LibraryItemDetailViewModel? detail, bool includeFileLevel)
    {
        var rows = new List<DetailFileFactRow>();
        var facts = model.Facts;
        if (includeFileLevel)
        {
            Add(rows, "Container", ItemContainer(detail));
        }

        Add(rows, "Length", facts?.Runtime ?? facts?.Duration ?? detail?.Runtime);
        Add(rows, "Video", PlaybackTechnicalSummaryDisplay.VideoChip(detail?.PlaybackSummary));
        Add(rows, "Audio", PlaybackTechnicalSummaryDisplay.AudioChip(detail?.PlaybackSummary));
        Add(rows, "Subtitles", PlaybackTechnicalSummaryDisplay.SubtitleChip(detail?.PlaybackSummary));
        Add(rows, "Pages", facts?.PageCount);
        Add(rows, "Language", facts?.Language ?? detail?.Language);
        if (includeFileLevel && detail?.FileSizeBytes is > 0)
        {
            Add(rows, "Size", DisplayFormat.FormatFileSize(detail.FileSizeBytes.Value));
        }

        return rows;
    }

    /// <summary>
    /// The stored audio and subtitle tracks of one file. The item summary already covers the file it
    /// describes when that file has a single track, so the lists only appear when they say more.
    /// </summary>
    private static void AddTracks(List<DetailFileFactRow> rows, MediaEditorAssetSelectorDto asset, bool isPrimary)
    {
        var audio = asset.AudioTracks
            .Select(track => TrackLabel(LanguageName(track.Language), NullIfBlank(track.Codec)))
            .Where(label => label is not null)
            .Select(label => label!)
            .ToList();
        if (audio.Count > (isPrimary ? 1 : 0))
        {
            Add(rows, "Audio tracks", string.Join(", ", audio));
        }

        var subtitles = asset.SubtitleLanguages
            .Select(LanguageName)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (subtitles.Count > (isPrimary ? 1 : 0))
        {
            Add(rows, "Subtitle tracks", string.Join(", ", subtitles));
        }
    }

    private static string? TrackLabel(string? language, string? codec) =>
        (language, codec) switch
        {
            (null, null) => null,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } name, { } format) => $"{name} ({format})",
        };

    private static readonly Lazy<Dictionary<string, string>> LanguageNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.TwoLetterISOLanguageName.Length == 2 && !names.ContainsKey(culture.TwoLetterISOLanguageName))
            {
                names[culture.TwoLetterISOLanguageName] = culture.EnglishName;
            }

            if (culture.ThreeLetterISOLanguageName.Length == 3 && !names.ContainsKey(culture.ThreeLetterISOLanguageName))
            {
                names[culture.ThreeLetterISOLanguageName] = culture.EnglishName;
            }
        }

        return names;
    });

    /// <summary>"eng" and "en" read as English; anything unrecognised is shown as stored. Never throws.</summary>
    private static string? LanguageName(string? code)
    {
        var trimmed = NullIfBlank(code);
        if (trimmed is null)
        {
            return null;
        }

        var key = trimmed.Split('-', '_')[0];
        return LanguageNames.Value.TryGetValue(key, out var name) ? name : trimmed;
    }

    /// <summary>The stored container is only worth a row when it says something the Format row does not.</summary>
    private static List<DetailFileFactRow> DropRepeatedContainer(List<DetailFileFactRow> rows)
    {
        var format = rows.FirstOrDefault(row => row.Label == "Format")?.Value;
        return format is null
            ? rows
            : rows.Where(row => !(row.Label == "Container" && string.Equals(row.Value, format, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static string? FormatFromFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToUpperInvariant();
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
