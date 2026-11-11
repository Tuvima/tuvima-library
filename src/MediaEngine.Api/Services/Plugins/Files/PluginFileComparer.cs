using System.Text.Json;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Decides whether a plugin file "differs from Tuvima's version" (Plugins and Integrations screens v5 D54). It compares what the
/// two files say, not how they are laid out: spacing, line endings, the order of words in an object and comments do not count,
/// and neither does a provider file's <c>enabled</c> switch, which is the person's own choice and is rewritten whenever a
/// source is switched on or off.
/// </summary>
public static class PluginFileComparer
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The top-level words of a provider source file that are the person's choices rather than part of Tuvima's version.</summary>
    public static readonly IReadOnlyList<string> SourceFileChoices = ["enabled"];

    /// <summary>True when the two texts say the same thing. Text that is not JSON is compared line by line with line endings ignored.</summary>
    public static bool Same(string a, string b, IReadOnlyList<string>? ignoredTopLevelWords = null)
    {
        try
        {
            using var left = JsonDocument.Parse(a, ReadOptions);
            using var right = JsonDocument.Parse(b, ReadOptions);
            return Same(left.RootElement, right.RootElement, ignoredTopLevelWords ?? [], top: true);
        }
        catch (JsonException)
        {
            return Normalise(a) == Normalise(b);
        }
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static bool Same(JsonElement a, JsonElement b, IReadOnlyList<string> ignored, bool top)
    {
        if (a.ValueKind != b.ValueKind && !(IsBoolean(a) && IsBoolean(b)))
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var left = a.EnumerateObject().Where(property => !(top && ignored.Contains(property.Name, StringComparer.Ordinal))).ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                    var right = b.EnumerateObject().Where(property => !(top && ignored.Contains(property.Name, StringComparer.Ordinal))).ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                    return left.Count == right.Count
                        && left.All(pair => right.TryGetValue(pair.Key, out var other) && Same(pair.Value, other, ignored, top: false));
                }

            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength()
                    && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Same(pair.First, pair.Second, ignored, top: false));
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return a.ValueKind == b.ValueKind;
            default:
                return true;
        }
    }

    private static bool IsBoolean(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
