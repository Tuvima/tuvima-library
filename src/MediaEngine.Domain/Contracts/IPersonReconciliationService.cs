using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Searches Wikidata for person entities by name and role.
/// Used to resolve unlinked person names (e.g. narrators from file metadata)
/// to Wikidata QIDs when structured properties (P50, P175, etc.) are unavailable.
/// </summary>
public interface IPersonReconciliationService
{
    /// <summary>
    /// Searches Wikidata for a person matching the given name and expected role.
    /// Returns a result only if confidence meets the auto-accept threshold (0.80).
    /// Returns <c>null</c> if no confident match is found (auto-skip for 30-day retry).
    /// </summary>
    /// <param name="name">The person's name to search for.</param>
    /// <param name="expectedRole">Expected role: Author, Narrator, Director, Composer, Actor, Performer, Artist.</param>
    /// <param name="workTitle">Optional work title for notable-work matching boost.</param>
    /// <param name="contextText">
    /// Optional description of the work (its subject). When several people share the name and no notable work
    /// settles it, this text is compared with each candidate's Wikidata description to pick the right one;
    /// if it cannot, no match is made rather than guessing on the name alone.
    /// </param>
    /// <param name="externalId">
    /// Optional ID the person already has from a provider (a MusicBrainz artist ID, a TMDB person ID). When given, the person is looked
    /// up on Wikidata by that exact ID first and the name search runs only if no single person carries it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<PersonSearchResult?> SearchPersonAsync(
        string name,
        string expectedRole,
        string? workTitle = null,
        CancellationToken ct = default,
        string? contextText = null,
        ExternalPersonId? externalId = null);

    /// <summary>
    /// Reconciles multiple person names in a single batch operation.
    /// Deduplicates by name (case-insensitive) before issuing any Wikidata calls —
    /// 30 tracks by the same artist cost one API round-trip instead of 30.
    /// </summary>
    /// <param name="requests">
    /// Sequence of (Name, Role, WorkTitle) tuples.
    /// Duplicate names are collapsed; the first occurrence's Role and WorkTitle win.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Dictionary keyed by name (lower-cased) → <see cref="PersonSearchResult"/> if a
    /// confident match was found, or <c>null</c> if no candidate met the auto-accept
    /// threshold (mirrors the <c>null</c> contract of <see cref="SearchPersonAsync"/>).
    /// </returns>
    Task<IReadOnlyDictionary<string, PersonSearchResult?>> SearchPersonsBatchAsync(
        IReadOnlyList<(string Name, string Role, string? WorkTitle)> requests,
        CancellationToken ct = default);
}
