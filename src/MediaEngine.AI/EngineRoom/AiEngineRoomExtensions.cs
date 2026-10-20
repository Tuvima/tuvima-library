using System.Text.Json;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.AI.EngineRoom;

/// <summary>The engine room turned a Library background request away because the machine is busy; try again later.</summary>
public sealed class AiRequestDeferredException(string message) : InvalidOperationException(message);

/// <summary>A plugin's request was refused because it used up its Local AI allowance for the hour.</summary>
public sealed class AiRequestRefusedException(string message) : InvalidOperationException(message);

/// <summary>A person using the app made this request step aside; the work can be retried later.</summary>
public sealed class AiRequestPreemptedException(string message) : OperationCanceledException(message);

/// <summary>
/// Call-site adapters that turn an engine room outcome back into what callers of the old inference service saw: the
/// value, <c>null</c> for an unreadable answer, or an exception. Callers keep their existing catch and fall-back logic.
/// </summary>
public static class AiEngineRoomExtensions
{
    /// <summary>Writes text; throws on any outcome other than success.</summary>
    public static async Task<string> WriteTextOrThrowAsync(
        this IAiEngineRoom room,
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string? grammar = null,
        AiRequestOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        var result = await room.WriteTextAsync(caller, ability, prompt, grammar, options, ct).ConfigureAwait(false);
        if (result.Outcome.IsSuccess && result.Text is not null)
        {
            return result.Text;
        }

        throw ToException(result.Outcome, ct);
    }

    /// <summary>Extracts JSON; returns null for an unreadable answer and throws on any other non-success outcome.</summary>
    public static async Task<T?> ExtractJsonOrThrowAsync<T>(
        this IAiEngineRoom room,
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string grammar,
        AiRequestOptions? options = null,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(room);
        var result = await room.ExtractJsonAsync<T>(caller, ability, prompt, grammar, options, ct).ConfigureAwait(false);
        if (result.Outcome.IsSuccess)
        {
            return result.Value;
        }

        if (result.Outcome.InvalidResponse)
        {
            return null;
        }

        throw ToException(result.Outcome, ct);
    }

    /// <summary>Reads text from an image; throws the exceptions the Tesseract service itself throws.</summary>
    public static async Task<AiImageText> ReadTextFromImageOrThrowAsync(
        this IAiEngineRoom room,
        AiCaller caller,
        AiImageTextRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        var result = await room.ReadTextFromImageAsync(caller, request, ct).ConfigureAwait(false);
        if (result.Outcome.IsSuccess && result.Value is not null)
        {
            return result.Value;
        }

        throw ToException(result.Outcome, ct);
    }

    /// <summary>The exception a failed outcome becomes. Cancelled work caused by the caller's own token keeps that token.</summary>
    public static Exception ToException(AiRequestOutcome outcome, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status switch
        {
            AiRequestStatus.Cancelled when outcome.Preempted => new AiRequestPreemptedException(outcome.Message),
            AiRequestStatus.Cancelled => new OperationCanceledException(outcome.Message, callerToken),
            AiRequestStatus.TimedOut => new TimeoutException(outcome.Message),
            AiRequestStatus.Unavailable => new UnavailableCapabilityException(outcome.Message),
            AiRequestStatus.RefusedOverBudget => new AiRequestRefusedException(outcome.Message),
            AiRequestStatus.Deferred => new AiRequestDeferredException(outcome.Message),
            _ when outcome.InvalidResponse => new JsonException(outcome.Message),
            _ => new InvalidOperationException(outcome.Message),
        };
    }
}
