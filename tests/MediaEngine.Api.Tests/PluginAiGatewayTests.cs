using MediaEngine.AI.EngineRoom;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Enums;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The door plugins that are not C# code use to reach Local AI: the declared permission is checked first, an unavailable engine
/// is a plain status rather than an error, and a flood of plugin requests queues behind the Library's own work.
/// </summary>
public sealed class PluginAiGatewayTests
{
    private const string PluginId = "tuvima.test.json";

    private static PluginAiGateway Gateway(IAiEngineRoom room, params string[] allowedRoles) =>
        new(new RoleGate(allowedRoles), room);

    [Fact]
    public async Task APluginWithTheDeclaredRole_GetsAnAnswerWhenLocalAiIsOn()
    {
        var room = new ScriptedRoom(new AiTextResult("a calm, quiet story", Success));

        var result = await Gateway(room, "text_fast").InferTextAsync(PluginId, "text_fast", "Describe it.");

        Assert.Equal(PluginAiStatus.Succeeded, result.Status);
        Assert.Equal("a calm, quiet story", result.Text);
        var request = Assert.Single(room.Requests);
        Assert.Equal(AiCaller.Plugin(PluginId), request.Caller);
        Assert.Equal(AiAbility.WriteTextQuick, request.Ability);
    }

    [Fact]
    public async Task WithLocalAiOff_TheAnswerIsAClearAiUnavailableStatus_NotAnError()
    {
        var room = new ScriptedRoom(new AiTextResult(null,
            new AiRequestOutcome(AiRequestStatus.Unavailable, "Local AI is switched off.")));

        var result = await Gateway(room, "text_fast").InferTextAsync(PluginId, "text_fast", "Describe it.");

        Assert.Equal(PluginAiStatus.AiUnavailable, result.Status);
        Assert.Equal("Local AI is switched off.", result.Message);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task APluginWithoutThePermission_IsRefused_AndLocalAiIsNeverAsked()
    {
        var room = new ScriptedRoom(new AiTextResult("never", Success));

        var result = await Gateway(room).InferTextAsync(PluginId, "text_fast", "Describe it.");

        Assert.Equal(PluginAiStatus.Refused, result.Status);
        Assert.Empty(room.Requests);
    }

    [Fact]
    public async Task ARoleThePluginNeverDeclared_IsRefused()
    {
        var room = new ScriptedRoom(new AiTextResult("never", Success));

        var result = await Gateway(room, "text_fast").InferTextAsync(PluginId, "text_scholar", "Describe it.");

        Assert.Equal(PluginAiStatus.Refused, result.Status);
        Assert.Empty(room.Requests);
    }

    [Theory]
    [InlineData("vision")]
    [InlineData("audio")]
    [InlineData("poetry")]
    public async Task ARoleLocalAiDoesNotOffer_IsRefusedWithAPlainMessage(string role)
    {
        var room = new ScriptedRoom(new AiTextResult("never", Success));

        var result = await Gateway(room, role).InferTextAsync(PluginId, role, "Describe it.");

        Assert.Equal(PluginAiStatus.Refused, result.Status);
        Assert.Contains(role, result.Message, StringComparison.Ordinal);
        Assert.Empty(room.Requests);
    }

    [Theory]
    [InlineData(AiRequestStatus.RefusedOverBudget, "OverAllowance")]
    [InlineData(AiRequestStatus.Deferred, "Busy")]
    [InlineData(AiRequestStatus.Cancelled, "Busy")]
    [InlineData(AiRequestStatus.TimedOut, "TimedOut")]
    [InlineData(AiRequestStatus.Failed, "Failed")]
    public async Task EveryEngineOutcome_ComesBackAsOneStatusWithTheEngineRoomsOwnWords(AiRequestStatus engine, string expected)
    {
        var room = new ScriptedRoom(new AiTextResult(null, new AiRequestOutcome(engine, "In the engine room's words.")));

        var result = await Gateway(room, "text_fast").InferTextAsync(PluginId, "text_fast", "Describe it.");

        Assert.Equal(Enum.Parse<PluginAiStatus>(expected), result.Status);
        Assert.Equal("In the engine room's words.", result.Message);
    }

    [Fact]
    public async Task AJsonAnswer_IsCheckedToBeJson()
    {
        var good = await Gateway(new ScriptedRoom(new AiTextResult("""{"tags":["calm"]}""", Success)), "text_fast")
            .InferJsonAsync(PluginId, "text_fast", "Tag it.", "root ::= object");
        var bad = await Gateway(new ScriptedRoom(new AiTextResult("not json at all", Success)), "text_fast")
            .InferJsonAsync(PluginId, "text_fast", "Tag it.", "root ::= object");

        Assert.Equal(PluginAiStatus.Succeeded, good.Status);
        Assert.Equal("""{"tags":["calm"]}""", good.Text);
        Assert.Equal(PluginAiStatus.InvalidAnswer, bad.Status);
        Assert.Null(bad.Text);
    }

    [Fact]
    public async Task AvailabilityIsAnswered_WithoutSpendingARequest()
    {
        var off = new ScriptedRoom(new AiTextResult("x", Success)) { Availability = AiAvailability.Unavailable("No model is installed yet.") };
        var on = new ScriptedRoom(new AiTextResult("x", Success));

        var unavailable = await Gateway(off, "text_fast").CheckAvailabilityAsync(PluginId, "text_fast");
        var available = await Gateway(on, "text_fast").CheckAvailabilityAsync(PluginId, "text_fast");
        var refused = await Gateway(on).CheckAvailabilityAsync(PluginId, "text_fast");

        Assert.Equal(PluginAiStatus.AiUnavailable, unavailable.Status);
        Assert.Equal("No model is installed yet.", unavailable.Message);
        Assert.Equal(PluginAiStatus.Succeeded, available.Status);
        Assert.Equal(PluginAiStatus.Refused, refused.Status);
        Assert.Empty(off.Requests);
        Assert.Empty(on.Requests);
    }

    [Fact]
    public async Task AFloodOfPluginRequests_DoesNotMakeAnIngestionRequestWait()
    {
        using var harness = new EngineRoomHarness();
        harness.Settings.PluginBudget.MaxWaitSeconds = 60;
        var release = new TaskCompletionSource();
        harness.Text.Handler = async (prompt, ct) =>
        {
            if (prompt == "hold")
            {
                await release.Task.WaitAsync(ct);
            }

            return ScriptedTextInference.Success("reply:" + prompt);
        };
        var room = harness.Room;
        var gateway = Gateway(room, "text_fast");

        var holder = room.WriteTextAsync(AiCaller.Ingestion("test_ingestion"), AiAbility.WriteTextQuick, "hold");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsRunning == 1, "the holder to start");
        var flood = Enumerable.Range(0, 20)
            .Select(index => gateway.InferTextAsync(PluginId, "text_fast", $"plugin-{index}"))
            .ToArray();
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 20, "the flood to queue");
        var ingestion = room.WriteTextAsync(AiCaller.Ingestion("test_ingestion"), AiAbility.WriteTextQuick, "ingestion");
        await EngineRoomHarness.WaitUntilAsync(() => room.TextRequestsWaiting == 21, "ingestion to queue");

        release.SetResult();
        await Task.WhenAll(flood.Cast<Task>().Append(holder).Append(ingestion));

        Assert.Equal("ingestion", harness.Text.ServedPrompts[1]);
        Assert.All(flood, task => Assert.Equal(PluginAiStatus.Succeeded, task.Result.Status));
    }

    private static AiRequestOutcome Success { get; } = new(AiRequestStatus.Succeeded, "Done.");

    private sealed class RoleGate(string[] allowedRoles) : IPluginPermissionGate
    {
        public PluginRegistration Demand(string pluginId, string permission) => throw new NotSupportedException();

        public PluginToolRequirement DemandTool(string pluginId, string toolId) => throw new NotSupportedException();

        public PluginAiPermission DemandAi(string pluginId, string role, PluginAiOptions? options) =>
            allowedRoles.Contains(role, StringComparer.Ordinal)
                ? new PluginAiPermission { Role = role, MaxTokens = 512 }
                : throw new UnauthorizedAccessException($"Plugin '{pluginId}' is not allowed to use AI role '{role}'.");
    }

    private sealed class ScriptedRoom(AiTextResult answer) : IAiEngineRoom
    {
        public List<(AiCaller Caller, AiAbility Ability)> Requests { get; } = [];

        public AiAvailability Availability { get; set; } = AiAvailability.Available;

        public Task<AiTextResult> WriteTextAsync(
            AiCaller caller, AiAbility ability, string prompt, string? grammar = null, AiRequestOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add((caller, ability));
            return Task.FromResult(answer);
        }

        public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
            AiCaller caller, AiAbility ability, string prompt, string grammar, AiRequestOptions? options = null, CancellationToken ct = default) where T : class =>
            throw new NotSupportedException();

        public Task<AiImageTextResult> ReadTextFromImageAsync(AiCaller caller, AiImageTextRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
            Task.FromResult(Availability);
    }
}
