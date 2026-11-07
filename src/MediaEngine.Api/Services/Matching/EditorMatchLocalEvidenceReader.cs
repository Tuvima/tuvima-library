using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Detection;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Where the Match search pre-fills a box from, as <see cref="EditorMatchFieldDto.From"/>.</summary>
public static class EditorMatchFieldSources
{
    public const string Tags = "tags";
    public const string FileName = "file_name";
    public const string Folder = "folder";
}

/// <summary>What the Engine read about one item's file, for the Match search to open on.</summary>
/// <param name="EntityId">The item.</param>
/// <param name="AssetId">The item's media asset.</param>
/// <param name="MediaType">The Library's media type name for the item.</param>
/// <param name="FilePath">The item's one media file; null when it has none.</param>
/// <param name="FileName">The file's name with its extension; null when it has none.</param>
/// <param name="Evidence">The local evidence shown beside the verdict.</param>
/// <param name="Fields">The cleaned values for each box of the kind, by field id, with where each came from.</param>
public sealed record EditorMatchLocalRead(
    Guid EntityId,
    Guid AssetId,
    string MediaType,
    string? FilePath,
    string? FileName,
    EditorMatchLocalEvidenceDto Evidence,
    IReadOnlyDictionary<string, (string Value, string From)> Fields);

/// <summary>
/// Reads what an item's file says about itself (universal editor v5, 3c): the cleaned title and number from the file name, the
/// embedded tags (ID3, EPUB, PDF, MKV title), the folders around it, the measured length from the media file and any identifier found
/// (an ISBN, a YouTube ID, an ISRC, an IMDb or TMDB ID in the name). Tags win over the file name, and the file name over the folders. Nothing
/// is sent to any provider and nothing is written. The noise words come from <c>config/filename_cleaning.json</c>.
/// </summary>
public sealed partial class EditorMatchLocalEvidenceReader(
    IMetadataEditorRepository launches,
    IItemCanonicalRepository items,
    IMetadataClaimRepository claims,
    ItemQuickFactsService quickFacts,
    ILogger<EditorMatchLocalEvidenceReader> logger)
{
    /// <summary>Reads the item's evidence; null when no such item is in the Library.</summary>
    public async Task<EditorMatchLocalRead?> ReadAsync(Guid entityId, string kind, CancellationToken ct = default)
    {
        var context = await items.ResolveWorkAssetContextAsync(entityId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        var rules = FilenameCleaner.Rules;
        var path = launch?.RepresentativeMediaFilePath;
        var fileName = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path.Replace('\\', '/'));
        var folders = Folders(path);
        var isTv = kind.StartsWith("tv_", StringComparison.Ordinal);
        var isMusic = kind is EditorKinds.Song or EditorKinds.Album;
        var parsed = isMusic ? EditorFileNameParser.ParseMusic(path, rules) : EditorFileNameParser.Parse(path, isTv, rules);
        var tags = await ReadTagsAsync(context.AssetId, ct).ConfigureAwait(false);
        var identifiers = ReadIdentifiers(kind, path, fileName, tags);

        double? measured = null;
        if (EditorMatchKinds.UsesMeasuredLength(kind))
        {
            measured = (await quickFacts.GetAsync(entityId, ct).ConfigureAwait(false))?.RuntimeSeconds;
        }

        var fields = new Dictionary<string, (string Value, string From)>(StringComparer.OrdinalIgnoreCase);
        var title = ChooseTitle(kind, parsed, tags, folders, rules, out var titleFrom);
        var year = tags.Year ?? parsed.Year ?? FolderYear(folders, rules);
        var creator = tags.Creator;
        string? series = null;
        int? number = parsed.Episode ?? parsed.Track;

        switch (kind)
        {
            case EditorKinds.Movie:
                Put(fields, EditorMatchFieldIds.Title, title, titleFrom);
                Put(fields, EditorMatchFieldIds.Year, year, YearFrom(tags, parsed));
                Put(fields, EditorMatchFieldIds.ProviderId, Identifier(identifiers, EditorMatchIdentifierKinds.Tmdb) ?? Identifier(identifiers, EditorMatchIdentifierKinds.Imdb), EditorMatchFieldSources.FileName);
                break;
            case EditorKinds.Book:
                creator ??= PersonFolder(folders, title, rules);
                Put(fields, EditorMatchFieldIds.Title, title, titleFrom);
                Put(fields, EditorMatchFieldIds.Author, creator, tags.Creator is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.Tags);
                Put(fields, EditorMatchFieldIds.Isbn, Identifier(identifiers, EditorMatchIdentifierKinds.Isbn), tags.Isbn is null ? EditorMatchFieldSources.FileName : EditorMatchFieldSources.Tags);
                break;
            case EditorKinds.Audiobook:
                creator ??= tags.Narrator ?? PersonFolder(folders, title, rules);
                Put(fields, EditorMatchFieldIds.Title, title, titleFrom);
                Put(fields, EditorMatchFieldIds.AuthorOrNarrator, creator, tags.Creator is null && tags.Narrator is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.Tags);
                break;
            case EditorKinds.Comic:
                {
                    var (nameSeries, nameIssue) = EditorFileNameParser.ReadIssue(parsed.Title);
                    series = tags.Series ?? nameSeries ?? FolderName(folders, 0, rules);
                    number = tags.Issue ?? nameIssue;
                    Put(fields, EditorMatchFieldIds.Series, series, tags.Series is null ? (nameSeries is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.FileName) : EditorMatchFieldSources.Tags);
                    Put(fields, EditorMatchFieldIds.Issue, number?.ToString(CultureInfo.InvariantCulture), tags.Issue is null ? EditorMatchFieldSources.FileName : EditorMatchFieldSources.Tags);
                    Put(fields, EditorMatchFieldIds.Year, year, YearFrom(tags, parsed));
                    break;
                }
            case EditorKinds.TvShow:
            case EditorKinds.TvSeason:
            case EditorKinds.TvEpisode:
                Put(fields, EditorMatchFieldIds.Show, parsed.Show ?? FolderName(folders, 0, rules), parsed.Show is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.FileName);
                Put(fields, EditorMatchFieldIds.Year, year, YearFrom(tags, parsed));
                break;
            case EditorKinds.Album:
            case EditorKinds.Song:
                creator = tags.Artist ?? tags.Creator;
                var artist = creator ?? FolderName(folders, 1, rules);
                var album = tags.Album ?? FolderName(folders, 0, rules);
                Put(fields, EditorMatchFieldIds.Artist, artist, creator is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.Tags);
                Put(fields, EditorMatchFieldIds.Album, album, tags.Album is null ? EditorMatchFieldSources.Folder : EditorMatchFieldSources.Tags);
                if (kind == EditorKinds.Song)
                {
                    Put(fields, EditorMatchFieldIds.Song, tags.Title is { } songTag ? EditorFileNameParser.CleanTitle(songTag, rules) : parsed.Title, tags.Title is null ? EditorMatchFieldSources.FileName : EditorMatchFieldSources.Tags);
                }

                title = kind == EditorKinds.Song ? fields.GetValueOrDefault(EditorMatchFieldIds.Song).Value : album;
                creator = artist;
                break;
            case EditorKinds.Video:
                Put(fields, EditorMatchFieldIds.ProviderId, Identifier(identifiers, EditorMatchIdentifierKinds.YouTube), EditorMatchFieldSources.FileName);
                break;
        }

        var evidence = new EditorMatchLocalEvidenceDto
        {
            FileTitle = isTv ? parsed.Show ?? title : title,
            Year = year,
            Number = number,
            Creator = creator,
            MeasuredSeconds = measured is > 0 ? measured : null,
            MeasuredText = EditorMatchVerdictCalculator.ClockText(measured),
            Identifiers = identifiers,
        };
        return new EditorMatchLocalRead(entityId, context.AssetId, context.MediaType, path, fileName, evidence, fields);
    }

    private static void Put(Dictionary<string, (string Value, string From)> fields, string key, string? value, string from)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = (value.Trim(), from);
        }
    }

    private static string? Identifier(IReadOnlyList<EditorMatchIdentifierDto> found, string kind) =>
        found.FirstOrDefault(item => item.Kind == kind)?.Value;

    private static string YearFrom(TagFacts tags, ParsedFileName parsed) =>
        tags.Year is not null ? EditorMatchFieldSources.Tags : parsed.Year is not null ? EditorMatchFieldSources.FileName : EditorMatchFieldSources.Folder;

    // Folders above the file, nearest last (Path.GetDirectoryName order reversed): [..., grandparent, parent].
    private static string[] Folders(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts ? parts[..^1] : [];

    // The folder `up` levels above the file (0 is the file's own folder), cleaned; null when it is not there or is only noise.
    private static string? FolderName(string[] folders, int up, FilenameCleaningRules rules) =>
        folders.Length > up ? EditorFileNameParser.CleanTitle(folders[^(up + 1)], rules) : null;

    private static string? FolderYear(string[] folders, FilenameCleaningRules rules) =>
        folders.Length == 0 ? null : rules.FindYear(folders[^1]);

    // A folder named for a person: two to four words, no digits, and not the title again.
    private static string? PersonFolder(string[] folders, string? title, FilenameCleaningRules rules)
    {
        foreach (var up in new[] { 1, 0 })
        {
            if (FolderName(folders, up, rules) is { } name
                && !name.Any(char.IsDigit)
                && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 2 and <= 4
                && !string.Equals(name, title, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static string? ChooseTitle(
        string kind, ParsedFileName parsed, TagFacts tags, string[] folders, FilenameCleaningRules rules, out string from)
    {
        if (kind is EditorKinds.Movie or EditorKinds.Book or EditorKinds.Audiobook or EditorKinds.Video)
        {
            if (EditorFileNameParser.CleanTitle(tags.Title, rules) is { } tagTitle)
            {
                from = EditorMatchFieldSources.Tags;
                return tagTitle;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Title))
            {
                from = EditorMatchFieldSources.FileName;
                return parsed.Title;
            }

            from = EditorMatchFieldSources.Folder;
            return FolderName(folders, 0, rules);
        }

        from = EditorMatchFieldSources.FileName;
        return parsed.Title;
    }

    private sealed record TagFacts(
        string? Title, string? Creator, string? Artist, string? Album, string? Year, string? Series, int? Issue, string? Narrator, string? Isbn,
        string? Isrc);

    // The embedded tags the scan read from the file (a claim from the local file reader), the newest of each.
    private async Task<TagFacts> ReadTagsAsync(Guid assetId, CancellationToken ct)
    {
        var current = (await claims.GetByEntityAsync(assetId, ct).ConfigureAwait(false))
            .Where(claim => claim.IsCurrent && WellKnownProviders.IsFileSource(claim.ProviderId) && !string.IsNullOrWhiteSpace(claim.ClaimValue))
            .GroupBy(claim => claim.ClaimKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(claim => claim.ClaimedAt).First().ClaimValue.Trim(), StringComparer.OrdinalIgnoreCase);

        string? Get(string key) => current.GetValueOrDefault(key);
        var year = Get(MetadataFieldConstants.Year);
        return new TagFacts(
            Get(MetadataFieldConstants.Title),
            Get(MetadataFieldConstants.Author) ?? Get(MetadataFieldConstants.Director),
            Get(MetadataFieldConstants.Artist),
            Get(MetadataFieldConstants.Album),
            year is { Length: >= 4 } ? year[..4] : year,
            Get(MetadataFieldConstants.Series),
            int.TryParse(Get(MetadataFieldConstants.SeriesPosition) ?? Get(MetadataFieldConstants.IssueNumber), NumberStyles.Integer, CultureInfo.InvariantCulture, out var issue) ? issue : null,
            Get(MetadataFieldConstants.Narrator),
            Get(BridgeIdKeys.Isbn13) ?? Get(BridgeIdKeys.Isbn),
            Get(BridgeIdKeys.Isrc));
    }

    private IReadOnlyList<EditorMatchIdentifierDto> ReadIdentifiers(string kind, string? path, string? fileName, TagFacts tags)
    {
        var found = new List<EditorMatchIdentifierDto>();
        void Add(string kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !found.Any(item => item.Kind == kind && item.Value == value))
            {
                found.Add(new EditorMatchIdentifierDto { Kind = kind, Value = value });
            }
        }

        Add(EditorMatchIdentifierKinds.Isbn, IsbnOf(tags.Isbn) ?? (EditorMatchKinds.HasEditions(kind) ? IsbnIn(fileName) : null));
        Add(EditorMatchIdentifierKinds.Isrc, tags.Isrc);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var hints = OrganizationHintParser.Parse(path);
            Add(EditorMatchIdentifierKinds.Tmdb, hints.BridgeIds.GetValueOrDefault(BridgeIdKeys.TmdbId));
            Add(EditorMatchIdentifierKinds.Imdb, hints.BridgeIds.GetValueOrDefault(BridgeIdKeys.ImdbId));
        }

        if (fileName is not null && ImdbIn(fileName) is { } imdb)
        {
            Add(EditorMatchIdentifierKinds.Imdb, imdb);
        }

        if (kind == EditorKinds.Video)
        {
            Add(EditorMatchIdentifierKinds.YouTube, YouTubeIdOf(path, fileName));
        }

        Add(EditorMatchIdentifierKinds.EmbeddedTitle, tags.Title);
        return found;
    }

    // A YouTube id sits in square brackets in the name youtube-dl and yt-dlp give a download ("Title [dQw4w9WgXcQ].mp4"), or is the id
    // of the .info.json written beside it.
    private string? YouTubeIdOf(string? path, string? fileName)
    {
        if (fileName is not null && YouTubeBracket().Match(fileName) is { Success: true } bracket)
        {
            return bracket.Groups["id"].Value;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var sidecar = Path.ChangeExtension(path, ".info.json");
        try
        {
            if (!File.Exists(sidecar))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = document.RootElement;
            var extractor = root.TryGetProperty("extractor_key", out var key) ? key.GetString() : null;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            return string.Equals(extractor, "Youtube", StringComparison.OrdinalIgnoreCase) && id is { Length: 11 } ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "The .info.json beside {File} could not be read for a YouTube ID", fileName);
            return null;
        }
    }

    private static string? IsbnIn(string? text) =>
        text is not null && IsbnPattern().Match(text) is { Success: true } hit ? IsbnOf(hit.Value) : null;

    // Digits only, and only when they make a 10 or 13 digit ISBN.
    private static string? IsbnOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var compact = new string(text.Where(ch => char.IsAsciiDigit(ch) || ch is 'X' or 'x').ToArray());
        return compact.Length == 13 && compact.All(char.IsAsciiDigit) && compact.StartsWith("97", StringComparison.Ordinal)
            ? compact
            : compact.Length == 10 && compact[..9].All(char.IsAsciiDigit) ? compact.ToUpperInvariant() : null;
    }

    private static string? ImdbIn(string text) => ImdbPattern().Match(text) is { Success: true } hit ? hit.Groups["id"].Value : null;

    [GeneratedRegex(@"\[(?<id>[A-Za-z0-9_-]{11})\]", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeBracket();

    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:97[89][- ]?(?:\d[- ]?){9}\d|\d{9}[\dXx])(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex IsbnPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<id>tt\d{6,10})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbPattern();
}
