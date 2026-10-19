using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>One technical log entry, newest first in a page.</summary>
public sealed record TechnicalLogEntry(DateTimeOffset Time, string Level, string Area, string Message);

/// <summary>A page of entries and, when there may be older ones, the cursor that continues after the last entry.</summary>
public sealed record TechnicalLogPage(IReadOnlyList<TechnicalLogEntry> Lines, string? NextCursor);

/// <summary>Every matching entry in a range, oldest first, and whether older ones were left out to stay within the limit.</summary>
public sealed record TechnicalLogRange(IReadOnlyList<TechnicalLogEntry> Lines, bool Truncated);

/// <summary>
/// What to read. <paramref name="Level"/> is the lowest level wanted: <c>error</c> (errors and worse),
/// <c>warning</c> (warnings and worse), <c>information</c>, or <c>all</c>/<see langword="null"/>.
/// </summary>
public sealed record TechnicalLogQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Level,
    string? Area,
    string? Search,
    string? Cursor,
    int Limit);

/// <summary>
/// Reads <c>tuvima-*.log</c> newest first without ever locking a file the Engine is still writing: files open
/// shared for reading and writing, so the live file can be read while Serilog appends to it. A line that cannot be
/// understood is tolerated (it joins the entry above it, or is dropped when it has none).
/// </summary>
public sealed partial class TechnicalLogService : ITechnicalLogService
{
    /// <summary>Longest message kept per entry; a long exception trace is cut here.</summary>
    internal const int MaxMessageLength = 8000;

    /// <summary>Largest page a caller can ask for.</summary>
    public const int MaxLimit = 500;

    public const int DefaultLimit = 100;

    // Serilog's file template: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}" then the message.
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[([A-Z]{3})\] ?(.*)$")]
    private static partial Regex HeaderPattern();

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    private readonly EngineLogLocation _location;

    public TechnicalLogService(EngineLogLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _location = location;
    }

    /// <inheritdoc/>
    public Task<TechnicalLogPage> ReadAsync(TechnicalLogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Read(query, ct), ct);
    }

    /// <summary>Turns a level word (<c>error</c>, <c>warning</c>, <c>information</c>, <c>all</c>) into a severity rank; false when it is not a level word.</summary>
    public static bool TryParseMinimumLevel(string? level, out int rank)
    {
        switch (level?.Trim().ToLowerInvariant())
        {
            case null or "" or "all" or "everything":
                rank = 0;
                return true;
            case "information" or "info":
                rank = LevelRank("INF");
                return true;
            case "warning" or "warnings":
                rank = LevelRank("WRN");
                return true;
            case "error" or "errors":
                rank = LevelRank("ERR");
                return true;
            default:
                rank = 0;
                return false;
        }
    }

    private TechnicalLogPage Read(TechnicalLogQuery query, CancellationToken ct)
    {
        var directory = Path.GetFullPath(_location.Directory);
        if (!Directory.Exists(directory))
        {
            return new TechnicalLogPage([], null);
        }

        TryParseMinimumLevel(query.Level, out var minimumRank);
        var area = LogAreas.Normalize(query.Area);
        var limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaxLimit);
        var cursor = ParseCursor(query.Cursor);

        // Newest file first. Date-stamped names sort the same way as the dates; "_001" style names come after their day.
        var files = new DirectoryInfo(directory)
            .EnumerateFiles(EngineLogLocation.FilePattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();

        var lines = new List<TechnicalLogEntry>(limit);
        string? nextCursor = null;

        for (var index = 0; index < files.Count && nextCursor is null; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];

            // Continue after the cursor: newer files were already served.
            if (cursor is { } position && string.CompareOrdinal(file.Name, position.File) > 0)
            {
                continue;
            }

            // A file last written before the window starts cannot hold anything inside it.
            if (query.From is { } from && file.LastWriteTimeUtc < from.UtcDateTime)
            {
                continue;
            }

            var ordinalLimit = cursor is { } at && file.Name == at.File ? at.Ordinal : int.MaxValue;
            var remaining = limit - lines.Count;
            var matches = ReadMatches(file.FullName, query, minimumRank, area, ordinalLimit, remaining + 1, ct);

            // Matches arrive oldest to newest; the page wants newest first.
            for (var i = matches.Count - 1; i >= 0 && lines.Count < limit; i--)
            {
                lines.Add(matches[i].Entry);
                if (lines.Count == limit)
                {
                    // Anything left in this file, or any older file, is for the next page.
                    var moreInFile = i > 0;
                    var olderFiles = index < files.Count - 1;
                    if (moreInFile || olderFiles)
                    {
                        nextCursor = FormatCursor(file.Name, matches[i].Ordinal);
                    }
                }
            }
        }

        return new TechnicalLogPage(lines, nextCursor);
    }

    // Streams one file and keeps only the newest `keep` entries that pass the filters, so a large file never sits in memory.
    private static List<Match> ReadMatches(
        string path,
        TechnicalLogQuery query,
        int minimumRank,
        string? area,
        int ordinalLimit,
        int keep,
        CancellationToken ct)
    {
        var window = new Queue<Match>(keep);
        ScanFile(path, ordinalLimit, ct, entry =>
        {
            if (!Passes(entry, query, minimumRank, area))
            {
                return;
            }

            if (window.Count == keep)
            {
                window.Dequeue();
            }

            window.Enqueue(new Match(entry.Ordinal, new TechnicalLogEntry(
                entry.Time, entry.Level, LogAreas.Classify(entry.Context), entry.Message())));
        });

        return [.. window];
    }

    /// <inheritdoc/>
    public Task<TechnicalLogRange> ReadRangeAsync(TechnicalLogQuery query, int maxLines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        return Task.Run(() => ReadRange(query, maxLines, ct), ct);
    }

    // Every matching entry, oldest first. Only the newest `maxLines` are kept, so a very wide range cannot fill memory.
    private TechnicalLogRange ReadRange(TechnicalLogQuery query, int maxLines, CancellationToken ct)
    {
        var directory = Path.GetFullPath(_location.Directory);
        if (!Directory.Exists(directory))
        {
            return new TechnicalLogRange([], false);
        }

        TryParseMinimumLevel(query.Level, out var minimumRank);
        var area = LogAreas.Normalize(query.Area);

        var files = new DirectoryInfo(directory)
            .EnumerateFiles(EngineLogLocation.FilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        var kept = new Queue<TechnicalLogEntry>();
        var truncated = false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // A file last written before the window starts cannot hold anything inside it.
            if (query.From is { } from && file.LastWriteTimeUtc < from.UtcDateTime)
            {
                continue;
            }

            ScanFile(file.FullName, int.MaxValue, ct, entry =>
            {
                if (!Passes(entry, query, minimumRank, area))
                {
                    return;
                }

                if (kept.Count == maxLines)
                {
                    kept.Dequeue();
                    truncated = true;
                }

                kept.Enqueue(new TechnicalLogEntry(entry.Time, entry.Level, LogAreas.Classify(entry.Context), entry.Message()));
            });
        }

        return new TechnicalLogRange([.. kept], truncated);
    }

    // Reads one file entry by entry, oldest first, and hands each to `onEntry`. Stops before entry number `ordinalLimit`.
    private static void ScanFile(string path, int ordinalLimit, CancellationToken ct, Action<Pending> onEntry)
    {
        try
        {
            // ReadWrite sharing is what lets the live file be read while the Engine keeps appending to it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            Pending? current = null;
            var ordinal = -1;
            var lineNumber = 0;

            while (reader.ReadLine() is { } line)
            {
                if (++lineNumber % 4096 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var header = HeaderPattern().Match(line);
                if (header.Success && TryParseTime(header.Groups[1].Value, out var time))
                {
                    if (current is not null)
                    {
                        onEntry(current);
                        current = null;
                    }

                    ordinal++;
                    if (ordinal >= ordinalLimit)
                    {
                        // Everything from here on is newer than the cursor.
                        return;
                    }

                    current = new Pending(ordinal, time, LevelName(header.Groups[2].Value), LevelRank(header.Groups[2].Value),
                        header.Groups[3].Value.Trim());
                    continue;
                }

                // The message line, an exception trace, or a line that could not be understood: it belongs to the entry above.
                current?.Append(line);
            }

            if (current is not null)
            {
                onEntry(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file removed by the nightly clean-up mid-read, or one the operating system will not share, is skipped
            // rather than failing the whole page.
        }
    }

    private static bool Passes(Pending entry, TechnicalLogQuery query, int minimumRank, string? area)
    {
        if (entry.Rank < minimumRank)
        {
            return false;
        }

        if (query.From is { } from && entry.Time < from)
        {
            return false;
        }

        if (query.To is { } to && entry.Time > to)
        {
            return false;
        }

        if (area is not null && !string.Equals(LogAreas.Classify(entry.Context), area, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            if (!entry.Message().Contains(term, StringComparison.OrdinalIgnoreCase)
                && !entry.Context.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTime(string text, out DateTimeOffset time) =>
        DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static int LevelRank(string code) => code switch
    {
        "VRB" => 0,
        "DBG" => 1,
        "INF" => 2,
        "WRN" => 3,
        "ERR" => 4,
        "FTL" => 5,
        _ => 2,
    };

    private static string LevelName(string code) => code switch
    {
        "VRB" => "verbose",
        "DBG" => "debug",
        "INF" => "information",
        "WRN" => "warning",
        "ERR" => "error",
        "FTL" => "fatal",
        _ => "information",
    };

    private static string FormatCursor(string file, int ordinal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{file}|{ordinal.ToString(CultureInfo.InvariantCulture)}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // A cursor that cannot be read is treated as "start from the newest" rather than an error.
    private static (string File, int Ordinal)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var split = text.LastIndexOf('|');
            if (split > 0 && int.TryParse(text.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                return (text[..split], ordinal);
            }
        }
        catch (FormatException)
        {
            // Not a cursor this service made; fall through to "no cursor".
        }

        return null;
    }

    private readonly record struct Match(int Ordinal, TechnicalLogEntry Entry);

    private sealed class Pending(int ordinal, DateTimeOffset time, string level, int rank, string context)
    {
        private readonly StringBuilder _message = new();
        private bool _truncated;

        public int Ordinal { get; } = ordinal;
        public DateTimeOffset Time { get; } = time;
        public string Level { get; } = level;
        public int Rank { get; } = rank;
        public string Context { get; } = context;

        public void Append(string line)
        {
            if (_truncated)
            {
                return;
            }

            if (_message.Length > 0)
            {
                _message.Append('\n');
            }

            _message.Append(_message.Length == 0 ? line.TrimStart() : line);
            if (_message.Length > MaxMessageLength)
            {
                _message.Length = MaxMessageLength;
                _message.Append('…');
                _truncated = true;
            }
        }

        public string Message() => _message.ToString();
    }
}
