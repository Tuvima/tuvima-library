using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>The Settings attention lines a profile can dismiss with the X. Only these keys are accepted.</summary>
public static class ProfileNoticeKeys
{
    /// <summary>Items are waiting in Needs Review. The fingerprint is the count when dismissed.</summary>
    public const string NeedsReview = "needs-review";

    /// <summary>A Required tool is missing. The fingerprint is the sorted, comma-joined ids of the missing tools when dismissed.</summary>
    public const string MissingTools = "missing-tools";

    public static bool IsKnown(string? key) => key is NeedsReview or MissingTools;
}

/// <summary>One dismissed Settings attention line and what was true when it was dismissed.</summary>
public sealed record ProfileNoticeDismissalDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fingerprint")] string Fingerprint);

/// <summary>The active profile's dismissed Settings attention lines.</summary>
public sealed record ProfileNoticesResponse(
    [property: JsonPropertyName("dismissed")] IReadOnlyList<ProfileNoticeDismissalDto> Dismissed);

/// <summary>Body of a dismissal: what was true when the person pressed the X.</summary>
public sealed record DismissProfileNoticeRequest(
    [property: JsonPropertyName("fingerprint")] string Fingerprint);
