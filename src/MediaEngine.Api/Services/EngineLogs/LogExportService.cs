using System.Globalization;
using System.Text;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// The log export (settings revamp D23). It reads the same Engine log files the Logs page shows, optionally adds the
/// system activity events for the range, runs every line through <see cref="LogRedactor"/>, and writes one UTF-8 text
/// file with a short header that lists the filters, says what was hidden and says exactly what was capped.
///
/// Filters (time, level, area, text) always run first; only then do the line and size limits drop the oldest lines. The
/// preview counts lines and bytes in a pass that never holds the finished file, and the export writes the same lines one
/// at a time, so neither builds the file in memory.
/// </summary>
public sealed class LogExportService : ILogExportService
{
    /// <summary>Most entries one export holds; a wider range keeps the newest and the header says so.</summary>
    public const int MaxLines = 200_000;

    /// <summary>Largest export, in bytes; a wider range keeps the newest and the header says so.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    // Room kept for the header, whose size depends on what was chosen.
    private const int HeaderAllowance = 4096;
    private const int ItemOrRunDisplayLength = 100;

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

        var unknown = (request.Areas ?? []).FirstOrDefault(area => LogAreas.Normalize(area) is null);
        if (unknown is not null)
        {
            return $"Area must be one of: {string.Join(", ", LogAreas.All)}.";
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<LogExportPlan> PlanAsync(LogExportRequest request, CancellationToken ct = default)
    {
        var problem = Validate(request);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var from = request.From!.Value;
        var to = request.To!.Value;
        var areas = (request.Areas ?? []).Select(area => LogAreas.Normalize(area)!).ToHashSet(StringComparer.Ordinal);
        var itemOrRun = string.IsNullOrWhiteSpace(request.ItemOrRun) ? null : request.ItemOrRun.Trim();
        var eventsIncluded = request.IncludeEvents && request.Level == LogExportLevels.Everything;

        // Log files. The reader applies time, level, area and text first, then its own line and size limits.
        var range = await _technicalLog.ReadRangeAsync(
            new TechnicalLogQuery(from, to, ReaderLevel(request.Level), Area: null, itemOrRun, Cursor: null, Limit: 0),
            areas,
            MaxLines,
            MaxBytes,
            ct).ConfigureAwait(false);
        var linesCapped = range.LinesCapped;
        var bytesCapped = range.BytesCapped;

        var candidates = new List<Candidate>(range.Lines.Count);
        foreach (var line in range.Lines)
        {
            candidates.Add(new Candidate(line.Time, LevelCode(line.Level), line.Area, line.Message));
        }

        // System activity events have no level, so they count as information: only an "everything" export holds them.
        if (eventsIncluded)
        {
            var (events, eventsCapped) = await ReadEventsAsync(from, to, areas, itemOrRun, ct).ConfigureAwait(false);
            linesCapped |= eventsCapped;
            foreach (var entry in events)
            {
                candidates.Add(new Candidate(entry.OccurredAt, "EVT", LogAreas.Classify(entry.ActionType), EventText(entry)));
            }
        }

        // Oldest first; equal times keep the order they were added in (log lines before events).
        candidates = [.. candidates.OrderBy(candidate => candidate.Time)];

        // Count from the newest entry back, redacting each, until a limit is reached. This is the only pass that decides what
        // the file holds; nothing is kept from it except how many entries and bytes.
        var bodyBudget = MaxBytes - HeaderAllowance;
        long bodyBytes = 0;
        var included = 0;
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();
            var size = Encoding.UTF8.GetByteCount(FormatLine(candidates[index], request.ShowFileNames)) + 1;
            if (included >= MaxLines)
            {
                linesCapped = true;
                break;
            }

            if (bodyBytes + size > bodyBudget)
            {
                bytesCapped = true;
                break;
            }

            bodyBytes += size;
            included++;
        }

        var first = candidates.Count - included;
        var fileName = FileNameFor(from, to);
        var header = BuildHeader(request, from, to, areas, itemOrRun, eventsIncluded, included, linesCapped, bytesCapped);
        return new Plan(
            fileName, included, Encoding.UTF8.GetByteCount(header) + bodyBytes, linesCapped || bytesCapped,
            header, candidates, first, request.ShowFileNames);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(LogExportPlan plan, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (plan is not Plan ready)
        {
            throw new ArgumentException("The plan did not come from this service.", nameof(plan));
        }

        var utf8 = new UTF8Encoding(false);
        await output.WriteAsync(utf8.GetBytes(ready.Header), ct).ConfigureAwait(false);
        for (var index = ready.First; index < ready.Candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            await output.WriteAsync(utf8.GetBytes(FormatLine(ready.Candidates[index], ready.ShowFileNames) + "\n"), ct)
                .ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The name the downloaded file gets, from the start and end of the range (UTC).</summary>
    public static string FileNameFor(DateTimeOffset from, DateTimeOffset to) =>
        string.Create(CultureInfo.InvariantCulture,
            $"tuvima-logs-{from.ToUniversalTime():yyyyMMdd-HHmm}-{to.ToUniversalTime():yyyyMMdd-HHmm}.txt");

    // The events of the range that pass the area and text filters (both worked out in the query), newest MaxLines of them.
    private async Task<(IReadOnlyList<SystemActivityEntry> Events, bool Capped)> ReadEventsAsync(
        DateTimeOffset from, DateTimeOffset to, HashSet<string> areas, string? itemOrRun, CancellationToken ct)
    {
        IReadOnlyCollection<string>? types = null;
        if (areas.Count > 0)
        {
            var all = await _events.GetActionTypesAsync(from, to, ct).ConfigureAwait(false);
            types = [.. all.Where(type => areas.Contains(LogAreas.Classify(type)))];
            if (types.Count == 0)
            {
                return ([], false);
            }
        }

        // A Guid matches the run or the item; any other text matches the words of the event.
        Guid? matchId = Guid.TryParse(itemOrRun, out var parsed) ? parsed : null;
        var detail = matchId is null ? itemOrRun : null;

        var found = await _events.GetRangeAsync(from, to, matchId, types, detail, MaxLines + 1, ct).ConfigureAwait(false);
        return found.Count > MaxLines ? ([.. found.Skip(found.Count - MaxLines)], true) : (found, false);
    }

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
    private static string FormatLine(Candidate candidate, bool showFileNames)
    {
        var redacted = LogRedactor.Redact(candidate.Message, showFileNames).Replace("\r", string.Empty).Replace("\n", "\n    ");
        return string.Create(CultureInfo.InvariantCulture,
            $"{candidate.Time.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}Z [{candidate.Level}] [{candidate.Area}] {redacted}");
    }

    private string BuildHeader(
        LogExportRequest request,
        DateTimeOffset from,
        DateTimeOffset to,
        HashSet<string> areas,
        string? itemOrRun,
        bool eventsIncluded,
        int lineCount,
        bool linesCapped,
        bool bytesCapped)
    {
        static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

        var text = new StringBuilder();
        text.Append("Library log export\n");
        text.Append("Created: ").Append(Utc(_clock.GetUtcNow())).Append('\n');
        text.Append("Range: ").Append(Utc(from)).Append(" to ").Append(Utc(to)).Append('\n');
        text.Append("Level: ").Append(request.Level switch
        {
            LogExportLevels.Errors => "errors only",
            LogExportLevels.WarningsAndAbove => "warnings and above",
            _ => "everything",
        }).Append('\n');
        text.Append("Areas: ").Append(areas.Count == 0 ? "all" : string.Join(", ", LogAreas.All.Where(areas.Contains))).Append('\n');
        var shownItem = itemOrRun is null ? "any" : LogRedactor.Redact(itemOrRun, request.ShowFileNames);
        if (shownItem.Length > ItemOrRunDisplayLength)
        {
            shownItem = shownItem[..ItemOrRunDisplayLength] + "…";
        }

        text.Append("Item or run: ").Append(shownItem).Append('\n');
        text.Append("Events included: ").Append(eventsIncluded ? "yes" : "no").Append('\n');
        text.Append("Private details hidden: yes (keys, tokens, sign-in details, file and folder paths");
        text.Append(request.ShowFileNames ? "; the last part of each file name is shown, never the folders" : "; file names are hidden").Append(")\n");
        text.Append("Lines: ").Append(lineCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (linesCapped)
        {
            text.Append("Capped: the export holds at most ").Append(MaxLines.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" lines, so older lines that matched were left out; the newest are here.\n");
        }

        if (bytesCapped)
        {
            text.Append("Capped: the export holds at most ").Append((MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture))
                .Append(" MB, so older lines that matched were left out; the newest are here.\n");
        }

        if (!linesCapped && !bytesCapped)
        {
            text.Append("Capped: no\n");
        }

        text.Append("Times are UTC.\n");
        text.Append("----------------------------------------\n");
        return text.ToString();
    }

    private sealed record Candidate(DateTimeOffset Time, string Level, string Area, string Message);

    private sealed class Plan(
        string fileName, int lineCount, long sizeBytes, bool truncated,
        string header, List<Candidate> candidates, int first, bool showFileNames) : LogExportPlan
    {
        public override string FileName { get; } = fileName;
        public override int LineCount { get; } = lineCount;
        public override long SizeBytes { get; } = sizeBytes;
        public override bool Truncated { get; } = truncated;
        public string Header { get; } = header;
        public List<Candidate> Candidates { get; } = candidates;
        public int First { get; } = first;
        public bool ShowFileNames { get; } = showFileNames;
    }
}
