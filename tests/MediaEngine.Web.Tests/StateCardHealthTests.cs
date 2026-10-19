using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The four health words (Working, Off, Needs attention, Broken) and the plain words on a state card, mapped from each
/// area's existing status. Anything switched off is Off and never red; Broken means switched on and unable to work.
/// </summary>
public sealed class StateCardHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public void Plugin_SwitchedOff_IsOff_EvenWithALoadError()
    {
        var result = StateCardHealth.ForPlugin(Plugin(enabled: false, loadError: "Could not load."), null, null);

        Assert.Equal(HealthWord.Off, result.Word);
        Assert.Null(result.LastProblem);
    }

    [Fact]
    public void Plugin_SwitchedOff_IsOff_EvenWhenIncompatibleOrDegradedOrFailing()
    {
        var plugin = Plugin(enabled: false, compatible: false, compatibilityProblem: "Needs a newer Library.");

        Assert.Equal(HealthWord.Off, StateCardHealth.ForPlugin(plugin, Health("degraded"), Run("failed")).Word);
    }

    [Fact]
    public void Plugin_SwitchedOnWithALoadError_IsBroken_WithTheLoadErrorAsTheLastProblem()
    {
        var result = StateCardHealth.ForPlugin(Plugin(enabled: true, loadError: "The manifest is missing."), Health("healthy"), null);

        Assert.Equal(HealthWord.Broken, result.Word);
        Assert.Equal("The manifest is missing.", result.LastProblem);
    }

    [Fact]
    public void Plugin_SwitchedOnButIncompatible_IsBroken_WithTheCompatibilityProblem()
    {
        var result = StateCardHealth.ForPlugin(Plugin(enabled: true, compatible: false, compatibilityProblem: "Needs Library 2.0."), null, null);

        Assert.Equal(HealthWord.Broken, result.Word);
        Assert.Equal("Needs Library 2.0.", result.LastProblem);
    }

    [Fact]
    public void Plugin_SwitchedOnAndDegraded_NeedsAttention_WithTheHealthMessage()
    {
        var health = Health("degraded", "FFmpeg is missing.");

        var result = StateCardHealth.ForPlugin(Plugin(enabled: true), health, null);

        Assert.Equal(HealthWord.NeedsAttention, result.Word);
        Assert.Equal("FFmpeg is missing.", result.LastProblem);
    }

    [Fact]
    public void Plugin_SwitchedOnWhoseLatestRunFailed_NeedsAttention_WithTheOutcomeText()
    {
        var result = StateCardHealth.ForPlugin(Plugin(enabled: true), Health("healthy"), Run("failed", "The data store was busy."));

        Assert.Equal(HealthWord.NeedsAttention, result.Word);
        Assert.Equal("The data store was busy.", result.LastProblem);
    }

    [Theory]
    [InlineData("healthy", "succeeded")]
    [InlineData("unknown", "succeeded")]
    [InlineData("unknown", "skipped")]
    [InlineData("healthy", "running")]
    public void Plugin_SwitchedOnAndNothingWrong_IsWorking(string healthStatus, string outcome)
    {
        var result = StateCardHealth.ForPlugin(Plugin(enabled: true), Health(healthStatus), Run(outcome));

        Assert.Equal(HealthWord.Working, result.Word);
        Assert.Null(result.LastProblem);
    }

    [Fact]
    public void Plugin_SwitchedOnWithNoHealthCheckYetAndNoRuns_IsWorking()
    {
        Assert.Equal(HealthWord.Working, StateCardHealth.ForPlugin(Plugin(enabled: true), null, null).Word);
    }

    [Fact]
    public void LatestRun_PicksTheNewestRecordForThatPluginOnly()
    {
        var tasks = new List<ScheduledTaskDto>
        {
            TaskFor("older", "vibe", new ScheduledTaskLastRunDto { StartedAt = Now.AddHours(-5), FinishedAt = Now.AddHours(-4), Outcome = "succeeded" }),
            TaskFor("newer", "vibe", new ScheduledTaskLastRunDto { StartedAt = Now.AddHours(-2), FinishedAt = Now.AddHours(-1), Outcome = "failed" }),
            TaskFor("other", "tldr", new ScheduledTaskLastRunDto { StartedAt = Now, FinishedAt = Now, Outcome = "succeeded" }),
            TaskFor("never", "vibe", null),
        };

        var latest = StateCardHealth.LatestRun(tasks, "vibe");

        Assert.Equal("failed", latest!.Outcome);
        Assert.Equal(Now.AddHours(-1), StateCardHealth.ActivityTime(latest));
        Assert.Null(StateCardHealth.LatestRun(tasks, "nobody"));
        Assert.Null(StateCardHealth.ActivityTime(null));
    }

    [Fact]
    public void ActivityTime_FallsBackToTheStartTime_WhileARunIsStillGoing()
    {
        var run = new ScheduledTaskLastRunDto { StartedAt = Now, FinishedAt = null, Outcome = "running" };

        Assert.Equal(Now, StateCardHealth.ActivityTime(run));
    }

    [Fact]
    public void IsPluginBroken_OnlyForSwitchedOnPluginsThatCannotWork()
    {
        Assert.True(StateCardHealth.IsPluginBroken(Plugin(enabled: true, loadError: "Broken.")));
        Assert.False(StateCardHealth.IsPluginBroken(Plugin(enabled: false, loadError: "Broken.")));
        Assert.False(StateCardHealth.IsPluginBroken(Plugin(enabled: true)));
    }

    // ── Local AI features and engine ─────────────────────────────────────────

    [Fact]
    public void AiFeature_FlagOff_IsOff_EvenWhileLocalAiIsBlocked()
    {
        Assert.Equal(HealthWord.Off, StateCardHealth.ForAiFeature(enabled: false, localAiBlocked: true, "Local AI engine is missing", modelReady: false).Word);
    }

    [Fact]
    public void AiFeature_FlagOnWhileLocalAiIsBlocked_IsBroken_WithTheFirstBlocker()
    {
        var result = StateCardHealth.ForAiFeature(enabled: true, localAiBlocked: true, "Local AI engine is missing", modelReady: true);

        Assert.Equal(HealthWord.Broken, result.Word);
        Assert.Equal("Local AI engine is missing", result.LastProblem);
    }

    [Fact]
    public void AiFeature_FlagOnWithItsModelNotReady_NeedsAttention_WithModelNotReady()
    {
        var result = StateCardHealth.ForAiFeature(enabled: true, localAiBlocked: false, null, modelReady: false);

        Assert.Equal(HealthWord.NeedsAttention, result.Word);
        Assert.Equal("Model not ready", result.LastProblem);
    }

    [Fact]
    public void AiFeature_FlagOnWithEverythingReady_IsWorking()
    {
        Assert.Equal(HealthWord.Working, StateCardHealth.ForAiFeature(enabled: true, localAiBlocked: false, null, modelReady: true).Word);
    }

    [Theory]
    [InlineData("Ready", false, true)]
    [InlineData("Loaded", false, true)]
    [InlineData("Downloading", false, false)]
    [InlineData("NotDownloaded", false, false)]
    [InlineData("Error", false, false)]
    [InlineData("NotDownloaded", true, true)]
    public void IsModelReady_IsTrueForReadyOrLoadedModelsOnly(string state, bool loaded, bool expected)
    {
        Assert.Equal(expected, StateCardHealth.IsModelReady(new AiModelStatusDto { State = state, Loaded = loaded }));
        Assert.False(StateCardHealth.IsModelReady(null));
    }

    [Fact]
    public void LocalAiEngine_Blocked_IsBroken()
    {
        var result = StateCardHealth.ForLocalAiEngine(localAiBlocked: true, "Local AI text model is missing", new AiModelStatusDto { State = "Ready" });

        Assert.Equal(HealthWord.Broken, result.Word);
        Assert.Equal("Local AI text model is missing", result.LastProblem);
    }

    [Fact]
    public void LocalAiEngine_ModelDownloading_NeedsAttention()
    {
        Assert.Equal(HealthWord.NeedsAttention, StateCardHealth.ForLocalAiEngine(false, null, new AiModelStatusDto { State = "Downloading" }).Word);
    }

    [Fact]
    public void LocalAiEngine_ModelInError_IsBroken()
    {
        Assert.Equal(HealthWord.Broken, StateCardHealth.ForLocalAiEngine(false, null, new AiModelStatusDto { State = "Error" }).Word);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Loaded")]
    [InlineData("NotDownloaded")]
    public void LocalAiEngine_OtherwiseWorking_AndNeverOff(string state)
    {
        Assert.Equal(HealthWord.Working, StateCardHealth.ForLocalAiEngine(false, null, new AiModelStatusDto { State = state }).Word);
        Assert.Equal(HealthWord.Working, StateCardHealth.ForLocalAiEngine(false, null, null).Word);
    }

    [Theory]
    [InlineData(ToolRequirementStates.Missing, "Local AI engine is missing")]
    [InlineData(ToolRequirementStates.Problem, "Local AI engine is not working")]
    [InlineData(ToolRequirementStates.Downloading, "Local AI engine is still downloading")]
    public void BlockerText_SaysInPlainWordsWhatStopsLocalAi(string state, string expected)
    {
        var tool = new ToolRequirementDto(ToolRequirementIds.AiRuntime, "Local AI engine", "tool", ToolRequirementLevels.Required, state,
            "", null, "", null, 0, false, false, false, null);

        Assert.Equal(expected, StateCardHealth.BlockerText(tool));
    }

    // ── The words ────────────────────────────────────────────────────────────

    [Fact]
    public void TheFourHealthWords_HaveTheirTextAndTone_AndOnlyBrokenIsRed()
    {
        Assert.Equal(("Working", AppUiTone.Success), (StateCardHealth.Text(HealthWord.Working), StateCardHealth.Tone(HealthWord.Working)));
        Assert.Equal(("Off", AppUiTone.Neutral), (StateCardHealth.Text(HealthWord.Off), StateCardHealth.Tone(HealthWord.Off)));
        Assert.Equal(("Needs attention", AppUiTone.Warning), (StateCardHealth.Text(HealthWord.NeedsAttention), StateCardHealth.Tone(HealthWord.NeedsAttention)));
        Assert.Equal(("Broken", AppUiTone.Error), (StateCardHealth.Text(HealthWord.Broken), StateCardHealth.Tone(HealthWord.Broken)));
    }

    [Theory]
    [InlineData(StateCardKind.Plugin, "Plugin")]
    [InlineData(StateCardKind.Integration, "Integration")]
    [InlineData(StateCardKind.Tester, "Tester")]
    [InlineData(StateCardKind.LocalAiEngine, "Local AI engine")]
    public void EveryKind_HasItsLabel(StateCardKind kind, string label)
    {
        Assert.Equal(label, StateCardHealth.KindLabel(kind));
    }

    [Theory]
    [InlineData("media.read", "Reads your media files")]
    [InlineData("network.http", "Connects to the internet")]
    [InlineData("process.execute", "Runs helper programs")]
    [InlineData("tool.download", "Downloads helper tools")]
    [InlineData("ai.infer", "Uses Local AI")]
    [InlineData("storage.plugin", "Keeps its own data")]
    [InlineData("secrets.plugin", "Keeps its own keys and logins")]
    public void PermissionIds_AreShownInPlainWords(string id, string words)
    {
        Assert.Equal(words, StateCardHealth.PermissionText(id));
    }

    [Fact]
    public void AnUnknownPermissionId_IsShownAsItIs()
    {
        Assert.Equal("future.thing", StateCardHealth.PermissionText("future.thing"));
    }

    [Fact]
    public void PluginAllowedTo_ListsEachPermissionInPlainWords()
    {
        var plugin = Plugin(enabled: true, permissions: ["media.read", "ai.infer", "odd.id"]);

        Assert.Equal(["Reads your media files", "Uses Local AI", "odd.id"], StateCardHealth.PluginAllowedTo(plugin));
    }

    [Fact]
    public void ThePluginSendsDataOut_OnlyWhenItCanConnectToTheInternet()
    {
        Assert.True(StateCardHealth.PluginSendsDataOut(Plugin(enabled: true, permissions: ["media.read", "network.http"])));
        Assert.False(StateCardHealth.PluginSendsDataOut(Plugin(enabled: true, permissions: ["media.read"])));
        Assert.Equal("the internet", StateCardHealth.TheInternet);
    }

    [Theory]
    [InlineData("smart_labeling", "Reads file and folder names", "Suggests titles, authors, years and episode numbers when files are added")]
    [InlineData("type_logic", "Reads file names and basic file facts", "Decides the media type when it is unclear")]
    [InlineData("series_alignment", "Reads series and title details", "Suggests the order within a series")]
    [InlineData("vibe_tags", "Reads descriptions and genres", "Adds mood tags")]
    [InlineData("description_intelligence", "Reads descriptions", "Adds themes, mood, setting, period, audience, pace, content warnings and people")]
    public void EachAiFeature_HasItsPlainWords(string key, string first, string second)
    {
        Assert.Equal([first, second], StateCardHealth.AiFeatureAllowedTo(key));
    }

    [Fact]
    public void TheShortSummaryFeature_AndTheEngine_HaveTheirPlainWords()
    {
        Assert.Equal(["Shows the short summary made by Description insights"], StateCardHealth.AiFeatureAllowedTo("tldr"));
        Assert.Equal(["Runs AI models on this machine"], StateCardHealth.EngineAllowedTo);
        Assert.Equal("Runs on this machine", StateCardHealth.RunsOnThisMachine);
        Assert.Empty(StateCardHealth.AiFeatureAllowedTo("not_a_feature"));
    }

    [Theory]
    [InlineData(20, "Just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(300, "5 minutes ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(172800, "2 days ago")]
    public void RelativeTime_SaysHowLongAgoInPlainWords(int secondsAgo, string expected)
    {
        Assert.Equal(expected, StateCardHealth.RelativeTime(Now.AddSeconds(-secondsAgo), Now));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PluginSummaryResponse Plugin(
        bool enabled,
        string? loadError = null,
        bool compatible = true,
        string? compatibilityProblem = null,
        List<string>? permissions = null) =>
        new()
        {
            Id = "test.plugin",
            Name = "Test plugin",
            Enabled = enabled,
            LoadError = loadError,
            IsCompatible = compatible,
            CompatibilityProblem = compatibilityProblem,
            Permissions = permissions ?? [],
        };

    private static PluginHealthResponse Health(string status, string? message = null) =>
        new()
        {
            PluginId = "test.plugin",
            Status = status,
            Checks = message is null ? [] : [new PluginHealthCheckDto { Status = status, Message = message }],
        };

    private static ScheduledTaskLastRunDto Run(string outcome, string? message = null) =>
        new() { StartedAt = Now.AddMinutes(-10), FinishedAt = Now.AddMinutes(-9), Outcome = outcome, Message = message };

    private static ScheduledTaskDto TaskFor(string key, string pluginId, ScheduledTaskLastRunDto? run) =>
        new() { Key = key, PluginId = pluginId, LastRun = run };
}
