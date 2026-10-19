using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Adapters;

internal sealed partial class ProviderFlowRunner
{
    private static readonly Regex FormatPlaceholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)(\.[^{}]*)?\}", RegexOptions.Compiled);

    /// <summary>The value of an expression: a JSON node (for variables), a text, a number, or nothing.</summary>
    private JsonNode? Evaluate(FlowExpression expression)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return JsonValue.Create(literal.Value);
            case NumberExpression literalNumber:
                return Number(literalNumber.Value);
            case VariableExpression variable:
                return Read(variable);
            case RequestExpression request:
                return Text(_requestField(request.Field));
            case HintExpression hint:
                return Text(_request.Hints?.GetValueOrDefault(hint.Key));
            case BuiltInExpression builtIn:
                return builtIn.Name == "name" ? JsonValue.Create(_providerName) : Number(_limit);
            case CoalesceExpression coalesce:
                return coalesce.Items.Select(Evaluate).FirstOrDefault(HasValue);
            case FirstNonNullExpression nonNull:
                return nonNull.Items.Select(Evaluate).FirstOrDefault(node => node is not null);
            case FirstCharsExpression first:
                return Evaluate(first.Source) is { } source && TextOf(source) is { } text && text.Length >= first.Count
                    ? JsonValue.Create(text[..first.Count])
                    : null;
            case FormatExpression format:
                return JsonValue.Create(FormatPlaceholder.Replace(format.Template, match =>
                    TextOf(Read(new VariableExpression(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value[1..] : null))) ?? string.Empty));
            case UrlOnHostsExpression urls:
                var address = urls.Sources.Select(source => TextOf(Evaluate(source)))
                    .FirstOrDefault(url => IsHttpsOnHosts(url, urls.Hosts));
                return address is null ? null : Text(new Uri(address).ToString());
            case DigitsOfExpression digits:
                return Text(digits.Sources.Select(source => TextOf(Evaluate(source)))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit)));
            case ClampExpression clamp:
                return double.TryParse(TextOf(Evaluate(clamp.Source)), NumberStyles.Float, CultureInfo.InvariantCulture, out var clamped)
                    ? Number(Math.Clamp(clamped, clamp.Min, clamp.Max))
                    : Number(clamp.Min);
            case IntExpression integer:
                return int.TryParse(TextOf(Evaluate(integer.Source)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                    ? JsonValue.Create(whole)
                    : null;
            case ObjectExpression objectExpression:
                var created = new JsonObject();
                foreach (var (key, field) in objectExpression.Fields)
                {
                    created[key] = Evaluate(field)?.DeepClone();
                }

                return created;
            default:
                return null;
        }
    }

    private JsonNode? Read(VariableExpression variable)
    {
        var node = _variables.GetValueOrDefault(variable.Name);
        if (variable.Path is null)
        {
            return node;
        }

        // A field read from an answer is its trimmed text, or nothing when blank.
        var found = JsonPathEvaluator.Evaluate(node, variable.Path);
        return found is JsonValue value && value.TryGetValue<string>(out var text)
            ? string.IsNullOrWhiteSpace(text) ? null : JsonValue.Create(text.Trim())
            : found;
    }

    private static JsonNode? Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < long.MaxValue ? JsonValue.Create((long)value) : JsonValue.Create(value);

    private static JsonNode? Text(string? value) => value is null ? null : JsonValue.Create(value);

    /// <summary>The text of a value; objects and lists have none.</summary>
    internal static string? TextOf(JsonNode? node) => JsonPathEvaluator.GetStringValue(node);

    private static bool HasValue(JsonNode? node) =>
        node is not null && (node is not JsonValue || !string.IsNullOrWhiteSpace(TextOf(node)));

    private static bool IsHttpsOnHosts(string? url, IReadOnlyList<string> hosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var source)
        && source.Scheme == Uri.UriSchemeHttps
        && hosts.Any(host => source.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || source.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private bool Holds(FlowCondition condition)
    {
        switch (condition)
        {
            case BlankCondition blank:
                return !HasValue(Evaluate(blank.Value));
            case PresentCondition present:
                return HasValue(Evaluate(present.Value));
            case NotNullCondition notNull:
                return Evaluate(notNull.Value) is not null;
            case EqualsCondition equal:
                return string.Equals(
                    TextOf(Evaluate(equal.Left)),
                    TextOf(Evaluate(equal.Right)),
                    equal.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            case NumberEqualsCondition numbers:
                return SameNumber(TextOf(Evaluate(numbers.Left)), TextOf(Evaluate(numbers.Right)));
            case AnyCondition any:
                return any.Conditions.Any(Holds);
            case AllCondition all:
                return all.Conditions.All(Holds);
            case NotCondition not:
                return !Holds(not.Condition);
            default:
                return false;
        }
    }

    private bool Holds(IReadOnlyList<FlowCondition> conditions, bool all) =>
        all ? conditions.All(Holds) : conditions.Any(Holds);

    private static bool SameNumber(string? left, string? right) =>
        int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
        && int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
        && a == b;
}
