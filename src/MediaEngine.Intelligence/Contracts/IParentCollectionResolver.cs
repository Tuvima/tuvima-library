namespace MediaEngine.Intelligence.Contracts;

/// <summary>
/// Decides which Universe a Series shelf belongs to and keeps the Universe shelves in step with it.
/// </summary>
public interface IParentCollectionResolver
{
    /// <summary>
    /// Decides the Universe of the given Series shelf from its relationships (the one Universe rule), records it on the
    /// shelf, and creates or updates the Universe shelf once two Series shelves share the Universe.
    ///
    /// Safe to call repeatedly — idempotent. The decision is recomputed every time, so a shelf that moves to another
    /// Universe leaves the old one without a stale parent.
    /// </summary>
    Task ResolveParentCollectionAsync(Guid collectionId, CancellationToken ct = default);
}
