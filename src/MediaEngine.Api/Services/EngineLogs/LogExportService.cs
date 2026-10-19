using System.Globalization;
using System.Text;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// The log export (settings revamp D23). It reads the same Engine log files the Logs page shows, optionally adds the
/// system activity events for the range, runs every line through <see cref="LogRedactor"/>, and writes one UTF-8 text
/// file with a short header that lists the filters and says what was hidden.
/// </summary>
public sealed class LogExportService : ILogExportService
{
    /// <summary>Most entries one export holds; a wider range keeps the newest and the header says so.</summary>
    public const int MaxLines = 200_000;

    private readonly ITechnicalLogService _technicalLog;
    private readonly ISystemActivityRangeReader _events;
    private readonly TimeProvider _clock;

    public LogExportService(ITechnicalLogService technicalLog, ISystemActivityRangeReader events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(technicalLog);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        _technicalLog = technicalLog;
        _events = events;
        _clock = clock;
    }

    /// <summary>The sentence to refuse a request with, or <see langword="null"/> when it can be built.</summary>
    public static string? Validate(LogExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is not { } from || request.To is not { } to)
        {
            return "Choose the time range to export.";
        }

        if (from > to)
        {
            return "The start of the time range must be before its end.";
        }

        if (!LogExportLevels.IsKnown(request.Level))
        {
            return "Level must be one of: errors, warnings_and_above, everything.";
        }

        var unknown = request.Areas.FirstOrDefault(area => LogAreas.Normalize(area) is null);
        if (unknown is not null)
        {
            return $"Area must be one of: {string.Join(", ", LogAreas.All)}.";
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<LogExportResult> BuildAsync(LogExportRequest request, CancellationToken ct = default)
    {
        var problem = Validate(request);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var from = request.From!.Value;
        var to = request.To!.Value;
        var areas = request.Areas.Select(area => LogAreas.Normalize(area)!).ToHashSet(StringComparer.Ordinal);
        var itemOrRun = string.IsNullOrWhiteSpace(request.ItemOrRun) ? null : request.ItemOrRun.Trim();

        var entries = new List<(DateTimeOffset Time, string Text)>();

        // Log files. The reader already applies the time range, the level, and the text match for an id.
        var range = await _technicalLog.ReadRangeAsync(
            new TechnicalLogQuery(from, to, ReaderLevel(request.Level), Area: null, itemOrRun, Cursor: null, Limit: 0),
            MaxLines,
            ct).ConfigureAwait(false);
        var truncated = range.Truncated;
        foreach (var line in range.Lines)
        {
            if (areas.Count > 0 && !areas.Contains(line.Area))
            {
                continue;
            }

            entries.Add((line.Time, Format(line.Time, LevelCode(line.Level), line.Area, line.Message, request.ShowFileNames)));
        }

        // System activity events have no level, so they count as information: only an "everything" export holds them.
        if (request.IncludeEvents && request.Level == LogExportLevels.Everything)
        {
            Guid? matchId = Guid.TryParse(itemOrRun, out var parsed) ? parsed : null;
            var events = await _events.GetRangeAsync(from, to, matchId, MaxLines + 1, ct).ConfigureAwait(false);
            if (events.Count > MaxLines)
            {
                truncated = true;
            }

            foreach (var entry in events.Take(MaxLines))
            {
                var area = LogAreas.Classify(entry.ActionType);
                if (areas.Count > 0 && !areas.Contains(area))
                {
                    continue;
                }

                // An id that is not a Guid matches by the words of the event instead.
                if (itemOrRun is not null && matchId is null
                    && !(entry.Detail?.Contains(itemOrRun, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                entries.Add((entry.OccurredAt, Format(entry.OccurredAt, "EVT", area, EventText(entry), request.ShowFileNames)));
            }
        }

        // Oldest first; equal times keep the order they were added in (log lines before events).
        var ordered = entries.OrderBy(entry => entry.Time).ToList();
        if (ordered.Count > MaxLines)
        {
            ordered = ordered.GetRange(ordered.Count - MaxLines, MaxLines);
            truncated = true;
        }

        var fileName = FileNameFor(from, to);
        var text = new StringBuilder();
        AppendHeader(text, request, from, to, areas, itemOrRun, ordered.Count, truncated);
        foreach (var entry in ordered)
        {
            text.Append(entry.Text).Append('\n');
        }

        return new LogExportResult(fileName, new UTF8Encoding(false).GetBytes(text.ToString()), ordered.Count, truncated);
    }

    /// <summary>The name the downloaded file gets, from the start and end of the range (UTC).</summary>
    public static string FileNameFor(DateTimeOffset from, DateTimeOffset to) =>
        string.Create(CultureInfo.InvariantCulture,
            $"tuvima-logs-{from.ToUniversalTime():yyyyMMdd-HHmm}-{to.ToUniversalTime():yyyyMMdd-HHmm}.txt");

    private static string ReaderLevel(string level) => level switch
    {
        LogExportLevels.Errors => "error",
        LogExportLevels.WarningsAndAbove => "warning",
        _ => "all",
    };

    private static string LevelCode(string level) => level.ToLowerInvariant() switch
    {
        "verbose" => "VRB",
        "debug" => "DBG",
        "warning" => "WRN",
        "error" => "ERR",
        "fatal" => "FTL",
        _ => "INF",
    };

    private static string EventText(SystemActivityEntry entry)
    {
        var text = string.IsNullOrWhiteSpace(entry.Detail) ? entry.ActionType : $"{entry.ActionType}: {entry.Detail}";
        return entry.EntityId is { } id ? $"{text} (item {id})" : text;
    }

    // "2026-10-19 09:00:00.000Z [ERR] [Ingestion] message", with an error's detail lines indented under it.
    private static string Format(DateTimeOffset time, string level, string area, string message, bool showFileNames)
    {
        var redacted = LogRedactor.Redact(message, showFileNames).Replace("\r", string.Empty).Replace("\n", "\n    ");
        return string.Create(CultureInfo.InvariantCulture,
            $"{time.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}Z [{level}] [{area}] {redacted}");
    }

    private void AppendHeader(
        StringBuilder text,
        LogExportRequest request,
        DateTimeOffset from,
        DateTimeOffset to,
        HashSet<string> areas,
        string? itemOrRun,
        int lineCount,
        bool truncated)
    {
        string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

        text.Append("Tuvima Library log export\n");
        text.Append("Created: ").Append(Utc(_clock.GetUtcNow())).Append('\n');
        text.Append("Range: ").Append(Utc(from)).Append(" to ").Append(Utc(to)).Append('\n');
        text.Append("Level: ").Append(request.Level switch
        {
            LogExportLevels.Errors => "errors only",
            LogExportLevels.WarningsAndAbove => "warnings and above",
            _ => "everything",
        }).Append('\n');
        text.Append("Areas: ").Append(areas.Count == 0 ? "all" : string.Join(", ", LogAreas.All.Where(areas.Contains))).Append('\n');
        text.Append("Item or run: ").Append(itemOrRun is null ? "any" : LogRedactor.Redact(itemOrRun, request.ShowFileNames)).Append('\n');
        text.Append("Events included: ")
            .Append(request.IncludeEvents && request.Level == LogExportLevels.Everything ? "yes" : "no").Append('\n');
        text.Append("Private details hidden: yes (keys, tokens, sign-in details, file and folder paths");
        text.Append(request.ShowFileNames ? "; the last part of each file name is shown, never the folders" : "; file names are hidden").Append(")\n");
        text.Append("Lines: ").Append(lineCount.ToString(CultureInfo.InvariantCulture));
        if (truncated)
        {
            text.Append(" (limited to the newest ").Append(MaxLines.ToString("N0", CultureInfo.InvariantCulture)).Append("; older ones were left out)");
        }

        text.Append("\nTimes are UTC.\n");
        text.Append("----------------------------------------\n");
    }
}
