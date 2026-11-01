using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Collections;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// A set written out as a pack file (Collections and lists spec D14): the name the browser saves it under and the file's text. The
/// Engine makes the file's content; this only puts it on disk so another Library can import it through the shared Import.
/// </summary>
public static class CollectionSetPackFile
{
    public const string ContentType = "application/json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>A safe file name for a set: its name in lower case with letters and digits kept, then <c>.tuvima-pack.json</c>.</summary>
    public static string FileNameFor(string? setName)
    {
        var builder = new StringBuilder();
        foreach (var character in setName ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var stem = builder.ToString().Trim('-');
        return (stem.Length == 0 ? "collection-pack" : stem) + ".tuvima-pack.json";
    }

    /// <summary>The file's bytes: the pack as indented JSON, in the names the Engine's pack reader expects.</summary>
    public static byte[] Write(CollectionPackFileDto file) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, WriteOptions));
}
