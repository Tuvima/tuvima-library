namespace MediaEngine.Domain.Enums;

/// <summary>
/// Discriminator for the kind of intellectual content a <see cref="Aggregates.Work"/> represents.
/// Maps to the <c>media_type</c> TEXT column in the <c>works</c> table.
/// New types are added here; the storage layer persists the string name (e.g. "Movies").
/// Spec: Phase 2 – Extension Points § Polymorphic Assets.
/// </summary>
public enum MediaType
{
    /// <summary>
    /// Type could not be determined; used by <c>GenericFileProcessor</c> as a fallback.
    /// Explicit value 0 ensures uninitialized fields default to Unknown rather than Movies.
    /// </summary>
    Unknown = 0,

    /// <summary>Feature-length film or short film.</summary>
    Movies,

    /// <summary>Electronic publication (EPUB / PDF).</summary>
    Books,

    /// <summary>Narrated audio version of a book. May span multiple files via <c>MediaManifest</c>.</summary>
    Audiobooks,

    /// <summary>Sequential-art publication (CBZ, CBR, PDF comic).</summary>
    Comics,

    /// <summary>Episodic television or web series.</summary>
    TV,

    /// <summary>Music album or single track.</summary>
    Music,

    /// <summary>Saved downloads, courses and channels with no movie or TV database entry (Watch). Local identity only.</summary>
    Videos,
}
