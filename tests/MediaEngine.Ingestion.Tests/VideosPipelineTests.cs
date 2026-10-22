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
    private readonly StubEventPublisher _publisher = new();
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

    // The library's kind decides the YouTube lookup (Library setup D17, D18, D38): only a YouTube library asks, and a plain kind never does.
    private const string KindlessLibraryId = "bbbbbbbb-0000-4000-8000-000000000001";

    private void UseKind(string kindKey, bool youtubeLookupField)
    {
        _h.SetLibrary(new LibraryFolderConfig
        {
            Id = KindlessLibraryId,
            Name = "Videos",
            Kind = LibraryKinds.Catalogued,
            KindKey = kindKey,
            Area = LibraryAreas.Watch,
            MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
            YoutubeLookup = youtubeLookupField,
            MediaTypes = ["Videos"],
            Sources = [ImportFolderHarness.AsIs("videos-asis", _videos)],
        });
        _h.Options.LibraryFolders = _h.Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList();
    }

    [Fact]
    public async Task AVideoInAVaultLibrary_IsNeverLookedUpOnYoutube_EvenInAYoutubeLibraryWithTheSwitchOn()
    {
        UseKind("youtube", youtubeLookupField: true);
        Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: false));

        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public async Task AVideoInAYoutubeLibrary_IsLookedUpOnYoutube_WhenTheGateAllows()
    {
        UseKind("youtube", youtubeLookupField: true);
        Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true));

        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task AVideoInAYoutubeLibrary_IsLookedUpOnYoutube_EvenWhenTheLibraryFieldSaysOff()
    {
        UseKind("youtube", youtubeLookupField: false);
        Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true));

        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task AVideoInAnOtherVideosLibrary_IsNeverLookedUpOnYoutube_EvenWhenTheLibraryFieldSaysTrueAndTheGateAllows()
    {
        UseKind("other_videos", youtubeLookupField: true);
        var path = Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true));

        Assert.Equal(0, lookup.Calls);
        // The ID in the name is still read locally; only the outside question is never asked.
        var asset = await _h.Assets.FindByPathRootAsync(path);
        var values = await _h.Canonicals.GetByEntityAsync(asset!.Id);
        Assert.Equal("youtube_id", values.First(v => v.Key == "video_identified_by").Value);
    }

    [Fact]
    public async Task AVideoInALibraryWithNoKind_FollowsItsOwnYoutubeSwitch_AsItAlwaysHas()
    {
        UseKind("", youtubeLookupField: true);
        Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var on = new CountingLookup();
        await RunAsync(1, on, new FixedGate(allowed: true));
        Assert.Equal(1, on.Calls);
    }

    [Fact]
    public async Task AVideoInALibraryWithNoKindAndTheSwitchOff_IsNotLookedUpOnYoutube()
    {
        UseKind("", youtubeLookupField: false);
        Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var off = new CountingLookup();
        await RunAsync(1, off, new FixedGate(allowed: true));
        Assert.Equal(0, off.Calls);
    }

    // ── Unmatched: a YouTube library file with no YouTube ID (Library setup D18, D34) ──────────────────

    [Fact]
    public async Task AFileInAYoutubeLibraryWithAnIdInItsName_IsLookedUp_AndIsNotMarkedUnmatched()
    {
        UseKind("youtube", youtubeLookupField: true);
        var path = Write("Holiday clip [dQw4w9WgXcQ].mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true), marker: NewMarker());

        Assert.Equal(1, lookup.Calls);
        var asset = await _h.Assets.FindByPathRootAsync(path);
        Assert.Null(await new UnmatchedAssetRepository(_h.Db.Connection).GetAsync(asset!.Id));
    }

    [Fact]
    public async Task AFileInAYoutubeLibraryWithAnIdInItsInfoJson_IsNotMarkedUnmatched()
    {
        UseKind("youtube", youtubeLookupField: true);
        var path = Write("Holiday clip.mp4");
        File.WriteAllText(Path.ChangeExtension(path, ".info.json"), "{\"id\":\"dQw4w9WgXcQ\",\"title\":\"Holiday clip\"}");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true), marker: NewMarker());

        Assert.Equal(1, lookup.Calls);
        var asset = await _h.Assets.FindByPathRootAsync(path);
        Assert.Null(await new UnmatchedAssetRepository(_h.Db.Connection).GetAsync(asset!.Id));
    }

    [Fact]
    public async Task AFileInAYoutubeLibraryWithNoId_IsMarkedUnmatched_StaysInPlace_AndIsNeverLookedUp()
    {
        UseKind("youtube", youtubeLookupField: true);
        var path = Write("Birthday party.mp4");
        var lookup = new CountingLookup();

        await RunAsync(1, lookup, new FixedGate(allowed: true), marker: NewMarker());

        Assert.Equal(0, lookup.Calls);
        Assert.True(File.Exists(path), "the file is not moved or renamed");
        var asset = await _h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        var row = await new UnmatchedAssetRepository(_h.Db.Connection).GetAsync(asset.Id);
        Assert.NotNull(row);
        Assert.Equal(UnmatchedReasons.NoYoutubeId, row.Reason);
        Assert.Equal(KindlessLibraryId, row.LibraryId);
        Assert.False(row.KeptAsIs);
        Assert.Empty(await _h.Reviews.GetPendingByEntityAsync(asset.Id));
        Assert.Null(await _h.Jobs.GetByEntityAsync(asset.Id));
    }

    [Theory]
    [InlineData("other_videos")]
    [InlineData("")]
    public async Task AFileWithNoYoutubeIdInOtherVideosOrAKindlessLibrary_IsNeverMarkedUnmatched(string kindKey)
    {
        UseKind(kindKey, youtubeLookupField: true);
        var path = Write("Birthday party.mp4");

        await RunAsync(1, new CountingLookup(), new FixedGate(allowed: true), marker: NewMarker());

        var asset = await _h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        Assert.Null(await new UnmatchedAssetRepository(_h.Db.Connection).GetAsync(asset.Id));
    }

    private UnmatchedFileMarker NewMarker() =>
        new(new UnmatchedAssetRepository(_h.Db.Connection), _h.Config, NullLogger<UnmatchedFileMarker>.Instance);

    // ── The Vault's part of ingestion (Videos spec WP12, D32) ───────────────────────────────────

    private const string VaultLibraryId = "bbbbbbbb-0000-4000-8000-000000000001";

    [Fact]
    public async Task AFileInAVaultLibrary_IsHandedToTheVaultHookOnce_AndAnnouncesNothingToOpenScreens()
    {
        var path = Write("Quiet clip.mp4");
        var hook = new RecordingVaultHook(VaultLibraryId);

        await RunAsync(1, vaultHook: hook);

        var registered = Assert.Single(hook.Registered);
        Assert.Equal(path, registered.FilePath);
        Assert.Equal(VaultLibraryId, registered.LibraryId);
        Assert.Equal((await _h.Assets.FindByPathRootAsync(path))!.Id, registered.AssetId);
        Assert.DoesNotContain(_publisher.Published, p => p.EventName.StartsWith("Ingestion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileInAVaultLibrary_NeverGetsANeedsReviewItem_AndAPictureBesideItStaysOutOfTheSharedArtwork()
    {
        Write("Quiet clip.mp4");
        Write("Quiet clip.jpg");
        var hook = new RecordingVaultHook(VaultLibraryId);

        await RunAsync(1, vaultHook: hook);

        using var conn = _h.Db.Connection.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM review_queue"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE key IN ('cover', 'cover_state', 'cover_url')"));
        Assert.NotEmpty(hook.Registered);
    }

    [Fact]
    public async Task AFileInANormalLibrary_IsNeverHandedToTheVaultHook_AndStillAnnouncesItsProgress()
    {
        Write("Ordinary clip.mp4");
        var hook = new RecordingVaultHook("00000000-0000-4000-8000-00000000dead");

        await RunAsync(1, vaultHook: hook);

        Assert.Empty(hook.Registered);
        Assert.Contains(_publisher.Published, p => p.EventName.StartsWith("Ingestion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVaultHookThatFails_NeverFailsIngestion()
    {
        var path = Write("Quiet clip.mp4");
        var hook = new RecordingVaultHook(VaultLibraryId) { Fail = true };

        await RunAsync(1, vaultHook: hook);

        Assert.NotNull(await _h.Assets.FindByPathRootAsync(path));
    }

    [Fact]
    public async Task ADryRun_NeverListsOrReadsAVaultFile_ButListsTheSameFileInAnOrdinaryLibrary()
    {
        var path = Write("Quiet clip.mp4");
        IReadOnlyList<PendingOperation>? ordinary = null;
        IReadOnlyList<PendingOperation>? vault = null;

        await RunAsync(1, probe: async engine => ordinary = await engine.DryRunAsync(_videos));
        await RunAsync(1, vaultHook: new RecordingVaultHook(VaultLibraryId), probe: async engine => vault = await engine.DryRunAsync(_videos));

        Assert.Contains(ordinary!, operation => operation.SourcePath == path);
        Assert.NotNull(vault);
        Assert.Empty(vault!);
    }

    private sealed class RecordingVaultHook(string vaultLibraryId) : IVaultIngestionHook
    {
        public List<VaultRegisteredFile> Registered { get; } = [];

        public bool Fail { get; init; }

        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(libraryId, vaultLibraryId, StringComparison.OrdinalIgnoreCase));

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult(Registered.Any(file => file.AssetId == assetId));

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default)
        {
            Registered.Add(file);
            return Fail ? throw new InvalidOperationException("The lookup fell over.") : Task.CompletedTask;
        }
    }

    private sealed class CountingLookup : IVideoOnlineLookup
    {
        public int Calls { get; private set; }

        public Task<VideoOnlineInfo?> LookupAsync(string youtubeVideoId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<VideoOnlineInfo?>(null);
        }
    }

    private sealed class FixedGate(bool allowed) : IProviderAudienceGate
    {
        public Task<bool> MayAskAboutItemAsync(string providerName, Guid assetOrWorkId, CancellationToken ct = default) => Task.FromResult(allowed);

        public Task<bool> MayAskAboutLibraryAsync(string providerName, string? libraryId, CancellationToken ct = default) => Task.FromResult(allowed);
    }

    private async Task RunAsync(
        int expectedAssets, IVideoOnlineLookup? lookup = null, IProviderAudienceGate? gate = null, IVaultIngestionHook? vaultHook = null,
        Func<IIngestionEngine, Task>? probe = null, UnmatchedFileMarker? marker = null)
    {
        var db = _h.Db.Connection;
        var publisher = _publisher;
        var workRepo = new WorkRepository(db);
        var operationRepo = new MediaOperationRepository(db);
        var tracker = new MediaOperationTracker(operationRepo, new MediaOperationEventRepository(db), publisher, NullLogger<MediaOperationTracker>.Instance, new VaultLibraryRepository(db));
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
            vaultHook ?? new NoVaultIngestionHook(),
            operationTracker: tracker, operationRepository: operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(monitor),
            sourceMutationPolicyGate: new SourceMutationPolicyGate(),
            videoReader: reader,
            videoLookup: lookup,
            audienceGate: gate,
            unmatchedMarker: marker);

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
            if (probe is not null)
            {
                await probe(engine);
            }
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
