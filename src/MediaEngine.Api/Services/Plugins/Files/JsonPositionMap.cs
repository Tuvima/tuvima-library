using System.Text;
using System.Text.Json;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Where every value and every property name of a JSON text sits, as 1-based line and column, found by walking the text once with
/// <see cref="Utf8JsonReader"/> and its byte positions. A place is named by a JSON pointer (<c>/network/0/host</c>) so the
/// checker can look up the line of whatever it complains about. Comments and trailing commas are allowed, as the loaders allow them.
/// </summary>
public sealed class JsonPositionMap
{
    private readonly Dictionary<string, (int Line, int Column)> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Line, int Column)> _names = new(StringComparer.Ordinal);

    private JsonPositionMap()
    {
    }

    /// <summary>The line and column of the value at <paramref name="pointer"/>, or null when there is none there.</summary>
    public (int Line, int Column)? ValueAt(string pointer) => _values.TryGetValue(pointer, out var at) ? at : null;

    /// <summary>The line and column of the property name that leads to <paramref name="pointer"/>, or null (the root and array items have none).</summary>
    public (int Line, int Column)? NameAt(string pointer) => _names.TryGetValue(pointer, out var at) ? at : null;

    /// <summary>The best place to point at for <paramref name="pointer"/>: the property name, else the value, else the nearest parent that exists, else the top of the file.</summary>
    public (int Line, int Column) Best(string pointer)
    {
        var current = pointer;
        while (true)
        {
            if (NameAt(current) is { } name)
            {
                return name;
            }

            if (ValueAt(current) is { } value)
            {
                return value;
            }

            if (current.Length == 0)
            {
                return (1, 1);
            }

            current = current[..current.LastIndexOf('/')];
        }
    }

    /// <summary>
    /// Walks <paramref name="text"/>. Returns the map, or null with a syntax <paramref name="problem"/> that has the line and column
    /// where the text stops being JSON.
    /// </summary>
    public static JsonPositionMap? TryBuild(string text, out PluginFileProblem? problem)
    {
        problem = null;
        var bytes = Encoding.UTF8.GetBytes(text);
        var lines = LineStarts(bytes);
        var map = new JsonPositionMap();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var path = new List<Frame>();
        try
        {
            while (reader.Read())
            {
                var at = PositionOf(bytes, lines, (int)reader.TokenStartIndex);
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        {
                            var name = reader.GetString() ?? string.Empty;
                            path[^1].Property = name;
                            map._names[Pointer(path)] = at;
                            break;
                        }

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        map._values[Pointer(path)] = at;
                        path.Add(new Frame(reader.TokenType == JsonTokenType.StartArray));
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        path.RemoveAt(path.Count - 1);
                        AfterValue(path);
                        break;

                    default:
                        map._values[Pointer(path)] = at;
                        AfterValue(path);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            problem = SyntaxProblem(ex, bytes, lines);
            return null;
        }

        return map;
    }

    /// <summary>Escapes one pointer segment (<c>~</c> and <c>/</c>).</summary>
    public static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static void AfterValue(List<Frame> path)
    {
        if (path.Count > 0 && path[^1].IsArray)
        {
            path[^1].Index++;
        }
    }

    private static string Pointer(List<Frame> path)
    {
        if (path.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var frame in path)
        {
            builder.Append('/');
            builder.Append(frame.IsArray ? frame.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : Escape(frame.Property ?? string.Empty));
        }

        return builder.ToString();
    }

    private static List<int> LineStarts(byte[] bytes)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static (int Line, int Column) PositionOf(byte[] bytes, List<int> lines, int offset)
    {
        var index = lines.BinarySearch(offset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        var start = lines[index];
        // Columns count characters, not bytes, so a line with accents points at the right place.
        var column = Encoding.UTF8.GetCharCount(bytes, start, Math.Max(0, offset - start)) + 1;
        return (index + 1, column);
    }

    private static PluginFileProblem SyntaxProblem(JsonException ex, byte[] bytes, List<int> lines)
    {
        var line = (int)Math.Max(0, ex.LineNumber ?? 0) + 1;
        line = Math.Min(line, lines.Count);
        var start = lines[line - 1];
        var column = Encoding.UTF8.GetCharCount(bytes, start, (int)Math.Min(Math.Max(0, ex.BytePositionInLine ?? 0), bytes.Length - start)) + 1;
        return new PluginFileProblem(line, column, PluginFileSeverity.Error, PluginFileProblemCodes.Syntax, SyntaxWords(ex.Message));
    }

    private static string SyntaxWords(string message)
    {
        var detail = message;
        var cut = detail.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = detail.IndexOf(" LineNumber:", StringComparison.Ordinal);
        }

        if (cut > 0)
        {
            detail = detail[..cut];
        }

        if (detail.Contains("end of", StringComparison.OrdinalIgnoreCase) || detail.Contains("ended unexpectedly", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Expected depth", StringComparison.OrdinalIgnoreCase))
        {
            return "The file stops before the JSON is finished. A closing bracket, brace or quote is probably missing above this point.";
        }

        if (detail.Contains("invalid start of a property name", StringComparison.OrdinalIgnoreCase))
        {
            return "A name in quotes was expected here. Check for a missing or extra comma just before this point.";
        }

        if (detail.Contains("invalid start of a value", StringComparison.OrdinalIgnoreCase))
        {
            return "A value was expected here. Check for a missing value, a stray character or a missing comma just before this point.";
        }

        if (detail.Contains("after a property name", StringComparison.OrdinalIgnoreCase) || detail.Contains("':' ", StringComparison.Ordinal))
        {
            return "A colon is missing between a name and its value.";
        }

        if (detail.Contains("after a value", StringComparison.OrdinalIgnoreCase))
        {
            return "A comma is missing between two values, or something extra follows a value.";
        }

        return "This is not valid JSON here (" + detail.TrimEnd('.') + ").";
    }

    private sealed class Frame(bool isArray)
    {
        public bool IsArray { get; } = isArray;

        public int Index { get; set; }

        public string? Property { get; set; }
    }
}
