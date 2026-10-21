using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Pages;

/// <summary>Who may see a plugin page. A plugin cannot show a page to more people than the audience it declares.</summary>
internal enum PluginPageAudience
{
    /// <summary>Server administrators only (the default).</summary>
    Administrators,

    /// <summary>Every adult profile; child profiles never see it.</summary>
    Adults,

    /// <summary>Every profile.</summary>
    Everyone,
}

internal sealed record PluginPageColumn(string Key, string Label);

internal sealed record PluginPageFormField(string Key, string Label, string Kind, bool Required);

/// <summary>One page a plugin declares in its manifest. Types are the Library's own: the plugin supplies data, never screens.</summary>
internal sealed record PluginPageDeclaration(
    string Key,
    string Title,
    string Type,
    PluginPageAudience Audience,
    string? MenuLabel,
    string Collection,
    string TitleField,
    string SubtitleField,
    string ImageField,
    string GroupField,
    string StartsAtField,
    string EndsAtField,
    IReadOnlyList<PluginPageColumn> Columns,
    IReadOnlyList<PluginPageFormField> FormFields,
    string SubmitLabel,
    string DetailRecord)
{
    public const string List = "list";
    public const string Grid = "grid";
    public const string Guide = "guide";
    public const string Form = "form";
    public const string Detail = "detail";

    /// <summary>Phones are for consuming media; a guide timeline needs the width of a tablet or more.</summary>
    public bool PhoneSupported => Type != Guide;
}

/// <summary>
/// Reads the <c>pages</c> part of a plugin manifest. A page that cannot be understood is left out (and reported), never
/// guessed at, so a broken declaration shows nothing rather than something wrong.
/// </summary>
internal static partial class PluginPageDeclarations
{
    public const int MaxPages = 16;
    public const int MaxColumns = 12;
    public const int MaxFormFields = 20;

    private static readonly string[] Types = [PluginPageDeclaration.List, PluginPageDeclaration.Grid, PluginPageDeclaration.Guide, PluginPageDeclaration.Form, PluginPageDeclaration.Detail];
    private static readonly string[] FieldKinds = ["text", "number", "yes_no"];

    public static string AudienceWords(PluginPageAudience audience) => audience switch
    {
        PluginPageAudience.Everyone => "everyone, including child profiles",
        PluginPageAudience.Adults => "adults",
        _ => "administrators",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex KeyPattern();

    /// <summary>The pages a manifest declares and the problems found. Empty when the manifest has no <c>pages</c>.</summary>
    public static (IReadOnlyList<PluginPageDeclaration> Pages, IReadOnlyList<string> Problems) Read(PluginManifest manifest)
    {
        var pages = new List<PluginPageDeclaration>();
        var problems = new List<string>();
        if (manifest.Pages is not { ValueKind: JsonValueKind.Array } array)
        {
            if (manifest.Pages is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            {
                problems.Add("'pages' must be a list.");
            }

            return (pages, problems);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            if (pages.Count >= MaxPages)
            {
                problems.Add($"A plugin can declare at most {MaxPages} pages.");
                break;
            }

            var page = ReadPage(element, problems);
            if (page is null)
            {
                continue;
            }

            if (!seen.Add(page.Key))
            {
                problems.Add($"Page '{page.Key}' is declared twice.");
                continue;
            }

            pages.Add(page);
        }

        return (pages, problems);
    }

    private static PluginPageDeclaration? ReadPage(JsonElement element, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add("A page must be an object.");
            return null;
        }

        var key = Text(element, "key");
        if (key is null || !KeyPattern().IsMatch(key))
        {
            problems.Add("A page needs a 'key' of lower-case letters, digits, '-' or '_' (up to 48 characters).");
            return null;
        }

        var title = Text(element, "title", 80);
        var type = Text(element, "type")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(title) || type is null || !Types.Contains(type, StringComparer.Ordinal))
        {
            problems.Add($"Page '{key}' needs a 'title' and a 'type' of list, grid, guide, form or detail.");
            return null;
        }

        var audience = (Text(element, "audience") ?? "administrators").ToLowerInvariant() switch
        {
            "administrators" => (PluginPageAudience?)PluginPageAudience.Administrators,
            "adults" => PluginPageAudience.Adults,
            "everyone" => PluginPageAudience.Everyone,
            _ => null,
        };
        if (audience is null)
        {
            problems.Add($"Page '{key}' has an 'audience' that is not administrators, adults or everyone.");
            return null;
        }

        string? menuLabel = null;
        if (element.TryGetProperty("menu", out var menu) && menu.ValueKind == JsonValueKind.Object)
        {
            menuLabel = Text(menu, "label", 40);
            if (string.IsNullOrEmpty(menuLabel))
            {
                problems.Add($"Page '{key}' has a menu entry without a 'label'.");
                return null;
            }
        }

        var fields = element.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;
        string Field(string name, string fallback) => (fields is { } map ? Text(map, name, 64) : null) ?? fallback;

        var columns = new List<PluginPageColumn>();
        if (element.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
        {
            foreach (var col in cols.EnumerateArray())
            {
                var columnKey = col.ValueKind == JsonValueKind.Object ? Text(col, "key", 64) : null;
                var label = col.ValueKind == JsonValueKind.Object ? Text(col, "label", 60) : null;
                if (columnKey is null || string.IsNullOrEmpty(label) || columns.Count >= MaxColumns)
                {
                    problems.Add($"Page '{key}' has a column without a 'key' and 'label', or more than {MaxColumns} columns.");
                    return null;
                }

                columns.Add(new PluginPageColumn(columnKey, label));
            }
        }

        var formFields = new List<PluginPageFormField>();
        var submitLabel = "Save";
        if (type == PluginPageDeclaration.Form)
        {
            if (!element.TryGetProperty("form", out var form) || form.ValueKind != JsonValueKind.Object
                || !form.TryGetProperty("fields", out var formArray) || formArray.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"Form page '{key}' needs a 'form' with 'fields'.");
                return null;
            }

            submitLabel = Text(form, "submit_label", 30) ?? "Save";
            var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in formArray.EnumerateArray())
            {
                var fieldKey = field.ValueKind == JsonValueKind.Object ? Text(field, "key", 48) : null;
                var label = field.ValueKind == JsonValueKind.Object ? Text(field, "label", 60) : null;
                var kind = field.ValueKind == JsonValueKind.Object ? (Text(field, "kind") ?? "text").ToLowerInvariant() : "";
                if (fieldKey is null || fieldKey == "submitted_at" || !KeyPattern().IsMatch(fieldKey) || string.IsNullOrEmpty(label)
                    || !FieldKinds.Contains(kind, StringComparer.Ordinal) || !fieldKeys.Add(fieldKey) || formFields.Count >= MaxFormFields)
                {
                    problems.Add($"Form page '{key}' has a field that needs a unique 'key', a 'label' and a 'kind' of text, number or yes_no.");
                    return null;
                }

                formFields.Add(new PluginPageFormField(
                    fieldKey,
                    label,
                    kind,
                    field.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True));
            }

            if (formFields.Count == 0)
            {
                problems.Add($"Form page '{key}' needs at least one field.");
                return null;
            }
        }

        // A form always saves into its own collection, so visitors can never write into state the plugin trusts.
        var collection = type == PluginPageDeclaration.Form ? "form-" + key : Text(element, "collection", 64) ?? "";
        if (type != PluginPageDeclaration.Form && string.IsNullOrEmpty(collection))
        {
            problems.Add($"Page '{key}' needs a 'collection' to draw its data from.");
            return null;
        }

        return new PluginPageDeclaration(
            key,
            title,
            type,
            audience.Value,
            menuLabel,
            collection,
            Field("title", "title"),
            Field("subtitle", "subtitle"),
            Field("image", "image_url"),
            Field("group", "group"),
            Field("starts_at", "starts_at"),
            Field("ends_at", "ends_at"),
            columns,
            formFields,
            submitLabel,
            Text(element, "record", 64) ?? "main");
    }

    private static string? Text(JsonElement element, string property, int maxLength = 128)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > maxLength ? null : text;
    }
}
