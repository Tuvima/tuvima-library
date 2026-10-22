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
/// A file in an Other audio or Other reading library goes through the real ingestion engine and a real data store, and no identity
/// work is queued for it: no retail search, no Wikidata bridge, no Needs Review item, no YouTube question. A Music library beside
/// them still queues its identity work, so the check can fail (Library setup D38).
/// </summary>
public sealed class PlainKindIngestionTests : IDisposable
{
    private readonly ImportFolderHarness _h = new();
    private readonly StubEventPublisher _publisher = new();
    private readonly CountingLookup _youtube = new();
    private readonly string _folder;

    public PlainKindIngestionTests()
    {
        _folder = Path.Combine(_h.Root, "files");
        Directory.CreateDirectory(_folder);
        _h.Options.WatchDirectories = [_folder];
        _h.Options.AutoOrganize = false;
    }

    public void Dispose() => _h.Dispose();

    // The libraries keep an "enriched" policy and the YouTube switch on, so it is the kind alone that stops the lookup.
    private void UseLibrary(string kindKey, string mediaType, string area)
    {
        _h.SetLibrary(new LibraryFolderConfig
        {
            Id = "cccccccc-0000-4000-8000-000000000001",
            Name = "Files",
            Kind = LibraryKinds.Catalogued,
            KindKey = kindKey,
            Area = area,
            MetadataPolicy = LibraryMetadataPolicies.Enriched,
            YoutubeLookup = true,
            MediaTypes = [mediaType],
            Sources = [ImportFolderHarness.AsIs("files-asis", _folder)],
        });
        _h.Options.LibraryFolders = _h.Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList();
    }

    private string Write(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "bytes of " + name);
        return path;
    }

    [Theory]
    [InlineData("other_audio", "Music", LibraryAreas.Listen, "Interview with a neighbour.mp3", MediaType.Music)]
    [InlineData("other_reading", "Books", LibraryAreas.Read, "Loose notes.epub", MediaType.Books)]
    public async Task AFileInAPlainKindLibrary_IsRegisteredButQueuesNoOutsideWorkAtAll(string kind, string mediaType, string area, string fileName, MediaType detected)
    {
        UseLibrary(kind, mediaType, area);
        var path = Write(fileName);

        await RunAsync(1, detected);

        Assert.NotNull(await _h.Assets.FindByPathRootAsync(path));
        using var conn = _h.Db.Connection.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM review_queue"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM retail_match_candidates"));
        Assert.Equal(0, _youtube.Calls);
    }

    [Fact]
    public async Task AFileInAMusicLibrary_StillQueuesItsIdentityWork_AsBefore()
    {
        UseLibrary("music", "Music", LibraryAreas.Listen);
        var path = Write("Come Together.mp3");

        await RunAsync(1, MediaType.Music);

        var asset = await _h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        using var conn = _h.Db.Connection.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs WHERE entity_id = @id", new { id = asset!.Id }));
    }

    private async Task RunAsync(int expectedAssets, MediaType detectedType)
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
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            processors.SetResultForPath(file, new ProcessorResult
            {
                FilePath = file,
                DetectedType = detectedType,
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
            new NoVaultIngestionHook(),
            operationTracker: tracker, operationRepository: operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(monitor),
            sourceMutationPolicyGate: new SourceMutationPolicyGate(),
            videoReader: reader,
            videoLookup: _youtube,
            audienceGate: new AllowingGate());

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

    private sealed class CountingLookup : IVideoOnlineLookup
    {
        public int Calls { get; private set; }

        public Task<VideoOnlineInfo?> LookupAsync(string youtubeVideoId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<VideoOnlineInfo?>(null);
        }
    }

    private sealed class AllowingGate : IProviderAudienceGate
    {
        public Task<bool> MayAskAboutItemAsync(string providerName, Guid assetOrWorkId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> MayAskAboutLibraryAsync(string providerName, string? libraryId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private async Task<int> AssetCountAsync()
    {
        using var conn = _h.Db.Connection.CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_assets;");
    }
}
