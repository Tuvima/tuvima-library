using System.Diagnostics;
using System.Text;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// A measurement (page speed round two) of what the shared-list cache saves a reader: the first read pays the full cost, a read of a
/// list that is getting old is served at once while one background reload replaces it. Prints, at 4,000 works, the milliseconds a
/// reader waits for the whole-library list (Details of a show, album or Series, Collections) and for the People credit reads, cold
/// and warm. Writes <c>.tmp/page-speed-shared-lists.md</c>. It asserts only that reads returned something.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class SharedListMeasurementTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SharedListMeasurementTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [MeasurementFact]
    public async Task SharedLists_MeasureWhatAReaderWaitsForColdAndWarm()
    {
        DapperConfiguration.Configure();
        var db = _fixture.Small;
        var time = new ManualTime();
        var service = new DisplayProjectionReadService(
            new DisplayWorkProjectionReader(db),
            new DisplayVisibleWorkReader(db),
            new DisplayJourneyProjectionReader(db),
            new DisplayFavoriteProjectionReader(db),
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            logger: null,
            time: time);

        var text = new StringBuilder("# Page speed: shared lists at 4,000 works\n\n| Read | Reader waits (ms) |\n|---|---:|\n");
        async Task<double> TimeAsync(string name, Func<Task> read)
        {
            var timer = Stopwatch.StartNew();
            await read();
            timer.Stop();
            text.AppendLine($"| {name} | {timer.Elapsed.TotalMilliseconds:N1} |");
            return timer.Elapsed.TotalMilliseconds;
        }

        IReadOnlyList<DisplayWorkRow> rows = [];
        await TimeAsync("Whole-library list (Details of a show/album/Series, Collections): first read", async () => rows = await service.LoadWorksAsync(default));
        await TimeAsync("Whole-library list: read 10 s later", async () => { time.Advance(TimeSpan.FromSeconds(10)); await service.LoadWorksAsync(default); });
        await TimeAsync("Whole-library list: read when 25 s old (old list served, reload behind it)", async () => { time.Advance(TimeSpan.FromSeconds(15)); await service.LoadWorksAsync(default); });
        Assert.NotEmpty(rows);

        var viewer = new ViewerCatalogue(rows.Select(work => new ViewerCatalogueFile(work.WorkId, work.AssetId, work.CreatedAt)));
        var people = new PersonRepository(db);
        var direct = new PersonAssetScopeReadService(people, db);
        var shared = new PersonAssetScopeReadService(people, db, new SharedListCache(new MemoryCache(new MemoryCacheOptions()), null, time));
        await TimeAsync("People role counts, credits read each time", async () => _ = await direct.GetCanonicalRoleCountsAsync(viewer, default));
        await TimeAsync("People person ids, credits read each time", async () => _ = await direct.GetCanonicalPersonIdsAsync(viewer, default));
        await TimeAsync("People role counts, credits shared: first read", async () => _ = await shared.GetCanonicalRoleCountsAsync(viewer, default));
        await TimeAsync("People person ids, credits shared: next read", async () => _ = await shared.GetCanonicalPersonIdsAsync(viewer, default));
        await TimeAsync("People role counts, credits shared: next read", async () => _ = await shared.GetCanonicalRoleCountsAsync(viewer, default));

        _output.WriteLine(text.ToString());
        var path = Path.Combine(FindRepositoryRoot(), ".tmp", "page-speed-shared-lists.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
