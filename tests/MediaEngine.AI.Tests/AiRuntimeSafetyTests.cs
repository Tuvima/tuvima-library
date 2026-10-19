using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Llama;
using MediaEngine.AI.Whisper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

public sealed class AiRuntimeSafetyTests
{
    [Fact]
    public void Validator_RejectsTraversalInsecureDownloadsAndInvalidChecksums()
    {
        var settings = CreateSettings();
        settings.Models.TextFast.File = "../escape.gguf";
        settings.Models.TextFast.DownloadUrl = "http://example.test/model.gguf";
        settings.Models.TextFast.Sha256 = "not-a-checksum";

        var errors = AiSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Path == "models.text_fast.file");
        Assert.Contains(errors, error => error.Path == "models.text_fast.download_url");
        Assert.Contains(errors, error => error.Path == "models.text_fast.sha256");
    }

    [Fact]
    public void RuntimeSnapshot_IsDetachedFromMutableConfiguration()
    {
        var settings = CreateSettings();
        var originalFile = settings.Models.TextFast.File;
        var snapshot = AiRuntimeSettingsSnapshot.Create(settings);

        settings.Models.TextFast.File = "changed.gguf";
        settings.InferenceTimeoutSeconds = 999;

        Assert.Equal(originalFile, snapshot.GetModel(AiModelRole.TextFast).File);
        Assert.NotEqual(settings.InferenceTimeoutSeconds, snapshot.InferenceTimeoutSeconds);
    }

    [Fact]
    public void Inventory_RejectsCorruptExistingModel()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var modelPath = System.IO.Path.Combine(directory.Path, "llama", settings.Models.TextFast.File);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(modelPath)!);
        File.WriteAllText(modelPath, "corrupt");
        SetSharedTextMetadata(settings, sha256: Convert.ToHexStringLower(SHA256.HashData("expected"u8.ToArray())));

        var inventory = CreateInventory(settings);

        Assert.Equal(AiModelState.Error, inventory.GetState(AiModelRole.TextFast));
    }

    [Fact]
    public async Task DownloadManager_VerifiesChecksumBeforePublishingModel()
    {
        using var directory = new TemporaryDirectory();
        var payload = "verified-model"u8.ToArray();
        var settings = CreateSettings(directory.Path);
        settings.MinimumFreeDiskMB = 256;
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: Convert.ToHexStringLower(SHA256.HashData(payload)));
        var inventory = CreateInventory(settings);
        await using var manager = new ModelDownloadManager(
            settings,
            inventory,
            new StubHttpClientFactory(payload),
            new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance);

        await manager.StartDownloadAsync(AiModelRole.TextFast);
        var result = await manager.WaitForCompletionAsync(AiModelRole.TextFast);

        Assert.Equal(ModelDownloadOutcome.Succeeded, result.Outcome);
        Assert.Equal(payload, await File.ReadAllBytesAsync(inventory.GetModelPath(AiModelRole.TextFast)));
        Assert.False(File.Exists(inventory.GetModelPath(AiModelRole.TextFast) + ".downloading"));
    }

    [Fact]
    public async Task DownloadManager_ReturnsFailedResultAndRemovesUnverifiedArtifact()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        settings.MinimumFreeDiskMB = 256;
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: new string('0', 64));
        var inventory = CreateInventory(settings);
        await using var manager = new ModelDownloadManager(
            settings,
            inventory,
            new StubHttpClientFactory("wrong-content"u8.ToArray()),
            new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance);

        await manager.StartDownloadAsync(AiModelRole.TextFast);
        var result = await manager.WaitForCompletionAsync(AiModelRole.TextFast);

        Assert.Equal(ModelDownloadOutcome.Failed, result.Outcome);
        Assert.Equal(AiModelState.Error, inventory.GetState(AiModelRole.TextFast));
        Assert.False(File.Exists(inventory.GetModelPath(AiModelRole.TextFast)));
        Assert.False(File.Exists(inventory.GetModelPath(AiModelRole.TextFast) + ".downloading"));
    }

    [Fact]
    public async Task DownloadManager_RecordsStartedAndFinishedInTheActivityLog()
    {
        using var directory = new TemporaryDirectory();
        var payload = "verified-model"u8.ToArray();
        var settings = CreateSettings(directory.Path);
        settings.MinimumFreeDiskMB = 256;
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: Convert.ToHexStringLower(SHA256.HashData(payload)));
        var activity = new SpyActivity();
        await using var manager = new ModelDownloadManager(
            settings, CreateInventory(settings), new StubHttpClientFactory(payload), new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance, activity);

        await manager.StartDownloadAsync(AiModelRole.TextFast);
        await manager.WaitForCompletionAsync(AiModelRole.TextFast);

        Assert.Equal([SystemActionType.ToolDownloadStarted, SystemActionType.ToolDownloadFinished], activity.ActionTypes);
        Assert.DoesNotContain(directory.Path, string.Join(' ', activity.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadManager_RecordsAChecksumRefusalWhenTheModelDoesNotMatch()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        settings.MinimumFreeDiskMB = 256;
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: new string('0', 64));
        var activity = new SpyActivity();
        await using var manager = new ModelDownloadManager(
            settings, CreateInventory(settings), new StubHttpClientFactory("wrong-content"u8.ToArray()), new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance, activity);

        await manager.StartDownloadAsync(AiModelRole.TextFast);
        await manager.WaitForCompletionAsync(AiModelRole.TextFast);

        Assert.Equal([SystemActionType.ToolDownloadStarted, SystemActionType.ToolDownloadChecksumRefused], activity.ActionTypes);
    }

    [Fact]
    public async Task DownloadManager_RecordsACancelledDownload()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        settings.MinimumFreeDiskMB = 256;
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: new string('0', 64));
        var activity = new SpyActivity();
        var http = new GatedHttpClientFactory("never-delivered"u8.ToArray());
        await using var manager = new ModelDownloadManager(
            settings, CreateInventory(settings), http, new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance, activity);

        await manager.StartDownloadAsync(AiModelRole.TextFast);
        await manager.CancelDownloadAsync(AiModelRole.TextFast);

        Assert.Equal([SystemActionType.ToolDownloadStarted, SystemActionType.ToolDownloadCancelled], activity.ActionTypes);
    }

    [Fact]
    public async Task SharedArtifact_DownloadsOnceAndRefreshesEveryRoleOnCompletionAndDeletion()
    {
        using var directory = new TemporaryDirectory();
        var payload = "shared-qwen-model"u8.ToArray();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(payload));
        var settings = CreateSettings(directory.Path);
        SetSharedTextMetadata(settings, sizeMb: 1, sha256: checksum);
        var inventory = CreateInventory(settings);
        var http = new GatedHttpClientFactory(payload);
        await using var manager = new ModelDownloadManager(
            settings,
            inventory,
            http,
            new NoOpEventPublisher(),
            NullLogger<ModelDownloadManager>.Instance);

        await manager.StartDownloadAsync(AiModelRole.TextScholar);
        await manager.StartDownloadAsync(AiModelRole.TextCjk);
        Assert.Equal(1, http.RequestCount);
        Assert.Equal(AiModelState.Downloading, inventory.GetState(AiModelRole.TextScholar));
        Assert.Equal(AiModelState.Downloading, inventory.GetState(AiModelRole.TextCjk));

        http.Release();
        var scholar = await manager.WaitForCompletionAsync(AiModelRole.TextScholar);
        var cjk = await manager.WaitForCompletionAsync(AiModelRole.TextCjk);

        Assert.Equal(ModelDownloadOutcome.Succeeded, scholar.Outcome);
        Assert.Equal(ModelDownloadOutcome.Succeeded, cjk.Outcome);
        Assert.Equal(AiModelRole.TextCjk, cjk.Role);
        Assert.Equal(AiModelState.Ready, inventory.GetState(AiModelRole.TextScholar));
        Assert.Equal(AiModelState.Ready, inventory.GetState(AiModelRole.TextCjk));
        Assert.Equal(
            inventory.GetModelPath(AiModelRole.TextScholar),
            inventory.GetModelPath(AiModelRole.TextCjk));

        await manager.DeleteModelAsync(AiModelRole.TextCjk);

        Assert.Equal(AiModelState.NotDownloaded, inventory.GetState(AiModelRole.TextScholar));
        Assert.Equal(AiModelState.NotDownloaded, inventory.GetState(AiModelRole.TextCjk));
    }

    [Fact]
    public void Validator_RejectsConflictingSharedArtifactMetadataAndOutOfRangePluginBudgets()
    {
        var settings = CreateSettings();
        settings.PluginBudget.RequestsPerHour = 0;
        settings.PluginBudgetOverrides["tuvima.test"] = new AiPluginBudgetOverride { MaxWaitSeconds = 4000 };
        settings.Models.TextCjk.Sha256 = new string('a', 64);
        var errors = AiSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Path == "plugin_budget.requests_per_hour");
        Assert.Contains(errors, error => error.Path == "plugin_budget_overrides.tuvima.test.max_wait_seconds");
        Assert.Contains(errors, error => error.Path == "models.text_cjk.sha256");
    }

    [Fact]
    public async Task LifecycleLease_BlocksUnloadAndInvokesNativeResourceDisposer()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var inventory = CreateInventory(settings);
        inventory.SetState(AiModelRole.TextFast, AiModelState.Ready);
        await using var lifecycle = CreateLifecycle(settings, inventory);
        var disposedRoles = new List<AiModelRole>();
        using var registration = lifecycle.RegisterModelDisposer((role, _) =>
        {
            disposedRoles.Add(role);
            return ValueTask.CompletedTask;
        });

        var lease = await lifecycle.AcquireInferenceLeaseAsync(AiModelRole.TextFast);
        var unload = lifecycle.UnloadCurrentAsync();
        await Task.Delay(50);
        Assert.False(unload.IsCompleted);

        await lease.DisposeAsync();
        await unload;

        Assert.Equal([AiModelRole.TextFast], disposedRoles);
        Assert.Null(lifecycle.CurrentlyLoadedRole);
    }

    [Fact]
    public async Task InferenceTimeout_DiscardsPartialOutputAndReturnsTypedOutcome()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var inventory = CreateInventory(settings);
        inventory.SetState(AiModelRole.TextFast, AiModelState.Ready);
        await using var lifecycle = CreateLifecycle(settings, inventory);
        var backend = new ScriptedBackend(new Script("partial", WaitForCancellation: true));
        await using var inference = new LlamaInferenceService(
            settings,
            lifecycle,
            inventory,
            NullLogger<LlamaInferenceService>.Instance,
            backend);

        var outcome = await inference.InferWithOutcomeAsync(
            AiModelRole.TextFast,
            "prompt",
            options: new(0.1, 16, TimeSpan.FromMilliseconds(50)));

        Assert.Equal(InferenceOutcomeStatus.TimedOut, outcome.Status);
        Assert.Null(outcome.Value);
    }

    [Fact]
    public async Task InvalidJson_DoesNotTriggerAnotherFullInference()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        settings.Models.TextFast.Temperature = 0.1;
        var inventory = CreateInventory(settings);
        inventory.SetState(AiModelRole.TextFast, AiModelState.Ready);
        await using var lifecycle = CreateLifecycle(settings, inventory);
        var backend = new ScriptedBackend(new Script("not-json"));
        await using var inference = new LlamaInferenceService(
            settings,
            lifecycle,
            inventory,
            NullLogger<LlamaInferenceService>.Instance,
            backend);

        var outcome = await inference.InferJsonWithOutcomeAsync<TestEnvelope>(
            AiModelRole.TextFast,
            "prompt",
            "root ::= object");

        Assert.Equal(InferenceOutcomeStatus.InvalidResponse, outcome.Status);
        Assert.Null(outcome.Value);
        Assert.Equal([0.1], backend.Temperatures, new DoubleComparer(0.0001));
        Assert.Equal(0.1, settings.Models.TextFast.Temperature);
    }

    [Fact]
    public async Task ConcurrentInference_IsSerializedByLifecycleLeases()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var inventory = CreateInventory(settings);
        inventory.SetState(AiModelRole.TextFast, AiModelState.Ready);
        await using var lifecycle = CreateLifecycle(settings, inventory);
        var backend = new ScriptedBackend(
            new Script("one", Delay: TimeSpan.FromMilliseconds(40)),
            new Script("two", Delay: TimeSpan.FromMilliseconds(40)));
        await using var inference = new LlamaInferenceService(
            settings,
            lifecycle,
            inventory,
            NullLogger<LlamaInferenceService>.Instance,
            backend);

        await Task.WhenAll(
            inference.InferWithOutcomeAsync(AiModelRole.TextFast, "first"),
            inference.InferWithOutcomeAsync(AiModelRole.TextFast, "second"));

        Assert.Equal(1, backend.MaximumConcurrency);
    }

    [Fact]
    public async Task WhisperInference_HoldsLeaseAndDisposesNativeFactoryOnLifecycleUnload()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var inventory = CreateInventory(settings);
        inventory.SetState(AiModelRole.Audio, AiModelState.Ready);
        await using var lifecycle = CreateLifecycle(settings, inventory);
        var backend = new StubWhisperBackend();
        await using var whisper = new WhisperInferenceService(
            settings,
            lifecycle,
            inventory,
            NullLogger<WhisperInferenceService>.Instance,
            backend);
        var wavPath = System.IO.Path.Combine(directory.Path, "sample.wav");
        await File.WriteAllBytesAsync(wavPath, [1, 2, 3]);

        var segments = await whisper.TranscribeAsync(wavPath);
        await lifecycle.UnloadCurrentAsync();

        Assert.Single(segments);
        Assert.Equal("transcribed", segments[0].Text);
        Assert.Equal([AiModelRole.Audio], backend.DisposedModelRoles);
        Assert.Null(lifecycle.CurrentlyLoadedRole);
    }

    [Fact]
    public void AiSchema_IsValidJsonAndRejectsRawModelConfiguration()
    {
        var path = FindRepositoryFile("config", "schemas", "ai.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var properties = root.GetProperty("properties");
        Assert.Equal("standard", properties.GetProperty("resource_profile").GetProperty("enum")[1].GetString());
        Assert.True(properties.GetProperty("audio_pack_enabled").GetProperty("type").GetString() == "boolean");
        Assert.False(properties.TryGetProperty("models", out _));
        Assert.False(properties.TryGetProperty("model_catalog", out _));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    private static AiSettings CreateSettings(string? modelsDirectory = null)
    {
        var settings = new AiSettings
        {
            ModelsDirectory = modelsDirectory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tuvima-ai-tests"),
            IdleUnloadSeconds = 60,
            InferenceTimeoutSeconds = 2,
            MinimumFreeDiskMB = 256,
        };
        return settings;
    }

    private static ModelInventory CreateInventory(AiSettings settings) =>
        new(settings, NullLogger<ModelInventory>.Instance);

    private static void SetSharedTextMetadata(AiSettings settings, int? sizeMb = null, string? sha256 = null)
    {
        foreach (var definition in new[]
                 {
                     settings.Models.TextFast,
                     settings.Models.TextQuality,
                     settings.Models.TextScholar,
                     settings.Models.TextCjk,
                 })
        {
            if (sizeMb.HasValue)
            {
                definition.SizeMB = sizeMb.Value;
            }

            if (sha256 is not null)
            {
                definition.Sha256 = sha256;
            }
        }
    }

    [Fact]
    public void InferenceThreads_AlwaysLeaveAProportionalCpuReserve()
    {
        var settings = CreateSettings();
        settings.ReservedCpuCores = 2;
        settings.Models.TextFast.Threads = int.MaxValue;

        var resolved = settings.ResolveInferenceThreads(AiModelRole.TextFast);
        var expectedReserve = Math.Max(2, (int)Math.Ceiling(Environment.ProcessorCount * 0.25));

        Assert.InRange(resolved, 1, Math.Max(1, Environment.ProcessorCount - expectedReserve));
    }

    private static ModelLifecycleManager CreateLifecycle(AiSettings settings, ModelInventory inventory) =>
        new(settings, inventory, new FixedRuntimeStatus(textLoads: true), new NoOpEventPublisher(), NullLogger<ModelLifecycleManager>.Instance);

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(System.IO.Path.DirectorySeparatorChar, segments));
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(byte[] payload) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(payload));
    }

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class SpyActivity : ISystemActivityRepository
    {
        private readonly List<SystemActivityEntry> _entries = [];

        public IReadOnlyList<string> ActionTypes
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Select(entry => entry.ActionType).ToList();
                }
            }
        }

        public IReadOnlyList<string> Details
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Select(entry => entry.Detail ?? string.Empty).ToList();
                }
            }
        }

        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class GatedHttpClientFactory(byte[] payload) : IHttpClientFactory
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => _requestCount;
        public void Release() => _release.TrySetResult();
        public HttpClient CreateClient(string name) => new(new Handler(this, payload));

        private sealed class Handler(GatedHttpClientFactory owner, byte[] payload) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._requestCount);
                await owner._release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
            }
        }
    }

    private sealed class StubWhisperBackend : IWhisperExecutionBackend
    {
        public List<AiModelRole> DisposedModelRoles { get; } = [];

        public Task<IReadOnlyList<TranscriptionSegment>> TranscribeAsync(
            string modelPath,
            string language,
            bool translate,
            string wavFilePath,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<TranscriptionSegment> segments =
            [
                new()
                {
                    StartMs = 0,
                    EndMs = 100,
                    Text = "transcribed",
                    Confidence = 0.9,
                },
            ];
            return Task.FromResult(segments);
        }

        public Task<(string LanguageCode, double Confidence)> DetectLanguageAsync(
            string modelPath,
            string wavFilePath,
            CancellationToken cancellationToken) => Task.FromResult(("en", 0.9));

        public ValueTask DisposeModelAsync(
            AiModelRole role,
            CancellationToken cancellationToken)
        {
            DisposedModelRoles.Add(role);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Script(
        string Output,
        bool WaitForCancellation = false,
        TimeSpan? Delay = null);

    private sealed class ScriptedBackend(params Script[] scripts) : ILlamaExecutionBackend
    {
        private readonly ConcurrentQueue<Script> _scripts = new(scripts);
        private int _active;
        private int _maximumConcurrency;

        public List<double> Temperatures { get; } = [];
        public int MaximumConcurrency => _maximumConcurrency;

        public async IAsyncEnumerable<string> InferAsync(
            AiModelRole role,
            string prompt,
            string? gbnfGrammar,
            InferenceRequestOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximumConcurrency, active);
            Temperatures.Add(options.Temperature);
            try
            {
                Assert.True(_scripts.TryDequeue(out var script));
                if (!string.IsNullOrEmpty(script.Output))
                {
                    yield return script.Output;
                }

                if (script.WaitForCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else if (script.Delay.HasValue)
                {
                    await Task.Delay(script.Delay.Value, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask DisposeModelAsync(AiModelRole role, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class TestEnvelope
    {
        public string Name { get; set; } = "";
    }

    private sealed class DoubleComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => obj.GetHashCode();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima-ai-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup is best-effort on platforms with delayed file-handle release.
            }
        }
    }
}
