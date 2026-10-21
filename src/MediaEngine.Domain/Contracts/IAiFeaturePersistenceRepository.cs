using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persists complete AI feature projections without allowing generated values
/// to silently replace manual or trusted provider values.
/// </summary>
public interface IAiFeaturePersistenceRepository
{
    Task<AiFeatureWriteResult> ReplaceAiFeatureAsync(
        AiFeatureWriteRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Removes what a feature published and still owns (values a person or a provider has since replaced are left
    /// alone) and forgets its state, so switching the feature's plugin off takes its suggestions away. Returns how
    /// many items had a record.
    /// </summary>
    Task<int> RetractAiFeatureAsync(
        string featureKey,
        CancellationToken ct = default);

    Task<AiFeatureState?> GetAiFeatureStateAsync(
        Guid entityId,
        string featureKey,
        CancellationToken ct = default);

    Task<AiFeatureState> RecordAiFeatureFailureAsync(
        AiFeatureFailureRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Releases AI feature rows that were poisoned only because their configured
    /// model capability was unavailable. Genuine content failures remain quarantined.
    /// </summary>
    Task<int> RecoverUnavailableCapabilityFailuresAsync(CancellationToken ct = default)
        => Task.FromResult(0);
}
