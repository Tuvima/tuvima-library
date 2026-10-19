using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The log export for bug reports: the filters decide what is in the file, every line is redacted, the preview is the
/// same work as the export, and the system activity events come from the same time range.
/// </summary>
public sealed partial class LogExportServiceTests : IDisposable
{
    private const string Ingestion = "MediaEngine.Ingestion.IngestionEngine";
    private const string Providers = "MediaEngine.Providers.Workers.RetailMatchWorker";
    private const string Security = "MediaEngine.Identity.SignInService";
    private const string ApiKey = "9f8e7d6c5b4a39281706fedcba987654";

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima-log-export-{Guid.NewGuid():N}");
    private readonly SpyEvents _events = new();
    private readonly LogExportService _service;

    public LogExportServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _service = new LogExportService(new TechnicalLogService(new EngineLogLocation(_directory)), _events, new FixedClock(Now));
    }

    [Fact]
    public async Task ErrorsFromTheLastDay_InIngestionOnly_ContainOnlyThoseLines()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-18 11:00:00.000 +00:00", "ERR", Ingestion, "too old"),
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, "ingest failed"),
            Entry("2026-10-19 09:01:00.000 +00:00", "WRN", Ingestion, "ingest slow"),
            Entry("2026-10-19 09:02:00.000 +00:00", "ERR", Providers, "provider down"),
            Entry("2026-10-19 09:03:00.000 +00:00", "INF", Ingestion, "ingest fine"));

        var result = await _service.BuildAsync(Request(from: Now.AddDays(-1), to: Now, level: LogExportLevels.Errors, areas: ["Ingestion"]));

        var lines = EntryLines(result);
        Assert.Single(lines);
        Assert.Contains("[ERR] [Ingestion] ingest failed", lines[0], StringComparison.Ordinal);
        Assert.Equal(1, result.LineCount);
    }

    [Theory]
    [InlineData(LogExportLevels.Errors, new[] { "boom" })]
    [InlineData(LogExportLevels.WarningsAndAbove, new[] { "careful", "boom" })]
    [InlineData(LogExportLevels.Everything, new[] { "hello", "careful", "boom" })]
    public async Task TheLevelChoice_KeepsThatLevelAndAbove_OldestFirst(string level, string[] expected)
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "hello"),
            Entry("2026-10-19 09:01:00.000 +00:00", "WRN", Ingestion, "careful"),
            Entry("2026-10-19 09:02:00.000 +00:00", "ERR", Ingestion, "boom"));

        var result = await _service.BuildAsync(Request(level: level, includeEvents: false));

        Assert.Equal(expected, EntryLines(result).Select(line => line[(line.IndexOf("] ", line.IndexOf("] ", StringComparison.Ordinal) + 2, StringComparison.Ordinal) + 2)..]));
    }

    [Fact]
    public async Task PreviewAndExport_AreTheSameWork_SoTheirCountsAndSizesMatch()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, "one"),
            "2026-10-19 09:01:00.000 +00:00 [ERR] " + Ingestion + "\n  two\nSystem.IO.IOException: detail\n   at Foo()\n",
            Entry("2026-10-19 09:02:00.000 +00:00", "ERR", Providers, "three"));
        var request = Request(level: LogExportLevels.Errors);

        var preview = await _service.BuildAsync(request);
        var export = await _service.BuildAsync(request);

        Assert.Equal(3, preview.LineCount);
        Assert.Equal(preview.LineCount, export.LineCount);
        Assert.Equal(preview.Content.Length, export.Content.Length);
        // Three entries, however many physical lines an error's detail takes.
        Assert.Equal(export.LineCount, EntryLines(export).Count);
        Assert.Contains("    System.IO.IOException: detail", Text(export), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiKeyInALogLine_NeverAppearsInTheExport()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Providers, $"Lookup failed: https://api.example.org/search?api_key={ApiKey}&q=dune"),
            Entry("2026-10-19 09:01:00.000 +00:00", "ERR", Providers, $"Header was Authorization: Bearer {ApiKey}"),
            Entry("2026-10-19 09:02:00.000 +00:00", "ERR", Providers, $"A bare run {ApiKey} in the text"));

        var result = await _service.BuildAsync(Request(level: LogExportLevels.Everything, showFileNames: true));

        Assert.DoesNotContain(ApiKey, Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("9f8e7d6c5b4a", Text(result), StringComparison.Ordinal);
        Assert.Equal(3, result.LineCount);
    }

    [Fact]
    public async Task PathsAndSignInDetails_AreHiddenInTheFile_ButTheFileNameSwitchKeepsTheLastPart()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, @"Could not read C:\Media\Movies\Dune.mkv"),
            Entry("2026-10-19 09:01:00.000 +00:00", "WRN", Security, "Sign-in failed for alice@example.org"));

        var hidden = Text(await _service.BuildAsync(Request()));
        var shown = Text(await _service.BuildAsync(Request(showFileNames: true)));

        Assert.DoesNotContain("Dune", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("Media", hidden, StringComparison.Ordinal);
        Assert.Contains("[path]", hidden, StringComparison.Ordinal);
        Assert.Contains("[path]/Dune.mkv", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Movies", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHeader_ListsTheFilters_AndSaysPrivateDetailsAreHidden()
    {
        WriteLog("tuvima-20261019.log", Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, "x"));

        var text = Text(await _service.BuildAsync(Request(level: LogExportLevels.Errors, areas: ["Ingestion", "AI"])));

        Assert.StartsWith("Tuvima Library log export\n", text, StringComparison.Ordinal);
        Assert.Contains("Created: 2026-10-19 12:00:00Z", text, StringComparison.Ordinal);
        Assert.Contains("Level: errors only", text, StringComparison.Ordinal);
        Assert.Contains("Areas: Ingestion, AI", text, StringComparison.Ordinal);
        Assert.Contains("Private details hidden: yes", text, StringComparison.Ordinal);
        Assert.Contains("file names are hidden", text, StringComparison.Ordinal);
        Assert.Contains("Lines: 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFile_IsUtf8WithoutAByteOrderMark_AndNamedFromTheRange()
    {
        WriteLog("tuvima-20261019.log", Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, "Café ñ 日本語"));
        var from = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

        var result = await _service.BuildAsync(Request(from: from, to: to));

        Assert.Equal("tuvima-logs-20261019-0830-20261019-1000.txt", result.FileName);
        Assert.NotEqual(0xEF, result.Content[0]);
        Assert.Contains("Café ñ 日本語", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemOrRun_KeepsOnlyLinesAndEventsThatMentionIt()
    {
        var run = Guid.NewGuid();
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, $"Run {run} failed"),
            Entry("2026-10-19 09:01:00.000 +00:00", "ERR", Ingestion, "Some other run failed"));
        _events.Entries.Add(Event(Now.AddHours(-2), "BatchCompleted", "Run done", run));
        _events.Entries.Add(Event(Now.AddHours(-2), "BatchCompleted", "Other done", Guid.NewGuid()));

        var result = await _service.BuildAsync(Request(level: LogExportLevels.Everything, itemOrRun: run.ToString()));

        var lines = EntryLines(result);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.DoesNotContain("Some other run", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain("Other done", line, StringComparison.Ordinal));
        Assert.Equal(run, _events.LastMatchId);
    }

    [Fact]
    public async Task Events_AreMergedWithLogLinesInTimeOrder_AndRedactedLikeThem()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 08:00:00.000 +00:00", "INF", Ingestion, "first log"),
            Entry("2026-10-19 10:00:00.000 +00:00", "INF", Ingestion, "last log"));
        _events.Entries.Add(Event(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), "FileAdded", @"Added C:\Media\Movies\Dune.mkv", null));

        var result = await _service.BuildAsync(Request(level: LogExportLevels.Everything));

        var lines = EntryLines(result);
        Assert.Equal(3, lines.Count);
        Assert.Contains("first log", lines[0], StringComparison.Ordinal);
        Assert.Contains("[EVT]", lines[1], StringComparison.Ordinal);
        Assert.Contains("FileAdded: Added [path]", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Dune", lines[1], StringComparison.Ordinal);
        Assert.Contains("last log", lines[2], StringComparison.Ordinal);
        Assert.Contains("Events included: yes", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_AreLeftOut_WhenSwitchedOff_OrWhenTheLevelIsAboveEverything()
    {
        WriteLog("tuvima-20261019.log", Entry("2026-10-19 08:00:00.000 +00:00", "ERR", Ingestion, "an error"));
        _events.Entries.Add(Event(Now.AddHours(-1), "FileAdded", "Added a file", null));

        var off = await _service.BuildAsync(Request(level: LogExportLevels.Everything, includeEvents: false));
        var errorsOnly = await _service.BuildAsync(Request(level: LogExportLevels.Errors, includeEvents: true));

        Assert.Equal(1, off.LineCount);
        Assert.Equal(1, errorsOnly.LineCount);
        Assert.Contains("Events included: no", Text(off), StringComparison.Ordinal);
        Assert.Contains("Events included: no", Text(errorsOnly), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsAreFilteredByAreaToo()
    {
        _events.Entries.Add(Event(Now.AddHours(-1), "ProviderOutage", "Provider was down", null));
        _events.Entries.Add(Event(Now.AddHours(-1), "BatchCompleted", "Done", null));

        var result = await _service.BuildAsync(Request(level: LogExportLevels.Everything, areas: ["Providers"]));

        var line = Assert.Single(EntryLines(result));
        Assert.Contains("ProviderOutage", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingMatching_TheFileIsJustItsHeader()
    {
        var result = await _service.BuildAsync(Request());

        Assert.Equal(0, result.LineCount);
        Assert.Empty(EntryLines(result));
        Assert.Contains("Lines: 0", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEventsAreAskedForOverTheSameRangeAsTheLogFiles()
    {
        var from = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

        await _service.BuildAsync(Request(from: from, to: to, level: LogExportLevels.Everything));

        Assert.Equal(from, _events.LastFrom);
        Assert.Equal(to, _events.LastTo);
    }

    [Theory]
    [InlineData(null, "2026-10-19T10:00:00Z", "level", "range")]
    [InlineData("2026-10-19T10:00:00Z", "2026-10-19T09:00:00Z", "level", "before its end")]
    [InlineData("2026-10-19T09:00:00Z", "2026-10-19T10:00:00Z", "loud", "Level must be")]
    public void BadRequests_AreRefusedWithASentence(string? from, string to, string level, string expectedPart)
    {
        var request = new LogExportRequest
        {
            From = from is null ? null : DateTimeOffset.Parse(from),
            To = DateTimeOffset.Parse(to),
            Level = level == "level" ? LogExportLevels.Everything : level,
        };

        var problem = LogExportService.Validate(request);

        Assert.NotNull(problem);
        Assert.Contains(expectedPart, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownArea_IsRefused()
    {
        var problem = LogExportService.Validate(Request(areas: ["Nonsense"]));

        Assert.Contains("Area must be", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealActivityStore_ReturnsEventsInTheRange_ForARunOrItem()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-log-export-{Guid.NewGuid():N}.db");
        try
        {
            DapperConfiguration.Configure();
            using var database = new DatabaseConnection(databasePath);
            database.InitializeSchema();
            var repository = new SystemActivityRepository(database);
            var run = Guid.NewGuid();
            var item = Guid.NewGuid();
            await repository.LogAsync(new SystemActivityEntry { ActionType = "Before", OccurredAt = Now.AddDays(-5) });
            await repository.LogAsync(new SystemActivityEntry { ActionType = "InRange", OccurredAt = Now.AddHours(-3), IngestionRunId = run });
            await repository.LogAsync(new SystemActivityEntry { ActionType = "ItemEvent", OccurredAt = Now.AddHours(-2), EntityId = item });
            await repository.LogAsync(new SystemActivityEntry { ActionType = "Unrelated", OccurredAt = Now.AddHours(-1) });
            await repository.LogAsync(new SystemActivityEntry { ActionType = "After", OccurredAt = Now.AddDays(3) });

            var all = await repository.GetRangeAsync(Now.AddDays(-1), Now, null, 100);
            var forRun = await repository.GetRangeAsync(Now.AddDays(-1), Now, run, 100);
            var forItem = await repository.GetRangeAsync(Now.AddDays(-1), Now, item, 100);
            var limited = await repository.GetRangeAsync(Now.AddDays(-1), Now, null, 2);

            Assert.Equal(["InRange", "ItemEvent", "Unrelated"], all.Select(entry => entry.ActionType));
            Assert.Equal(["InRange"], forRun.Select(entry => entry.ActionType));
            Assert.Equal(["ItemEvent"], forItem.Select(entry => entry.ActionType));
            Assert.Equal(2, limited.Count);
        }
        finally
        {
            TestTemp.DeleteDatabase(databasePath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z \[[A-Z]{3}\] ")]
    private static partial Regex EntryStart();

    private static string Text(LogExportResult result) => Encoding.UTF8.GetString(result.Content);

    // The entries of the file (not the header, not the indented detail lines).
    private static List<string> EntryLines(LogExportResult result) =>
        Text(result).Split('\n').Where(line => EntryStart().IsMatch(line)).ToList();

    private static LogExportRequest Request(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string level = LogExportLevels.WarningsAndAbove,
        string[]? areas = null,
        string? itemOrRun = null,
        bool showFileNames = false,
        bool includeEvents = true) =>
        new()
        {
            From = from ?? Now.AddDays(-1),
            To = to ?? Now,
            Level = level,
            Areas = [.. areas ?? []],
            ItemOrRun = itemOrRun,
            ShowFileNames = showFileNames,
            IncludeEvents = includeEvents,
        };

    private static SystemActivityEntry Event(DateTimeOffset at, string action, string detail, Guid? entity) =>
        new() { ActionType = action, Detail = detail, OccurredAt = at, EntityId = entity };

    private static string Entry(string timestamp, string level, string logger, string message) =>
        $"{timestamp} [{level}] {logger}\n  {message}\n";

    private void WriteLog(string name, params string[] entries)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Concat(entries), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose() => TestTemp.DeleteDirectory(_directory);

    private sealed class SpyEvents : ISystemActivityRangeReader
    {
        public List<SystemActivityEntry> Entries { get; } = [];
        public DateTimeOffset? LastFrom { get; private set; }
        public DateTimeOffset? LastTo { get; private set; }
        public Guid? LastMatchId { get; private set; }

        public Task<IReadOnlyList<SystemActivityEntry>> GetRangeAsync(
            DateTimeOffset from, DateTimeOffset to, Guid? matchId, int limit, CancellationToken ct = default)
        {
            (LastFrom, LastTo, LastMatchId) = (from, to, matchId);
            IEnumerable<SystemActivityEntry> found = Entries.Where(entry => entry.OccurredAt >= from && entry.OccurredAt <= to);
            if (matchId is { } id)
            {
                found = found.Where(entry => entry.EntityId == id || entry.IngestionRunId == id);
            }

            return Task.FromResult<IReadOnlyList<SystemActivityEntry>>([.. found.Take(limit)]);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
