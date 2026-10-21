using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// A measurement (not a guardrail) for bug B29, "the details overlay opens slowly". Opening a title calls
/// <c>GET /api/v1/details/{entityType}/{id}</c> (<c>DetailEndpoints.cs</c>), which reads the display projection, finds the
/// authorized assets of a work, composes the page and then marks which credits are still being enriched. This class runs those
/// steps one after another for one item of each kind of page on the 4,000 and 8,000 work libraries and prints, per stage, the
/// elapsed milliseconds (median of 5 warm runs after one warm-up) and the SQLite work in thousands of instructions (the same on
/// every run), plus the size of the JSON the Dashboard receives. It asserts nothing except that every page composed.
/// Left out because they need an HTTP request: the request authority, the per-asset access checks (the asset list is read with
/// the same query but not filtered), the authorized wrapper around the projection (the raw read is measured), the profile-aware
/// Universe link and the content-limit policy.
/// </summary>
public sealed class DetailOpenMeasurementTests : IClassFixture<LibraryListFixture>
{
    private const int WarmRuns = 5;

    private static readonly string[] StageNames =
        ["projection", "assets", "composition", "personal", "unmatched", "readiness"];

    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DetailOpenMeasurementTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        DapperConfiguration.Configure();
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public async Task OpeningEachKindOfDetailPage_MeasuresEveryStage()
    {
        var results = new List<PageMeasurement>();
        results.AddRange(await MeasureLibraryAsync(LibraryListFixture.SmallLeafWorks, _fixture.Small));
        results.AddRange(await MeasureLibraryAsync(LibraryListFixture.LargeLeafWorks, _fixture.Large));

        var table = BuildTable(results);
        _output.WriteLine(table);
        var path = Path.Combine(FindRepositoryRoot(), ".tmp", "b29-detail-measure.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, table);

        Assert.All(results, result => Assert.NotNull(result.Page));
    }

    private async Task<List<PageMeasurement>> MeasureLibraryAsync(int leafWorks, DatabaseConnection database)
    {
        var counter = new SqliteWorkCounter(database);
        var targets = FindTargets(database);
        var profileId = FindProfileId(database);
        var pipeline = new Pipeline(counter, profileId);
        var results = new List<PageMeasurement>();
        foreach (var target in targets)
        {
            results.Add(await pipeline.MeasureAsync(leafWorks, target));
        }

        return results;
    }

    // One item of each kind, found with small look-ups so the same kinds exist at both sizes.
    private static List<Target> FindTargets(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        Guid One(string sql, object? parameters = null) => connection.QueryFirst<Guid>(sql, parameters);

        var movie = One("SELECT id FROM works WHERE media_type = 'Movies' AND work_kind = 'standalone' ORDER BY rowid LIMIT 1");
        var seriesBook = One("SELECT id FROM works WHERE media_type = 'Books' AND collection_id IS NOT NULL ORDER BY rowid LIMIT 1");
        var seriesId = One("SELECT collection_id FROM works WHERE id = @id", new { id = GuidSql.ToBlob(seriesBook) });
        var show = One(
            "SELECT season.parent_work_id FROM works episode INNER JOIN works season ON season.id = episode.parent_work_id WHERE episode.id = @id",
            new { id = GuidSql.ToBlob(LibraryListFixture.TvEpisodeWorkId) });
        var album = One(
            """
            SELECT work.id FROM works work
            INNER JOIN canonical_value_arrays credit ON credit.entity_id = work.id AND credit.key = 'artist' AND credit.value = 'Featured Author'
            WHERE work.media_type = 'Music' AND work.work_kind = 'parent' LIMIT 1
            """);

        return
        [
            new("Movie", DetailEntityType.Movie, movie),
            new("Book (in a series)", DetailEntityType.Book, seriesBook),
            new("Audiobook", DetailEntityType.Audiobook, LibraryListFixture.AudiobookWorkId),
            new("TV show", DetailEntityType.TvShow, show),
            new("TV episode", DetailEntityType.TvEpisode, LibraryListFixture.TvEpisodeWorkId),
            new("Music album", DetailEntityType.MusicAlbum, album),
            new("Book series", DetailEntityType.BookSeries, seriesId),
            new("Person", DetailEntityType.Person, LibraryListFixture.FeaturedPersonId),
        ];
    }

    // The fixture has no profile of its own; a profile with play history (the year-borrowing listener) gives the personal-status
    // read real rows to look at, as a signed-in profile would have. The data store's default profile is used when it has one.
    private static Guid FindProfileId(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        var existing = connection.QueryFirstOrDefault<Guid?>("SELECT id FROM profiles ORDER BY rowid LIMIT 1");
        return existing ?? LibraryListFixture.YearBorrowingListenerId;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MediaEngine.slnx was not found above the test output.");
    }

    private static string BuildTable(List<PageMeasurement> results)
    {
        var text = new StringBuilder();
        text.AppendLine("# B29 detail open measurement");
        text.AppendLine();
        text.AppendLine($"Median of {WarmRuns} warm runs after 1 warm-up. Each cell is `ms / kilo-instructions` (thousands of SQLite virtual-machine instructions).");
        text.AppendLine("`projection` is the cold read (a fresh cache each run); for show, album and series pages it is the whole-library read (`LoadWorksAsync`), for work and person pages the one-item read (`LoadDetailWorksAsync`). The Engine caches the whole-library read for 30 seconds.");
        text.AppendLine("`composition` is the full `BuildAuthorizedAsync` with the profile; `personal` is the difference between the composition with and without the profile (personal actions); `unmatched` is the `StampUnmatchedAsync` pass re-run on the finished page (it is inside `composition`).");
        text.AppendLine();
        foreach (var size in results.Select(result => result.LeafWorks).Distinct())
        {
            text.AppendLine($"## {size:N0} leaf works");
            text.AppendLine();
            text.AppendLine("| Page | " + string.Join(" | ", StageNames) + " | total (projection + assets + composition + readiness) | JSON bytes |");
            text.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", StageNames.Length + 2)));
            foreach (var result in results.Where(result => result.LeafWorks == size))
            {
                var cells = StageNames.Select(name => result.Stages[name].ToString());
                var total = new[] { "projection", "assets", "composition", "readiness" }
                    .Select(name => result.Stages[name])
                    .Aggregate(new StageResult(0, 0), (sum, stage) => new StageResult(sum.Ms + stage.Ms, sum.Kilo + stage.Kilo));
                text.AppendLine($"| {result.Name} | {string.Join(" | ", cells)} | {total} | {result.JsonBytes:N0} |");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private sealed record Target(string Name, DetailEntityType Type, Guid Id);

    private readonly record struct StageResult(double Ms, long Kilo)
    {
        public override string ToString() => $"{Ms:F1} / {Kilo:N0}";
    }

    private sealed record PageMeasurement(
        int LeafWorks, string Name, DetailPageViewModel? Page, int JsonBytes, Dictionary<string, StageResult> Stages);

    /// <summary>The endpoint's steps, built from the same services the Engine registers, all reading through one counter.</summary>
    private sealed class Pipeline
    {
        private static readonly MethodInfo BuildAuthorized =
            typeof(DetailComposerService).GetMethod("BuildAuthorizedAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailComposerService.BuildAuthorizedAsync was not found.");

        private static readonly FieldInfo ComposerField =
            typeof(DetailComposerService).GetField("_composer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailComposerService._composer was not found.");

        private static readonly Type AuthorizationContextType = typeof(DetailComposerService).Assembly
            .GetType("MediaEngine.Api.Services.Details.Internals.DetailActionAuthorizationContext")
            ?? throw new InvalidOperationException("DetailActionAuthorizationContext was not found.");

        private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

        private readonly SqliteWorkCounter _counter;
        private readonly Guid _profileId;
        private readonly DisplayWorkProjectionReader _projectionReader;
        private readonly DetailComposerService _composer;
        private readonly ContributorReadiness _readiness;
        private readonly object _authorization;

        public Pipeline(SqliteWorkCounter counter, Guid profileId)
        {
            _counter = counter;
            _profileId = profileId;
            _projectionReader = new DisplayWorkProjectionReader(counter);
            _readiness = new ContributorReadiness(counter);
            // The endpoint resolves the signed-in person's permissions; an administrator on the desktop can manage metadata.
            _authorization = Activator.CreateInstance(AuthorizationContextType, true)!;
            AuthorizationContextType.GetProperty("CanManageMetadata")!.SetValue(_authorization, true);
            var collections = new CollectionRepository(counter);
            var browse = new CollectionBrowseReadService(
                collections, counter, Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionBrowseReadService>.Instance);
            var catalog = new CollectionCatalogReadService(
                collections,
                new SeriesManifestRepository(counter),
                new PersonRepository(counter),
                new ArtworkPaletteService(),
                new CollectionMediaLookupReadService(counter),
                counter,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionCatalogReadService>.Instance);
            _composer = new DetailComposerService(
                counter,
                new LibraryItemRepository(counter),
                new PersonRepository(counter),
                new EntityAssetRepository(counter),
                new CanonicalValueArrayRepository(counter),
                new SeriesManifestRepository(counter),
                new PersonCreditReadService(new CanonicalValueArrayRepository(counter), new PersonRepository(counter), counter),
                collectionBrowse: browse,
                collectionCatalog: catalog,
                profiles: new ProfileRepository(counter));
        }

        public async Task<PageMeasurement> MeasureAsync(int leafWorks, Target target)
        {
            // One warm-up, then the measured runs; every stage is timed inside each run and the median taken per stage.
            await RunOnceAsync(target);
            var runs = new List<Dictionary<string, StageResult>>();
            DetailPageViewModel? page = null;
            for (var run = 0; run < WarmRuns; run++)
            {
                var (stages, built) = await RunOnceAsync(target);
                runs.Add(stages);
                page = built;
            }

            var medians = StageNames.ToDictionary(
                name => name,
                name => new StageResult(
                    Median(runs.Select(stages => stages[name].Ms)),
                    (long)Median(runs.Select(stages => (double)stages[name].Kilo))));
            var bytes = page is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(page, WebOptions).Length;
            return new PageMeasurement(leafWorks, target.Name, page, bytes, medians);
        }

        private async Task<(Dictionary<string, StageResult> Stages, DetailPageViewModel? Page)> RunOnceAsync(Target target)
        {
            var ct = CancellationToken.None;
            var stages = new Dictionary<string, StageResult>();
            var isWork = target.Type is DetailEntityType.Movie or DetailEntityType.TvEpisode or DetailEntityType.Book
                or DetailEntityType.Audiobook or DetailEntityType.ComicIssue or DetailEntityType.Work;

            // 1. Projection: the endpoint reads one item's rows for a work or person and the whole library otherwise.
            // A fresh cache each run, so the whole-library read is the cold one a first open after 30 seconds pays.
            var projection = new DisplayProjectionReadService(
                _projectionReader, null!, null!, null!, null!, new MemoryCache(new MemoryCacheOptions()));
            var works = await Stage(stages, "projection", () => isWork || target.Type is DetailEntityType.Person
                ? projection.LoadDetailWorksAsync(target.Id, ct)
                : projection.LoadWorksAsync(ct));

            // 2. The authorized assets of a work: the same query as the endpoint's, without the per-asset access checks.
            IReadOnlyList<Guid>? assetIds = null;
            await Stage(stages, "assets", async () =>
            {
                if (isWork)
                {
                    using var connection = _counter.CreateConnection();
                    assetIds = (await connection.QueryAsync<Guid>(
                        """
                        SELECT ma.id
                        FROM work_owned_assets woa
                        JOIN media_assets ma ON ma.id=woa.asset_id
                        LEFT JOIN user_states us ON us.asset_id=ma.id AND us.user_id=@profileId
                        WHERE woa.work_id=@workId AND ma.status='Normal' AND ma.is_orphaned=0
                        ORDER BY woa.is_covered,
                                 CASE WHEN LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END,
                                 us.last_accessed DESC, ma.id;
                        """,
                        new { workId = GuidSql.ToBlob(target.Id), profileId = GuidSql.ToBlob(_profileId) })).AsList();
                }

                return 0;
            });

            // 3. Composition with the profile, and the same without it so the personal actions show as the difference.
            DetailPageViewModel? page = null;
            await Stage(stages, "composition", async () => page = await ComposeAsync(target, _profileId, assetIds, works, ct));
            var withoutProfileStages = new Dictionary<string, StageResult>();
            await Stage(withoutProfileStages, "composition", async () => await ComposeAsync(target, null, assetIds, works, ct));
            stages["personal"] = new StageResult(
                Math.Max(0, stages["composition"].Ms - withoutProfileStages["composition"].Ms),
                Math.Max(0, stages["composition"].Kilo - withoutProfileStages["composition"].Kilo));

            // 3b. The unmatched pass alone, re-run on the finished page (private; reached by reflection).
            var orchestrator = ComposerField.GetValue(_composer)!;
            var stamp = orchestrator.GetType().GetMethod("StampUnmatchedAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("StampUnmatchedAsync was not found.");
            await Stage(stages, "unmatched", async () =>
            {
                if (page is not null)
                {
                    await (Task)stamp.Invoke(orchestrator, [page, ct])!;
                }

                return 0;
            });

            // 4. Contributor readiness.
            await Stage(stages, "readiness", async () =>
            {
                if (page is not null)
                {
                    await _readiness.ApplyAsync(page, ct);
                }

                return 0;
            });

            return (stages, page);
        }

        private Task<DetailPageViewModel?> ComposeAsync(
            Target target, Guid? profileId, IReadOnlyList<Guid>? assetIds, IReadOnlyList<DisplayWorkRow> works, CancellationToken ct) =>
            (Task<DetailPageViewModel?>)BuildAuthorized.Invoke(_composer,
            [
                target.Type, target.Id, DetailPresentationContext.Default, ct, null, profileId, _authorization,
                assetIds, works, null, null,
            ])!;

        private async Task<T> Stage<T>(Dictionary<string, StageResult> stages, string name, Func<Task<T>> body)
        {
            _counter.Reset();
            var timer = Stopwatch.StartNew();
            var result = await body();
            timer.Stop();
            stages[name] = new StageResult(timer.Elapsed.TotalMilliseconds, _counter.KiloInstructions);
            return result;
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted[sorted.Length / 2];
        }
    }
}
