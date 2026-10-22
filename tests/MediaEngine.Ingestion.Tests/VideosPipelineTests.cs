using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Ingestion.Videos;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Models;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A Videos folder through the real ingestion engine and a real data store: placement, titles and "how identified" are
/// recorded, and no identity (outside lookup) work is queued for any video.
/// </summary>
public sealed class VideosPipelineTests : IDisposable
{
    private readonly ImportFolderHarness _h = new();
    private readonly string _videos;

    public VideosPipelineTests()
    {
        _videos = Path.Combine(_h.Root, "videos");
        Directory.CreateDirectory(_videos);
        _h.SetLibrary(new LibraryFolderConfig
        {
            Id = "bbbbbbbb-0000-4000-8000-000000000001",
            Name = "Videos",
            Kind = LibraryKinds.Catalogued,
            KindKey = "other_videos",
            Area = LibraryAreas.Watch,
            MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
            MediaTypes = ["Videos"],
            Sources = [ImportFolderHarness.AsIs("videos-asis", _videos)],
        });
        _h.Options.LibraryFolders = _h.Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList();
        _h.Options.WatchDirectories = [_videos];
        _h.Options.AutoOrganize = false;
    }

    public void Dispose() => _h.Dispose();

    private string Write(string relative)
    {
        var path = Path.Combine(_videos, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "bytes of " + relative);
        return path;
    }

    [Fact]
    public async Task ACourseAndALooseVideoAreRegisteredWithPlacementAndNoIdentityWork()
    {
        var loose = Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var a = Write("Python Course/01 - Basics/01 - Hello.mp4");
        var b = Write("Python Course/01 - Basics/02 - Variables.mp4");
        var c = Write("Python Course/02 - Advanced/01 - Classes.mp4");

        await RunAsync(4);

        using var conn = _h.Db.Connection.CreateConnection();
        var types = conn.Query<string>("SELECT DISTINCT w.media_type FROM works w").ToList();
        Assert.Equal(["Videos"], types);

        // One series, two sections, three episodes, one standalone.
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent' AND parent_work_id IS NULL"));
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent' AND parent_work_id IS NOT NULL"));
        Assert.Equal(3, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'child'"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'standalone'"));

        // The series and sections carry their titles.
        var parentTitles = conn.Query<string>("""
            SELECT cv.value FROM canonical_values cv
            JOIN works w ON w.id = cv.entity_id
            WHERE w.work_kind = 'parent' AND cv.key = 'title'
            ORDER BY cv.value
            """).ToList();
        Assert.Equal(["Advanced", "Basics", "Python Course"], parentTitles);

        // How each was identified, and the lookup is never queued.
        var looseAsset = await _h.Assets.FindByPathRootAsync(loose);
        var looseValues = await _h.Canonicals.GetByEntityAsync(looseAsset!.Id);
        Assert.Equal("youtube_id", looseValues.First(v => v.Key == "video_identified_by").Value);
        Assert.Equal("Holiday clip", looseValues.First(v => v.Key == "title").Value);
        var bAsset = await _h.Assets.FindByPathRootAsync(b);
        var bValues = await _h.Canonicals.GetByEntityAsync(bAsset!.Id);
        Assert.Equal("file_name", bValues.First(v => v.Key == "video_identified_by").Value);
        Assert.Equal("Variables", bValues.First(v => v.Key == "title").Value);
        Assert.Equal("2", bValues.First(v => v.Key == "video_episode_index").Value);
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs"));
        Assert.NotNull(await _h.Assets.FindByPathRootAsync(a));
        Assert.NotNull(await _h.Assets.FindByPathRootAsync(c));
    }

    private async Task RunAsync(int expectedAssets)
    {
        var db = _h.Db.Connection;
        var publisher = new StubEventPublisher();
        var workRepo = new WorkRepository(db);
        var operationRepo = new MediaOperationRepository(db);
        var tracker = new MediaOperationTracker(operationRepo, new MediaOperationEventRepository(db), publisher, NullLogger<MediaOperationTracker>.Instance);
        var options = _h.Options;
        using var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(20),
            MaxProbeAttempts = 2,
            MaxProbeDelay = TimeSpan.FromMilliseconds(50),
        });
        var monitor = new OptionsMonitorStub<IngestionOptions>(options);
        var processors = new TestProcessorLibraryItem();
        foreach (var file in Directory.EnumerateFiles(_videos, "*", SearchOption.AllDirectories))
        {
            processors.SetResultForPath(file, new ProcessorResult
            {
                FilePath = file,
                DetectedType = MediaType.Unknown,
                Claims = [new ExtractedClaim { Key = "title", Value = Path.GetFileNameWithoutExtension(file), Confidence = 0.5 }],
            });
        }

        var reader = new VideoLibraryReader(
            new VideoNamingConfigurationProvider(() => null), workRepo, _h.Canonicals);
        using var engine = new IngestionEngine(
            new StubFileWatcher(), debounce, new TestAssetHasher(), processors,
            new PriorityCascadeEngine(new StubConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance),
            new StubFileOrganizer(), Enumerable.Empty<IMetadataTagger>(), _h.Assets, new InlineBackgroundWorker(), publisher,
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<IngestionEngine>.Instance,
            new MetadataClaimRepository(db), _h.Canonicals, new StubRecursiveIdentity(),
            new MediaEntityChainFactory(db, new HierarchyResolver(workRepo, null, new BookFormatSiblingFinder(db))),
            _h.Reviews, _h.Activity, new StubReconciliation(), new OrganizationGate(new ScoringConfiguration()),
            new IngestionLogRepository(db), new StubSmartLabeler(), new StubMediaTypeAdvisor(), new StubEntityTimelineRepository(),
            new ScoringConfiguration(), _h.Jobs,
            new MediaTypeResolver(monitor, new LibraryFolderResolver(monitor), new StubMediaTypeAdvisor(),
                new MediaTypeExtensionCatalog(new StubConfigurationLoader()), NullLogger<MediaTypeResolver>.Instance),
            new DuplicateResolver(_h.Assets),
            new IngestionLogScribe(new IngestionLogRepository(db), publisher, NullLogger<IngestionLogScribe>.Instance),
            new HashDedupeStageDependencies(null), new ScoreIdentifyStageDependencies(null), new OrganizeStageDependencies(null),
            new WriteBackStageDependencies(new EntityAssetRepository(db), new AssetPathService(_h.LibraryRoot), workRepo, null),
            new IdentityJobStageDependencies(new IdentityPipelineSignal()),
            operationTracker: tracker, operationRepository: operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(monitor),
            sourceMutationPolicyGate: new SourceMutationPolicyGate(),
            videoReader: reader);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await engine.StartAsync(cts.Token);
            while (!cts.IsCancellationRequested && await AssetCountAsync() < expectedAssets)
            {
                await Task.Delay(100, cts.Token);
            }

            // Placement is recorded just after registration; give it a moment to finish.
            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The assertions say whether the wait ran out.
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await engine.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // A slow stop is acceptable in a test.
            }
        }
    }

    private async Task<int> AssetCountAsync()
    {
        using var conn = _h.Db.Connection.CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_assets;");
    }
}
