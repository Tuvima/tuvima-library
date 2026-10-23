namespace MediaEngine.Ingestion.Services.ImportUploads;

/// <summary>
/// What a file name asked for in an upload into the Import folder must look like (Library setup spec D44). A name is refused, never
/// quietly rewritten: it has no path parts, no characters a file name cannot hold on any system, no Windows device name (a share
/// may be read from Windows), and it does not start with a dot (a hidden file would never be seen again).
/// </summary>
public static class ImportUploadFileNames
{
    /// <summary>The longest name accepted, in characters. A little under the usual 255 so a number can be added for a unique name.</summary>
    public const int MaxLength = 200;

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when <paramref name="fileName"/> can be saved as it is. Otherwise <paramref name="problem"/> says why, in plain words.</summary>
    public static bool TryValidate(string? fileName, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            problem = "The file needs a name.";
            return false;
        }

        if (fileName.Length > MaxLength)
        {
            problem = "The file name is too long.";
            return false;
        }

        if (fileName.IndexOfAny(ForbiddenCharacters) >= 0 || fileName.Any(char.IsControl))
        {
            problem = "The file name has characters a file name cannot hold, or path parts. Send the name only.";
            return false;
        }

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName is "." or "..")
        {
            problem = "The file name must be a name only, not a path.";
            return false;
        }

        if (fileName.StartsWith('.') || fileName.StartsWith(' ') || fileName.EndsWith(' ') || fileName.EndsWith('.'))
        {
            problem = "The file name cannot start with a dot or a space, or end with a dot or a space.";
            return false;
        }

        // Windows reads CON.txt as the console too, so the part before the first dot is what counts.
        var stem = fileName.Split('.', 2)[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            problem = "That file name is reserved by Windows and cannot be used.";
            return false;
        }

        return true;
    }

    /// <summary>The name to try for the <paramref name="attempt"/>th time in a folder: the name itself first, then <c>name (1).ext</c>, <c>name (2).ext</c> and so on.</summary>
    public static string WithNumber(string fileName, int attempt)
    {
        if (attempt <= 0)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        return $"{stem} ({attempt}){extension}";
    }
}
