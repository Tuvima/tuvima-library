using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The one merged list of library kinds: the kinds in <c>config/library_kinds.json</c> plus the kinds of switched-on declarative
/// plugins. Whatever decides what a library may look up (ingestion, the lookup guard, text tracks, artwork) reads this list, so a plugin's
/// plain kind (lookup "none") is plain everywhere and a change to a plugin's kinds takes effect without a restart.
/// </summary>
public interface ILibraryKindSource
{
    /// <summary>Every kind, available or not, built-in kinds first, read fresh on each call.</summary>
    IReadOnlyList<LibraryKindDefinition> GetKinds();

    /// <summary>Raised after the merged list may have changed (a plugin was switched on or off, installed or reloaded).</summary>
    event Action? Changed;
}
