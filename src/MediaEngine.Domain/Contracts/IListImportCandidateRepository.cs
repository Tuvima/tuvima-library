namespace MediaEngine.Domain.Contracts;

/// <summary>
/// A title in the library that an imported list entry might be. One per display root (a film, a book, a show, an album), with
/// the identifiers Library already stores for it.
/// </summary>
/// <param name="WorkId">The title as a collection holds it.</param>
/// <param name="MediaType">Movies, TV, Books, Audiobooks, Music or Comics.</param>
/// <param name="Title">Its title.</param>
/// <param name="Year">Its release year, when known.</param>
/// <param name="Creator">Its first author, director or artist, when known.</param>
/// <param name="Ids">Its identifiers by name (<c>imdb_id</c>, <c>tmdb_id</c>, <c>isbn</c>...), lower case values.</param>
public sealed record ListImportCandidate(
    Guid WorkId,
    string MediaType,
    string Title,
    int? Year,
    string? Creator,
    IReadOnlyDictionary<string, string> Ids);

/// <summary>Reads the owned titles a list import is matched against.</summary>
public interface IListImportCandidateRepository
{
    /// <summary>
    /// Every owned, Ready title (collapsed to the root a collection holds: a show, not its episodes). When
    /// <paramref name="allowedWorkIds"/> is given, a root counts only if the set holds the root or one of its works.
    /// </summary>
    Task<IReadOnlyList<ListImportCandidate>> GetCandidatesAsync(
        IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct = default);
}
