namespace MediaEngine.Domain.Enums;

/// <summary>
/// What a caller asks the Local AI engine room to do. Callers ask for an ability, never for a model; the engine room
/// decides which engine serves it (<see cref="AiEngine"/>), so a model can change without breaking a caller.
/// </summary>
public enum AiAbility
{
    /// <summary>Write text, quickly (short answers, small tasks).</summary>
    WriteTextQuick = 1,

    /// <summary>Write text, carefully (balanced quality for batch and ingestion work).</summary>
    WriteTextCareful = 2,

    /// <summary>Write text, at length (deep analysis; slow, scheduled use).</summary>
    WriteTextLong = 3,

    /// <summary>Pull structured facts out of text. Always carries a grammar.</summary>
    ExtractStructured = 4,

    /// <summary>Choose a label or a few labels for some text. Always carries a grammar.</summary>
    Classify = 5,

    /// <summary>Read the words in a picture of a page or a photo. Served by Tesseract, which works with Local AI off.</summary>
    ReadTextFromImage = 6,

    /// <summary>Recorded for later: turn speech into text. Always answers unavailable for now.</summary>
    TranscribeSpeech = 7,

    /// <summary>Recorded for later: find items like another item. Always answers unavailable for now.</summary>
    FindSimilar = 8,

    /// <summary>Recorded for later: describe what is in a picture. Always answers unavailable for now.</summary>
    DescribeImage = 9,
}

/// <summary>The engine that serves an ability.</summary>
public enum AiEngine
{
    /// <summary>No engine yet (abilities recorded for later).</summary>
    None = 0,

    /// <summary>The local text model.</summary>
    TextModel = 1,

    /// <summary>The Tesseract text reader. A tool, not AI for the person: it works with Local AI off.</summary>
    Tesseract = 2,
}

/// <summary>Who is asking. The order is the queue priority, highest first.</summary>
public enum AiCallerKind
{
    /// <summary>A person using the app. Never deferred; running Library background and plugin work steps aside.</summary>
    Person = 0,

    /// <summary>Ingestion of new files. Never deferred by admission rules; only ordered.</summary>
    Ingestion = 1,

    /// <summary>The Library's own scheduled and background work. Deferred when the machine is busy.</summary>
    LibraryBackground = 2,

    /// <summary>A plugin. Waits behind everything else, up to its maximum wait, and has a per-hour budget.</summary>
    Plugin = 3,
}

/// <summary>How one request to the engine room ended.</summary>
public enum AiRequestStatus
{
    Succeeded = 0,
    Failed = 1,
    Unavailable = 2,
    RefusedOverBudget = 3,
    Deferred = 4,
    Cancelled = 5,
    TimedOut = 6,
}

/// <summary>Stable snake_case words for the enums above, used in stored usage records and plain labels.</summary>
public static class AiVocabulary
{
    public static string ToKey(this AiAbility ability) => ability switch
    {
        AiAbility.WriteTextQuick => "write_text_quick",
        AiAbility.WriteTextCareful => "write_text_careful",
        AiAbility.WriteTextLong => "write_text_long",
        AiAbility.ExtractStructured => "extract_structured",
        AiAbility.Classify => "classify",
        AiAbility.ReadTextFromImage => "read_text_from_image",
        AiAbility.TranscribeSpeech => "transcribe_speech",
        AiAbility.FindSimilar => "find_similar",
        AiAbility.DescribeImage => "describe_image",
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, null),
    };

    /// <summary>The plain name shown to people.</summary>
    public static string ToDisplayName(this AiAbility ability) => ability switch
    {
        AiAbility.WriteTextQuick => "Write text (quick)",
        AiAbility.WriteTextCareful => "Write text (careful)",
        AiAbility.WriteTextLong => "Write text (long)",
        AiAbility.ExtractStructured => "Pull out structured facts",
        AiAbility.Classify => "Classify",
        AiAbility.ReadTextFromImage => "Read text from images",
        AiAbility.TranscribeSpeech => "Transcribe speech",
        AiAbility.FindSimilar => "Find similar",
        AiAbility.DescribeImage => "Describe image",
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, null),
    };

    public static bool TryParseAbility(string? key, out AiAbility ability)
    {
        foreach (var candidate in Enum.GetValues<AiAbility>())
        {
            if (string.Equals(candidate.ToKey(), key, StringComparison.Ordinal))
            {
                ability = candidate;
                return true;
            }
        }

        ability = default;
        return false;
    }

    public static string ToKey(this AiCallerKind kind) => kind switch
    {
        AiCallerKind.Person => "person",
        AiCallerKind.Ingestion => "ingestion",
        AiCallerKind.LibraryBackground => "library_background",
        AiCallerKind.Plugin => "plugin",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParseCallerKind(string? key, out AiCallerKind kind)
    {
        foreach (var candidate in Enum.GetValues<AiCallerKind>())
        {
            if (string.Equals(candidate.ToKey(), key, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string ToKey(this AiEngine engine) => engine switch
    {
        AiEngine.None => "none",
        AiEngine.TextModel => "text_model",
        AiEngine.Tesseract => "tesseract",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
    };

    public static string ToKey(this AiRequestStatus status) => status switch
    {
        AiRequestStatus.Succeeded => "succeeded",
        AiRequestStatus.Failed => "failed",
        AiRequestStatus.Unavailable => "unavailable",
        AiRequestStatus.RefusedOverBudget => "refused_over_budget",
        AiRequestStatus.Deferred => "deferred",
        AiRequestStatus.Cancelled => "cancelled",
        AiRequestStatus.TimedOut => "timed_out",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
