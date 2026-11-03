using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>One finding of the structure check: where (a JSON pointer), what kind, how serious and the plain words.</summary>
internal sealed record SchemaFinding(string Pointer, string Code, PluginFileSeverity Severity, string Message);

/// <summary>
/// The structure layer of the plugin file check: a plugin's JSON against the published schema file for its kind (the files under
/// <c>config/schemas/plugins</c> and the provider source schema, shipped inside the Engine). Library has no JSON Schema library, and
/// the published files use a small, fixed set of words, so this reads exactly those: <c>type</c>, <c>enum</c>, <c>const</c>,
/// <c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>required</c>,
/// <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>uniqueItems</c>,
/// <c>allOf</c>, <c>oneOf</c> and <c>$ref</c>. Anything else in a schema is ignored. A word the schema forbids (an unknown word) is a
/// note ("Library will ignore it"), never an error: the vocabulary promises that unknown words are collected, not refused.
/// </summary>
internal static class PluginSchemaValidator
{
    private const string ResourcePrefix = "MediaEngine.Api.plugin-schemas.";
    private static readonly Dictionary<string, JsonDocument> Documents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>The schema file name for a provider source file.</summary>
    public const string SourceSchema = "provider.schema.json";

    /// <summary>The schema file for the settings description that ships beside a compiled-in plugin.</summary>
    public const string SettingsDescriptionSchema = "plugin-settings.schema.json";

    /// <summary>The schema file for a plugin manifest of the given <c>kind</c> word (official, declarative, service, sandboxed); anything else uses the official one.</summary>
    public static string ManifestSchema(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "declarative" => "plugin-declarative.schema.json",
        "service" => "plugin-service.schema.json",
        "sandboxed" => "plugin-sandboxed.schema.json",
        _ => "plugin-official.schema.json",
    };

    /// <summary>Checks <paramref name="instance"/> against the named schema file and returns what it found.</summary>
    public static IReadOnlyList<SchemaFinding> Validate(JsonElement instance, string schemaFile)
    {
        var findings = new List<SchemaFinding>();
        var root = Load(schemaFile);
        if (root is null)
        {
            return findings;
        }

        new Walker(findings).Validate(root.Value, new SchemaScope(schemaFile, root.Value), instance, string.Empty);
        return findings;
    }

    private static JsonElement? Load(string file)
    {
        lock (Lock)
        {
            if (Documents.TryGetValue(file, out var cached))
            {
                return cached.RootElement;
            }

            var assembly = typeof(PluginSchemaValidator).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file);
            if (stream is null)
            {
                return null;
            }

            var document = JsonDocument.Parse(stream);
            Documents[file] = document;
            return document.RootElement;
        }
    }

    private sealed record SchemaScope(string File, JsonElement Root);

    private sealed class Walker(List<SchemaFinding> findings)
    {
        public void Validate(JsonElement schema, SchemaScope scope, JsonElement value, string pointer)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String && Resolve(reference.GetString()!, scope) is { } target)
            {
                Validate(target.Schema, target.Scope, value, pointer);
            }

            if (schema.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                foreach (var branch in all.EnumerateArray())
                {
                    Validate(branch, scope, value, pointer);
                }
            }

            if (schema.TryGetProperty("oneOf", out var one) && one.ValueKind == JsonValueKind.Array)
            {
                OneOf(one, scope, value, pointer);
            }

            if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
            {
                Add(pointer, PluginFileProblemCodes.Type, $"{Name(pointer)} should be {TypeWords(type)}, but it is {KindWords(value)}.");
                return; // The rest of the schema only makes sense for the right kind of value.
            }

            if (schema.TryGetProperty("const", out var constant) && !Same(constant, value))
            {
                Add(pointer, PluginFileProblemCodes.Value, $"{Name(pointer)} must be {Show(constant)}.");
            }

            if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array && !choices.EnumerateArray().Any(choice => Same(choice, value)))
            {
                Add(pointer, PluginFileProblemCodes.Value, $"{Name(pointer)} must be one of: {string.Join(", ", choices.EnumerateArray().Select(Show))}.");
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    Numbers(schema, value, pointer);
                    break;
                case JsonValueKind.String:
                    Strings(schema, value, pointer);
                    break;
                case JsonValueKind.Object:
                    Objects(schema, scope, value, pointer);
                    break;
                case JsonValueKind.Array:
                    Arrays(schema, scope, value, pointer);
                    break;
            }
        }

        private void Numbers(JsonElement schema, JsonElement value, string pointer)
        {
            var number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var minimum) && minimum.TryGetDouble(out var low) && number < low)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} must be at least {Format(low)}, but it is {Format(number)}.");
            }

            if (schema.TryGetProperty("maximum", out var maximum) && maximum.TryGetDouble(out var high) && number > high)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} must be at most {Format(high)}, but it is {Format(number)}.");
            }

            if (schema.TryGetProperty("exclusiveMinimum", out var exclusive) && exclusive.TryGetDouble(out var above) && number <= above)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} must be more than {Format(above)}, but it is {Format(number)}.");
            }

            if (schema.TryGetProperty("exclusiveMaximum", out var exclusiveMax) && exclusiveMax.TryGetDouble(out var below) && number >= below)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} must be less than {Format(below)}, but it is {Format(number)}.");
            }
        }

        private void Strings(JsonElement schema, JsonElement value, string pointer)
        {
            var text = value.GetString() ?? string.Empty;
            if (schema.TryGetProperty("minLength", out var minimum) && minimum.TryGetInt32(out var shortest) && text.Length < shortest)
            {
                Add(pointer, PluginFileProblemCodes.Value, shortest <= 1
                    ? $"{Name(pointer)} is empty. It needs a value."
                    : $"{Name(pointer)} is too short. It needs at least {shortest} characters.");
            }

            if (schema.TryGetProperty("maxLength", out var maximum) && maximum.TryGetInt32(out var longest) && text.Length > longest)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} is too long. It can have at most {longest} characters, and this has {text.Length}.");
            }

            if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String && !Matches(pattern.GetString()!, text))
            {
                Add(pointer, PluginFileProblemCodes.Value, $"{Name(pointer)} is not written in the form Library expects.");
            }
        }

        private void Objects(JsonElement schema, SchemaScope scope, JsonElement value, string pointer)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null))
                {
                    if (!value.TryGetProperty(name!, out _))
                    {
                        Add(pointer, PluginFileProblemCodes.Missing, pointer.Length == 0
                            ? $"\"{name}\" is missing. This file needs it."
                            : $"{Name(pointer)} is missing \"{name}\".");
                    }
                }
            }

            var declared = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object ? properties : (JsonElement?)null;
            schema.TryGetProperty("additionalProperties", out var additional);
            foreach (var property in value.EnumerateObject())
            {
                var child = pointer + "/" + JsonPositionMap.Escape(property.Name);
                if (declared is { } known && known.TryGetProperty(property.Name, out var propertySchema))
                {
                    Validate(propertySchema, scope, property.Value, child);
                }
                else if (additional.ValueKind == JsonValueKind.False)
                {
                    Add(child, PluginFileProblemCodes.UnknownWord, $"Library does not know the word \"{property.Name}\" here and will ignore it.", PluginFileSeverity.Note);
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Validate(additional, scope, property.Value, child);
                }
            }
        }

        private void Arrays(JsonElement schema, SchemaScope scope, JsonElement value, string pointer)
        {
            var count = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var minimum) && minimum.TryGetInt32(out var fewest) && count < fewest)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} needs at least {fewest} {(fewest == 1 ? "entry" : "entries")}, but has {count}.");
            }

            if (schema.TryGetProperty("maxItems", out var maximum) && maximum.TryGetInt32(out var most) && count > most)
            {
                Add(pointer, PluginFileProblemCodes.Range, $"{Name(pointer)} can have at most {most} {(most == 1 ? "entry" : "entries")}, but has {count}.");
            }

            if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Validate(items, scope, item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture));
                    index++;
                }
            }

            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!seen.Add(item.GetRawText()))
                    {
                        Add(pointer + "/" + index.ToString(CultureInfo.InvariantCulture), PluginFileProblemCodes.Value, $"{Name(pointer)} lists {Show(item)} more than once.");
                    }

                    index++;
                }
            }
        }

        private void OneOf(JsonElement branches, SchemaScope scope, JsonElement value, string pointer)
        {
            var attempts = new List<(JsonElement Branch, List<SchemaFinding> Findings)>();
            foreach (var branch in branches.EnumerateArray())
            {
                var local = new List<SchemaFinding>();
                new Walker(local).Validate(branch, scope, value, pointer);
                if (!local.Any(finding => finding.Severity == PluginFileSeverity.Error))
                {
                    findings.AddRange(local);
                    return;
                }

                attempts.Add((branch, local));
            }

            // Nothing fit. Say what the branch that suits this kind of value complained about; if none suits it, say what is allowed.
            var suited = attempts.FirstOrDefault(attempt => attempt.Branch.TryGetProperty("type", out var type) && TypeMatches(type, value));
            if (suited.Findings is { Count: > 0 })
            {
                findings.AddRange(suited.Findings);
                return;
            }

            var kinds = attempts.Select(attempt => attempt.Branch.TryGetProperty("type", out var type) ? TypeWords(type) : null).Where(words => words is not null).Distinct().ToList();
            Add(pointer, PluginFileProblemCodes.Type, kinds.Count > 0
                ? $"{Name(pointer)} should be {string.Join(" or ", kinds)}, but it is {KindWords(value)}."
                : $"{Name(pointer)} is not written in a form Library understands.");
        }

        private void Add(string pointer, string code, string message, PluginFileSeverity severity = PluginFileSeverity.Error) =>
            findings.Add(new SchemaFinding(pointer, code, severity, message));
    }

    private static (JsonElement Schema, SchemaScope Scope)? Resolve(string reference, SchemaScope scope)
    {
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        var file = hash < 0 ? reference : reference[..hash];
        var fragment = hash < 0 ? string.Empty : reference[(hash + 1)..];
        var target = scope;
        if (file.Length > 0 && !string.Equals(file, scope.File, StringComparison.OrdinalIgnoreCase))
        {
            var loaded = Load(file);
            if (loaded is null)
            {
                return null;
            }

            target = new SchemaScope(file, loaded.Value);
        }

        var node = target.Root;
        foreach (var segment in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var next))
            {
                return null;
            }

            node = next;
        }

        return (node, target);
    }

    private static bool TypeMatches(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => OneTypeMatches(type.GetString()!, value),
        JsonValueKind.Array => type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && OneTypeMatches(item.GetString()!, value)),
        _ => true,
    };

    private static bool OneTypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsWhole(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsWhole(JsonElement number) =>
        number.TryGetInt64(out _) || (number.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < double.Epsilon);

    private static string TypeWords(JsonElement type)
    {
        var kinds = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [type.GetString() ?? string.Empty];
        return string.Join(" or ", kinds.Select(Article).Distinct());
    }

    private static string Article(string type) => type switch
    {
        "object" => "an object (in { })",
        "array" => "a list (in [ ])",
        "string" => "text (in quotes)",
        "number" => "a number",
        "integer" => "a whole number",
        "boolean" => "true or false",
        "null" => "empty (null)",
        _ => type,
    };

    private static string KindWords(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => IsWhole(value) ? "a number" : "a number with decimals",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "empty",
        _ => "something else",
    };

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind && !(a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => a.ValueKind == b.ValueKind,
            JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static string Show(JsonElement value) => value.ValueKind == JsonValueKind.String ? $"\"{value.GetString()}\"" : value.GetRawText();

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool Matches(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return true; // A schema pattern this runtime cannot read is not the file's fault.
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>How a place is named in a message: the word it sits under in quotes, or "item 3 of ..." for a list entry, or "The file" for the top.</summary>
    internal static string Name(string pointer)
    {
        if (pointer.Length == 0)
        {
            return "The file";
        }

        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
        var last = segments[^1];
        if (int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var owner = segments.Count > 1 ? segments[^2] : "the file";
            return $"Entry {index + 1} of \"{owner}\"";
        }

        return $"\"{last}\"";
    }

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
