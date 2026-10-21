using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Llama;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Hand-written <see cref="IAiRuntimeStatus"/>: answers whether the native runtimes load, without touching files.</summary>
internal sealed class StubRuntimeStatus(bool textLoads = true) : IAiRuntimeStatus
{
    public bool TextRuntimeLoads { get; set; } = textLoads;

    public bool AudioRuntimeLoads { get; set; }

    public int RefreshCalls { get; private set; }

    public bool RefreshTextRuntime()
    {
        RefreshCalls++;
        return TextRuntimeLoads;
    }
}

/// <summary>Hand-written admission rules: <see cref="Reason"/> is why background work may not start now, or null.</summary>
internal sealed class StubAdmissionRules : IAiAdmissionRules
{
    public string? Reason { get; set; }

    public event Action? InteractiveTrafficStarted;

    public string? WhyNotNow(AiModelRole role) => Reason;

    public void RaiseInteractiveTraffic() => InteractiveTrafficStarted?.Invoke();
}

/// <summary>A configuration loader that only knows the text recognition settings.</summary>
internal sealed class OcrOnlyConfigurationLoader : IConfigurationLoader
{
    public OcrSettings Ocr { get; set; } = new();

    public OcrSettings LoadOcr() => Ocr;

    public string ConfigDirectoryPath => string.Empty;
    public CoreConfiguration LoadCore() => new();
    public void SaveCore(CoreConfiguration config) { }
    public ScoringSettings LoadScoring() => new();
    public void SaveScoring(ScoringSettings settings) { }
    public MaintenanceSettings LoadMaintenance() => new();
    public void SaveMaintenance(MaintenanceSettings settings) { }
    public HydrationSettings LoadHydration() => new();
    public void SaveHydration(HydrationSettings settings) { }
    public PipelineConfiguration LoadPipelines() => new();
    public void SavePipelines(PipelineConfiguration config) { }
    public DisambiguationSettings LoadDisambiguation() => new();
    public void SaveDisambiguation(DisambiguationSettings settings) { }
    public MediaTypeConfiguration LoadMediaTypes() => new();
    public void SaveMediaTypes(MediaTypeConfiguration config) { }
    public TranscodingSettings LoadTranscoding() => new();
    public void SaveTranscoding(TranscodingSettings settings) { }
    public LibrariesConfiguration LoadLibraries() => new();
    public FieldPriorityConfiguration LoadFieldPriorities() => new();
    public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
    public ProviderConfiguration? LoadProvider(string name) => null;
    public void DeleteProvider(string name) { }
    public void SaveProvider(ProviderConfiguration config) { }
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
    public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    public T? LoadAi<T>() where T : class => default;
    public void SaveAi<T>(T settings) where T : class { }
    public PaletteConfiguration LoadPalette() => new();
    public void SavePalette(PaletteConfiguration palette) { }
}

/// <summary>
/// Hand-written text inference stand-in. By default it answers "reply:" plus the prompt at once; set
/// <see cref="Handler"/> to hold a call open, fail it, or wait for cancellation. Every call is recorded in served order.
/// </summary>
internal sealed class ScriptedTextInference : ITextInferenceService
{
    private int _active;
    private int _maximumConcurrency;

    public Func<string, CancellationToken, Task<InferenceOutcome<string>>> Handler { get; set; } =
        (prompt, _) => Task.FromResult(Success("reply:" + prompt));

    public List<string> ServedPrompts { get; } = [];

    public List<(AiModelRole Role, InferenceRequestOptions? Options)> Calls { get; } = [];

    public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

    public static InferenceOutcome<string> Success(string value) =>
        new(InferenceOutcomeStatus.Success, value, null, TimeSpan.FromMilliseconds(5));

    public static InferenceOutcome<string> Failure(InferenceOutcomeStatus status, string error) =>
        new(status, null, error, TimeSpan.FromMilliseconds(5));

    public Task<string> InferAsync(AiModelRole role, string prompt, string? gbnfGrammar = null, CancellationToken ct = default) =>
        throw new NotSupportedException("The engine room uses the outcome methods.");

    public Task<T?> InferJsonAsync<T>(AiModelRole role, string prompt, string gbnfGrammar, CancellationToken ct = default) where T : class =>
        throw new NotSupportedException("The engine room uses the outcome methods.");

    public async Task<InferenceOutcome<string>> InferWithOutcomeAsync(
        AiModelRole role,
        string prompt,
        string? gbnfGrammar = null,
        InferenceRequestOptions? options = null,
        CancellationToken ct = default)
    {
        lock (ServedPrompts)
        {
            ServedPrompts.Add(prompt);
            Calls.Add((role, options));
        }

        var active = Interlocked.Increment(ref _active);
        InterlockedMax(ref _maximumConcurrency, active);
        try
        {
            return await Handler(prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(InferenceOutcomeStatus.Cancelled, "Inference was cancelled.");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async Task<InferenceOutcome<T>> InferJsonWithOutcomeAsync<T>(
        AiModelRole role,
        string prompt,
        string gbnfGrammar,
        InferenceRequestOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        var text = await InferWithOutcomeAsync(role, prompt, gbnfGrammar, options, ct).ConfigureAwait(false);
        if (!text.IsSuccess)
        {
            return new InferenceOutcome<T>(text.Status, null, text.Error, text.Duration);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(text.Value!.StartsWith("reply:", StringComparison.Ordinal) ? "{}" : text.Value!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value is null
                ? new InferenceOutcome<T>(InferenceOutcomeStatus.InvalidResponse, null, "The model did not return valid JSON.", text.Duration)
                : new InferenceOutcome<T>(InferenceOutcomeStatus.Success, value, null, text.Duration);
        }
        catch (JsonException)
        {
            return new InferenceOutcome<T>(InferenceOutcomeStatus.InvalidResponse, null, "The model did not return valid JSON.", text.Duration);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (current < value)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}

/// <summary>An in-memory usage store that can be told to fail, for the "a failed record never fails the request" rule.</summary>
internal sealed class ThrowingUsageRepository : IAiUsageRecordRepository
{
    public Task AddAsync(AiUsageRecord record, CancellationToken ct = default) =>
        throw new IOException("The data store is not available.");

    public Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(new AiUsageTotals(0, 0));

    public Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(int limit, int skip = 0, AiCallerKind? kind = null, string? callerId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AiUsageRecord>>([]);

    public Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AiUsageSummaryRow>>([]);

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>An in-memory usage store for tests that do not need the data store.</summary>
internal sealed class InMemoryUsageRepository : IAiUsageRecordRepository
{
    public List<AiUsageRecord> Records { get; } = [];

    public Task AddAsync(AiUsageRecord record, CancellationToken ct = default)
    {
        lock (Records)
        {
            Records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default)
    {
        lock (Records)
        {
            var counted = Records.Where(record =>
                record.CallerKind == kind && record.CallerId == callerId && record.StartedAt >= since
                && record.Outcome is not (AiRequestStatus.RefusedOverBudget or AiRequestStatus.Unavailable)).ToList();
            return Task.FromResult(new AiUsageTotals(counted.Count, counted.Sum(record => (long)record.DurationMs)));
        }
    }

    public Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(int limit, int skip = 0, AiCallerKind? kind = null, string? callerId = null, CancellationToken ct = default)
    {
        lock (Records)
        {
            return Task.FromResult<IReadOnlyList<AiUsageRecord>>(Records.AsEnumerable().Reverse().Skip(skip).Take(limit).ToList());
        }
    }

    public Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AiUsageSummaryRow>>([]);

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>Builds an engine room for tests that only exercise text reading.</summary>
internal static class OcrEngineRoom
{
    public static LocalAiEngineRoom For(IOcrService ocr, IConfigurationLoader configuration, InMemoryUsageRepository? usage = null)
    {
        var settings = new AiSettings
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-ocr-room-{Guid.NewGuid():N}"),
        };
        var records = usage ?? new InMemoryUsageRepository();
        return new LocalAiEngineRoom(
            settings,
            new FakeAiFeatureSwitches(),
            new ModelInventory(settings, NullLogger<ModelInventory>.Instance),
            new StubRuntimeStatus(textLoads: false),
            new StubAdmissionRules(),
            new ScriptedTextInference(),
            ocr,
            configuration,
            records,
            new PluginAiBudget(settings, records),
            NullLogger<LocalAiEngineRoom>.Instance);
    }
}

/// <summary>
/// A real engine room over a real temporary SQLite store, with hand-written stand-ins for the model, the text reader, the
/// machine's rules and the runtime check. Defaults: Local AI on, runtime loads, model file present, rules pass.
/// </summary>
internal sealed class EngineRoomHarness : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-engine-room-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;

    public EngineRoomHarness(bool modelPresent = true, bool localAiOn = true)
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "test.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        Usage = new AiUsageRecordRepository(_database);

        Settings = new AiSettings { ModelsDirectory = Path.Combine(_root, "models") };
        foreach (var definition in new[]
                 {
                     Settings.Models.TextFast,
                     Settings.Models.TextQuality,
                     Settings.Models.TextScholar,
                     Settings.Models.TextCjk,
                 })
        {
            definition.Sha256 = null;
        }

        Switches = localAiOn ? new FakeAiFeatureSwitches(AiFeature.SmartLabeling) : new FakeAiFeatureSwitches();
        Settings.PluginBudget.MaxWaitSeconds = 1;
        if (modelPresent)
        {
            PlaceTextModel();
        }

        Inventory = new ModelInventory(Settings, NullLogger<ModelInventory>.Instance);
    }

    public AiSettings Settings { get; }

    public FakeAiFeatureSwitches Switches { get; }

    public ModelInventory Inventory { get; }

    public AiUsageRecordRepository Usage { get; }

    public StubRuntimeStatus Runtime { get; } = new();

    public StubAdmissionRules Admission { get; } = new();

    public ScriptedTextInference Text { get; } = new();

    public FakeOcrService Ocr { get; } = new();

    public OcrOnlyConfigurationLoader Configuration { get; } = new();

    public LocalAiEngineRoomTiming Timing { get; } = new(TimeSpan.FromMilliseconds(20));

    public TimeProvider Time { get; set; } = TimeProvider.System;

    public LocalAiEngineRoom Room => _room ??= NewRoom();

    private LocalAiEngineRoom? _room;

    /// <summary>A second engine room over the same durable store, like the Engine after a restart.</summary>
    public LocalAiEngineRoom NewRoom(IAiUsageRecordRepository? usage = null, ILogger<LocalAiEngineRoom>? logger = null)
    {
        var records = usage ?? Usage;
        return new LocalAiEngineRoom(
            Settings,
            Switches,
            Inventory,
            Runtime,
            Admission,
            Text,
            Ocr,
            Configuration,
            records,
            new PluginAiBudget(Settings, records, Time),
            logger ?? NullLogger<LocalAiEngineRoom>.Instance,
            Timing,
            Time);
    }

    public PluginAiBudget Budget => new(Settings, Usage, Time);

    public void PlaceTextModel()
    {
        var path = Path.Combine(Settings.ModelsDirectory, "llama", Settings.Models.TextQuality.File);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    public async Task<IReadOnlyList<AiUsageRecord>> RecordsAsync() => await Usage.ListRecentAsync(100);

    /// <summary>Waits until a condition holds, so a test never sleeps for a fixed time.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {because}");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _room?.Dispose();
        _database.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup is best-effort on platforms with delayed file-handle release.
        }
    }
}
