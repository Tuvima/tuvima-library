using System.Reflection;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Persons;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Search;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests.Support;

internal class EngineApiClientStub : DispatchProxy
{
    private static readonly MethodInfo TaskFromResultMethod =
        typeof(Task)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult));

    private readonly Dictionary<string, Func<object?[]?, object?>> _handlers =
        new(StringComparer.Ordinal);

    public static IEngineApiClient CreateDefault()
    {
        var proxy = Create<IEngineApiClient, EngineApiClientStub>();
        var stub = (EngineApiClientStub)(object)proxy;
        stub.RegisterDefaults();
        return proxy;
    }

    public static IEngineApiClient Create(Action<EngineApiClientStub> configure)
    {
        var proxy = Create<IEngineApiClient, EngineApiClientStub>();
        var stub = (EngineApiClientStub)(object)proxy;
        stub.RegisterDefaults();
        configure(stub);
        return proxy;
    }

    /// <summary>Every client method the code under test called, in order.</summary>
    public List<string> Calls { get; } = [];

    public void SetHandler(string methodName, Func<object?[]?, object?> handler)
    {
        _handlers[methodName] = handler;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Add(targetMethod.Name);

        if (_handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args);
        }

        return CreateDefaultValue(targetMethod.ReturnType);
    }

    private void RegisterDefaults()
    {
        _handlers[nameof(IEngineApiClient.ToAbsoluteEngineUrl)] =
            args => args?[0]?.ToString() ?? string.Empty;

        _handlers[nameof(IEngineApiClient.GetResolvedUISettingsAsync)] =
            _ => Task.FromResult<ResolvedUISettingsViewModel?>(new ResolvedUISettingsViewModel
            {
                DeviceClass = "web",
            });

        _handlers[nameof(IEngineApiClient.GetProfilesAsync)] =
            _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    "Test User",
                    "#C9922E",
                    "Administrator",
                    DateTimeOffset.UtcNow),
            });

        _handlers[nameof(IEngineApiClient.GetPlaybackSettingsAsync)] =
            args => Task.FromResult<UserPlaybackSettingsDto?>(
                UserPlaybackSettingsDto.CreateDefaults((Guid)(args?[0] ?? Guid.Parse("00000000-0000-0000-0000-000000000001"))));

        _handlers[nameof(IEngineApiClient.UpdatePlaybackSettingsAsync)] =
            args => Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto?)args?[1]);

        _handlers[nameof(IEngineApiClient.GetProfileOverviewAsync)] =
            _ => Task.FromResult<ProfileOverviewViewModel?>(new ProfileOverviewViewModel
            {
                Profile = new ProfileViewModel(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    "Test User",
                    "#C9922E",
                    "Administrator",
                    DateTimeOffset.UtcNow.AddYears(-1)),
                Stats = new ProfileOverviewStatsViewModel
                {
                    TotalItems = 3,
                    InProgress = 1,
                    Completed = 1,
                    RecentActivity = 2,
                    MediaTypeMix = new Dictionary<string, int>
                    {
                        ["Movies"] = 1,
                        ["Books"] = 1,
                        ["Music"] = 1,
                    },
                    LibraryCounts = new Dictionary<string, int>
                    {
                        ["Movies"] = 12,
                        ["Shows"] = 4,
                        ["Books"] = 8,
                    },
                    ActivityBuckets = new Dictionary<string, int>
                    {
                        ["PlaybackUpdated"] = 2,
                    },
                    TopGenres = new Dictionary<string, int>
                    {
                        ["Drama"] = 3,
                        ["Sci-Fi"] = 2,
                    },
                    ConsumedSeconds = 7200,
                    ConsumedSecondsByMediaType = new Dictionary<string, double>
                    {
                        ["Movies"] = 5400,
                        ["Books"] = 1800,
                    },
                },
                ContinueItems =
                [
                    new ProfileOverviewItemViewModel
                    {
                        AssetId = Guid.Parse("91000000-0000-0000-0000-000000000001"),
                        WorkId = Guid.Parse("91000000-0000-0000-0000-000000000101"),
                        Title = "Landman",
                        Subtitle = "S2 - E10",
                        MediaType = "TV",
                        CollectionName = "Landman",
                        Route = "/watch/player/91000000-0000-0000-0000-000000000001",
                        PositionSeconds = 1800,
                        DurationSeconds = 2800,
                        ProgressPct = 64,
                        LastAccessed = DateTimeOffset.UtcNow.AddHours(-2),
                    },
                ],
                RecentItems =
                [
                    new ProfileOverviewItemViewModel
                    {
                        AssetId = Guid.Parse("91000000-0000-0000-0000-000000000002"),
                        WorkId = Guid.Parse("91000000-0000-0000-0000-000000000102"),
                        Title = "The Substance",
                        Subtitle = "Movie",
                        MediaType = "Movies",
                        CollectionName = "Movies",
                        Route = "/watch/player/91000000-0000-0000-0000-000000000002",
                        ProgressPct = 100,
                        LastAccessed = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
                CompletedItems =
                [
                    new ProfileOverviewItemViewModel
                    {
                        AssetId = Guid.Parse("91000000-0000-0000-0000-000000000003"),
                        WorkId = Guid.Parse("91000000-0000-0000-0000-000000000103"),
                        Title = "Dexter: Resurrection",
                        Subtitle = "S1 - E10",
                        MediaType = "TV",
                        CollectionName = "Dexter",
                        Route = "/watch/player/91000000-0000-0000-0000-000000000003",
                        ProgressPct = 100,
                        LastAccessed = DateTimeOffset.UtcNow.AddDays(-4),
                    },
                ],
                RecentlyAddedItems =
                [
                    new ProfileOverviewItemViewModel
                    {
                        AssetId = Guid.Parse("91000000-0000-0000-0000-000000000004"),
                        WorkId = Guid.Parse("91000000-0000-0000-0000-000000000104"),
                        Title = "Dune: Part Two",
                        Subtitle = "Movie",
                        MediaType = "Movies",
                        ProgressPct = 0,
                        LastAccessed = DateTimeOffset.UtcNow.AddDays(-3),
                        AddedAt = DateTimeOffset.UtcNow.AddDays(-3),
                    },
                ],
                Activity =
                [
                    new ProfileOverviewActivityViewModel
                    {
                        Id = 1,
                        ActionType = "PlaybackUpdated",
                        Detail = "Continued Landman",
                        OccurredAt = DateTimeOffset.UtcNow.AddHours(-2),
                    },
                ],
                Taste = new TasteProfileDto
                {
                    UserId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    Summary = "Test profile built from a mixed library.",
                    MediaTypeMix = new Dictionary<string, double>
                    {
                        ["Books"] = 0.45,
                        ["Movies"] = 0.25,
                        ["Music"] = 0.30,
                    },
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                },
            });

        _handlers[nameof(IEngineApiClient.GetReviewCountAsync)] =
            _ => Task.FromResult<int?>(3);

        _handlers[nameof(IEngineApiClient.GetPendingReviewsAsync)] =
            _ => Task.FromResult(new List<ReviewItemViewModel>
            {
                new()
                {
                    Id = Guid.Parse("70000000-0000-0000-0000-000000000001"),
                    EntityId = Guid.Parse("70000000-0000-0000-0000-000000000101"),
                    EntityType = "Work",
                    EntityTitle = "Unmatched Album",
                    MediaType = "Music",
                    Trigger = "LowConfidence",
                    Status = "Pending",
                    ConfidenceScore = 0.42,
                    CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                    BridgeIdentifiers = new Dictionary<string, string>
                    {
                        ["isrc"] = "TEST123",
                    },
                },
            });

        // By default the attempt view of the pending list mirrors whatever GetPendingReviewsAsync is set to.
        _handlers[nameof(IEngineApiClient.GetPendingReviewsAttemptAsync)] =
            args => _handlers[nameof(IEngineApiClient.GetPendingReviewsAsync)](args) is Task<List<ReviewItemViewModel>> list
                ? list.ContinueWith(task => new ReviewListAttempt(task.Result, null), TaskScheduler.Default)
                : Task.FromResult(new ReviewListAttempt([], null));

        _handlers[nameof(IEngineApiClient.GetSystemStatusAsync)] =
            _ => Task.FromResult<SystemStatusViewModel?>(new SystemStatusViewModel
            {
                Status = "ok",
                Version = "10.0.0-test",
                Language = "en",
            });

        _handlers[nameof(IEngineApiClient.GetSystemActivityOperationsAsync)] =
            _ => Task.FromResult<IReadOnlyList<SystemActivityOperationViewModel>>([]);

        _handlers[nameof(IEngineApiClient.GetLibrariesAsync)] =
            _ => Task.FromResult<LibrariesConfigurationDto?>(new LibrariesConfigurationDto
            {
                Libraries =
                [
                    new LibraryFolderDto
                    {
                        Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                        Name = "Movies",
                        Kind = "catalogued",
                        Area = "watch",
                        Sources =
                        [
                            new LibrarySourceDto
                            {
                                Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                                Path = @"C:\Tuvima\Movies",
                            },
                        ],
                    },
                ],
            });

        _handlers[nameof(IEngineApiClient.TestPathAsync)] =
            args =>
            {
                var path = args?[0]?.ToString() ?? string.Empty;
                return Task.FromResult<PathTestResultDto?>(new PathTestResultDto(path, true, true, true));
            };

        _handlers[nameof(IEngineApiClient.GetProviderStatusAsync)] =
            _ => Task.FromResult<IReadOnlyList<ProviderStatusDto>>([
                new ProviderStatusDto(
                    Name: "open_library",
                    DisplayName: "Open Library",
                    Enabled: true,
                    IsZeroKey: true,
                    IsReachable: true),
                new ProviderStatusDto(
                    Name: "tmdb",
                    DisplayName: "TMDB",
                    Enabled: true,
                    IsZeroKey: false,
                    IsReachable: false,
                    RequiresApiKey: true,
                    HasApiKey: false),
            ]);

        _handlers[nameof(IEngineApiClient.GetAiProfileAsync)] =
            _ => Task.FromResult<HardwareProfileDto?>(new HardwareProfileDto
            {
                Tier = "starter",
                Backend = "cpu",
                AvailableRamMb = 8192,
                TokensPerSecond = 8,
            });

        _handlers[nameof(IEngineApiClient.GetAiStatusAsync)] =
            _ => Task.FromResult<AiHealthStatusDto?>(new AiHealthStatusDto
            {
                IsReady = false,
                MemoryProfile = "conservative",
                MemoryLimitMB = 3000,
                Models =
                [
                    new AiModelStatusDto
                    {
                        Role = "text_fast",
                        Supported = true,
                        State = "Ready",
                        Description = "Fast local text model",
                        ModelFile = "fast.gguf",
                        SizeMB = 750,
                        RequiredHardwareTier = "low",
                    },
                ],
            });

        _handlers[nameof(IEngineApiClient.GetAiModelStatusesAsync)] =
            _ => Task.FromResult<IReadOnlyList<AiModelStatusDto>>([
                new AiModelStatusDto
                {
                    Role = "text_fast",
                    Supported = true,
                    State = "Ready",
                    Description = "Fast local text model",
                    ModelFile = "fast.gguf",
                    SizeMB = 750,
                    DownloadUrlHost = "huggingface.co",
                    RequiredHardwareTier = "low",
                },
                new AiModelStatusDto
                {
                    Role = "text_cjk",
                    Supported = false,
                    State = "NotDownloaded",
                    Description = "Configured optional CJK model",
                    ModelFile = "cjk.gguf",
                    SizeMB = 2048,
                    RequiredHardwareTier = "medium",
                },
            ]);

        _handlers[nameof(IEngineApiClient.StartAiModelDownloadAsync)] = _ => Task.FromResult(AiOperationResultDto.Success());
        _handlers[nameof(IEngineApiClient.CancelAiModelDownloadAsync)] = _ => Task.FromResult(AiOperationResultDto.Success());
        _handlers[nameof(IEngineApiClient.LoadAiModelAsync)] = _ => Task.FromResult(AiOperationResultDto.Success());
        _handlers[nameof(IEngineApiClient.UnloadAiModelAsync)] = _ => Task.FromResult(AiOperationResultDto.Success());
        _handlers[nameof(IEngineApiClient.RunBenchmarkAsync)] = _ =>
            Task.FromResult(AiOperationResultDto<HardwareProfileDto>.Success(new HardwareProfileDto
            {
                Tier = "starter",
                Backend = "cpu",
            }));
        _handlers[nameof(IEngineApiClient.InvalidateBenchmarkAsync)] = _ =>
            Task.FromResult(AiOperationResultDto<HardwareProfileDto>.Success(new HardwareProfileDto
            {
                Outcome = "invalidated",
                Tier = "auto",
                Backend = "cpu",
            }));

        _handlers[nameof(IEngineApiClient.GetAiConfigAsync)] =
            _ => Task.FromResult<AiConfigDto?>(new AiConfigDto
            {
                ModelsDirectory = @"C:\Tuvima\Models",
                IdleUnloadSeconds = 300,
                InferenceTimeoutSeconds = 60,
                EnrichmentBatchSize = 10,
                ResourceProfile = "standard",
                AudioPackEnabled = false,
                Features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["smart_labeling"] = true,
                    ["type_logic"] = true,
                    ["series_alignment"] = true,
                    ["vibe_tags"] = true,
                    ["tldr"] = true,
                    ["description_intelligence"] = true,
                },
                VibeVocabulary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["books"] = ["atmospheric", "cozy"],
                    ["movies_tv"] = ["tense"],
                    ["music"] = ["mellow"],
                    ["comics"] = ["noir"],
                },
                Scheduling = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["vibe_batch_cron"] = "0 4 * * *",
                    ["series_check_cron"] = "0 3 * * *",
                    ["description_intelligence_cron"] = "*/15 * * * *",
                },
            });

        _handlers[nameof(IEngineApiClient.SaveAiConfigAsync)] =
            _ => Task.FromResult(true);

        _handlers[nameof(IEngineApiClient.GetResourceSnapshotAsync)] =
            _ => Task.FromResult<ResourceSnapshotDto?>(new ResourceSnapshotDto
            {
                TotalRamMb = 16384,
                FreeRamMb = 8192,
                EngineRamMb = 512,
                CpuPressure = 10,
            });

        _handlers[nameof(IEngineApiClient.GetIngestionOperationsSnapshotAsync)] =
            _ => Task.FromResult<IngestionOperationsSnapshotDto?>(new IngestionOperationsSnapshotDto
            {
                Summary = new IngestionOperationsSummaryDto
                {
                    EngineStatus = "Online",
                    HealthLabel = "Ready",
                    LastSuccessfulScanTime = null,
                },
                GeneratedAt = DateTimeOffset.UtcNow,
            });

        _handlers[nameof(IEngineApiClient.GetMediaOperationsAsync)] =
            _ => Task.FromResult<IReadOnlyList<OperationDto>>([]);
        _handlers[nameof(IEngineApiClient.GetMediaOperationAsync)] =
            _ => Task.FromResult<OperationDetailDto?>(null);
        _handlers[nameof(IEngineApiClient.GetMediaOperationsSummaryAsync)] =
            _ => Task.FromResult(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        _handlers[nameof(IEngineApiClient.RetryMediaOperationAsync)] =
            _ => Task.FromResult(true);
        _handlers[nameof(IEngineApiClient.CancelMediaOperationAsync)] =
            _ => Task.FromResult(true);
        _handlers[nameof(IEngineApiClient.GetIngestionBatchItemsAsync)] =
            _ => Task.FromResult<PagedResponse<IngestionBatchItemResponse>?>(new PagedResponse<IngestionBatchItemResponse>(
                [],
                Offset: 0,
                Limit: 100,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetActivityBatchesAsync)] =
            _ => Task.FromResult<PagedResponse<ActivityBatchSummaryDto>?>(new PagedResponse<ActivityBatchSummaryDto>(
                [],
                Offset: 0,
                Limit: 25,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetActivityBatchAsync)] =
            _ => Task.FromResult<ActivityBatchSummaryDto?>(null);
        _handlers[nameof(IEngineApiClient.GetActivityBatchGroupsAsync)] =
            _ => Task.FromResult(new List<ActivityMediaTypeGroupDto>());
        _handlers[nameof(IEngineApiClient.GetActivityBatchInsightsAsync)] =
            _ => Task.FromResult<ActivityBatchInsightsDto?>(new ActivityBatchInsightsDto());
        _handlers[nameof(IEngineApiClient.GetActivityBatchItemsAsync)] =
            _ => Task.FromResult<PagedResponse<ActivityBatchItemDto>?>(new PagedResponse<ActivityBatchItemDto>(
                [],
                Offset: 0,
                Limit: 25,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetActivityBatchEventsAsync)] =
            _ => Task.FromResult<PagedResponse<ActivityTechnicalEventDto>?>(new PagedResponse<ActivityTechnicalEventDto>(
                [],
                Offset: 0,
                Limit: 25,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetActivityBatchItemDetailAsync)] =
            _ => Task.FromResult<ActivityBatchItemDetailDto?>(null);
        _handlers[nameof(IEngineApiClient.GetActivityPeopleAsync)] =
            _ => Task.FromResult<PagedResponse<ActivityPersonAuditDto>?>(new PagedResponse<ActivityPersonAuditDto>(
                [],
                Offset: 0,
                Limit: 25,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetAssetCapabilitiesAsync)] =
            _ => Task.FromResult<IReadOnlyList<CapabilityStateDto>>([]);
        _handlers[nameof(IEngineApiClient.GetCapabilitySummaryAsync)] =
            _ => Task.FromResult(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        _handlers[nameof(IEngineApiClient.GetImportFolderWaitingAsync)] =
            _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null);

        _handlers[nameof(IEngineApiClient.TriggerRescanAsync)] =
            _ => Task.FromResult(true);

        _handlers[nameof(IEngineApiClient.GetServerGeneralAsync)] =
            _ => Task.FromResult<ServerGeneralSettingsDto?>(new ServerGeneralSettingsDto(
                ServerName: "Tuvima Library",
                Language: "en",
                DisplayLanguage: "en",
                MetadataLanguage: "en",
                AdditionalLanguages: ["fr"],
                AcceptAnyLanguage: true,
                Country: "US",
                DateFormat: "system",
                TimeFormat: "system"));

        _handlers[nameof(IEngineApiClient.GetLibraryOverviewAsync)] =
            _ => Task.FromResult<LibraryOverviewDto?>(new LibraryOverviewDto
            {
                EnrichedStage3 = 12,
                UniverseAssigned = 10,
                ArtPending = 2,
            });

        _handlers[nameof(IEngineApiClient.GetManagedCollectionsAsync)] =
            _ => Task.FromResult(new List<ManagedCollectionViewModel>
            {
                new()
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    Name = "Summer Movies",
                    Description = "Fast access to the current movie shortlist.",
                    CollectionType = "Playlist",
                    Visibility = "shared",
                    IsEnabled = true,
                    ItemCount = 12,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
                    CanEdit = true,
                    CanShare = true,
                },
                new()
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    Name = "Quiet Reads",
                    Description = "A slower reading lane for long-form titles.",
                    CollectionType = "Smart",
                    Visibility = "private",
                    IsEnabled = false,
                    ItemCount = 0,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                    CanEdit = false,
                    CanShare = false,
                },
            });

        _handlers[nameof(IEngineApiClient.GetCollectionCatalogAsync)] =
            _ => Task.FromResult(new List<CollectionManagementCatalogViewModel>
            {
                new()
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    Name = "Dune Universe",
                    Description = "A generated Wikidata-backed universe across formats.",
                    CollectionType = "Universe",
                    Family = "Global",
                    Visibility = "shared",
                    IsGlobal = true,
                    IsEnabled = true,
                    IsFeatured = true,
                    ItemCount = 12,
                    WatchCount = 4,
                    ListenCount = 3,
                    ReadCount = 5,
                    MovieCount = 3,
                    TvCount = 1,
                    BookCount = 4,
                    AudiobookCount = 1,
                    MusicCount = 2,
                    ComicCount = 1,
                    EarliestYear = 1965,
                    LatestYear = 2024,
                    PrimaryLane = "CrossMedia",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
                    CanEdit = true,
                    CanShare = true,
                    CanToggleGlobal = true,
                    CanRename = true,
                    CanDelete = true,
                    ArtworkItems =
                    [
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000001"), Title = "Dune", MediaType = "Book", CoverUrl = "/img/dune-book.jpg", ArtworkShape = "book" },
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000002"), Title = "Dune: Part Two", MediaType = "Movie", CoverUrl = "/img/dune-part-two.jpg", ArtworkShape = "poster" },
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000003"), Title = "Dune Audiobook", MediaType = "Audiobook", CoverUrl = "/img/dune-audio.jpg", ArtworkShape = "square" },
                    ],
                },
                new()
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    Name = "Middle-earth",
                    Description = "A generated universe connected from Tolkien works.",
                    CollectionType = "Universe",
                    Family = "User",
                    Visibility = "private",
                    IsEnabled = true,
                    ItemCount = 8,
                    WatchCount = 2,
                    ReadCount = 6,
                    MovieCount = 2,
                    BookCount = 6,
                    EarliestYear = 1937,
                    LatestYear = 2014,
                    PrimaryLane = "CrossMedia",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                    CanEdit = false,
                    CanShare = false,
                    ArtworkItems =
                    [
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000004"), Title = "The Hobbit", MediaType = "Book", CoverUrl = "/img/hobbit-book.jpg", ArtworkShape = "book" },
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000005"), Title = "The Fellowship of the Ring", MediaType = "Movie", CoverUrl = "/img/fellowship.jpg", ArtworkShape = "poster" },
                    ],
                },
                new()
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                    Name = "Weekend Picks",
                    Description = "A custom user collection.",
                    CollectionType = "Custom",
                    Family = "User",
                    Visibility = "private",
                    IsEnabled = true,
                    ItemCount = 4,
                    WatchCount = 2,
                    ReadCount = 2,
                    MovieCount = 2,
                    BookCount = 2,
                    EarliestYear = 1969,
                    LatestYear = 2024,
                    PrimaryLane = "CrossMedia",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-20),
                    CanEdit = true,
                    CanShare = false,
                    CanRename = true,
                    CanDelete = true,
                    ArtworkItems =
                    [
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000006"), Title = "Arrival", MediaType = "Movie", CoverUrl = "/img/arrival.jpg", ArtworkShape = "poster" },
                        new() { WorkId = Guid.Parse("20000000-0000-0000-0000-000000000007"), Title = "The Left Hand of Darkness", MediaType = "Book", CoverUrl = "/img/left-hand.jpg", ArtworkShape = "book" },
                    ],
                },
            });

        _handlers[nameof(IEngineApiClient.GetContentGroupsAsync)] =
            _ => Task.FromResult(new List<ContentGroupViewModel>
            {
                new()
                {
                    CollectionId = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                    DisplayName = "Dune",
                    PrimaryMediaType = "Books",
                    WorkCount = 6,
                    DistinctTitleCount = 6,
                    EarliestYear = 1965,
                    LatestYear = 1985,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-12),
                    PreviewItems =
                    [
                        new(Guid.Parse("41000000-0000-0000-0000-000000000001"), "Dune", "/img/dune-book.jpg", "portrait", "1"),
                        new(Guid.Parse("41000000-0000-0000-0000-000000000002"), "Dune Messiah", "/img/dune-messiah.jpg", "portrait", "2"),
                    ],
                },
                new()
                {
                    CollectionId = Guid.Parse("40000000-0000-0000-0000-000000000002"),
                    DisplayName = "The Dark Knight",
                    PrimaryMediaType = "Movies",
                    WorkCount = 3,
                    DistinctTitleCount = 3,
                    EarliestYear = 2005,
                    LatestYear = 2012,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                    PreviewItems =
                    [
                        new(Guid.Parse("42000000-0000-0000-0000-000000000001"), "Batman Begins", "/img/batman-begins.jpg", "portrait", "1"),
                        new(Guid.Parse("42000000-0000-0000-0000-000000000002"), "The Dark Knight", "/img/dark-knight.jpg", "portrait", "2"),
                    ],
                },
            });

        var catalogPersonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        _handlers[nameof(IEngineApiClient.GetPersonsPageAsync)] =
            _ => Task.FromResult<PagedResponse<PersonListItemResponse>?>(new(
                [
                    new PersonListItemResponse(
                        id: catalogPersonId,
                        name: "Octavia Butler",
                        roles: ["Author"],
                        wikidata_qid: "Q128820",
                        headshot_url: "/img/octavia-butler.jpg",
                        has_local_headshot: true,
                        is_pseudonym: false,
                        is_group: false,
                        biography: "American science fiction author.",
                        occupation: "Author"),
                ],
                Offset: 0,
                Limit: 100,
                HasMore: false));
        _handlers[nameof(IEngineApiClient.GetPersonRoleCountsAsync)] =
            _ => Task.FromResult(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Author"] = 1,
            });
        _handlers[nameof(IEngineApiClient.GetPersonPresenceAsync)] =
            _ => Task.FromResult(new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                [catalogPersonId.ToString()] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Books"] = 3,
                },
            });

        _handlers[nameof(IEngineApiClient.GetCollectionSummaryAsync)] =
            _ => Task.FromResult<CollectionManagementCatalogViewModel?>(new CollectionManagementCatalogViewModel
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Name = "Dune Universe",
                Description = "A generated Wikidata-backed universe across formats.",
                CollectionType = "Universe",
                Family = "Global",
                Visibility = "shared",
                IsGlobal = true,
                IsEnabled = true,
                IsFeatured = true,
                ItemCount = 12,
                WatchCount = 4,
                ListenCount = 3,
                ReadCount = 5,
                MovieCount = 3,
                TvCount = 1,
                BookCount = 4,
                AudiobookCount = 1,
                MusicCount = 2,
                ComicCount = 1,
                EarliestYear = 1965,
                LatestYear = 2024,
                PrimaryLane = "CrossMedia",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
                CanEdit = true,
                CanShare = true,
                CanToggleGlobal = true,
                CanRename = true,
                CanDelete = true,
            });

        _handlers[nameof(IEngineApiClient.GetDisplayHomeAsync)] =
            _ => Task.FromResult<DisplayPageDto?>(CreateDisplayPage("home", "Home"));

        _handlers[nameof(IEngineApiClient.GetDisplayBrowseAsync)] =
            args =>
            {
                var lane = args?[0]?.ToString();
                var mediaType = args?[1]?.ToString();
                var grouping = args?[2]?.ToString();
                if (string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<DisplayPageDto?>(CreateAudiobookDisplayPage());
                }

                var key = string.Equals(lane, "listen", StringComparison.OrdinalIgnoreCase)
                          && string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
                          && string.Equals(grouping, "home", StringComparison.OrdinalIgnoreCase)
                    ? "listen-music"
                    : lane ?? "browse";
                var title = key == "listen-music" ? "Music" : key;
                return Task.FromResult<DisplayPageDto?>(CreateDisplayPage(key, title));
            };

        _handlers[nameof(IEngineApiClient.SearchWorksAsync)] =
            _ => Task.FromResult(new List<SearchResultDto>());
    }

    private static DisplayPageDto CreateDisplayPage(string key, string title)
    {
        var workId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var action = new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: "/listen/music?browse=songs");
        var artwork = new DisplayArtworkDto(
            CoverUrl: "/art/test-cover.jpg",
            CoverSmallUrl: "/art/test-cover-s.jpg",
            CoverMediumUrl: "/art/test-cover-m.jpg",
            CoverLargeUrl: "/art/test-cover-l.jpg",
            SquareUrl: "/art/test-square.jpg",
            SquareSmallUrl: "/art/test-square-s.jpg",
            SquareMediumUrl: "/art/test-square-m.jpg",
            SquareLargeUrl: "/art/test-square-l.jpg",
            BannerUrl: null,
            BannerSmallUrl: null,
            BannerMediumUrl: null,
            BannerLargeUrl: null,
            BackgroundUrl: null,
            BackgroundSmallUrl: null,
            BackgroundMediumUrl: null,
            BackgroundLargeUrl: null,
            LogoUrl: null,
            CoverWidthPx: null,
            CoverHeightPx: null,
            SquareWidthPx: null,
            SquareHeightPx: null,
            BannerWidthPx: null,
            BannerHeightPx: null,
            BackgroundWidthPx: null,
            BackgroundHeightPx: null,
            AccentColor: "#1ED760");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Music",
            GroupingType: "work",
            Title: "Test Track",
            Subtitle: "Test Artist",
            Facts: ["Test Artist", "Test Album"],
            Artwork: artwork,
            PreferredShape: "square",
            Presentation: "album",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: DateTimeOffset.UtcNow);

        return new DisplayPageDto(
            Key: key,
            Title: title,
            Subtitle: null,
            Hero: new DisplayHeroDto(title, "Test Artist", "Library", artwork, null, [action]),
            Shelves: [new DisplayShelfDto("recently-added", "Recently Added", "Fresh arrivals", [card], "/listen/music/playlists/system/recently-added")],
            Catalog: [card]);
    }

    private static DisplayPageDto CreateAudiobookDisplayPage()
    {
        var workId = Guid.Parse("92000000-0000-0000-0000-000000000001");
        var action = new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: $"/listen/audiobook/{workId:D}");
        var artwork = new DisplayArtworkDto(
            CoverUrl: "/art/dune-audiobook.jpg",
            CoverSmallUrl: "/art/dune-audiobook-s.jpg",
            CoverMediumUrl: "/art/dune-audiobook-m.jpg",
            CoverLargeUrl: "/art/dune-audiobook-l.jpg",
            SquareUrl: null,
            SquareSmallUrl: null,
            SquareMediumUrl: null,
            SquareLargeUrl: null,
            BannerUrl: null,
            BannerSmallUrl: null,
            BannerMediumUrl: null,
            BannerLargeUrl: null,
            BackgroundUrl: null,
            BackgroundSmallUrl: null,
            BackgroundMediumUrl: null,
            BackgroundLargeUrl: null,
            LogoUrl: null,
            CoverWidthPx: null,
            CoverHeightPx: null,
            SquareWidthPx: null,
            SquareHeightPx: null,
            BannerWidthPx: null,
            BannerHeightPx: null,
            BackgroundWidthPx: null,
            BackgroundHeightPx: null,
            AccentColor: "#7C5CFF");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Audiobook",
            GroupingType: "work",
            Title: "Dune Audiobook",
            Subtitle: "Frank Herbert",
            Facts: ["Dune", "21h 2m"],
            Artwork: artwork,
            PreferredShape: "cover",
            Presentation: "work",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: DateTimeOffset.UtcNow.AddDays(-2));

        return new DisplayPageDto(
            Key: "audiobooks",
            Title: "Audiobooks",
            Subtitle: null,
            Hero: null,
            Shelves: [],
            Catalog: [card]);
    }

    private static object? CreateDefaultValue(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GenericTypeArguments[0];
            var result = CreateTaskResultValue(resultType);
            return TaskFromResultMethod.MakeGenericMethod(resultType).Invoke(null, [result]);
        }

        if (returnType.IsValueType)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    private static object? CreateTaskResultValue(Type resultType)
    {
        if (resultType.IsGenericType)
        {
            var openGeneric = resultType.GetGenericTypeDefinition();

            if (openGeneric == typeof(List<>)
                || openGeneric == typeof(Dictionary<,>))
            {
                return Activator.CreateInstance(resultType);
            }

            if (openGeneric == typeof(IReadOnlyList<>)
                || openGeneric == typeof(IEnumerable<>)
                || openGeneric == typeof(ICollection<>)
                || openGeneric == typeof(IList<>))
            {
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(resultType.GenericTypeArguments[0]));
            }

            if (openGeneric == typeof(IReadOnlyDictionary<,>))
            {
                return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(resultType.GenericTypeArguments));
            }
        }

        if (resultType.IsValueType)
        {
            return Activator.CreateInstance(resultType);
        }

        return null;
    }
}
