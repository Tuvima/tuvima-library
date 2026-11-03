using System.Globalization;
using System.Text.Json;

namespace MediaEngine.Plugins;

/// <summary>The control types a plugin's setting can ask for (Plugins and Integrations screens v5 D47).</summary>
public static class PluginSettingControls
{
    public const string Toggle = "toggle";
    public const string Choice = "choice";
    public const string Number = "number";
    public const string Text = "text";
    public const string Url = "url";
    public const string Path = "path";
    public const string Secret = "secret";

    /// <summary>A list of choices where several can be ticked.</summary>
    public const string Choices = "choices";

    /// <summary>A repeating group (D49).</summary>
    public const string GroupList = "group_list";

    public static readonly IReadOnlyList<string> All = [Toggle, Choice, Number, Text, Url, Path, Secret, Choices, GroupList];
}

/// <summary>Who sets a setting: the household (default) or each person for themselves.</summary>
public static class PluginSettingScopes
{
    public const string Household = "household";
    public const string Profile = "profile";
}

/// <summary>The buttons a row of a group list may carry (D49). A fixed list.</summary>
public static class PluginRowActions
{
    public const string TestConnection = "test_connection";
    public const string Remove = "remove";
    public const string CheckNow = "check_now";
    public const string OpenLink = "open_link";

    public static readonly IReadOnlyList<string> All = [TestConnection, Remove, CheckNow, OpenLink];
}

/// <summary>The tests a plugin may declare (D50). A plugin that declares none shows none.</summary>
public static class PluginDeclaredActions
{
    public const string TestConnection = "test_connection";
    public const string CheckNow = "check_now";
    public const string DryRun = "dry_run";

    public static readonly IReadOnlyList<string> All = [TestConnection, CheckNow, DryRun];
}

/// <summary>What a setting needs before it can be used (<c>requires</c>): a <c>tool</c>, <c>local_ai</c> or a <c>key</c>.</summary>
public static class PluginSettingRequirementKinds
{
    public const string Tool = "tool";
    public const string LocalAi = "local_ai";
    public const string Key = "key";

    public static readonly IReadOnlyList<string> All = [Tool, LocalAi, Key];
}

public sealed record PluginSettingChoice(string Value, string Label);

/// <summary>Show a setting only while another setting has a given value.</summary>
public sealed record PluginSettingShowWhen(string Setting, JsonElement Value);

/// <summary>A setting that is greyed with a reason until a tool, Local AI or a key is there.</summary>
public sealed record PluginSettingRequirement(string Kind, string Name, string Reason);

/// <summary>What one item of a repeating group looks like, and what can be done with the items (D49).</summary>
public sealed record PluginGroupListDefinition(
    string ItemTitle,
    string AddLabel,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> RowActions,
    IReadOnlyList<PluginSettingDefinition> Fields);

/// <summary>One setting of a plugin, read from its <c>settings_schema</c> with every optional word filled with its plain default.</summary>
public sealed record PluginSettingDefinition
{
    public string Key { get; init; } = "";
    public string Control { get; init; } = PluginSettingControls.Text;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Group { get; init; } = "";
    public int Order { get; init; }
    public bool Advanced { get; init; }
    public bool ReadOnly { get; init; }
    public string Scope { get; init; } = PluginSettingScopes.Household;
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public bool IsInteger { get; init; }
    public IReadOnlyList<PluginSettingChoice> Choices { get; init; } = [];
    public JsonElement? Default { get; init; }
    public PluginSettingShowWhen? ShowWhen { get; init; }
    public PluginSettingRequirement? Requires { get; init; }

    /// <summary>The name of an Engine-side list that holds the current value (for example a list of servers kept in the data store), or empty when the value is the saved setting.</summary>
    public string ValueSource { get; init; } = "";

    public PluginGroupListDefinition? GroupList { get; init; }
}

/// <summary>A test or check the plugin says it supports.</summary>
public sealed record PluginDeclaredAction(string Kind, string Label);

/// <summary>A word the reader did not understand, kept so the Plugin file card can tell the author. Never an error.</summary>
public sealed record PluginSettingsNote(string Where, string Word, string Message);

/// <summary>Everything a plugin says about its settings.</summary>
public sealed record PluginSettingsDescription(
    IReadOnlyList<PluginSettingDefinition> Settings,
    IReadOnlyList<PluginDeclaredAction> Actions,
    IReadOnlyList<PluginSettingsNote> ThingsToKnow)
{
    public static PluginSettingsDescription Empty { get; } = new([], [], []);
}

/// <summary>
/// Reads the <c>settings_schema</c> of a plugin manifest or of a provider source file (Plugins and Integrations screens v5 D47).
/// The schema keeps its existing shape (<c>properties</c>, each with <c>type</c>, <c>title</c>, <c>enum</c>, <c>minimum</c> ...), so
/// every plugin that shipped before keeps working; the new words only add to it. A word the reader does not know is ignored and
/// collected as a note, never thrown.
/// </summary>
public static class PluginSettingsDescriptionReader
{
    private static readonly HashSet<string> RootWords = new(StringComparer.Ordinal)
    {
        "$schema", "type", "title", "description", "properties", "required", "actions",
    };

    private static readonly HashSet<string> SettingWords = new(StringComparer.Ordinal)
    {
        "type", "control", "title", "description", "placeholder", "unit", "group", "order", "advanced", "read_only", "show_when", "requires",
        "scope", "minimum", "maximum", "default", "enum", "enum_labels", "choices", "format", "value_source",
        "item_title", "add_label", "locked", "row_actions", "fields",
        "label", "help_text", "ui:advanced", "x-advanced",
    };

    private static readonly HashSet<string> FieldWords = new(StringComparer.Ordinal)
    {
        "type", "control", "title", "description", "placeholder", "unit", "order", "read_only", "minimum", "maximum", "default",
        "enum", "enum_labels", "choices", "format", "advanced", "group", "show_when", "requires", "scope",
        "label", "help_text", "ui:advanced", "x-advanced",
    };

    /// <summary>Reads a settings description. A null, missing or malformed schema gives an empty description (and a note when it is malformed).</summary>
    public static PluginSettingsDescription Read(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } root)
        {
            return schema is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }
                ? new PluginSettingsDescription([], [], [new PluginSettingsNote("", "settings_schema", "The settings description must be an object, so it was ignored.")])
                : PluginSettingsDescription.Empty;
        }

        var notes = new List<PluginSettingsNote>();
        foreach (var property in root.EnumerateObject().Where(property => !RootWords.Contains(property.Name)))
        {
            notes.Add(new PluginSettingsNote("", property.Name, $"The word \"{property.Name}\" is not part of the settings description, so it was ignored."));
        }

        var settings = new List<PluginSettingDefinition>();
        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var position = 0;
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    notes.Add(new PluginSettingsNote(property.Name, property.Name, $"The setting \"{property.Name}\" is not described by an object, so it was ignored."));
                    continue;
                }

                settings.Add(ReadSetting(property.Name, property.Value, position++, topLevel: true, notes));
            }
        }
        else if (root.TryGetProperty("properties", out _))
        {
            notes.Add(new PluginSettingsNote("", "properties", "The word \"properties\" must be an object of settings, so it was ignored."));
        }

        var actions = ReadActions(root, notes);
        return new PluginSettingsDescription(
            [.. settings.OrderBy(setting => setting.Order).ThenBy(setting => setting.Key, StringComparer.Ordinal)],
            actions,
            notes);
    }

    /// <summary>The settings a plugin may save are the ones it declares; used by checks that need only the names.</summary>
    public static IReadOnlyList<string> Keys(JsonElement? schema) => [.. Read(schema).Settings.Select(setting => setting.Key)];

    private static List<PluginDeclaredAction> ReadActions(JsonElement root, List<PluginSettingsNote> notes)
    {
        var actions = new List<PluginDeclaredAction>();
        if (!root.TryGetProperty("actions", out var declared))
        {
            return actions;
        }

        if (declared.ValueKind != JsonValueKind.Array)
        {
            notes.Add(new PluginSettingsNote("", "actions", "The word \"actions\" must be a list, so it was ignored."));
            return actions;
        }

        foreach (var item in declared.EnumerateArray())
        {
            var kind = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => ReadString(item, "kind"),
                _ => null,
            };
            kind = kind?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (kind is null || !PluginDeclaredActions.All.Contains(kind))
            {
                notes.Add(new PluginSettingsNote("", "actions", $"The action \"{kind ?? item.ToString()}\" is not one of test_connection, check_now or dry_run, so it was ignored."));
                continue;
            }

            if (actions.Any(action => action.Kind == kind))
            {
                continue;
            }

            var label = item.ValueKind == JsonValueKind.Object ? ReadString(item, "label") : null;
            actions.Add(new PluginDeclaredAction(kind, string.IsNullOrWhiteSpace(label) ? DefaultActionLabel(kind) : label.Trim()));
        }

        return actions;
    }

    /// <summary>The standard words on a declared test button.</summary>
    public static string DefaultActionLabel(string kind) => kind switch
    {
        PluginDeclaredActions.TestConnection => "Test connection",
        PluginDeclaredActions.CheckNow => "Check now",
        PluginDeclaredActions.DryRun => "Dry run",
        _ => kind,
    };

    private static PluginSettingDefinition ReadSetting(string key, JsonElement definition, int position, bool topLevel, List<PluginSettingsNote> notes)
    {
        var known = topLevel ? SettingWords : FieldWords;
        foreach (var property in definition.EnumerateObject().Where(property => !known.Contains(property.Name)))
        {
            notes.Add(new PluginSettingsNote(key, property.Name, $"The word \"{property.Name}\" on the setting \"{key}\" is not part of the settings description, so it was ignored."));
        }

        var type = ReadString(definition, "type")?.Trim().ToLowerInvariant();
        var format = ReadString(definition, "format")?.Trim().ToLowerInvariant();
        var choices = ReadChoices(definition, key, notes);
        var control = ReadControl(definition, key, type, format, choices.Count > 0, notes);
        var scope = PluginSettingScopes.Household;
        if (ReadString(definition, "scope") is { } declaredScope)
        {
            var wanted = declaredScope.Trim().ToLowerInvariant();
            if (wanted is PluginSettingScopes.Household or PluginSettingScopes.Profile)
            {
                scope = wanted;
            }
            else
            {
                notes.Add(new PluginSettingsNote(key, "scope", $"The scope \"{declaredScope}\" on the setting \"{key}\" is not household or profile, so household is used."));
            }
        }

        JsonElement? defaultValue = definition.TryGetProperty("default", out var found) && found.ValueKind != JsonValueKind.Undefined ? found.Clone() : null;
        return new PluginSettingDefinition
        {
            Key = key,
            Control = control,
            Title = (ReadString(definition, "title") ?? ReadString(definition, "label"))?.Trim() is { Length: > 0 } title ? title : HumanizeKey(key),
            Description = (ReadString(definition, "description") ?? ReadString(definition, "help_text"))?.Trim() ?? "",
            Placeholder = ReadString(definition, "placeholder")?.Trim() ?? "",
            Unit = ReadString(definition, "unit")?.Trim() ?? "",
            Group = ReadString(definition, "group")?.Trim() ?? "",
            Order = ReadInt(definition, "order") ?? position,
            Advanced = ReadBool(definition, "advanced") || ReadBool(definition, "ui:advanced") || ReadBool(definition, "x-advanced"),
            ReadOnly = ReadBool(definition, "read_only"),
            Scope = scope,
            Minimum = ReadDouble(definition, "minimum"),
            Maximum = ReadDouble(definition, "maximum"),
            IsInteger = type is "integer" or "int",
            Choices = choices,
            Default = defaultValue,
            ShowWhen = ReadShowWhen(definition, key, notes),
            Requires = ReadRequirement(definition, key, notes),
            ValueSource = ReadString(definition, "value_source")?.Trim() ?? "",
            GroupList = control == PluginSettingControls.GroupList ? ReadGroupList(definition, key, notes) : null,
        };
    }

    private static string ReadControl(JsonElement definition, string key, string? type, string? format, bool hasChoices, List<PluginSettingsNote> notes)
    {
        if (ReadString(definition, "control") is { } declared)
        {
            var wanted = declared.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (PluginSettingControls.All.Contains(wanted))
            {
                return wanted;
            }

            notes.Add(new PluginSettingsNote(key, "control", $"The control \"{declared}\" on the setting \"{key}\" is not one Library knows, so a plain one is used."));
        }

        if (definition.TryGetProperty("fields", out _))
        {
            return PluginSettingControls.GroupList;
        }

        if (type == "array")
        {
            return hasChoices ? PluginSettingControls.Choices : PluginSettingControls.Text;
        }

        if (hasChoices)
        {
            return PluginSettingControls.Choice;
        }

        return type switch
        {
            "boolean" or "bool" => PluginSettingControls.Toggle,
            "integer" or "int" or "number" => PluginSettingControls.Number,
            _ => format switch
            {
                "uri" or "url" => PluginSettingControls.Url,
                "path" or "file-path" or "directory" => PluginSettingControls.Path,
                "password" or "secret" => PluginSettingControls.Secret,
                _ => PluginSettingControls.Text,
            },
        };
    }

    private static List<PluginSettingChoice> ReadChoices(JsonElement definition, string key, List<PluginSettingsNote> notes)
    {
        var result = new List<PluginSettingChoice>();
        var labels = definition.TryGetProperty("enum_labels", out var labelObject) && labelObject.ValueKind == JsonValueKind.Object
            ? labelObject.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (definition.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind == JsonValueKind.Object)
                {
                    var value = ReadString(choice, "value") ?? (choice.TryGetProperty("value", out var raw) ? raw.ToString() : null);
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(new PluginSettingChoice(value, ReadString(choice, "label")?.Trim() is { Length: > 0 } label ? label : HumanizeKey(value)));
                    }
                }
                else if (choice.ValueKind != JsonValueKind.Null)
                {
                    var value = choice.ValueKind == JsonValueKind.String ? choice.GetString() ?? "" : choice.ToString();
                    result.Add(new PluginSettingChoice(value, labels.GetValueOrDefault(value) ?? HumanizeKey(value)));
                }
            }
        }
        else if (definition.TryGetProperty("choices", out _))
        {
            notes.Add(new PluginSettingsNote(key, "choices", $"The word \"choices\" on the setting \"{key}\" must be a list, so it was ignored."));
        }

        if (result.Count == 0 && definition.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in enumeration.EnumerateArray().Where(value => value.ValueKind != JsonValueKind.Null))
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
                result.Add(new PluginSettingChoice(text, labels.GetValueOrDefault(text) ?? HumanizeKey(text)));
            }
        }

        return result;
    }

    private static PluginSettingShowWhen? ReadShowWhen(JsonElement definition, string key, List<PluginSettingsNote> notes)
    {
        if (!definition.TryGetProperty("show_when", out var showWhen))
        {
            return null;
        }

        if (showWhen.ValueKind == JsonValueKind.Object
            && ReadString(showWhen, "setting")?.Trim() is { Length: > 0 } setting
            && showWhen.TryGetProperty("equals", out var equals))
        {
            return new PluginSettingShowWhen(setting, equals.Clone());
        }

        notes.Add(new PluginSettingsNote(key, "show_when", $"The word \"show_when\" on the setting \"{key}\" needs a \"setting\" and an \"equals\", so the setting is always shown."));
        return null;
    }

    private static PluginSettingRequirement? ReadRequirement(JsonElement definition, string key, List<PluginSettingsNote> notes)
    {
        if (!definition.TryGetProperty("requires", out var requires))
        {
            return null;
        }

        var kind = requires.ValueKind == JsonValueKind.Object ? ReadString(requires, "kind")?.Trim().ToLowerInvariant().Replace('-', '_') : null;
        if (kind is not null && PluginSettingRequirementKinds.All.Contains(kind))
        {
            var name = ReadString(requires, "name")?.Trim() ?? "";
            var reason = ReadString(requires, "reason")?.Trim();
            return new PluginSettingRequirement(kind, name, string.IsNullOrWhiteSpace(reason) ? DefaultReason(kind, name) : reason);
        }

        notes.Add(new PluginSettingsNote(key, "requires", $"The word \"requires\" on the setting \"{key}\" needs a kind of tool, local_ai or key, so it was ignored."));
        return null;
    }

    private static string DefaultReason(string kind, string name) => kind switch
    {
        PluginSettingRequirementKinds.Tool => string.IsNullOrEmpty(name) ? "Needs a tool that is not installed." : $"Needs {name}, which is not installed.",
        PluginSettingRequirementKinds.LocalAi => "Needs Local AI.",
        _ => string.IsNullOrEmpty(name) ? "Needs a key." : $"Needs the key \"{name}\".",
    };

    private static PluginGroupListDefinition ReadGroupList(JsonElement definition, string key, List<PluginSettingsNote> notes)
    {
        var fields = new List<PluginSettingDefinition>();
        if (definition.TryGetProperty("fields", out var declared) && declared.ValueKind == JsonValueKind.Object)
        {
            var position = 0;
            foreach (var field in declared.EnumerateObject().Where(field => field.Value.ValueKind == JsonValueKind.Object))
            {
                fields.Add(ReadSetting($"{key}.{field.Name}", field.Value, position++, topLevel: false, notes) with { Key = field.Name });
            }
        }

        var rowActions = new List<string>();
        if (definition.TryGetProperty("row_actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') : null))
            {
                if (action is not null && PluginRowActions.All.Contains(action))
                {
                    if (!rowActions.Contains(action))
                    {
                        rowActions.Add(action);
                    }
                }
                else
                {
                    notes.Add(new PluginSettingsNote(key, "row_actions", $"The row action \"{action}\" on the setting \"{key}\" is not test_connection, remove, check_now or open_link, so it was ignored."));
                }
            }
        }

        var locked = definition.TryGetProperty("locked", out var lockedItems) && lockedItems.ValueKind == JsonValueKind.Array
            ? lockedItems.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList()
            : [];
        return new PluginGroupListDefinition(
            ReadString(definition, "item_title")?.Trim() is { Length: > 0 } itemTitle ? itemTitle : "Item",
            ReadString(definition, "add_label")?.Trim() is { Length: > 0 } addLabel ? addLabel : "Add",
            locked,
            rowActions,
            fields);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    /// <summary>"request_delay_ms" becomes "Request delay ms": the plain fallback when a setting has no title.</summary>
    public static string HumanizeKey(string key)
    {
        var words = key.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? key : string.Concat(char.ToUpper(words[0], CultureInfo.InvariantCulture).ToString(), words.AsSpan(1));
    }
}
