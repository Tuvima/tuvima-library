using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>
/// Reads a list file into entries (Collections and lists spec 4d): an IMDb list or ratings export, a Letterboxd export (one CSV
/// or the whole ZIP), a Trakt or generic CSV (columns found by their headers), a Goodreads or StoryGraph export (with shelves)
/// and a plain text list with one title per line and an optional "(Year)". Everything is read from the bytes in memory; nothing
/// is fetched and nothing leaves the machine.
/// </summary>
public static partial class ListFileParser
{
    private const long MaximumZipEntryBytes = 16L * 1024 * 1024;
    private const long MaximumZipTotalBytes = 48L * 1024 * 1024;
    private const int MaximumCompressionRatio = 200;
    private const int MaximumEntries = 50_000;

    private static readonly string[] TitleColumns = ["title", "name", "film", "filmname", "movie", "book", "booktitle", "originaltitle"];
    private static readonly string[] YearColumns = ["originalpublicationyear", "year", "releaseyear", "yearpublished", "releasedyear"];
    private static readonly string[] CreatorColumns = ["author", "authors", "directors", "director", "artist", "artists", "creator"];
    private static readonly string[] TypeColumns = ["titletype", "type", "mediatype"];
    private static readonly string[] ImdbColumns = ["const", "imdb", "imdbid", "imdbconst", "imdbtitleid"];
    private static readonly string[] TmdbColumns = ["tmdb", "tmdbid", "tmdbmovieid"];
    private static readonly string[] TvdbColumns = ["tvdb", "tvdbid"];
    private static readonly string[] IsbnColumns = ["isbn13", "isbn", "isbnuid", "isbn10"];
    private static readonly string[] AsinColumns = ["asin"];
    private static readonly string[] MusicBrainzColumns = ["musicbrainz", "musicbrainzid", "mbid"];
    private static readonly string[] ComicVineColumns = ["comicvine", "comicvineid"];

    /// <summary>Reads a file by its bytes. A ZIP is a Letterboxd export; anything else is text.</summary>
    /// <exception cref="ListFileNotUnderstoodException">The file is not a list Library understands.</exception>
    public static ParsedList Parse(string fileName, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ListFileNotUnderstoodException();
        }

        return IsZip(bytes) ? ParseLetterboxdZip(bytes) : ParseText(fileName, DecodeText(bytes));
    }

    /// <summary>True when the bytes start like a ZIP archive.</summary>
    public static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] is 0x03 or 0x05 or 0x07 && bytes[3] is 0x04 or 0x06 or 0x08;

    /// <summary>Reads a file or pasted text. The name only decides between a text list and a CSV when the content is unclear.</summary>
    public static ParsedList ParseText(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ListFileNotUnderstoodException();
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var rows = extension == ".txt" ? null : TryReadCsv(text);
        if (rows is not null)
        {
            return ParseCsv(fileName ?? string.Empty, rows);
        }

        return ParsePlainText(fileName ?? string.Empty, text);
    }

    // ── Plain text ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static ParsedList ParsePlainText(string fileName, string text)
    {
        var entries = new List<ParsedListEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            line = LeadingMarkerRegex().Replace(line, string.Empty).Trim();
            int? year = null;
            var yearMatch = TrailingYearRegex().Match(line);
            if (yearMatch.Success)
            {
                year = int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                line = line[..yearMatch.Index].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            entries.Add(new ParsedListEntry { Position = entries.Count + 1, Title = line, Year = year });
            if (entries.Count > MaximumEntries)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            throw new ListFileNotUnderstoodException();
        }

        return new ParsedList
        {
            SourceKind = ListSourceKinds.Text,
            Name = NameFromFile(fileName),
            Entries = entries,
        };
    }

    // ── CSV ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    // A file is a CSV when some row near the top has a column that names a title. Anything else is treated as plain text.
    private static List<List<string>>? TryReadCsv(string text)
    {
        var rows = CsvTable.Read(text);
        var limit = Math.Min(rows.Count, 12);
        for (var i = 0; i < limit; i++)
        {
            var normalised = rows[i].Select(Norm).ToList();
            if (normalised.Count >= 2 && IndexOfAny(normalised, TitleColumns) >= 0 && !LooksLikeData(rows[i]))
            {
                return rows;
            }
        }

        return null;
    }

    // A row of header names has no cell as long as a sentence; a title line such as "Hello, World" would otherwise pass.
    private static bool LooksLikeData(List<string> row) => row.Any(cell => cell.Length > 60);

    private static ParsedList ParseCsv(string fileName, List<List<string>> rows)
    {
        // Letterboxd list files start with a metadata block ("Letterboxd list export v7", the list's name) and then, after a
        // blank row, the real table headed "Position,Name,Year,URL,Description".
        var name = string.Empty;
        var headerIndex = -1;
        var letterboxdList = rows.Count > 0 && rows[0].Count > 0 && rows[0][0].StartsWith("Letterboxd list export", StringComparison.OrdinalIgnoreCase);
        if (letterboxdList)
        {
            for (var i = 1; i < rows.Count; i++)
            {
                var normalised = rows[i].Select(Norm).ToList();
                if (name.Length == 0 && i + 1 < rows.Count && normalised.Contains("name") && normalised.Contains("date"))
                {
                    var nameColumn = normalised.IndexOf("name");
                    name = rows[i + 1].Count > nameColumn ? rows[i + 1][nameColumn].Trim() : string.Empty;
                }

                if (normalised.Contains("position") && normalised.Contains("name"))
                {
                    headerIndex = i;
                    break;
                }
            }
        }
        else
        {
            for (var i = 0; i < Math.Min(rows.Count, 12); i++)
            {
                var normalised = rows[i].Select(Norm).ToList();
                if (normalised.Count >= 2 && IndexOfAny(normalised, TitleColumns) >= 0 && !LooksLikeData(rows[i]))
                {
                    headerIndex = i;
                    break;
                }
            }
        }

        if (headerIndex < 0)
        {
            throw new ListFileNotUnderstoodException();
        }

        var header = rows[headerIndex].Select(Norm).ToList();
        var table = new ColumnMap(header);
        var kind = DetectKind(header, rows, letterboxdList);
        var entries = new List<ParsedListEntry>();
        var shelfCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shelfOrder = new List<string>();

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (CsvTable.IsBlank(row))
            {
                continue;
            }

            var title = table.Value(row, TitleColumns);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var shelves = ShelvesFor(kind, table, row);
            foreach (var shelf in shelves)
            {
                shelfCounts[shelf] = shelfCounts.GetValueOrDefault(shelf) + 1;
                if (!shelfOrder.Contains(shelf, StringComparer.OrdinalIgnoreCase))
                {
                    shelfOrder.Add(shelf);
                }
            }

            entries.Add(new ParsedListEntry
            {
                Position = entries.Count + 1,
                Title = title.Trim(),
                Year = YearFor(table, row),
                Creator = FirstCreator(table.Value(row, CreatorColumns)),
                Shelves = shelves,
                Ids = IdentifiersFor(table, row),
                MediaTypes = MediaTypesFor(kind, table, row),
            });
            if (entries.Count > MaximumEntries)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            throw new ListFileNotUnderstoodException();
        }

        return new ParsedList
        {
            SourceKind = kind,
            Name = name.Length > 0 ? name : NameFromFile(fileName),
            Entries = entries,
            Shelves = shelfOrder.Select(shelf => (shelf, shelfCounts[shelf])).ToList(),
        };
    }

    private static string DetectKind(List<string> header, List<List<string>> rows, bool letterboxdList)
    {
        if (header.Contains("readstatus") && (header.Contains("authors") || header.Contains("title")))
        {
            return ListSourceKinds.StoryGraph;
        }

        if (header.Contains("exclusiveshelf") || (header.Contains("bookid") && header.Contains("bookshelves")))
        {
            return ListSourceKinds.Goodreads;
        }

        if (letterboxdList || header.Contains("letterboxduri")
            || (header.Contains("url") && rows.Skip(1).Take(5).Any(row => row.Any(cell => cell.Contains("boxd.it", StringComparison.OrdinalIgnoreCase)
                || cell.Contains("letterboxd.com", StringComparison.OrdinalIgnoreCase)))))
        {
            return ListSourceKinds.Letterboxd;
        }

        if (header.Contains("const") || (header.Contains("titletype") && header.Contains("imdbrating")))
        {
            return ListSourceKinds.Imdb;
        }

        if (header.Any(column => column is "trakt" or "traktid" or "traktrating" or "slug" or "listedat"))
        {
            return ListSourceKinds.Trakt;
        }

        return ListSourceKinds.Csv;
    }

    private static IReadOnlyList<string> ShelvesFor(string kind, ColumnMap table, List<string> row)
    {
        var shelves = new List<string>();
        void Add(string? value)
        {
            var shelf = value?.Trim();
            if (!string.IsNullOrEmpty(shelf) && !shelves.Contains(shelf, StringComparer.OrdinalIgnoreCase))
            {
                shelves.Add(shelf);
            }
        }

        switch (kind)
        {
            case ListSourceKinds.Goodreads:
                Add(table.Value(row, ["exclusiveshelf"]));
                foreach (var shelf in SplitList(table.Value(row, ["bookshelves"])))
                {
                    Add(shelf);
                }

                break;
            case ListSourceKinds.StoryGraph:
                Add(table.Value(row, ["readstatus"]));
                foreach (var shelf in SplitList(table.Value(row, ["tags"])))
                {
                    Add(shelf);
                }

                break;
        }

        return shelves;
    }

    private static IReadOnlyList<string> MediaTypesFor(string kind, ColumnMap table, List<string> row)
    {
        switch (kind)
        {
            case ListSourceKinds.Letterboxd:
                return ["Movies"];
            case ListSourceKinds.Goodreads:
            case ListSourceKinds.StoryGraph:
                return ["Books", "Audiobooks"];
        }

        var type = Norm(table.Value(row, TypeColumns) ?? string.Empty);
        return type switch
        {
            "movie" or "film" or "tvmovie" or "short" or "tvshort" or "video" => ["Movies"],
            "tvseries" or "tvminiseries" or "tvspecial" or "show" or "tv" or "episode" or "tvepisode" or "season" => ["TV"],
            "videogame" => ["Games"],
            "book" or "ebook" => ["Books", "Audiobooks"],
            "audiobook" => ["Audiobooks"],
            "album" or "music" or "song" or "track" => ["Music"],
            "comic" => ["Comics"],
            _ => [],
        };
    }

    private static int? YearFor(ColumnMap table, List<string> row)
    {
        foreach (var column in YearColumns)
        {
            var value = table.Value(row, [column]);
            if (value is null)
            {
                continue;
            }

            // "1999", "1999-07-16" or "07/16/1999": the first four-digit run that looks like a year.
            var match = YearInTextRegex().Match(value);
            if (match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1000 and <= 2999)
            {
                return year;
            }
        }

        return null;
    }

    private static string? FirstCreator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var first = SplitList(value).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    private static IReadOnlyDictionary<string, string> IdentifiersFor(ColumnMap table, List<string> row)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);

        var imdb = CleanCell(table.Value(row, ImdbColumns))?.ToLowerInvariant();
        if (imdb is not null && ImdbIdRegex().IsMatch(imdb))
        {
            ids[ListIdentifierKeys.Imdb] = imdb;
        }

        var tmdb = DigitsOnly(table.Value(row, TmdbColumns));
        if (tmdb is not null)
        {
            ids[ListIdentifierKeys.Tmdb] = tmdb;
        }

        var tvdb = DigitsOnly(table.Value(row, TvdbColumns));
        if (tvdb is not null)
        {
            ids[ListIdentifierKeys.Tvdb] = tvdb;
        }

        foreach (var column in IsbnColumns)
        {
            var value = CleanCell(table.Value(row, [column]))?.Replace("-", string.Empty, StringComparison.Ordinal);
            if (value is null)
            {
                continue;
            }

            if (IsbnRegex().IsMatch(value))
            {
                ids.TryAdd(ListIdentifierKeys.Isbn, value.ToUpperInvariant());
            }
            else if (AsinRegex().IsMatch(value))
            {
                // StoryGraph's "ISBN/UID" column also carries Amazon identifiers.
                ids.TryAdd(ListIdentifierKeys.Asin, value.ToUpperInvariant());
            }
        }

        var asin = CleanCell(table.Value(row, AsinColumns));
        if (asin is not null && AsinRegex().IsMatch(asin))
        {
            ids[ListIdentifierKeys.Asin] = asin.ToUpperInvariant();
        }

        var musicBrainz = CleanCell(table.Value(row, MusicBrainzColumns))?.ToLowerInvariant();
        if (musicBrainz is not null && GuidTextRegex().IsMatch(musicBrainz))
        {
            ids[ListIdentifierKeys.MusicBrainz] = musicBrainz;
        }

        var comicVine = DigitsOnly(table.Value(row, ComicVineColumns));
        if (comicVine is not null)
        {
            ids[ListIdentifierKeys.ComicVine] = comicVine;
        }

        return ids;
    }

    // Goodreads writes numbers that must stay text as ="0441172717".
    private static string? CleanCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim();
        if (cleaned.StartsWith('='))
        {
            cleaned = cleaned[1..];
        }

        cleaned = cleaned.Trim('"').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? DigitsOnly(string? value)
    {
        var cleaned = CleanCell(value);
        return cleaned is not null && cleaned.All(char.IsAsciiDigit) && cleaned != "0" ? cleaned : null;
    }

    // ── Letterboxd ZIP ───────────────────────────────────────────────────────────────────────────────────────────────

    private static ParsedList ParseLetterboxdZip(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            long total = 0;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (path.Contains("../", StringComparison.Ordinal) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total += entry.Length;
                if (entry.Length > MaximumZipEntryBytes || total > MaximumZipTotalBytes
                    || (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio))
                {
                    throw new ListFileNotUnderstoodException("This ZIP file is too large or not safe to open.");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files[path] = DecodeText(buffer.ToArray());
            }

            // Only the Letterboxd lists people keep: the watchlist, each list under lists/, and the diary.
            var shelves = new List<(string Name, List<ParsedListEntry> Entries)>();
            if (files.TryGetValue("watchlist.csv", out var watchlist))
            {
                shelves.Add(("Watchlist", ReadLetterboxdTable(watchlist)));
            }

            foreach (var (path, content) in files.Where(pair => pair.Key.StartsWith("lists/", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rows = CsvTable.Read(content);
                var listEntries = ReadLetterboxdTable(content);
                if (listEntries.Count == 0)
                {
                    continue;
                }

                var listName = LetterboxdListName(rows);
                shelves.Add((listName.Length > 0 ? listName : NameFromFile(path), listEntries));
            }

            if (files.TryGetValue("diary.csv", out var diary))
            {
                shelves.Add(("Diary", ReadLetterboxdTable(diary)));
            }

            shelves = shelves.Where(shelf => shelf.Entries.Count > 0).ToList();
            if (shelves.Count == 0)
            {
                throw new ListFileNotUnderstoodException();
            }

            return CombineShelves(shelves);
        }
        catch (InvalidDataException)
        {
            throw new ListFileNotUnderstoodException();
        }
    }

    private static string LetterboxdListName(List<List<string>> rows)
    {
        if (rows.Count == 0 || rows[0].Count == 0 || !rows[0][0].StartsWith("Letterboxd list export", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        for (var i = 1; i + 1 < rows.Count; i++)
        {
            var normalised = rows[i].Select(Norm).ToList();
            var nameColumn = normalised.IndexOf("name");
            if (nameColumn >= 0 && normalised.Contains("date"))
            {
                return rows[i + 1].Count > nameColumn ? rows[i + 1][nameColumn].Trim() : string.Empty;
            }
        }

        return string.Empty;
    }

    private static List<ParsedListEntry> ReadLetterboxdTable(string content)
    {
        var rows = CsvTable.Read(content);
        var headerIndex = -1;
        for (var i = 0; i < Math.Min(rows.Count, 12); i++)
        {
            var normalised = rows[i].Select(Norm).ToList();
            if (normalised.Contains("name") && normalised.Contains("year"))
            {
                headerIndex = i;
                break;
            }
        }

        var entries = new List<ParsedListEntry>();
        if (headerIndex < 0)
        {
            return entries;
        }

        var table = new ColumnMap(rows[headerIndex].Select(Norm).ToList());
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var title = table.Value(rows[i], ["name"]);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            entries.Add(new ParsedListEntry
            {
                Position = entries.Count + 1,
                Title = title.Trim(),
                Year = YearFor(table, rows[i]),
                MediaTypes = ["Movies"],
            });
        }

        return entries;
    }

    // One entry per film across the shelves it is on; the first shelf's position decides the order.
    private static ParsedList CombineShelves(List<(string Name, List<ParsedListEntry> Entries)> shelves)
    {
        var byKey = new Dictionary<string, ParsedListEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (name, entries) in shelves)
        {
            foreach (var entry in entries)
            {
                var key = $"{Norm(entry.Title)}|{entry.Year}";
                if (byKey.TryGetValue(key, out var existing))
                {
                    byKey[key] = existing with { Shelves = [.. existing.Shelves, name] };
                }
                else
                {
                    byKey[key] = entry with { Shelves = [name] };
                    order.Add(key);
                }
            }
        }

        var combined = order.Select((key, index) => byKey[key] with { Position = index + 1 }).ToList();
        return new ParsedList
        {
            SourceKind = ListSourceKinds.Letterboxd,
            Entries = combined,
            Shelves = shelves.Select(shelf => (shelf.Name, shelf.Entries.Count)).ToList(),
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string DecodeText(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string NameFromFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Replace('_', ' ').Replace('-', ' ').Trim();
        return name;
    }

    private static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // A column header or title reduced to lower-case letters and digits, so "Original Publication Year" and "original_publication_year" agree.
    internal static string Norm(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static int IndexOfAny(List<string> header, string[] names)
    {
        foreach (var name in names)
        {
            var index = header.IndexOf(name);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class ColumnMap(List<string> header)
    {
        /// <summary>The first non-empty cell among the named columns, in the order the names are given.</summary>
        public string? Value(List<string> row, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var index = header.IndexOf(name);
                if (index < 0 || index >= row.Count)
                {
                    continue;
                }

                var cell = row[index];
                if (!string.IsNullOrWhiteSpace(cell))
                {
                    return cell;
                }
            }

            return null;
        }
    }

    [GeneratedRegex(@"^\s*(?:\d+\s*[.)]|[-*•])\s+")]
    private static partial Regex LeadingMarkerRegex();

    [GeneratedRegex(@"\(\s*(\d{4})\s*\)\s*$")]
    private static partial Regex TrailingYearRegex();

    [GeneratedRegex(@"(?<!\d)(1[0-9]{3}|2[0-9]{3})(?!\d)")]
    private static partial Regex YearInTextRegex();

    [GeneratedRegex(@"^tt\d{6,10}$")]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"^(?:\d{9}[\dX]|\d{13})$", RegexOptions.IgnoreCase)]
    private static partial Regex IsbnRegex();

    [GeneratedRegex(@"^B0[0-9A-Z]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex AsinRegex();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex GuidTextRegex();
}
