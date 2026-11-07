namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Answers whether a person applied the item's match from the editor (universal editor v5, D8 "pinned match"). A pinned match is the
/// person's decision: a later scan, a folder hint that now disagrees or an automatic identity job never re-identifies the item. Only the
/// editor's own Change match, Remove match or a rematch moves it.
/// </summary>
public interface IPinnedMatchGuard
{
    /// <summary>True when the media asset or work carries a match a person applied. An id that carries none, or that cannot be read, is not pinned.</summary>
    Task<bool> IsPinnedAsync(Guid entityId, CancellationToken ct = default);
}
