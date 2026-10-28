using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every place that sends an event out of the Engine (a live push, an app event, a webhook, a plugin event) either never sees a Vault
/// file or is stopped before it can say anything about one (Videos spec D17, D33, WP17 part C). Two halves: an inventory that fails
/// when a new publisher or reader appears without being reviewed, and a run of the real projection and outbox over a Vault file and
/// an ordinary one.
/// </summary>
public sealed class VaultEventPublisherInventoryTests : IDisposable
{
    private static readonly string Repo = FindRepoRoot();

    // Every source file that can hand an event to the live hub, the app and webhook outbox or the plugin host, with why it is safe.
    // A new file in any of the sets below fails the test until it is added here with a reason.
    private static readonly Dictionary<string, string> LiveHub = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Api/Services/SignalREventPublisher.cs"] =
            "The one funnel for live pushes: it projects to apps first (path and id checks) and asks IntercomAudienceAuthorizer per recipient.",
        ["src/MediaEngine.Api/Services/Events/ApplicationEventDispatcher.cs"] =
            "Reads only rows already in the application_events outbox, which refuses Vault rows by trigger and in the producer.",
    };

    private static readonly Dictionary<string, string> OutboxTouchers = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Storage/ApplicationEventRepository.cs"] = "The only writer; trg_application_events_no_vault runs on its insert.",
        ["src/MediaEngine.Api/Services/Plugins/PluginEventDispatcher.cs"] = "Reads the outbox for plugins; it cannot see a row the trigger ignored.",
        ["src/MediaEngine.Api/DevSupport/DevHarnessResetService.cs"] = "Development reset only; it deletes rows.",
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = "Names the table in the backup list only (marked as not backed up); it reads and writes nothing.",
    };

    private static readonly Dictionary<string, string> DirectProducers = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Api/Services/Events/ApplicationEventProjectionPublisher.cs"] = "Drops path events by VaultPathClassifier; id events by the producer and the trigger.",
        ["src/MediaEngine.Api/Services/Events/ApplicationEventProducer.cs"] = "Drops any draft whose library is a Vault library before it is stored.",
        ["src/MediaEngine.Storage/Playback/PlaybackTelemetryRepository.cs"] = "Playback of a Vault asset records no session and no event (checked by NotVaultAsset first).",
        ["src/MediaEngine.Api/Endpoints/PluginEndpoints.cs"] = "Publishes plugin job events with a fixed subject (a job name), never an item.",
        ["src/MediaEngine.Api/Services/Notifications/NotificationService.cs"] = "Publishes system notices with a fixed subject (a system component) through the producer, which drops any draft whose library is a Vault library; notices name no item.",
        ["src/MediaEngine.Api/DependencyInjection/ApplicationEventServiceCollectionExtensions.cs"] = "Registration only.",
        ["src/MediaEngine.Domain/Events/ApplicationEvents.cs"] = "The contract itself.",
    };

    // Every file that publishes to IEventPublisher (so to the funnel above). Each may only name events that IntercomAudienceAuthorizer
    // has classified or that are listed as free of Vault content, which IntercomAudienceVaultEventsTests proves for every constant.
    private static readonly Dictionary<string, string> EventPublishers = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.AI/Infrastructure/ModelDownloadManager.cs"] = "AI model downloads; no media.",
        ["src/MediaEngine.AI/Infrastructure/ModelLifecycleManager.cs"] = "AI model loading; no media.",
        ["src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs"] = "Item edits; events carry ids that the audience and the outbox classify.",
        ["src/MediaEngine.Api/Services/Matching/RetailMatchWriter.cs"] = "The retail-match write shared by the editor action and the edit session; its event carries ids that the audience and the outbox classify.",
        ["src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs"] = "Metadata edits; events carry ids that the audience and the outbox classify.",
        ["src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs"] = "Review actions; Vault items answer not found before any event.",
        ["src/MediaEngine.Api/Program.cs"] = "Registration only.",
        ["src/MediaEngine.Api/Services/FolderHealthService.cs"] = "Folder health events carry a path; classified by path.",
        ["src/MediaEngine.Api/Services/IngestionQueueProgressService.cs"] = "Counts only; the queue query leaves Vault files out.",
        ["src/MediaEngine.Api/Services/Libraries/LibraryItemDeletionService.cs"] = "Deletion events carry ids and a path; classified.",
        ["src/MediaEngine.Api/Services/LibraryReconciliationService.cs"] = "Reconciliation cleans Vault assets quietly and never counts them.",
        ["src/MediaEngine.Api/Services/ProviderActivityBroadcastService.cs"] = "Provider activity; no media.",
        ["src/MediaEngine.Api/Services/ProviderHealthMonitorService.cs"] = "Provider health; no media.",
        ["src/MediaEngine.Api/Services/RetagSweepWorker.cs"] = "Retag sweep counts; the sweep candidates leave Vault files out.",
        ["src/MediaEngine.Api/Services/Review/ReviewMoveToTvService.cs"] = "Review move; Vault items answer not found before it.",
        ["src/MediaEngine.Api/Services/SignalREventPublisher.cs"] = "The funnel.",
        ["src/MediaEngine.Api/Services/UniverseEnrichmentService.cs"] = "Universe enrichment; works through shared records that never include Vault works.",
        ["src/MediaEngine.Api/Services/WorkReadinessService.cs"] = "Readiness events carry work ids that the audience and the outbox classify.",
        ["src/MediaEngine.Domain/Contracts/IEventPublisher.cs"] = "The contract itself.",
        ["src/MediaEngine.Ingestion/AutoOrganizeService.cs"] = "Ingestion events carry paths; classified by path and suppressed at the source.",
        ["src/MediaEngine.Ingestion/IngestionEngine.cs"] = "Ingestion events are suppressed at the source for Vault paths.",
        ["src/MediaEngine.Ingestion/NullEventPublisher.cs"] = "Publishes nothing.",
        ["src/MediaEngine.Ingestion/Services/IngestionLogScribe.cs"] = "Ingestion log events are suppressed at the source for Vault paths.",
        ["src/MediaEngine.Ingestion/Services/InitialSweepService.cs"] = "Vault libraries are left out of the sweep (InitialSweepVaultTests).",
        ["src/MediaEngine.Providers/Helpers/StageOutcomeFactory.cs"] = "Stage outcomes carry ids that the audience classifies.",
        ["src/MediaEngine.Providers/Services/MetadataHarvestingService.cs"] = "Harvest events carry asset ids that the audience and the outbox classify.",
        ["src/MediaEngine.Providers/Workers/CoverArtWorker.cs"] = "Cover events carry asset ids that the audience classifies.",
        ["src/MediaEngine.Storage/MediaOperationTracker.cs"] = "Operation events about a Vault entity are not announced (VaultEventsAndLogsTests).",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_event_inventory_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly string _vaultFolder;
    private readonly Guid _vaultLibrary = Guid.NewGuid();
    private readonly Guid _ordinaryLibrary = Guid.NewGuid();
    private readonly Guid _vaultWork = Guid.NewGuid();
    private readonly Guid _vaultAsset = Guid.NewGuid();
    private readonly Guid _ordinaryWork = Guid.NewGuid();
    private readonly Guid _ordinaryAsset = Guid.NewGuid();

    public VaultEventPublisherInventoryTests()
    {
        Directory.CreateDirectory(_root);
        _vaultFolder = (_root + "/Media/Private").Replace('\\', '/');
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Inventory ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheFunnelAndTheOutboxReaderTouchTheLiveHub()
    {
        AssertSameSet("the live hub", FilesMatching(@"IHubContext<"), LiveHub);
    }

    [Fact]
    public void TheOutboxHasOneWriter_AndTheDataStructureStillSkipsAVaultRow()
    {
        var writers = FilesMatching(@"INSERT\s+(OR\s+\w+\s+)?INTO\s+application_events", includeSchema: false);
        Assert.Equal(["src/MediaEngine.Storage/ApplicationEventRepository.cs"], writers.Order(StringComparer.Ordinal).ToList());

        AssertSameSet("the application_events table", FilesMatching(@"application_events", includeSchema: false), OutboxTouchers);

        var upgrade = File.ReadAllText(Path.Combine(Repo, "src/MediaEngine.Storage/Schema/schema.snapshot.sql"));
        Assert.Contains("CREATE TRIGGER trg_application_events_no_vault", upgrade);
    }

    [Fact]
    public void EveryDirectProducerOfAnAppEvent_IsReviewed()
    {
        AssertSameSet("direct application event producers", FilesMatching(@"IApplicationEventProducer|ApplicationEventDraft\("), DirectProducers);
    }

    [Fact]
    public void EveryPublisherToTheEventFunnel_IsReviewed_AndNamesOnlyClassifiedEvents()
    {
        var found = FilesMatching(@"\bIEventPublisher\b");
        AssertSameSet("event publishers", found, EventPublishers);

        var known = IntercomAudienceAuthorizer.ClassifiedEvents.Concat(IntercomAudienceAuthorizer.VaultFreeEvents.Keys).ToHashSet(StringComparer.Ordinal);
        var constants = typeof(SignalREvents).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var file in found)
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(Path.Combine(Repo, file)), @"SignalREvents\.(?<name>\w+)"))
            {
                var name = match.Groups["name"].Value;
                if (!constants.TryGetValue(name, out var value) || !known.Contains(value))
                {
                    unknown.Add($"{file}: SignalREvents.{name}");
                }
            }
        }

        Assert.True(unknown.Count == 0, "These events are published but not classified for the live audience: " + string.Join(", ", unknown.Distinct()));
    }

    [Fact]
    public void PluginsAndWebhooks_ReadOnlyTheOutbox_NeverTheEventFunnel()
    {
        foreach (var file in new[]
        {
            "src/MediaEngine.Api/Services/Plugins/PluginEventDispatcher.cs",
            "src/MediaEngine.Api/Services/Webhooks/ApplicationWebhookDispatcher.cs",
            "src/MediaEngine.Api/Services/Webhooks/WebhookTransport.cs",
        })
        {
            var source = File.ReadAllText(Path.Combine(Repo, file));
            Assert.DoesNotContain("IEventPublisher", source);
            Assert.DoesNotContain("SignalREvents.", source);
            Assert.DoesNotContain("IHubContext", source);
        }
    }

    // ── The real projection and outbox ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryEventAboutAVaultFile_IsDroppedBeforeAnAppOrAWebhookCanSeeIt_ButOrdinaryOnesAreStored()
    {
        Seed();
        var vaultLibraries = new VaultLibraryRepository(_database);
        var producer = new ApplicationEventProducer(
            _database, new ApplicationEventOutboxWriter(), new ApplicationEventRegistry(new PermissionRegistry()), vaultLibraries);
        var projection = new ApplicationEventProjectionPublisher(
            producer, new ApplicationEventResourceResolver(_database), NullLogger<ApplicationEventProjectionPublisher>.Instance,
            new VaultPathClassifier(vaultLibraries, _configuration));
        var now = DateTimeOffset.UtcNow;
        var vaultFile = _vaultFolder + "/clips/one.mkv";
        var ordinaryFile = _root.Replace('\\', '/') + "/Media/Movies/Dune.mkv";

        async Task RunAsync(string path, Guid work, Guid asset, string folder)
        {
            await projection.ProjectAsync(SignalREvents.IngestionStarted, new IngestionStartedEvent(path, now), default);
            await projection.ProjectAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent(path, "Unreadable", now), default);
            await projection.ProjectAsync(SignalREvents.IngestionCompleted, new IngestionCompletedEvent(path, "Videos", now), default);
            await projection.ProjectAsync(SignalREvents.FolderHealthChanged, new FolderHealthChangedEvent(folder, true, true, true, now), default);
            await projection.ProjectAsync(SignalREvents.MediaAdded, new MediaAddedEvent(work, null, "Videos", "Title", asset), default);
            await projection.ProjectAsync(SignalREvents.MediaRemoved, new MediaRemovedEvent(asset, path, "Missing"), default);
            await projection.ProjectAsync(SignalREvents.MetadataHarvested, new MetadataHarvestedEvent(asset, "stash", ["title"]), default);
            await projection.ProjectAsync(SignalREvents.HydrationStageCompleted, new HydrationStageCompletedEvent(asset, 1, 3, "stash"), default);
            await projection.ProjectAsync(SignalREvents.ReviewItemCreated, new ReviewItemCreatedEvent(Guid.NewGuid(), work, "LowConfidence", "Title"), default);
        }

        await RunAsync(vaultFile, _vaultWork, _vaultAsset, _vaultFolder);
        using (var connection = _database.CreateConnection())
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM application_events;"));
        }

        await RunAsync(ordinaryFile, _ordinaryWork, _ordinaryAsset, _root.Replace('\\', '/') + "/Media/Movies");
        using (var connection = _database.CreateConnection())
        {
            var stored = connection.Query<(string Subject, string Payload)>("SELECT subject_id, payload_json FROM application_events;").ToList();
            Assert.NotEmpty(stored);
            foreach (var (subject, payload) in stored)
            {
                foreach (var secret in new[] { _vaultAsset.ToString("D"), _vaultWork.ToString("D"), "Private", "one.mkv" })
                {
                    Assert.DoesNotContain(secret, subject + payload, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO vault_libraries(library_id,lookup_source,adults_confirmed_at,created_at,updated_at) VALUES(@vault,'none',@now,@now,@now);",
            new { vault = _vaultLibrary.ToString("D"), now });
        foreach (var (work, asset, library, path) in new[]
        {
            (_vaultWork, _vaultAsset, _vaultLibrary, _vaultFolder + "/clips/one.mkv"),
            (_ordinaryWork, _ordinaryAsset, _ordinaryLibrary, _root.Replace('\\', '/') + "/Media/Movies/Dune.mkv"),
        })
        {
            connection.Execute(
                """
                INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,'Videos','standalone',0,'Owned');
                INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id) VALUES(@asset,@edition,@hash,@path,@library);
                """,
                new
                {
                    work = GuidSql.ToBlob(work),
                    edition = GuidSql.ToBlob(Guid.NewGuid()),
                    asset = GuidSql.ToBlob(asset),
                    hash = asset.ToString("N"),
                    path,
                    library = library.ToString("D"),
                });
        }

        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = _root.Replace('\\', '/') + "/Media", AllowWrite = true }],
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = _vaultLibrary.ToString("D"), Name = "Private", Category = "Movies",
                    Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = _vaultFolder }],
                },
                new LibraryFolderConfig
                {
                    Id = _ordinaryLibrary.ToString("D"), Name = "Movies", Category = "Movies",
                    Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = _root.Replace('\\', '/') + "/Media/Movies" }],
                },
            ],
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static void AssertSameSet(string what, IReadOnlyCollection<string> found, Dictionary<string, string> reviewed)
    {
        Assert.All(reviewed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
        var added = found.Except(reviewed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var gone = reviewed.Keys.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 0, $"New code touches {what} without a Vault review. Add it to the list with a reason: {string.Join(", ", added)}");
        Assert.True(gone.Count == 0, $"These reviewed files no longer touch {what}; remove them from the list: {string.Join(", ", gone)}");
    }

    private static List<string> FilesMatching(string pattern, bool includeSchema = true)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled);
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Repo, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.StartsWith("src/MediaEngine.Web/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!includeSchema && relative.Contains("/Schema/"))
            {
                continue;
            }

            if (regex.IsMatch(File.ReadAllText(file)))
            {
                matches.Add(relative);
            }
        }

        return matches;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
