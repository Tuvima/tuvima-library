using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// Where a filed photo or video goes (Photo and video filing spec D2): the folders come from the library's pattern and the date the
/// photo was taken, the file keeps its own name, and a short number is added only when two files would clash.
/// </summary>
public static class ViewFilingLayout
{
    private static readonly Regex Token = new(@"\{(?<name>[A-Za-z]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] SeparatorCharacters = ['/', '\\'];

    // The characters Windows refuses in a name, on every system: a library folder is often a Windows share or moves to one.
    private static readonly char[] UnstorableCharacters = [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*'];

    /// <summary>
    /// True when <paramref name="pattern"/> is usable: made of folders, each built from plain text and the tokens <c>{year}</c>,
    /// <c>{month}</c> and <c>{day}</c>, with at least one token so photos of different days do not pile into one folder.
    /// </summary>
    public static bool IsValidPattern(string? pattern, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            problem = "The layout is empty.";
            return false;
        }

        if (Path.IsPathRooted(pattern) || pattern.StartsWith("//", StringComparison.Ordinal))
        {
            problem = "The layout must be relative to the library folder.";
            return false;
        }

        var segments = pattern.Split(SeparatorCharacters, StringSplitOptions.None);
        var tokens = 0;
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Trim() != segment || segment.EndsWith('.'))
            {
                problem = "Each folder in the layout needs a name.";
                return false;
            }

            foreach (Match match in Token.Matches(segment))
            {
                tokens++;
                if (match.Groups["name"].Value is not ("year" or "month" or "day"))
                {
                    problem = $"{{{match.Groups["name"].Value}}} is not a known part of the layout. Use {{year}}, {{month}} and {{day}}.";
                    return false;
                }
            }

            var literal = Token.Replace(segment, string.Empty);
            if (literal.IndexOfAny(UnstorableCharacters) >= 0 || literal.Contains('{') || literal.Contains('}'))
            {
                problem = "A folder name in the layout has a character a folder name cannot hold.";
                return false;
            }
        }

        if (tokens == 0)
        {
            problem = "The layout needs at least {year}, {month} or {day}.";
            return false;
        }

        return true;
    }

    /// <summary>The usable pattern: the configured one, or the default when it is not usable.</summary>
    public static string EffectivePattern(string? configured) =>
        IsValidPattern(configured, out _) ? configured!.Trim() : PhotoFilingSettings.DefaultLayoutPattern;

    /// <summary>The folders (relative to the library folder) for a photo taken on <paramref name="date"/>, joined with the platform separator.</summary>
    public static string FolderFor(string? pattern, DateTimeOffset date)
    {
        var effective = EffectivePattern(pattern);
        var local = date.ToLocalTime();
        var segments = effective.Split(SeparatorCharacters, StringSplitOptions.None)
            .Select(segment => Token.Replace(segment, match => match.Groups["name"].Value switch
            {
                "year" => local.Year.ToString("0000", CultureInfo.InvariantCulture),
                "month" => local.Month.ToString("00", CultureInfo.InvariantCulture),
                _ => local.Day.ToString("00", CultureInfo.InvariantCulture),
            }));
        return Path.Combine([.. segments]);
    }

    /// <summary>
    /// Picks the names for a group of files filed together: they keep their own names, and when any of them would clash with a file
    /// already in <paramref name="directory"/> (or with another name in the group) the whole group gets the same short number, so a
    /// RAW and its JPEG never end up with different names. <paramref name="exists"/> says whether a path is taken.
    /// </summary>
    public static IReadOnlyList<string> NamesFor(string directory, IReadOnlyList<string> fileNames, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(exists);
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var candidate = fileNames.Select(name => attempt == 1 ? name : WithNumber(name, attempt)).ToList();
            var taken = candidate.Any(name => exists(Path.Combine(directory, name)))
                || candidate.Count != candidate.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (!taken)
            {
                return candidate;
            }
        }

        throw new IOException("No free name could be found for the file.");
    }

    /// <summary>A name with a short number before the extension: <c>IMG_4521 (2).jpg</c>.</summary>
    public static string WithNumber(string fileName, int number)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return string.Create(CultureInfo.InvariantCulture, $"{stem} ({number}){extension}");
    }

    /// <summary>
    /// True when a file name can be stored: not empty, no path parts, no characters the file system refuses, no trailing dot or space,
    /// not a reserved device name, and not so long that the full path would not fit.
    /// </summary>
    public static bool IsStorableName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200 || fileName != Path.GetFileName(fileName))
        {
            return false;
        }

        if (fileName.IndexOfAny(UnstorableCharacters) >= 0 || fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant);
    }
}
