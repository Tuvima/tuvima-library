using System.Text.Json;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Tests;

/// <summary>One request a feature made to <see cref="StubAiEngineRoom"/>.</summary>
internal sealed record StubEngineRoomRequest(AiCaller Caller, AiAbility Ability, string Prompt, string? Grammar);

/// <summary>
/// Hand-written test double for <see cref="IAiEngineRoom"/>. Answers with a pre-configured JSON string, an empty answer,
/// or a failing outcome, without loading any model, and remembers who asked for what.
/// </summary>
internal sealed class StubAiEngineRoom : IAiEngineRoom
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _jsonResponse;
    private readonly AiRequestOutcome? _failure;

    private StubAiEngineRoom(string? jsonResponse, AiRequestOutcome? failure)
    {
        _jsonResponse = jsonResponse;
        _failure = failure;
    }

    public List<StubEngineRoomRequest> Requests { get; } = [];

    public static StubAiEngineRoom ReturningJson(string json) => new(json, null);

    public static StubAiEngineRoom ReturningNull() => new(null, null);

    public static StubAiEngineRoom Failing(AiRequestStatus status, string message) =>
        new(null, new AiRequestOutcome(status, message));

    public static StubAiEngineRoom Failing(AiRequestOutcome outcome) => new(null, outcome);

    public Task<AiTextResult> WriteTextAsync(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string? grammar = null,
        AiRequestOptions? options = null,
        CancellationToken ct = default)
    {
        Requests.Add(new StubEngineRoomRequest(caller, ability, prompt, grammar));
        return Task.FromResult(_failure is null
            ? new AiTextResult(_jsonResponse ?? string.Empty, new AiRequestOutcome(AiRequestStatus.Succeeded, "Done"))
            : new AiTextResult(null, _failure));
    }

    public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string grammar,
        AiRequestOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        Requests.Add(new StubEngineRoomRequest(caller, ability, prompt, grammar));
        if (_failure is not null)
        {
            return Task.FromResult(new AiJsonResult<T>(null, _failure));
        }

        var unreadable = new AiJsonResult<T>(
            null,
            new AiRequestOutcome(AiRequestStatus.Failed, "The model did not return valid JSON", InvalidResponse: true));
        if (_jsonResponse is null)
        {
            return Task.FromResult(unreadable);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(_jsonResponse, JsonOptions);
            return Task.FromResult(value is null
                ? unreadable
                : new AiJsonResult<T>(value, new AiRequestOutcome(AiRequestStatus.Succeeded, "Done")));
        }
        catch (JsonException)
        {
            return Task.FromResult(unreadable);
        }
    }

    public Task<AiImageTextResult> ReadTextFromImageAsync(
        AiCaller caller,
        AiImageTextRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This test double does not read images.");

    public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
        Task.FromResult(AiAvailability.Available);
}
