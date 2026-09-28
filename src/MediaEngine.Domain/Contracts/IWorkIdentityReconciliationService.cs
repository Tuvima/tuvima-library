namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reconciles duplicate work identities discovered during enrichment.
/// </summary>
public interface IWorkIdentityReconciliationService
{
    /// <summary>Combines same-media variants with a shared QID or explicit Calibre UUID, preserving their assets and editions.</summary>
    Task<int> MergeDuplicateReadWorksByQidAsync(CancellationToken ct = default);

    /// <summary>
    /// Copies the canonical, QID-backed author identities from owned books to
    /// owned audiobook variants of the same creative work.
    /// </summary>
    Task<int> AlignAudiobookAuthorsWithBooksByQidAsync(CancellationToken ct = default);
}
