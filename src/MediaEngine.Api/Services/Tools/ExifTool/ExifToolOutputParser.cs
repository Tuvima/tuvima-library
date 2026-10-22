using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Tools.ExifTool;

/// <summary>
/// Turns what ExifTool printed for one write into a result. ExifTool says how many files it changed on its standard output
/// ("1 image files updated") and complains on its standard error ("Error: ...", "Warning: ...").
/// </summary>
internal static partial class ExifToolOutputParser
{
    /// <summary>"    1 image files updated", "1 image files created", "1 image files copied" (a copy with nothing to change) or "1 image files unchanged".</summary>
    [GeneratedRegex(@"^\s*(?<count>\d+)\s+(?:image\s+)?files?\s+(?<what>updated|created|copied|unchanged)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenLine();

    public static ExifToolFileResult ParseWrite(IReadOnlyList<string> output, IReadOnlyList<string> errors)
    {
        var written = false;
        foreach (var line in output)
        {
            var match = WrittenLine().Match(line);
            if (match.Success && int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                written = true;
            }
        }

        var errorLines = errors.Where(line => line.StartsWith("Error:", StringComparison.Ordinal)).Select(Clean).ToList();
        var warningLines = errors.Where(line => line.StartsWith("Warning:", StringComparison.Ordinal)).Select(Clean).ToList();

        if (written && errorLines.Count == 0)
        {
            return ExifToolFileResult.Succeeded(warningLines.Count == 0 ? null : string.Join(" ", warningLines));
        }

        var unsupported = errorLines.FirstOrDefault(IsUnsupported);
        if (unsupported is not null)
        {
            return ExifToolFileResult.Unsupported(unsupported);
        }

        var reason = errorLines.Concat(warningLines).FirstOrDefault()
            ?? output.Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !WrittenLine().IsMatch(line))
            ?? "ExifTool did not change the file.";
        return ExifToolFileResult.Failed(reason);
    }

    /// <summary>"Writing of TXT files is not yet supported" and "Can't currently write XYZ files": the kind of file is the problem, not this file.</summary>
    private static bool IsUnsupported(string error) =>
        error.Contains("is not yet supported", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Can't currently write", StringComparison.OrdinalIgnoreCase)
        || (error.Contains("Can't write", StringComparison.OrdinalIgnoreCase) && error.Contains("files", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string line) => line.Trim();

    /// <summary>
    /// Reads the <c>-j -G1</c> output of a read: one JSON array holding one object per file. Each asked-for tag is matched to
    /// the key ExifTool printed, either exactly (<c>XMP-tuvima:AddedBy</c>) or by the tag name after its group (<c>AddedBy</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRead(IReadOnlyList<string> output, IReadOnlyList<string> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = string.Join('\n', output).Trim();
        if (text.Length == 0)
        {
            return result;
        }

        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return result;
        }

        var file = document.RootElement[0];
        if (file.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var tag in tags)
        {
            foreach (var property in file.EnumerateObject())
            {
                if (property.NameEquals("SourceFile") || !Matches(property.Name, tag))
                {
                    continue;
                }

                result[tag] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                break;
            }
        }

        return result;
    }

    private static bool Matches(string printedKey, string askedFor)
    {
        if (string.Equals(printedKey, askedFor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A tag asked for without its group matches the printed key's name part.
        return !askedFor.Contains(':')
            && printedKey.EndsWith(":" + askedFor, StringComparison.OrdinalIgnoreCase);
    }
}
