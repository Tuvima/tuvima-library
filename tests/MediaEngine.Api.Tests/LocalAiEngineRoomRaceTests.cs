using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Queue ordering under preemption, atomic plugin allowances, failed checks, and the benchmark's retry.</summary>
public sealed class LocalAiEngineRoomRaceTests
{
    private static readonly AiCaller Person = AiCaller.Person("test_person");
    private static readonly AiCaller Plugin = AiCaller.Plugin("tuvima.test");

    // ── Person preemption and the queue are one step ─────────────────────────

    [Fact]
    public async Task APersonArriving_TakesTheLineAndPreemptsInOneStep_SoAWaitingPluginCannotSlipIn()
    {
        var queue = new AiEngineQueue(1);
        using var background = await queue.AcquireAsync(AiCallerKind.LibraryBackground, false, CancellationToken.None);
        var plugin = queue.AcquireAsync(AiCallerKind.Plugin, false, CancellationToken.None);
        Assert.Equal(1, queue.WaitingCount);

        // By the time AcquireAsync returns, the person is already in the line and the running work has been asked to stop.
        var person = queue.AcquireAsync(AiCallerKind.Person, false, CancellationToken.None);
        Assert.Equal(2, queue.WaitingCount);
        Assert.True(background!.WasPreempted);

        // The running work steps aside at once: the person, not the earlier-waiting plugin, gets the slot.
        background.Dispose();
        using var personSlot = await person;

        Assert.NotNull(personSlot);
        Assert.False(plugin.IsCompleted);
        Assert.Equal(1, queue.ActiveCount);
    }

    [Fact]
    public async Task APersonArriving_NeverPreemptsRunningIngestion_ItWaitsItsTurn()
    {
        var queue = new AiEngineQueue(1);
        using var ingestion = await queue.AcquireAsync(AiCallerKind.Ingestion, false, CancellationToken.None);

        var person = queue.AcquireAsync(AiCallerKind.Person, false, CancellationToken.None);

        Assert.False(ingestion!.WasPreempted);
        Assert.Equal(1, queue.WaitingCount);
        ingestion.Dispose();
        using var slot = await person;
        Assert.NotNull(slot);
    }

    [Fact]
    public async Task APersonArriving_WhileBackgroundWorkRuns_IsServedBeforeAnEarlierWaitingPlugin_InTheEngineRoom()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 60;
        var release = new TaskCompletionSource();
        harness.Text.Handler = async (prompt, ct) =>
        {
            if (prompt == "background")
            {
                await release.Task.WaitAsync(ct);
            }

            return ScriptedTextInference.Success("reply:" + prompt);
        };
        var room = harness.Room;

        var background = room.WriteTextAsync(AiCaller.LibraryBackground("feature"), AiAbility.WriteTextQuick, "background");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the background work to start");
        var plugin = room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "plugin");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 1, "the plugin to queue");

        var person = await room.WriteTextAsync(Person, AiAbility.WriteTextQuick, "person");
        var stepped = await background;
        var pluginResult = await plugin;

        Assert.True(stepped.Outcome.Preempted);
        Assert.Equal(AiRequestStatus.Succeeded, person.Outcome.Status);
        Assert.Equal(AiRequestStatus.Succeeded, pluginResult.Outcome.Status);
        Assert.Equal(["background", "person", "plugin"], harness.Text.ServedPrompts);
    }

    // ── Plugin allowance: check and admit together ───────────────────────────

    [Fact]
    public async Task APluginRequestStillRunning_CountsAgainstItsAllowance_SoSimultaneousRequestsCannotAllPass()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;
        harness.Settings.PluginBudget.MaxWaitSeconds = 60;
        var release = new TaskCompletionSource();
        harness.Text.Handler = async (prompt, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return ScriptedTextInference.Success("reply:" + prompt);
        };
        var room = harness.Room;

        var first = room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "first");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the first request to start");
        var second = await room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "second");
        release.SetResult();
        var firstResult = await first;

        Assert.Equal(AiRequestStatus.RefusedOverBudget, second.Outcome.Status);
        Assert.Equal(AiRequestStatus.Succeeded, firstResult.Outcome.Status);
        Assert.Equal(["first"], harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task APluginsFinishedRequests_AreNotCountedTwice_OnceStoredAndOnceAsInFlight()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 3;

        var statuses = new List<AiRequestStatus>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await harness.Room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, $"p{i}")).Outcome.Status);
        }

        Assert.Equal(
            [AiRequestStatus.Succeeded, AiRequestStatus.Succeeded, AiRequestStatus.Succeeded, AiRequestStatus.RefusedOverBudget],
            statuses);
    }

    [Fact]
    public async Task AnAllowanceCheckThatFails_LeavesAFailedRecord_AndAnswersFailed()
    {
        using var harness = new EngineRoomHarness();
        var faulty = new FaultyTotalsUsageRepository(harness.Usage);
        using var room = harness.NewRoom(faulty);

        var result = await room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "work");

        Assert.Equal(AiRequestStatus.Failed, result.Outcome.Status);
        Assert.Empty(harness.Text.ServedPrompts);
        var record = Assert.Single(await harness.RecordsAsync());
        Assert.Equal(AiRequestStatus.Failed, record.Outcome);
    }

    [Fact]
    public async Task ATextReadingCheckThatFails_LeavesAFailedRecord_AndAnswersFailed()
    {
        using var harness = new EngineRoomHarness();
        harness.Ocr.CapabilityFailure = new IOException("settings could not be read");

        var result = await harness.Room.ReadTextFromImageAsync(
            Person, new AiImageTextRequest("never-read.png", ["eng"], false, TimeSpan.FromSeconds(5)));

        Assert.Equal(AiRequestStatus.Failed, result.Outcome.Status);
        Assert.Equal(0, harness.Ocr.Calls);
        Assert.Equal(AiRequestStatus.Failed, Assert.Single(await harness.RecordsAsync()).Outcome);
    }

    [Fact]
    public async Task APluginsTextReading_IsHeldToTheSameAtomicAllowance()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-race-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1]);
        try
        {
            var request = new AiImageTextRequest(path, ["eng"], false, TimeSpan.FromSeconds(5));
            var first = await harness.Room.ReadTextFromImageAsync(Plugin, request);
            var second = await harness.Room.ReadTextFromImageAsync(Plugin, request);

            Assert.Equal(AiRequestStatus.Succeeded, first.Outcome.Status);
            Assert.Equal(AiRequestStatus.RefusedOverBudget, second.Outcome.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── The runtime check is re-run when tools are refreshed ─────────────────

    [Fact]
    public void RefreshingTools_AsksTheRuntimeStatusToCheckAgain()
    {
        using var harness = new EngineRoomHarness();
        var runtime = new StubRuntimeStatus(textLoads: true);
        var probe = new ToolRuntimeProbe(runtime, harness.Inventory, NullLogger<ToolRuntimeProbe>.Instance);

        Assert.True(probe.AiRuntimeLoads());
        runtime.TextRuntimeLoads = false;
        Assert.False(probe.AiRuntimeLoads());

        Assert.Equal(2, runtime.RefreshCalls);
    }

    // ── The start-up benchmark and a person's request ────────────────────────

    [Fact]
    public async Task ABenchmarkThatAPersonPreempted_IsNotSavedAsFailed_AndThrowsSoItCanBeRetried()
    {
        using var directory = new TemporaryModelsDirectory();
        var settings = directory.Settings();
        var benchmark = Benchmark(settings, new ScriptedBenchmarkRoom(Preempted()));

        await Assert.ThrowsAsync<AiRequestPreemptedException>(() => benchmark.BenchmarkAsync());

        Assert.Equal(AiBenchmarkOutcomes.NotRun, benchmark.Current.Outcome);
        Assert.False(File.Exists(Path.Combine(settings.ModelsDirectory, ".tuvima", "hardware-benchmark.json")));
    }

    [Fact]
    public async Task TheStartupBenchmark_RetriesAfterBeingPreempted_UntilItHasRun()
    {
        using var directory = new TemporaryModelsDirectory();
        var settings = directory.Settings();
        settings.Features.SmartLabeling = true;
        var room = new ScriptedBenchmarkRoom(Preempted(), Busy(), Done());
        var benchmark = Benchmark(settings, room);
        var service = new HardwareBenchmarkBackgroundService(
            benchmark,
            settings,
            NullLogger<HardwareBenchmarkBackgroundService>.Instance,
            timing: new HardwareBenchmarkTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));

        await service.StartAsync(CancellationToken.None);
        await EngineRoomHarness.WaitUntilAsync(() => benchmark.Current.Outcome == AiBenchmarkOutcomes.Succeeded, "the benchmark to run");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(3, room.Requests);
    }

    private static AiRequestOutcome Preempted() => new(AiRequestStatus.Cancelled, "Paused", Preempted: true);

    private static AiRequestOutcome Busy() => new(AiRequestStatus.Deferred, "Waiting: the machine is busy");

    private static AiRequestOutcome Done() => new(AiRequestStatus.Succeeded, "Done");

    private static HardwareBenchmarkService Benchmark(AiSettings settings, IAiEngineRoom room) =>
        new(
            room,
            settings,
            new GpuBackendDetector(NullLogger<GpuBackendDetector>.Instance),
            new AiBenchmarkStateStore(settings, NullLogger<AiBenchmarkStateStore>.Instance),
            NullLogger<HardwareBenchmarkService>.Instance);

    private sealed class TemporaryModelsDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-benchmark-{Guid.NewGuid():N}");

        public AiSettings Settings() => new() { ModelsDirectory = _path };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // The benchmark never wrote anything.
            }
            catch (IOException)
            {
                // Test cleanup is best-effort on platforms with delayed file-handle release.
            }
        }
    }

    /// <summary>Answers each text request with the next scripted outcome (the last one repeats).</summary>
    private sealed class ScriptedBenchmarkRoom(params AiRequestOutcome[] outcomes) : IAiEngineRoom
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public Task<AiTextResult> WriteTextAsync(
            AiCaller caller, AiAbility ability, string prompt, string? grammar = null, AiRequestOptions? options = null, CancellationToken ct = default)
        {
            var index = Math.Min(Interlocked.Increment(ref _requests) - 1, outcomes.Length - 1);
            var outcome = outcomes[index];
            return Task.FromResult(new AiTextResult(outcome.IsSuccess ? "a paragraph about libraries" : null, outcome));
        }

        public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
            AiCaller caller, AiAbility ability, string prompt, string grammar, AiRequestOptions? options = null, CancellationToken ct = default) where T : class =>
            throw new NotSupportedException();

        public Task<AiImageTextResult> ReadTextFromImageAsync(AiCaller caller, AiImageTextRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
            Task.FromResult(AiAvailability.Available);
    }

    /// <summary>Reads like the real store but fails when asked what a plugin has used.</summary>
    private sealed class FaultyTotalsUsageRepository(IAiUsageRecordRepository inner) : IAiUsageRecordRepository
    {
        public Task AddAsync(AiUsageRecord record, CancellationToken ct = default) => inner.AddAsync(record, ct);

        public Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default) =>
            throw new IOException("The data store is not available.");

        public Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(int limit, int skip = 0, AiCallerKind? kind = null, string? callerId = null, CancellationToken ct = default) =>
            inner.ListRecentAsync(limit, skip, kind, callerId, ct);

        public Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
            inner.SummariseSinceAsync(since, ct);

        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => inner.PruneAsync(now, ct);
    }
}
