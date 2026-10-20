using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.EngineRoom;

/// <summary>
/// Who is asking the engine room. <paramref name="Id"/> is a stable key: a feature key such as
/// <see cref="AiCallerIds.SmartLabeling"/>, a text-reading key, or the plugin manifest id.
/// </summary>
public sealed record AiCaller(AiCallerKind Kind, string Id)
{
    public static AiCaller Person(string id) => new(AiCallerKind.Person, id);

    public static AiCaller Ingestion(string id) => new(AiCallerKind.Ingestion, id);

    public static AiCaller LibraryBackground(string id) => new(AiCallerKind.LibraryBackground, id);

    public static AiCaller Plugin(string pluginId) => new(AiCallerKind.Plugin, pluginId);
}

/// <summary>Stable caller keys for the Library's own features.</summary>
public static class AiCallerIds
{
    public const string SmartLabeling = "smart_labeling";
    public const string TypeLogic = "type_logic";
    public const string SeriesAlignment = "series_alignment";
    public const string VibeTags = "vibe_tags";
    public const string DescriptionIntelligence = "description_intelligence";
    public const string TasteSummary = "taste_summary";
    public const string HardwareBenchmark = "hardware_benchmark";
    public const string DocumentTextReading = "document_text_reading";
}

/// <summary>Per-request limits. A null value uses the engine room's default for the ability.</summary>
public sealed record AiRequestOptions(int? MaxTokens = null, double? Temperature = null, TimeSpan? Timeout = null);

/// <summary>How one request ended, in plain words, with how long it waited and ran.</summary>
/// <param name="Message">A plain-English reason or result. Never the prompt or the text read.</param>
/// <param name="InvalidResponse">True when the model answered but not with the JSON that was asked for.</param>
/// <param name="Preempted">True when a person's request made this one step aside (the status is then Cancelled).</param>
public sealed record AiRequestOutcome(
    AiRequestStatus Status,
    string Message,
    TimeSpan Wait = default,
    TimeSpan Duration = default,
    bool InvalidResponse = false,
    bool Preempted = false)
{
    public bool IsSuccess => Status == AiRequestStatus.Succeeded;
}

public sealed record AiTextResult(string? Text, AiRequestOutcome Outcome);

public sealed record AiJsonResult<T>(T? Value, AiRequestOutcome Outcome) where T : class;

/// <summary>One prepared image to read, with how the reader should run.</summary>
public sealed record AiImageTextRequest(
    string ImagePath,
    IReadOnlyList<string> Languages,
    bool LowPriority,
    TimeSpan Timeout);

/// <summary>The text read from one image and how sure the reader was (0-100), when it reports that.</summary>
public sealed record AiImageText(string Text, double? AverageConfidence, int WordCount);

public sealed record AiImageTextResult(AiImageText? Value, AiRequestOutcome Outcome);

/// <summary>Whether an ability can be used right now; when not, the plain reason.</summary>
public sealed record AiAvailability(bool IsAvailable, string? Reason)
{
    public static AiAvailability Available { get; } = new(true, null);

    public static AiAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>
/// The one door to the machine's local AI and text-reading engines. Callers ask for an ability, never a model.
/// Every request joins one queue per engine (a person, then ingestion, then Library background work, then plugins),
/// is checked against the machine's rules and, for plugins, an hourly allowance, and leaves a usage record.
/// </summary>
public interface IAiEngineRoom
{
    /// <summary>Writes text with a text ability. An optional grammar constrains the output.</summary>
    Task<AiTextResult> WriteTextAsync(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string? grammar = null,
        AiRequestOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Writes grammar-constrained text with a text ability and reads it as JSON.</summary>
    Task<AiJsonResult<T>> ExtractJsonAsync<T>(
        AiCaller caller,
        AiAbility ability,
        string prompt,
        string grammar,
        AiRequestOptions? options = null,
        CancellationToken ct = default) where T : class;

    /// <summary>Reads the words in one prepared image with Tesseract. Works with Local AI off.</summary>
    Task<AiImageTextResult> ReadTextFromImageAsync(
        AiCaller caller,
        AiImageTextRequest request,
        CancellationToken ct = default);

    /// <summary>Whether an ability can be used right now, for a plugin or a screen to show.</summary>
    Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default);
}
