using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The words and rules the generated plugin page shares (Plugins and Integrations screens v5 D46, D47): how a control's value is
/// read and compared, which controls show, which are greyed and why. Nothing here names a plugin; everything comes from the
/// page model the Engine built from the plugin's JSON.
/// </summary>
internal static class PluginSetupValues
{
    public const string ToggleControl = "toggle";
    public const string ChoiceControl = "choice";
    public const string NumberControl = "number";
    public const string UrlControl = "url";
    public const string PathControl = "path";
    public const string SecretControl = "secret";
    public const string ChoicesControl = "choices";
    public const string GroupListControl = "group_list";

    public const string StoredInSettings = "settings";
    public const string StoredInSourceConfig = "source_config";
    public const string StoredInValueSource = "value_source";
    public const string StoredInActivityCap = "activity_cap";

    public const string RowActionTest = "test_connection";
    public const string RowActionRemove = "remove";
    public const string RowActionCheckNow = "check_now";
    public const string RowActionOpenLink = "open_link";

    public static string Text(JsonElement? value) => value switch
    {
        null => string.Empty,
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? string.Empty,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
        { } other => other.GetRawText(),
    };

    public static bool IsOn(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text => bool.TryParse(text.GetString(), out var parsed) && parsed,
        { ValueKind: JsonValueKind.Number } number => number.TryGetDouble(out var d) && d != 0,
        _ => false,
    };

    public static double? Number(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetDouble(out var d) => d,
        { ValueKind: JsonValueKind.String } text when double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static IReadOnlyList<string> List(JsonElement? value) => value is { ValueKind: JsonValueKind.Array } array
        ? [.. array.EnumerateArray().Select(item => Text(item))]
        : [];

    /// <summary>True when two values mean the same thing (a number 5 and the text "5" do; a missing value and an empty one do).</summary>
    public static bool Same(JsonElement? left, JsonElement? right)
    {
        if (left is { ValueKind: JsonValueKind.Array } || right is { ValueKind: JsonValueKind.Array })
        {
            return List(left).OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(List(right).OrderBy(item => item, StringComparer.Ordinal), StringComparer.Ordinal);
        }

        if (Number(left) is { } a && Number(right) is { } b && left?.ValueKind != JsonValueKind.String && right?.ValueKind != JsonValueKind.String)
        {
            return a.Equals(b);
        }

        if (left is { ValueKind: JsonValueKind.True or JsonValueKind.False } || right is { ValueKind: JsonValueKind.True or JsonValueKind.False })
        {
            return IsOn(left) == IsOn(right);
        }

        return string.Equals(Text(left), Text(right), StringComparison.Ordinal);
    }

    /// <summary>Whether a control is shown: always, unless it says <c>show_when</c> and the other setting does not have that value.</summary>
    public static bool IsShown(PluginSetupControlDto control, Func<string, JsonElement?> currentValueOf)
    {
        if (control.ShowWhen is not { Setting.Length: > 0 } showWhen)
        {
            return true;
        }

        var current = currentValueOf(showWhen.Setting);
        return showWhen.Value is { } wanted && wanted.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? Same(current, wanted)
            : IsOn(current);
    }

    /// <summary>A plain sentence for why a control is greyed, or null when nothing holds it back.</summary>
    public static string? WhyDisabled(PluginSetupControlDto control, PluginSetupContext context)
    {
        if (control.Requires is not { } requires)
        {
            return null;
        }

        var unmet = requires.Kind switch
        {
            "tool" => context.ToolMissing,
            "local_ai" => context.LocalAiBlocked,
            "key" => context.KeyMissing,
            _ => false,
        };
        return unmet ? requires.Reason : null;
    }

    /// <summary>The label with its unit, as the controls show it ("Request timeout (seconds)").</summary>
    public static string Label(PluginSetupControlDto control) =>
        string.IsNullOrWhiteSpace(control.Unit) || control.Control != NumberControl ? control.Title : $"{control.Title} ({control.Unit})";

    /// <summary>The choice's label for a stored value, or the value itself when the file lists no such choice.</summary>
    public static string ChoiceLabel(PluginSetupControlDto control, string value) =>
        control.Choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Label ?? value;

    /// <summary>A short way to say a value in the Review changes list.</summary>
    public static string Say(PluginSetupControlDto control, JsonElement? value) => control.Control switch
    {
        ToggleControl => IsOn(value) ? "On" : "Off",
        ChoiceControl => ChoiceLabel(control, Text(value)),
        ChoicesControl => List(value) is { Count: > 0 } picked ? string.Join(", ", picked.Select(item => ChoiceLabel(control, item))) : "None",
        NumberControl => string.IsNullOrWhiteSpace(control.Unit) ? Text(value) : $"{Text(value)} {control.Unit}",
        _ => Text(value) is { Length: > 0 } text ? text : "Empty",
    };

    /// <summary>Turns what a field typed into the value the Engine expects for this control.</summary>
    public static JsonElement FromText(PluginSetupControlDto control, string? raw)
    {
        var text = raw ?? string.Empty;
        if (control.Control == NumberControl && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return control.Integer
                ? JsonSerializer.SerializeToElement((long)Math.Round(number))
                : JsonSerializer.SerializeToElement(number);
        }

        return JsonSerializer.SerializeToElement(text);
    }

    public static JsonElement FromBoolean(bool on) => JsonSerializer.SerializeToElement(on);

    public static JsonElement FromInteger(int number) => JsonSerializer.SerializeToElement(number);

    public static JsonElement FromList(IEnumerable<string> items) => JsonSerializer.SerializeToElement(items.ToArray());
}

/// <summary>What the page knows about the machine, so a control can be greyed with its reason (D47 <c>requires</c>).</summary>
public sealed record PluginSetupContext(bool ToolMissing, bool LocalAiBlocked, bool KeyMissing);
