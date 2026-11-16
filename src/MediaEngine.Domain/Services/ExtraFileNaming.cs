namespace MediaEngine.Domain.Services;

/// <summary>
/// The file name an extra takes when its kind changes: the name without its ending, then "-ending" (Plex and Jellyfin's own form,
/// "Making of Dune-behindthescenes.mkv"). Any ending the library knows is replaced, so changing Trailer to Interview never stacks two.
/// </summary>
public static class ExtraFileNaming
{
    /// <summary>
    /// The new file name (with its extension) for <paramref name="newEnding"/>, or null when the name would not change.
    /// <paramref name="knownEndings"/> are every ending the library uses, so an old one is swapped out.
    /// </summary>
    public static string? Rename(string fileName, string? newEnding, IEnumerable<string> knownEndings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var ending in knownEndings.Where(ending => !string.IsNullOrWhiteSpace(ending)).OrderByDescending(ending => ending.Length))
        {
            var suffix = "-" + ending.Trim();
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^suffix.Length].TrimEnd();
                break;
            }
        }

        var renamed = string.IsNullOrWhiteSpace(newEnding) ? stem + extension : $"{stem}-{newEnding.Trim()}{extension}";
        return string.Equals(renamed, fileName, StringComparison.Ordinal) ? null : renamed;
    }
}
