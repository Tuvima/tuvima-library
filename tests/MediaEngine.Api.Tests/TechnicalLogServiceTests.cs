using System.Text;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Logs page's technical log reader: it reads the Engine's Serilog files newest first, filters by time, level, area
/// and text, pages with a cursor, tolerates lines it cannot understand, and reads a file the Engine is still writing.
/// </summary>
public sealed class TechnicalLogServiceTests : IDisposable
{
    private const string Ingestion = "MediaEngine.Ingestion.IngestionEngine";
    private const string Providers = "MediaEngine.Providers.Workers.RetailMatchWorker";
    private const string Security = "MediaEngine.Identity.SignInService";
    private const string Playback = "MediaEngine.Api.Services.Playback.EncodeQueueService";
    private const string Network = "MediaEngine.Api.Services.Networking.RemoteConnectivityMonitor";
    private const string Ai = "MediaEngine.AI.Features.SeriesAligner";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima-technical-log-{Guid.NewGuid():N}");
    private readonly TechnicalLogService _service;

    public TechnicalLogServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _service = new TechnicalLogService(new EngineLogLocation(_directory));
    }

    [Fact]
    public async Task Lines_ComeBackNewestFirst_WithTimeLevelAreaAndMessage()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "Scan started"),
            Entry("2026-10-19 09:05:00.000 +00:00", "WRN", Providers, "Provider was slow"),
            Entry("2026-10-19 09:10:00.000 +00:00", "ERR", Security, "Sign-in failed"));

        var page = await _service.ReadAsync(Query());

        Assert.Equal(["Sign-in failed", "Provider was slow", "Scan started"], page.Lines.Select(line => line.Message));
        var newest = page.Lines[0];
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 10, 0, TimeSpan.Zero), newest.Time);
        Assert.Equal("error", newest.Level);
        Assert.Equal("Security", newest.Area);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("error", new[] { "boom", "fatal" })]
    [InlineData("warning", new[] { "boom", "fatal", "careful" })]
    [InlineData("information", new[] { "boom", "fatal", "careful", "hello" })]
    [InlineData("all", new[] { "boom", "fatal", "careful", "hello", "debugging" })]
    [InlineData(null, new[] { "boom", "fatal", "careful", "hello", "debugging" })]
    public async Task LevelFilter_IsTheLowestLevelWanted(string? level, string[] expected)
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "DBG", Ingestion, "debugging"),
            Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "hello"),
            Entry("2026-10-19 09:02:00.000 +00:00", "WRN", Ingestion, "careful"),
            Entry("2026-10-19 09:03:00.000 +00:00", "ERR", Ingestion, "boom"),
            Entry("2026-10-19 09:04:00.000 +00:00", "FTL", Ingestion, "fatal"));

        var page = await _service.ReadAsync(Query() with { Level = level });

        Assert.Equal(expected.Order(), page.Lines.Select(line => line.Message).Order());
    }

    [Theory]
    [InlineData("Ingestion", "scan")]
    [InlineData("Providers", "match")]
    [InlineData("Security", "signin")]
    [InlineData("Playback", "encode")]
    [InlineData("Network", "ping")]
    [InlineData("AI", "align")]
    [InlineData("Other", "misc")]
    public async Task AreaFilter_GroupsLoggerNamesIntoPlainAreas(string area, string expectedMessage)
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "scan"),
            Entry("2026-10-19 09:01:00.000 +00:00", "INF", Providers, "match"),
            Entry("2026-10-19 09:02:00.000 +00:00", "INF", Security, "signin"),
            Entry("2026-10-19 09:03:00.000 +00:00", "INF", Playback, "encode"),
            Entry("2026-10-19 09:04:00.000 +00:00", "INF", Network, "ping"),
            Entry("2026-10-19 09:05:00.000 +00:00", "INF", Ai, "align"),
            Entry("2026-10-19 09:06:00.000 +00:00", "INF", "SomethingElseEntirely", "misc"));

        var page = await _service.ReadAsync(Query() with { Area = area });

        var line = Assert.Single(page.Lines);
        Assert.Equal(expectedMessage, line.Message);
        Assert.Equal(area, line.Area);
    }

    [Fact]
    public async Task TimeRange_KeepsOnlyLinesInsideIt()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 08:00:00.000 +00:00", "INF", Ingestion, "early"),
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "inside"),
            Entry("2026-10-19 10:00:00.000 +00:00", "INF", Ingestion, "late"));

        var page = await _service.ReadAsync(Query() with
        {
            From = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero),
        });

        Assert.Equal(["inside"], page.Lines.Select(line => line.Message));
    }

    [Fact]
    public async Task Search_MatchesMessageTextIgnoringCase()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "Added Dune (2021)"),
            Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "Added Arrival (2016)"));

        var page = await _service.ReadAsync(Query() with { Search = "dune" });

        Assert.Equal(["Added Dune (2021)"], page.Lines.Select(line => line.Message));
    }

    [Fact]
    public async Task AnError_KeepsItsDetailLines_InTheMessage()
    {
        WriteLog("tuvima-20261019.log",
            "2026-10-19 09:00:00.000 +00:00 [ERR] " + Ingestion + "\n"
            + "  Could not read file\n"
            + "System.IO.IOException: The file is in use\n"
            + "   at Foo.Bar()\n"
            + Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "Next"));

        var page = await _service.ReadAsync(Query());

        Assert.Equal(2, page.Lines.Count);
        Assert.Equal("Next", page.Lines[0].Message);
        Assert.Equal("Could not read file\nSystem.IO.IOException: The file is in use\n   at Foo.Bar()", page.Lines[1].Message);
    }

    [Fact]
    public async Task Paging_ContinuesAfterTheCursor_AcrossFiles_WithoutRepeatingOrSkippingLines()
    {
        WriteLog("tuvima-20261018.log",
            Entry("2026-10-18 09:00:00.000 +00:00", "INF", Ingestion, "a1"),
            Entry("2026-10-18 09:01:00.000 +00:00", "INF", Ingestion, "a2"),
            Entry("2026-10-18 09:02:00.000 +00:00", "INF", Ingestion, "a3"));
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "b1"),
            Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "b2"),
            Entry("2026-10-19 09:02:00.000 +00:00", "INF", Ingestion, "b3"));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await _service.ReadAsync(Query() with { Limit = 2, Cursor = cursor });
            Assert.True(page.Lines.Count <= 2);
            seen.AddRange(page.Lines.Select(line => line.Message));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(["b3", "b2", "b1", "a3", "a2", "a1"], seen);
    }

    [Fact]
    public async Task Paging_WithAFilter_PagesOnlyThroughTheMatches()
    {
        WriteLog("tuvima-20261019.log",
            Entry("2026-10-19 09:00:00.000 +00:00", "ERR", Ingestion, "e1"),
            Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "noise"),
            Entry("2026-10-19 09:02:00.000 +00:00", "ERR", Ingestion, "e2"),
            Entry("2026-10-19 09:03:00.000 +00:00", "INF", Ingestion, "noise"),
            Entry("2026-10-19 09:04:00.000 +00:00", "ERR", Ingestion, "e3"));

        var first = await _service.ReadAsync(Query() with { Level = "error", Limit = 2 });
        var second = await _service.ReadAsync(Query() with { Level = "error", Limit = 2, Cursor = first.NextCursor });

        Assert.Equal(["e3", "e2"], first.Lines.Select(line => line.Message));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["e1"], second.Lines.Select(line => line.Message));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task LinesItCannotUnderstand_AreToleratedAndDoNotStopTheRest()
    {
        var path = Path.Combine(_directory, "tuvima-20261019.log");
        // Stray text before the first entry, a half-written header, and bytes that are not valid text.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("leftover from a cut file\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes(Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "good one")));
        bytes.AddRange(Encoding.UTF8.GetBytes("2026-10-19 09:0"));
        bytes.AddRange([0xFF, 0xFE, 0xFA, (byte)'\n']);
        bytes.AddRange(Encoding.UTF8.GetBytes(Entry("2026-10-19 09:02:00.000 +00:00", "INF", Ingestion, "good two")));
        File.WriteAllBytes(path, [.. bytes]);

        var page = await _service.ReadAsync(Query());

        Assert.Equal(["good two", "good one"], page.Lines.Select(line => line.Message.Split('\n')[0]));
    }

    [Fact]
    public async Task AFileTheEngineIsStillWriting_CanBeRead()
    {
        var path = Path.Combine(_directory, "tuvima-20261019.log");
        // Serilog keeps the live file open for writing while others read it.
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var first = Encoding.UTF8.GetBytes(Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "while writing"));
        await writer.WriteAsync(first);
        await writer.FlushAsync();

        var page = await _service.ReadAsync(Query());

        Assert.Equal(["while writing"], page.Lines.Select(line => line.Message));

        // The writer is not blocked by the read either.
        await writer.WriteAsync(Encoding.UTF8.GetBytes(Entry("2026-10-19 09:01:00.000 +00:00", "INF", Ingestion, "still writing")));
        await writer.FlushAsync();
        var again = await _service.ReadAsync(Query());
        Assert.Equal(["still writing", "while writing"], again.Lines.Select(line => line.Message));
    }

    [Fact]
    public async Task AMissingLogFolder_GivesAnEmptyPageInsteadOfAnError()
    {
        var service = new TechnicalLogService(new EngineLogLocation(Path.Combine(_directory, "nowhere")));

        var page = await service.ReadAsync(Query());

        Assert.Empty(page.Lines);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task AnUnreadableCursor_StartsFromTheNewestLine()
    {
        WriteLog("tuvima-20261019.log", Entry("2026-10-19 09:00:00.000 +00:00", "INF", Ingestion, "only"));

        var page = await _service.ReadAsync(Query() with { Cursor = "not-a-cursor!!" });

        Assert.Equal(["only"], page.Lines.Select(line => line.Message));
    }

    [Fact]
    public async Task FilesWrittenBeforeTheRangeStarts_AreNotEvenOpened()
    {
        var path = WriteLog("tuvima-20260101.log", Entry("2026-01-01 09:00:00.000 +00:00", "INF", Ingestion, "ancient"));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        var page = await _service.ReadAsync(Query() with { From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Empty(page.Lines);
    }

    [Theory]
    [InlineData("error", true)]
    [InlineData("Errors", true)]
    [InlineData("warning", true)]
    [InlineData("information", true)]
    [InlineData("all", true)]
    [InlineData(null, true)]
    [InlineData("loud", false)]
    public void LevelWords_AreRecognised(string? level, bool recognised)
    {
        Assert.Equal(recognised, TechnicalLogService.TryParseMinimumLevel(level, out _));
    }

    [Theory]
    [InlineData("MediaEngine.Ingestion.IngestionEngine", "Ingestion")]
    [InlineData("MediaEngine.Providers.Workers.WikidataBridgeWorker", "Providers")]
    [InlineData("MediaEngine.Api.Services.ProviderActivityBroadcastService", "Providers")]
    [InlineData("MediaEngine.Api.Services.Playback.AdaptiveHlsCleanupService", "Playback")]
    [InlineData("MediaEngine.Api.Services.Networking.LocalDiscoveryHostedService", "Network")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "Network")]
    [InlineData("MediaEngine.Api.Security.AuthorizationAuditWriter", "Security")]
    [InlineData("MediaEngine.AI.Infrastructure.ModelLifecycleManager", "AI")]
    [InlineData("MediaEngine.Api.Services.ActivityPruningService", "Other")]
    [InlineData(null, "Other")]
    public void LoggerNames_AreGroupedIntoPlainAreas(string? loggerName, string expectedArea)
    {
        Assert.Equal(expectedArea, LogAreas.Classify(loggerName));
    }

    private static TechnicalLogQuery Query() => new(null, null, null, null, null, null, 100);

    private static string Entry(string timestamp, string level, string logger, string message) =>
        $"{timestamp} [{level}] {logger}\n  {message}\n";

    private string WriteLog(string name, params string[] entries)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Concat(entries), new UTF8Encoding(false));
        // A file's last write time decides whether a time range can skip it; keep it later than every line written here.
        File.SetLastWriteTimeUtc(path, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    public void Dispose() => TestTemp.DeleteDirectory(_directory);
}
