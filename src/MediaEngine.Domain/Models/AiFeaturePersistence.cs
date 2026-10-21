using MediaEngine.Domain.Jobs;

namespace MediaEngine.Domain.Models;

/// <summary>
/// Durable outcome for one AI-generated feature projection.
/// </summary>
public enum AiFeatureStatus
{
    Published,
    ReviewRequired,
    Protected,
    InsufficientData,
    RetryPending,
    Poisoned,
}

/// <summary>
/// Complete replacement requested by an AI feature. Multi-valued and scalar
/// fields are committed in one transaction.
/// </summary>
/// <summary>The keys AI features record their output under; one per Local AI feature that stores results.</summary>
public static class AiFeatureKeys
{
    public const string Vibe = "vibe";
    public const string SeriesAlignment = "series_alignment";
    public const string DescriptionIntelligence = "description_intelligence";
}

public sealed record AiFeatureWriteRequest(
    Guid EntityId,
    string FeatureKey,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ArrayValues,
    IReadOnlyDictionary<string, string?> ScalarValues,
    Guid ProviderId,
    double Confidence,
    double PublishThreshold,
    double ReviewThreshold,
    string ModelId,
    string PromptVersion,
    string InputFingerprint);

public sealed record AiFeatureFailureRequest(
    Guid EntityId,
    string FeatureKey,
    Guid ProviderId,
    string ModelId,
    string PromptVersion,
    string InputFingerprint,
    string Error,
    int MaxAttempts = 3,
    TimeSpan? InitialRetryDelay = null,
    BackgroundJobOutcomeCategory Category = BackgroundJobOutcomeCategory.ContentFailure);

public sealed record AiFeatureWriteResult(
    AiFeatureStatus Status,
    IReadOnlyList<string> PublishedFields,
    IReadOnlyList<string> ProtectedFields,
    bool IsUnchanged)
{
    public bool RequiresReview => Status is AiFeatureStatus.ReviewRequired or AiFeatureStatus.Protected;
}

public sealed record AiFeatureState(
    Guid EntityId,
    string FeatureKey,
    Guid ProviderId,
    AiFeatureStatus Status,
    double? Confidence,
    string? ModelId,
    string? PromptVersion,
    string? InputFingerprint,
    string? OutputFingerprint,
    int Attempts,
    DateTimeOffset? NextRetryAt,
    string? LastError,
    string? OutcomeReason,
    BackgroundJobOutcomeCategory? LastOutcomeCategory,
    IReadOnlyList<string> PublishedFields,
    IReadOnlyList<string> ProtectedFields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PublishedValues,
    DateTimeOffset UpdatedAt)
{
    public bool IsCurrent(string inputFingerprint) =>
        Status is AiFeatureStatus.Published
            or AiFeatureStatus.ReviewRequired
            or AiFeatureStatus.Protected
            or AiFeatureStatus.InsufficientData
        && string.Equals(InputFingerprint, inputFingerprint, StringComparison.Ordinal);

    public bool CanAttempt(DateTimeOffset now) =>
        Status != AiFeatureStatus.Poisoned
        && (Status != AiFeatureStatus.RetryPending || NextRetryAt is null || NextRetryAt <= now);
}
