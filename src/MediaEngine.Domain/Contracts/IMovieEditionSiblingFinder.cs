namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Finds an existing standalone film that already represents the same film (another cut, or the same cut in another
/// quality), so a new file becomes another Edition of that film instead of a duplicate film.
/// </summary>
public interface IMovieEditionSiblingFinder
{
    /// <summary>
    /// Returns the id of the film Work the file should join, or <c>null</c> when it is a new film.
    /// </summary>
    /// <remarks>
    /// A film qualifies when its title is the same (letters and digits only) and either both years are known and equal,
    /// or one year is unknown and its file sits in the same folder. Two films with one title and different years
    /// (a remake) are never joined.
    /// </remarks>
    Task<Guid?> FindSiblingWorkAsync(
        string sourceFilePath,
        string? title,
        string? year,
        CancellationToken ct = default);
}
