using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Llama;
using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Local AI engine room: one queue per engine in priority order, the machine's rules, plugin allowances, real readiness,
/// and one usage record for every request that reaches it.
/// </summary>
public sealed class LocalAiEngineRoomTests
{
    private static readonly AiCaller Person = AiCaller.Person("test_person");
    private static readonly AiCaller Ingestion = AiCaller.Ingestion("test_ingestion");
    private static readonly AiCaller Background = AiCaller.LibraryBackground("test_background");
    private static readonly AiCaller Plugin = AiCaller.Plugin("tuvima.test");

    private static Task<AiTextResult> Ask(
        LocalAiEngineRoom room,
        AiCaller caller,
        string prompt,
        AiAbility ability = AiAbility.WriteTextQuick,
        CancellationToken ct = default) =>
        room.WriteTextAsync(caller, ability, prompt, ct: ct);

    private static Func<string, CancellationToken, Task<InferenceOutcome<string>>> HoldPrompt(string held, Task release) =>
        async (prompt, ct) =>
        {
            if (prompt == held)
            {
                await release.WaitAsync(ct);
            }

            return ScriptedTextInference.Success("reply:" + prompt);
        };

    // ── Queue order ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HeldTextSlot_ServesAPersonThenIngestionThenAPlugin_NotInArrivalOrder()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 60;
        var release = new TaskCompletionSource();
        harness.Text.Handler = HoldPrompt("hold", release.Task);
        var room = harness.Room;

        var holder = Ask(room, Ingestion, "hold");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the holder to start");
        var plugin = Ask(room, Plugin, "plugin");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 1, "the plugin to queue");
        var ingestion = Ask(room, Ingestion, "ingestion");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 2, "ingestion to queue");
        var person = Ask(room, Person, "person");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 3, "the person to queue");

        release.SetResult();
        var results = await Task.WhenAll(holder, plugin, ingestion, person);

        Assert.All(results, result => Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status));
        Assert.Equal(["hold", "person", "ingestion", "plugin"], harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task WithinOneKind_RequestsAreServedFirstComeFirstServed()
    {
        using var harness = new EngineRoomHarness();
        var release = new TaskCompletionSource();
        harness.Text.Handler = HoldPrompt("hold", release.Task);
        var room = harness.Room;

        var holder = Ask(room, Ingestion, "hold");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the holder to start");
        var first = Ask(room, Ingestion, "first");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 1, "the first to queue");
        var second = Ask(room, Ingestion, "second");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 2, "the second to queue");

        release.SetResult();
        await Task.WhenAll(holder, first, second);

        Assert.Equal(["hold", "first", "second"], harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task TheTextModel_RunsOneRequestAtATime()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = async (prompt, ct) =>
        {
            await Task.Delay(30, ct);
            return ScriptedTextInference.Success("reply:" + prompt);
        };

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Ask(harness.Room, i % 2 == 0 ? Person : Ingestion, $"p{i}")));

        Assert.Equal(1, harness.Text.MaximumConcurrency);
        Assert.Equal(5, harness.Text.ServedPrompts.Count);
    }

    [Fact]
    public async Task TextReading_KeepsItsPresentThroughput_ManyReadsRunAtOnce()
    {
        using var harness = new EngineRoomHarness();
        var release = new TaskCompletionSource();
        harness.Ocr.OnRecognize = async (call, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new OcrRecognition("words", 90, 1);
        };
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-ocr-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1]);
        try
        {
            var reads = Enumerable.Range(0, 3)
                .Select(_ => harness.Room.ReadTextFromImageAsync(
                    AiCaller.LibraryBackground("document_text_reading"),
                    new AiImageTextRequest(path, ["eng"], false, TimeSpan.FromSeconds(30))))
                .ToList();
            await EngineRoomHarness.WaitUntilAsync(() => harness.Room.TextReadingRequestsRunning == 3, "three reads to run together");

            release.SetResult();
            var results = await Task.WhenAll(reads);

            Assert.All(results, result => Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Preemption ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AiCallerKind.LibraryBackground)]
    [InlineData(AiCallerKind.Plugin)]
    public async Task APersonArriving_CancelsRunningLibraryBackgroundAndPluginWork(AiCallerKind running)
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 60;
        harness.Text.Handler = HoldPrompt("block", new TaskCompletionSource().Task);
        var room = harness.Room;

        var work = Ask(room, new AiCaller(running, "test_running"), "block");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the work to start");
        var person = await Ask(room, Person, "person");
        var stepped = await work;

        Assert.Equal(AiRequestStatus.Cancelled, stepped.Outcome.Status);
        Assert.True(stepped.Outcome.Preempted);
        Assert.Equal(AiRequestStatus.Succeeded, person.Outcome.Status);
    }

    [Fact]
    public async Task APersonArriving_DoesNotCancelRunningIngestionWork_ItWaitsItsTurn()
    {
        using var harness = new EngineRoomHarness();
        var release = new TaskCompletionSource();
        harness.Text.Handler = HoldPrompt("block", release.Task);
        var room = harness.Room;

        var ingestion = Ask(room, Ingestion, "block");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "ingestion to start");
        var person = Ask(room, Person, "person");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 1, "the person to queue");

        Assert.False(ingestion.IsCompleted);
        release.SetResult();

        Assert.Equal(AiRequestStatus.Succeeded, (await ingestion).Outcome.Status);
        Assert.Equal(AiRequestStatus.Succeeded, (await person).Outcome.Status);
        Assert.Equal(["block", "person"], harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task AppTraffic_AsksRunningLibraryBackgroundWorkToStepAside_AsItAlwaysHas()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = HoldPrompt("block", new TaskCompletionSource().Task);
        var room = harness.Room;

        var work = Ask(room, Background, "block");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the work to start");
        harness.Admission.RaiseInteractiveTraffic();
        var result = await work;

        Assert.Equal(AiRequestStatus.Cancelled, result.Outcome.Status);
        Assert.True(result.Outcome.Preempted);
    }

    [Fact]
    public async Task AppTraffic_DoesNotInterruptRunningIngestionWork()
    {
        using var harness = new EngineRoomHarness();
        var release = new TaskCompletionSource();
        harness.Text.Handler = HoldPrompt("block", release.Task);
        var room = harness.Room;

        var work = Ask(room, Ingestion, "block");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the work to start");
        harness.Admission.RaiseInteractiveTraffic();
        release.SetResult();

        Assert.Equal(AiRequestStatus.Succeeded, (await work).Outcome.Status);
    }

    // ── The machine's rules ──────────────────────────────────────────────────

    [Theory]
    [InlineData("onboarding-incomplete")]
    [InlineData("interactive-traffic")]
    public async Task LibraryBackground_WhenTheMachineRulesSayNotNow_IsDeferredAtOnce_WithoutRunningOrARecord(string reason)
    {
        using var harness = new EngineRoomHarness();
        harness.Admission.Reason = reason;

        var result = await Ask(harness.Room, Background, "work");

        Assert.Equal(AiRequestStatus.Deferred, result.Outcome.Status);
        Assert.Null(result.Text);
        Assert.Empty(harness.Text.ServedPrompts);
        Assert.Empty(await harness.RecordsAsync());
    }

    [Fact]
    public async Task LibraryBackground_WhenTheMachineRulesPass_RunsAndLeavesOneRecord()
    {
        using var harness = new EngineRoomHarness();

        var result = await Ask(harness.Room, Background, "work");

        Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status);
        Assert.Single(await harness.RecordsAsync());
    }

    [Fact]
    public async Task ASecondLibraryBackgroundRequest_WhileOneIsRunning_IsDeferred()
    {
        using var harness = new EngineRoomHarness();
        var release = new TaskCompletionSource();
        harness.Text.Handler = HoldPrompt("hold", release.Task);
        var room = harness.Room;

        var first = Ask(room, Background, "hold");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the first to start");
        var second = await Ask(room, AiCaller.LibraryBackground("another_feature"), "second");
        release.SetResult();
        await first;

        Assert.Equal(AiRequestStatus.Deferred, second.Outcome.Status);
        Assert.Equal(["hold"], harness.Text.ServedPrompts);
        Assert.Single(await harness.RecordsAsync());
    }

    [Fact]
    public async Task PersonAndIngestion_AreNeverHeldBackByTheMachineRules()
    {
        using var harness = new EngineRoomHarness();
        harness.Admission.Reason = "interactive-traffic";

        var person = await Ask(harness.Room, Person, "person");
        var ingestion = await Ask(harness.Room, Ingestion, "ingestion");

        Assert.Equal(AiRequestStatus.Succeeded, person.Outcome.Status);
        Assert.Equal(AiRequestStatus.Succeeded, ingestion.Outcome.Status);
    }

    [Fact]
    public async Task APlugin_WhenTheMachineRulesSayNotNow_WaitsAndTimesOutAfterItsMaximumWait()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 1;
        harness.Admission.Reason = "interactive-traffic";
        var started = DateTime.UtcNow;

        var result = await Ask(harness.Room, Plugin, "work");

        Assert.Equal(AiRequestStatus.TimedOut, result.Outcome.Status);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(800), "A plugin waits instead of being turned away.");
        Assert.Empty(harness.Text.ServedPrompts);
        var record = Assert.Single(await harness.RecordsAsync());
        Assert.Equal(AiRequestStatus.TimedOut, record.Outcome);
    }

    [Fact]
    public async Task APlugin_WaitsForTheMachineRulesToPass_ThenRuns()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 30;
        harness.Admission.Reason = "interactive-traffic";

        var pending = Ask(harness.Room, Plugin, "work");
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        harness.Admission.Reason = null;

        Assert.Equal(AiRequestStatus.Succeeded, (await pending).Outcome.Status);
    }

    // ── Plugin allowances ────────────────────────────────────────────────────

    [Fact]
    public async Task APluginOverItsRequestsPerHour_IsRefusedWithoutRunning_AndItsHealthIsDegraded()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 2;
        var room = harness.Room;

        await Ask(room, Plugin, "one");
        await Ask(room, Plugin, "two");
        var refused = await Ask(room, Plugin, "three");

        Assert.Equal(AiRequestStatus.RefusedOverBudget, refused.Outcome.Status);
        Assert.Equal("Used its Local AI allowance for this hour", refused.Outcome.Message);
        Assert.Equal(["one", "two"], harness.Text.ServedPrompts);
        var records = await harness.RecordsAsync();
        Assert.Equal(3, records.Count);
        Assert.Equal(AiRequestStatus.RefusedOverBudget, records[0].Outcome);

        var health = await PluginHealthComposer.ComposeAsync(Plugin.Id, [new PluginHealthResult { Status = "healthy" }], harness.Budget, default);
        Assert.Equal("degraded", health.Status);
        Assert.Contains(health.Checks, check => check.Status == "degraded" && check.Message == "Used its Local AI allowance for this hour");
    }

    [Fact]
    public async Task APluginPausedAfterRepeatedFailures_ReportsNeedsAttention_AndHealthyOtherwise()
    {
        using var harness = new EngineRoomHarness();
        var checks = new[] { new PluginHealthResult { Status = "healthy" } };

        var running = await PluginHealthComposer.ComposeAsync(Plugin.Id, checks, harness.Budget, default, pausedUntil: null);
        var paused = await PluginHealthComposer.ComposeAsync(Plugin.Id, checks, harness.Budget, default, pausedUntil: DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal("healthy", running.Status);
        Assert.Equal("degraded", paused.Status);
        Assert.Contains(paused.Checks, check => check.Status == "degraded" && check.Message == PluginHealthComposer.PausedMessage);
    }

    [Fact]
    public async Task APluginOverItsBusySecondsPerHour_IsRefusedWithoutRunning()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.BusySecondsPerHour = 600;
        await harness.Usage.AddAsync(new AiUsageRecord(
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-10), AiCallerKind.Plugin, Plugin.Id,
            AiAbility.WriteTextLong, AiEngine.TextModel, 0, 601_000, AiRequestStatus.Succeeded, null));

        var refused = await Ask(harness.Room, Plugin, "work");

        Assert.Equal(AiRequestStatus.RefusedOverBudget, refused.Outcome.Status);
        Assert.Empty(harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task APluginUnderItsAllowance_HasHealthyHealth()
    {
        using var harness = new EngineRoomHarness();
        await Ask(harness.Room, Plugin, "work");

        var health = await PluginHealthComposer.ComposeAsync(Plugin.Id, [new PluginHealthResult { Status = "healthy" }], harness.Budget, default);

        Assert.Equal("healthy", health.Status);
    }

    [Fact]
    public async Task APerPluginOverride_RaisesThatPluginsLimit_AndNoOthers()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;
        harness.Settings.PluginBudgetOverrides["tuvima.generous"] = new AiPluginBudgetOverride { RequestsPerHour = 3 };
        var generous = AiCaller.Plugin("tuvima.generous");
        var room = harness.Room;

        await Ask(room, Plugin, "default one");
        var defaultRefused = await Ask(room, Plugin, "default two");
        var generousResults = new List<AiRequestStatus>();
        for (var i = 0; i < 4; i++)
        {
            generousResults.Add((await Ask(room, generous, $"generous {i}")).Outcome.Status);
        }

        Assert.Equal(AiRequestStatus.RefusedOverBudget, defaultRefused.Outcome.Status);
        Assert.Equal(
            [AiRequestStatus.Succeeded, AiRequestStatus.Succeeded, AiRequestStatus.Succeeded, AiRequestStatus.RefusedOverBudget],
            generousResults);
    }

    [Fact]
    public async Task PluginAllowances_AreCountedFromTheDurableRecords_SoTheySurviveARestart()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 2;
        using (var before = harness.NewRoom())
        {
            await Ask(before, Plugin, "one");
            await Ask(before, Plugin, "two");
        }

        using var afterRestart = harness.NewRoom();
        var refused = await Ask(afterRestart, Plugin, "three");

        Assert.Equal(AiRequestStatus.RefusedOverBudget, refused.Outcome.Status);
    }

    [Fact]
    public async Task PluginAllowances_CountOnlyTheLastRollingHour()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;
        var clock = new ManualActivityClock(DateTimeOffset.UtcNow);
        harness.Time = clock;
        var room = harness.Room;

        await Ask(room, Plugin, "one");
        var refused = await Ask(room, Plugin, "two");
        clock.Advance(TimeSpan.FromMinutes(61));
        var allowedAgain = await Ask(room, Plugin, "three");

        Assert.Equal(AiRequestStatus.RefusedOverBudget, refused.Outcome.Status);
        Assert.Equal(AiRequestStatus.Succeeded, allowedAgain.Outcome.Status);
    }

    [Fact]
    public async Task OnlyPlugins_AreHeldToAnAllowance()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(AiRequestStatus.Succeeded, (await Ask(harness.Room, Ingestion, $"ingestion {i}")).Outcome.Status);
        }
    }

    // ── One usage record per request ─────────────────────────────────────────

    [Fact]
    public async Task ASuccessfulTextRequest_LeavesOneRecord_WithWhoWhatAndHowLong()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = async (prompt, ct) =>
        {
            await Task.Delay(40, ct);
            return ScriptedTextInference.Success("reply:" + prompt);
        };

        await harness.Room.ExtractJsonAsync<Dictionary<string, string>>(
            Ingestion, AiAbility.ExtractStructured, "work", "root ::= \"{}\"");

        var record = Assert.Single(await harness.RecordsAsync());
        Assert.Equal((AiCallerKind.Ingestion, "test_ingestion", AiAbility.ExtractStructured, AiEngine.TextModel, AiRequestStatus.Succeeded),
            (record.CallerKind, record.CallerId, record.Ability, record.Engine, record.Outcome));
        Assert.True(record.DurationMs >= 30);
        Assert.Null(record.Message);
    }

    [Fact]
    public async Task AFailedTextRequest_LeavesOneFailedRecord_WithoutThePrompt()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = (_, _) => Task.FromResult(ScriptedTextInference.Failure(InferenceOutcomeStatus.Failed, "secret prompt text leaked here"));

        var result = await Ask(harness.Room, Person, "secret prompt text");

        Assert.Equal(AiRequestStatus.Failed, result.Outcome.Status);
        var record = Assert.Single(await harness.RecordsAsync());
        Assert.Equal(AiRequestStatus.Failed, record.Outcome);
        Assert.DoesNotContain("secret", record.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnreadableJsonAnswer_LeavesOneFailedRecord_AndTheCallerCanTellItWasUnreadable()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = (_, _) => Task.FromResult(ScriptedTextInference.Success("not json"));

        var result = await harness.Room.ExtractJsonAsync<Dictionary<string, string>>(
            Ingestion, AiAbility.Classify, "work", "root ::= \"{}\"");

        Assert.Equal(AiRequestStatus.Failed, result.Outcome.Status);
        Assert.True(result.Outcome.InvalidResponse);
        Assert.Null(result.Value);
        Assert.Equal(AiRequestStatus.Failed, Assert.Single(await harness.RecordsAsync()).Outcome);
    }

    [Fact]
    public async Task ATimedOutTextRequest_LeavesOneTimedOutRecord()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = (_, _) => Task.FromResult(ScriptedTextInference.Failure(InferenceOutcomeStatus.TimedOut, "Inference timed out."));

        var result = await Ask(harness.Room, Person, "work");

        Assert.Equal(AiRequestStatus.TimedOut, result.Outcome.Status);
        Assert.Equal(AiRequestStatus.TimedOut, Assert.Single(await harness.RecordsAsync()).Outcome);
    }

    [Fact]
    public async Task ACancelledTextRequest_LeavesOneCancelledRecord_NotPreempted()
    {
        using var harness = new EngineRoomHarness();
        harness.Text.Handler = HoldPrompt("block", new TaskCompletionSource().Task);
        using var cancel = new CancellationTokenSource();

        var pending = Ask(harness.Room, Person, "block", ct: cancel.Token);
        await EngineRoomHarness.WaitUntilAsync(() => harness.Room.TextRequestsRunning == 1, "the request to start");
        cancel.Cancel();
        var result = await pending;

        Assert.Equal(AiRequestStatus.Cancelled, result.Outcome.Status);
        Assert.False(result.Outcome.Preempted);
        Assert.Equal(AiRequestStatus.Cancelled, Assert.Single(await harness.RecordsAsync()).Outcome);
    }

    [Fact]
    public async Task ARefusedPluginRequest_LeavesOneRefusedRecord()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.RequestsPerHour = 1;
        await Ask(harness.Room, Plugin, "one");

        await Ask(harness.Room, Plugin, "two");

        var records = await harness.RecordsAsync();
        Assert.Equal(2, records.Count);
        Assert.Single(records, record => record.Outcome == AiRequestStatus.RefusedOverBudget);
    }

    [Fact]
    public async Task AnAbilityRecordedForLater_AnswersNotAvailableYet_AndLeavesOneRecord()
    {
        using var harness = new EngineRoomHarness();

        var result = await Ask(harness.Room, Plugin, "work", AiAbility.TranscribeSpeech);
        var availability = await harness.Room.GetAvailabilityAsync(AiAbility.DescribeImage);

        Assert.Equal(AiRequestStatus.Unavailable, result.Outcome.Status);
        Assert.Equal("Not available yet", result.Outcome.Message);
        Assert.False(availability.IsAvailable);
        Assert.Equal("Not available yet", availability.Reason);
        var record = Assert.Single(await harness.RecordsAsync());
        Assert.Equal((AiAbility.TranscribeSpeech, AiEngine.None), (record.Ability, record.Engine));
        Assert.Empty(harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task AFailedRecordWrite_NeverFailsTheRequest_ButIsLoggedAsAWarning()
    {
        using var harness = new EngineRoomHarness();
        var logger = new ListLogger<LocalAiEngineRoom>();
        using var room = harness.NewRoom(new ThrowingUsageRepository(), logger);

        var result = await Ask(room, Person, "work");

        Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status);
        Assert.Equal("reply:work", result.Text);
        Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    // ── Text reading ─────────────────────────────────────────────────────────

    private static async Task<string> TempImageAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-read-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1]);
        return path;
    }

    private static AiImageTextRequest Read(string path) => new(path, ["eng"], false, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task TextReading_WithLocalAiOffAndNoModel_StillReadsAndLeavesOneRecord()
    {
        using var harness = new EngineRoomHarness(modelPresent: false, localAiOn: false);
        harness.Runtime.TextRuntimeLoads = false;
        var path = await TempImageAsync();
        try
        {
            var result = await harness.Room.ReadTextFromImageAsync(Person, Read(path));

            Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status);
            Assert.Equal("recognised words number 1", result.Value?.Text);
            var record = Assert.Single(await harness.RecordsAsync());
            Assert.Equal((AiAbility.ReadTextFromImage, AiEngine.Tesseract, AiRequestStatus.Succeeded), (record.Ability, record.Engine, record.Outcome));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TextReading_WithTextReadingSwitchedOff_AnswersUnavailable_AndLeavesOneRecord()
    {
        using var harness = new EngineRoomHarness();
        harness.Configuration.Ocr.Enabled = false;
        var availability = await harness.Room.GetAvailabilityAsync(AiAbility.ReadTextFromImage);

        var result = await harness.Room.ReadTextFromImageAsync(Person, Read("never-read.png"));

        Assert.False(availability.IsAvailable);
        Assert.Equal("Text reading is off", availability.Reason);
        Assert.Equal(AiRequestStatus.Unavailable, result.Outcome.Status);
        Assert.Equal("Text reading is off", result.Outcome.Message);
        Assert.Equal(0, harness.Ocr.Calls);
        Assert.Equal(AiRequestStatus.Unavailable, Assert.Single(await harness.RecordsAsync()).Outcome);
    }

    [Fact]
    public async Task TextReading_WithTesseractMissing_KeepsTheWordingPeopleAlreadySee()
    {
        using var harness = new EngineRoomHarness();
        harness.Ocr.Capability = OcrCapability.Unavailable("not found");

        var availability = await harness.Room.GetAvailabilityAsync(AiAbility.ReadTextFromImage);

        Assert.False(availability.IsAvailable);
        Assert.Equal("Text recognition is not installed.", availability.Reason);
    }

    private static async Task<OcrRecognition> ReadThatEndsBadlyAsync(string how, CancellationToken ct)
    {
        switch (how)
        {
            case "failed":
                throw new InvalidOperationException("engine crashed");
            case "timed_out":
                throw new TimeoutException("too slow");
            default:
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new OcrRecognition("", null, 0);
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("timed_out")]
    [InlineData("cancelled")]
    public async Task ATextReadThatEndsBadly_LeavesOneRecord_AndKeepsTheExceptionsCallersHandle(string how)
    {
        using var harness = new EngineRoomHarness();
        using var cancel = new CancellationTokenSource();
        harness.Ocr.OnRecognize = (_, ct) => ReadThatEndsBadlyAsync(how, ct);
        var path = await TempImageAsync();
        try
        {
            var read = harness.Room.ReadTextFromImageOrThrowAsync(Person, Read(path), cancel.Token);
            if (how == "cancelled")
            {
                await EngineRoomHarness.WaitUntilAsync(() => harness.Room.TextReadingRequestsRunning == 1, "the read to start");
                cancel.Cancel();
            }

            switch (how)
            {
                case "failed":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => read);
                    break;
                case "timed_out":
                    await Assert.ThrowsAsync<TimeoutException>(() => read);
                    break;
                default:
                    await Assert.ThrowsAsync<OperationCanceledException>(() => read);
                    break;
            }

            var expected = how switch
            {
                "failed" => AiRequestStatus.Failed,
                "timed_out" => AiRequestStatus.TimedOut,
                _ => AiRequestStatus.Cancelled,
            };
            Assert.Equal(expected, Assert.Single(await harness.RecordsAsync()).Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Availability (real readiness) ────────────────────────────────────────

    [Theory]
    [InlineData(AiAbility.WriteTextQuick)]
    [InlineData(AiAbility.WriteTextCareful)]
    [InlineData(AiAbility.WriteTextLong)]
    [InlineData(AiAbility.ExtractStructured)]
    [InlineData(AiAbility.Classify)]
    public async Task TextAbilities_WithTheModelFileMissing_AnswerModelNotDownloaded(AiAbility ability)
    {
        using var harness = new EngineRoomHarness(modelPresent: false);

        var availability = await harness.Room.GetAvailabilityAsync(ability);
        var request = await harness.Room.ExtractJsonAsync<Dictionary<string, string>>(
            Ingestion, ability, "work", "root ::= \"{}\"");

        Assert.False(availability.IsAvailable);
        Assert.Equal("Model not downloaded", availability.Reason);
        Assert.Equal(AiRequestStatus.Unavailable, request.Outcome.Status);
        Assert.Equal("Model not downloaded", request.Outcome.Message);
        Assert.Empty(harness.Text.ServedPrompts);
    }

    [Fact]
    public async Task TextAbilities_WithoutTheRuntime_AnswerAiRuntimeNotInstalled()
    {
        using var harness = new EngineRoomHarness();
        harness.Runtime.TextRuntimeLoads = false;

        var availability = await harness.Room.GetAvailabilityAsync(AiAbility.WriteTextQuick);

        Assert.Equal("AI runtime not installed", availability.Reason);
    }

    [Fact]
    public async Task TextAbilities_WithLocalAiOff_AnswerLocalAiIsOff_ForPluginsAndForTheQuestion()
    {
        using var harness = new EngineRoomHarness(localAiOn: false);

        var availability = await harness.Room.GetAvailabilityAsync(AiAbility.WriteTextQuick);
        var plugin = await Ask(harness.Room, Plugin, "work");

        Assert.Equal("Local AI is off", availability.Reason);
        Assert.Equal(AiRequestStatus.Unavailable, plugin.Outcome.Status);
        Assert.Equal("Local AI is off", plugin.Outcome.Message);
    }

    [Fact]
    public async Task TheLibrarysOwnFeatures_KeepTheirOwnSwitches_SoTheHardwareCheckCanRunBeforeAnySwitchIsOn()
    {
        using var harness = new EngineRoomHarness(localAiOn: false);

        var result = await Ask(harness.Room, Person, "benchmark");

        Assert.Equal(AiRequestStatus.Succeeded, result.Outcome.Status);
    }

    [Fact]
    public async Task EveryTextAbilityNowAvailable_IsAvailableWhenEverythingIsInPlace()
    {
        using var harness = new EngineRoomHarness();

        foreach (var info in AiAbilityCatalog.All.Where(entry => entry.IsAvailableNow))
        {
            Assert.True((await harness.Room.GetAvailabilityAsync(info.Ability)).IsAvailable, info.Ability.ToString());
        }
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestOptions_AreForwardedToTheModel_AndOtherwiseLeftToTheModelsDefaults()
    {
        using var harness = new EngineRoomHarness();

        await harness.Room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "limited", options: new AiRequestOptions(MaxTokens: 64));
        await harness.Room.WriteTextAsync(Plugin, AiAbility.WriteTextQuick, "plain");

        var limited = harness.Text.Calls[0];
        Assert.Equal(AiModelRole.TextFast, limited.Role);
        Assert.Equal(64, limited.Options?.MaxTokens);
        Assert.Equal(harness.Inventory.GetDefinition(AiModelRole.TextFast).Temperature, limited.Options?.Temperature);
        Assert.Null(harness.Text.Calls[1].Options);
    }

    [Theory]
    [InlineData(AiAbility.WriteTextQuick, AiModelRole.TextFast)]
    [InlineData(AiAbility.WriteTextCareful, AiModelRole.TextQuality)]
    [InlineData(AiAbility.WriteTextLong, AiModelRole.TextScholar)]
    public async Task Abilities_ReachTheModelRoleTheCatalogChooses(AiAbility ability, AiModelRole role)
    {
        using var harness = new EngineRoomHarness();

        await Ask(harness.Room, Person, "work", ability);

        Assert.Equal(role, Assert.Single(harness.Text.Calls).Role);
    }

    [Fact]
    public async Task StructuredAbilities_RequireAGrammar()
    {
        using var harness = new EngineRoomHarness();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Room.WriteTextAsync(Person, AiAbility.ExtractStructured, "work"));
    }
}
