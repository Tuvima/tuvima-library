using System.Diagnostics;
using System.Text;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// A measurement, not a guardrail (bug B31, "Collections &gt; People loads very slowly", data half). Opening the People
/// section waits for four Engine reads in parallel (the collection catalog, the content groups, <c>/persons/role-counts</c>
/// and the first <c>/persons</c> page) and then <c>/persons/presence</c> for that page. This measures, at 4,000 and 8,000
/// leaf works, the SQLite work (thousand virtual-machine instructions, the same on every run) and the median of five warm
/// runs for each read underneath, adds them up per Engine action, and writes the table to <c>.tmp/b31-people-measure.md</c>.
/// There are no budgets here: it only asserts that every read returned something.
/// <para>
/// The Engine actions need an HTTP request (<c>AuthorizedDisplayProjectionReadService</c> is internal and resolves its scope
/// from the request), so the closest public pieces are measured instead: <c>DisplayWorkProjectionReader.LoadAsync</c> is the
/// cache-miss cost of <c>LoadWorksAsync</c>; the in-memory step that service then applies for an administrator is replayed
/// by <c>FilterLikeAdministrator</c>; endpoint-only mapping (content-group tile building) is not measured.
/// </para>
/// </summary>
[Trait("Category", "Measurement")]
public sealed class PeopleSectionMeasurementTests : IClassFixture<LibraryListFixture>
{
    private const int WarmRuns = 5;
    private const int PeoplePageSize = 100;
    private const int StampedeRounds = 5;

    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PeopleSectionMeasurementTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        DapperConfiguration.Configure();
        _fixture = fixture;
        _output = output;
    }

    private sealed record Read(string Name, long KiloInstructions, double Ms);

    private sealed record SizeResult(
        int LeafWorks,
        int ProjectionRows,
        int PersonsInPage,
        int VisibleWorkRows,
        Read Works,
        Read FilterOnly,
        Read CachedWorks,
        Read VisibleWorks,
        Read PersonIds,
        Read RoleCounts,
        Read ListTitle,
        Read ListCount,
        Read Presence,
        Read CatalogBody,
        Read ContentGroupsBody,
        double StampedeLoads);

    [MeasurementFact]
    public async Task PeopleSection_MeasuresEachEngineReadAtFourAndEightThousandWorks()
    {
        var results = new List<SizeResult>
        {
            await MeasureAsync(_fixture.Small, LibraryListFixture.SmallLeafWorks),
            await MeasureAsync(_fixture.Large, LibraryListFixture.LargeLeafWorks),
        };

        var markdown = Render(results);
        _output.WriteLine(markdown);
        var path = Path.Combine(RepositoryRoot(), ".tmp", "b31-people-measure.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
        _output.WriteLine("Written to " + path);

        Assert.All(results, result =>
        {
            Assert.True(result.ProjectionRows > 0);
            Assert.True(result.PersonsInPage > 0);
        });
    }

    private static async Task<SizeResult> MeasureAsync(DatabaseConnection database, int leafWorks)
    {
        var ct = CancellationToken.None;
        var counter = new SqliteWorkCounter(database);
        var people = new PersonRepository(counter);
        var scope = new PersonAssetScopeReadService(people, counter);
        var presence = new PersonPresenceReadService(people);
        var collections = new CollectionRepository(counter);
        var browse = new CollectionBrowseReadService(collections, counter, NullLogger<CollectionBrowseReadService>.Instance);
        var catalog = new CollectionCatalogReadService(
            collections,
            new SeriesManifestRepository(counter),
            people,
            new ArtworkPaletteService(),
            new CollectionMediaLookupReadService(counter),
            counter,
            NullLogger<CollectionCatalogReadService>.Instance);
        var projection = new DisplayWorkProjectionReader(counter);
        var visible = new DisplayVisibleWorkReader(counter);

        // (a) the visible-works projection: what DisplayProjectionReadService.LoadWorksAsync runs when its 30 s cache misses.
        IReadOnlyList<DisplayWorkRow> rows = [];
        var works = await MeasureReadAsync(counter, "a. LoadWorksAsync (projection, cache miss)", async () => rows = await projection.LoadAsync(ct));
        Assert.NotEmpty(rows);

        // The in-memory step the request-scoped wrapper applies on every call, hit or miss.
        IReadOnlyList<DisplayWorkRow> filtered = [];
        var filterOnly = await MeasureReadAsync(counter, "a'. in-memory filter per call", () => { filtered = FilterLikeAdministrator(rows); return Task.CompletedTask; });
        var assetIds = filtered.Select(work => work.AssetId).ToArray();

        // The raw service with its real 30 s cache: a hit is what presence usually sees right after the list.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cached = new DisplayProjectionReadService(
            projection,
            visible,
            new DisplayJourneyProjectionReader(counter),
            new DisplayFavoriteProjectionReader(counter),
            // Never reached by LoadWorksAsync / LoadVisibleWorkIdsAsync.
            null!,
            cache);
        _ = await cached.LoadWorksAsync(ct);
        var cachedWorks = await MeasureReadAsync(counter, "a''. LoadWorksAsync (cache hit)", async () => _ = await cached.LoadWorksAsync(ct));
        var stampede = await MeasureStampedeAsync(counter, projection, visible);

        IReadOnlyList<DisplayVisibleWorkRow> visibleRows = [];
        var visibleWorks = await MeasureReadAsync(counter, "g. LoadVisibleWorkIdsAsync (light projection, cache miss)", async () => visibleRows = await visible.LoadAsync(ct));
        var visibleIds = visibleRows.Select(row => row.WorkId).ToHashSet();

        // (b), (c)
        IReadOnlySet<Guid> personIds = new HashSet<Guid>();
        var personIdsRead = await MeasureReadAsync(counter, "b. GetCanonicalPersonIdsAsync", async () => personIds = await scope.GetCanonicalPersonIdsAsync(assetIds, ct));
        Assert.NotEmpty(personIds);
        IReadOnlyDictionary<string, int> roleCounts = new Dictionary<string, int>();
        var roleCountsRead = await MeasureReadAsync(counter, "c. GetCanonicalRoleCountsAsync", async () => roleCounts = await scope.GetCanonicalRoleCountsAsync(assetIds, ct));
        Assert.NotEmpty(roleCounts);

        // (d) the first page the Dashboard asks for: default title sort, then the count sort the toolbar offers.
        IReadOnlyList<MediaEngine.Domain.Entities.Person> page = [];
        var listTitle = await MeasureReadAsync(counter, "d. ListCatalogPagedAsync (title)", async () => page = await people.ListCatalogPagedAsync(null, null, 0, PeoplePageSize + 1, null, null, ct));
        Assert.NotEmpty(page);
        var listCount = await MeasureReadAsync(counter, "d2. ListCatalogPagedAsync (count)", async () => _ = await people.ListCatalogPagedAsync(null, null, 0, PeoplePageSize + 1, null, "count", ct));

        // (e)
        var pageIds = page.Take(PeoplePageSize).Select(person => person.Id).ToList();
        IReadOnlyDictionary<string, Dictionary<string, int>> presenceResult = new Dictionary<string, Dictionary<string, int>>();
        var presenceRead = await MeasureReadAsync(counter, "e. GetPresenceAsync", async () => presenceResult = await presence.GetPresenceAsync(pageIds, ct));
        Assert.NotEmpty(presenceResult);

        // (f) the two reads the handlers do besides the shared visible-works read. No active profile (null) is the
        // unrestricted case; the Dashboard's content-group tile mapping stays inside the endpoint and is not measured.
        var catalogBody = await MeasureReadAsync(counter, "f1. CollectionCatalogReadService.GetCatalogAsync", async () => _ = await catalog.GetCatalogAsync(null, ct, visibleIds));
        var groupCount = 0;
        var contentGroupsBody = await MeasureReadAsync(counter, "f2. GetContentGroupsAsync + GetPrimaryAssetIdsAsync", async () =>
        {
            var groups = await collections.GetContentGroupsAsync(ct);
            groupCount = groups.Count;
            _ = await browse.GetPrimaryAssetIdsAsync(
                groups.SelectMany(group => group.Works).Where(work => visibleIds.Contains(work.Id)).Select(work => work.Id),
                ct);
        });
        Assert.True(groupCount >= 0);

        cache.Dispose();
        return new SizeResult(
            leafWorks,
            rows.Count,
            page.Count,
            visibleRows.Count,
            works,
            filterOnly,
            cachedWorks,
            visibleWorks,
            personIdsRead,
            roleCountsRead,
            listTitle,
            listCount,
            presenceRead,
            catalogBody,
            contentGroupsBody,
            stampede);
    }

    /// <summary>One warm-up, then <see cref="WarmRuns"/> timed runs; the SQLite work is the median of those runs.</summary>
    private static async Task<Read> MeasureReadAsync(SqliteWorkCounter counter, string name, Func<Task> read)
    {
        await read();
        var kilo = new List<long>();
        var ms = new List<double>();
        for (var run = 0; run < WarmRuns; run++)
        {
            counter.Reset();
            var stopwatch = Stopwatch.StartNew();
            await read();
            stopwatch.Stop();
            kilo.Add(counter.KiloInstructions);
            ms.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return new Read(name, Median(kilo), Median(ms));
    }

    /// <summary>
    /// Two requests that start together with a cold cache (role-counts and the list; the catalog and content groups for the
    /// light read) each find the cache empty, so both run the whole read. Returns how many times the projection ran, as the
    /// median of rounds of two simultaneous cold calls (total work divided by one call's work).
    /// </summary>
    private static async Task<double> MeasureStampedeAsync(SqliteWorkCounter counter, DisplayWorkProjectionReader projection, DisplayVisibleWorkReader visible)
    {
        counter.Reset();
        _ = await projection.LoadAsync(CancellationToken.None);
        var single = Math.Max(1, counter.KiloInstructions);

        var loads = new List<double>();
        for (var round = 0; round < StampedeRounds; round++)
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new DisplayProjectionReadService(
                projection,
                visible,
                new DisplayJourneyProjectionReader(counter),
                new DisplayFavoriteProjectionReader(counter),
                null!,
                cache);
            counter.Reset();
            using var gate = new ManualResetEventSlim(false);
            var calls = Enumerable.Range(0, 2)
                .Select(_ => Task.Run(async () =>
                {
                    gate.Wait();
                    return await service.LoadWorksAsync(CancellationToken.None);
                }))
                .ToArray();
            gate.Set();
            await Task.WhenAll(calls);
            loads.Add((double)counter.KiloInstructions / single);
        }

        return Median(loads);
    }

    // Replay of AuthorizedDisplayProjectionReadService.FilterWorks for an administrator (every row allowed): one row per
    // work, the earliest file, newest first. That class is internal, so the same steps are repeated here.
    private static IReadOnlyList<DisplayWorkRow> FilterLikeAdministrator(IReadOnlyList<DisplayWorkRow> rows) =>
        rows.GroupBy(row => row.WorkId)
            .Select(group => group.OrderBy(row => row.CreatedAt).ThenBy(row => row.AssetId).First())
            .OrderByDescending(row => row.CreatedAt)
            .ToList();

    private static string Render(IReadOnlyList<SizeResult> results)
    {
        var text = new StringBuilder();
        text.AppendLine("# B31 People section: Engine read measurement");
        text.AppendLine();
        text.AppendLine("SQLite work is thousand virtual-machine instructions (deterministic); time is the median of 5 warm runs on this machine, one read at a time (no contention).");
        text.AppendLine();

        foreach (var result in results)
        {
            text.AppendLine($"## {result.LeafWorks:N0} leaf works ({result.ProjectionRows:N0} projection rows, {result.VisibleWorkRows:N0} visible-work rows, {result.PersonsInPage} people on the first page)");
            text.AppendLine();
            text.AppendLine("| Read | SQLite k-instr | Median ms |");
            text.AppendLine("|---|---:|---:|");
            foreach (var read in new[]
            {
                result.Works, result.FilterOnly, result.CachedWorks, result.VisibleWorks, result.PersonIds, result.RoleCounts,
                result.ListTitle, result.ListCount, result.Presence, result.CatalogBody, result.ContentGroupsBody,
            })
            {
                text.AppendLine($"| {read.Name} | {read.KiloInstructions:N0} | {read.Ms:N1} |");
            }

            text.AppendLine();
            var a = result.Works;
            var filter = result.FilterOnly;
            var hit = result.CachedWorks;
            var g = result.VisibleWorks;
            // Per Engine action, every action paying a cache-miss projection (the first open, or the cache has expired).
            var roleCounts = Add("role-counts = a + a' + c", a, filter, result.RoleCounts);
            var list = Add("list = d + a + a' + b", result.ListTitle, a, filter, result.PersonIds);
            var presenceMiss = Add("presence (cache miss) = a + a' + b + e", a, filter, result.PersonIds, result.Presence);
            var presenceHit = Add("presence (cache hit) = hit + a' + b + e", hit, filter, result.PersonIds, result.Presence);
            var catalog = Add("catalog = g + f1", g, result.CatalogBody);
            var groups = Add("content groups = g + f2", g, result.ContentGroupsBody);

            text.AppendLine("| Engine action | SQLite k-instr | Ms |");
            text.AppendLine("|---|---:|---:|");
            foreach (var total in new[] { catalog, groups, roleCounts, list, presenceMiss, presenceHit })
            {
                text.AppendLine($"| {total.Name} | {total.KiloInstructions:N0} | {total.Ms:N1} |");
            }

            var first = new[] { catalog, groups, roleCounts, list };
            text.AppendLine();
            text.AppendLine($"- Time to first list, parallel (max of catalog, groups, role-counts, list) + presence after a cache hit: {first.Max(read => read.Ms) + presenceHit.Ms:N0} ms ({first.Max(read => read.Ms):N0} + {presenceHit.Ms:N0}).");
            text.AppendLine($"- Same, with presence also missing the cache: {first.Max(read => read.Ms) + presenceMiss.Ms:N0} ms.");
            text.AppendLine($"- Same, if the four requests do not overlap (sum, e.g. one busy core): {first.Sum(read => read.Ms) + presenceHit.Ms:N0} ms.");
            text.AppendLine($"- Times the visible-works projection (a) runs on one cold page open: role-counts 1, list 1, presence 1 (0 if inside the 30 s cache); measured {result.StampedeLoads:N1} runs when two cold requests start together (the cache has no lock). The light read (g) runs once each for the catalog and the content groups.");
            text.AppendLine($"- Total projection work on a cold page open: {2 * a.Ms:N0} ms of (a) across role-counts and list, plus {2 * g.Ms:N0} ms of (g) across catalog and groups.");
            text.AppendLine();
        }

        return text.ToString();
    }

    private static Read Add(string name, params Read[] reads) =>
        new(name, reads.Sum(read => read.KiloInstructions), reads.Sum(read => read.Ms));

    private static long Median(List<long> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[sorted.Count / 2];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MediaEngine.slnx was not found above the test output folder.");
    }
}
