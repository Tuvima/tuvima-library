using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.PersonalMedia;

/// <summary>
/// Parses and validates the versioned rule document used by Smart Galleries.
/// This is the only accepted rule vocabulary for personal media. Keeping the
/// vocabulary here prevents UI labels or arbitrary client values from becoming
/// query identifiers.
/// </summary>
public static class ViewSmartGalleryRules
{
    public const int CurrentVersion = 1;
    public const int MaximumDocumentLength = 64 * 1024;
    public const int MaximumGroups = 20;
    public const int MaximumConditionsPerGroup = 30;
    public const int MaximumValuesPerCondition = 50;
    public const int MaximumValueLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Operators =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["media_type"] = Set("eq", "neq", "in", "known", "unknown"),
            ["file_type"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["orientation"] = Set("eq", "neq", "in", "known", "unknown"),
            ["duration"] = Set("eq", "neq", "known", "unknown", "gt", "lt", "gte", "lte", "between"),
            ["captured_date"] = Set("eq", "neq", "known", "unknown", "gt", "lt", "gte", "lte", "between"),
            ["people"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["place"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["device"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["tags"] = Set("eq", "neq", "in", "all", "contains", "known", "unknown"),
            ["correspondent"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["document_type"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["document_date"] = Set("eq", "neq", "known", "unknown", "gt", "lt", "gte", "lte", "between", "within"),
            ["filing_state"] = Set("eq", "neq"),
            ["text_contains"] = Set("contains"),
            ["favorite"] = Set("eq", "neq", "known", "unknown"),
            ["owner"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
            ["source"] = Set("eq", "neq", "in", "contains", "known", "unknown"),
        };

    /// <summary>Prefix of a condition on one custom document field: <c>field:&lt;field id&gt;</c>.</summary>
    public const string CustomFieldPrefix = "field:";

    private static readonly IReadOnlySet<string> CustomFieldOperators =
        Set("eq", "neq", "contains", "gt", "lt", "gte", "lte", "between", "known", "unknown");

    /// <summary>The relative document-date ranges a rule can name, resolved when the rule is evaluated.</summary>
    public static readonly IReadOnlyList<string> RelativeDateRanges =
        ["last_7_days", "last_30_days", "last_90_days", "last_365_days", "this_month", "this_year", "last_year"];

    /// <summary>The field key for a custom document field, or null when the key is not one.</summary>
    public static bool TryParseCustomField(string? field, out Guid fieldId)
    {
        fieldId = Guid.Empty;
        return field is not null
            && field.StartsWith(CustomFieldPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(field[CustomFieldPrefix.Length..], "D", out fieldId)
            && fieldId != Guid.Empty;
    }

    public static string CustomFieldKey(Guid fieldId) =>
        CustomFieldPrefix + fieldId.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the rule is a saved Documents view: one group in which every condition must hold and one of them
    /// is "media type is document", so it can only ever match documents.
    /// </summary>
    public static bool IsDocumentView(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumDocumentLength)
        {
            return false;
        }

        try
        {
            var definition = Parse(json);
            return definition.Groups is [{ MatchMode: "all" } group] && group.Conditions.Any(IsDocumentCondition);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDocumentCondition(CollectionRulePredicate condition) =>
        condition.Field == "media_type" && condition.Op == "eq"
        && condition.GetEffectiveValues() is [var only] && only.Trim().Equals("document", StringComparison.OrdinalIgnoreCase);

    public static CollectionRuleDefinition Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumDocumentLength)
        {
            throw new ArgumentException($"Smart Gallery rules cannot exceed {MaximumDocumentLength} characters.", nameof(json));
        }

        CollectionRuleDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<CollectionRuleDefinition>(json, JsonOptions)
                ?? throw new ArgumentException("Smart Gallery rule JSON must contain a rule definition.", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Smart Gallery rule JSON is malformed.", nameof(json), exception);
        }

        Validate(definition);
        return definition;
    }

    public static string Normalize(string json) =>
        JsonSerializer.Serialize(Parse(json), JsonOptions);

    /// <summary>Validates a rule built in code and writes it as the stored rule document.</summary>
    public static string Serialize(CollectionRuleDefinition definition)
    {
        Validate(definition);
        var json = JsonSerializer.Serialize(definition, JsonOptions);
        return json.Length > MaximumDocumentLength
            ? throw new ArgumentException($"Smart Gallery rules cannot exceed {MaximumDocumentLength} characters.", nameof(definition))
            : json;
    }

    /// <summary>
    /// The first and last day a relative document-date range covers on <paramref name="today"/>. The range names are
    /// <see cref="RelativeDateRanges"/>; anything else is refused.
    /// </summary>
    public static (DateOnly From, DateOnly To) ResolveRelativeRange(string name, DateOnly today) => name switch
    {
        "last_7_days" => (today.AddDays(-6), today),
        "last_30_days" => (today.AddDays(-29), today),
        "last_90_days" => (today.AddDays(-89), today),
        "last_365_days" => (today.AddDays(-364), today),
        "this_month" => (new DateOnly(today.Year, today.Month, 1), today),
        "this_year" => (new DateOnly(today.Year, 1, 1), today),
        "last_year" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
        _ => throw new ArgumentException($"Unsupported document date range '{name}'.", nameof(name)),
    };

    /// <summary>
    /// The conditions a Vault gallery rule may use (Videos spec D44). The Vault uses the same rule document and the same checks as
    /// View's Smart Galleries, but only on what its images really have: their kind, file type, shape, date, own tags and
    /// favourite mark. Anything that reaches into View's people, devices, sources, owners or Documents is refused.
    /// </summary>
    public static readonly IReadOnlySet<string> VaultFields =
        Set("media_type", "file_type", "orientation", "captured_date", "tags", "favorite");

    /// <summary>Validates <paramref name="definition"/> as a Vault gallery rule: the View checks plus the Vault's narrower field list.</summary>
    public static void ValidateForVault(CollectionRuleDefinition definition)
    {
        Validate(definition);
        foreach (var condition in definition.Groups.SelectMany(group => group.Conditions))
        {
            if (!VaultFields.Contains(condition.Field))
            {
                throw new ArgumentException($"Vault gallery rules cannot use the field '{condition.Field}'.", nameof(definition));
            }
        }
    }

    /// <summary>Parses and validates a stored Vault gallery rule.</summary>
    public static CollectionRuleDefinition ParseForVault(string json)
    {
        var definition = Parse(json);
        ValidateForVault(definition);
        return definition;
    }

    /// <summary>Validates a Vault gallery rule built in code and writes it as the stored rule document.</summary>
    public static string SerializeForVault(CollectionRuleDefinition definition)
    {
        ValidateForVault(definition);
        return Serialize(definition);
    }

    public static void Validate(CollectionRuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Version != CurrentVersion)
        {
            throw new ArgumentException($"Smart Gallery rule version {definition.Version} is unsupported; expected version {CurrentVersion}.", nameof(definition));
        }

        if (definition.Groups is null || definition.Groups.Count is < 1 or > MaximumGroups)
        {
            throw new ArgumentException($"Smart Gallery rules require between 1 and {MaximumGroups} groups.", nameof(definition));
        }

        for (var groupIndex = 0; groupIndex < definition.Groups.Count; groupIndex++)
        {
            var group = definition.Groups[groupIndex]
                ?? throw new ArgumentException($"Smart Gallery rule group {groupIndex + 1} is missing.", nameof(definition));
            group.MatchMode = NormalizeChoice(group.MatchMode, "all", "any", $"group {groupIndex + 1} match mode");
            group.JoinWithPrevious = NormalizeChoice(group.JoinWithPrevious, "and", "or", $"group {groupIndex + 1} join");
            if (group.Conditions is null || group.Conditions.Count is < 1 or > MaximumConditionsPerGroup)
            {
                throw new ArgumentException($"Smart Gallery rule group {groupIndex + 1} requires between 1 and {MaximumConditionsPerGroup} conditions.", nameof(definition));
            }

            for (var conditionIndex = 0; conditionIndex < group.Conditions.Count; conditionIndex++)
            {
                ValidateCondition(group.Conditions[conditionIndex], groupIndex, conditionIndex);
            }
        }
    }

    private static void ValidateCondition(CollectionRulePredicate? condition, int groupIndex, int conditionIndex)
    {
        var label = $"condition {conditionIndex + 1} in group {groupIndex + 1}";
        if (condition is null)
        {
            throw new ArgumentException($"Smart Gallery rule {label} is missing.");
        }

        if (string.IsNullOrWhiteSpace(condition.Field) || string.IsNullOrWhiteSpace(condition.Op))
        {
            throw new ArgumentException($"Smart Gallery rule {label} requires a field and operator.");
        }

        condition.Field = condition.Field.Trim().ToLowerInvariant();
        condition.Op = condition.Op.Trim().ToLowerInvariant();
        IReadOnlySet<string>? supported;
        if (TryParseCustomField(condition.Field, out var customFieldId))
        {
            condition.Field = CustomFieldKey(customFieldId);
            supported = CustomFieldOperators;
        }
        else if (!Operators.TryGetValue(condition.Field, out supported))
        {
            throw new ArgumentException($"Smart Gallery field '{condition.Field}' is unsupported.");
        }

        if (!supported.Contains(condition.Op))
        {
            throw new ArgumentException($"Operator '{condition.Op}' is unsupported for Smart Gallery field '{condition.Field}'.");
        }

        var values = condition.GetEffectiveValues();
        if (condition.Op is "known" or "unknown")
        {
            if (values.Length != 0)
            {
                throw new ArgumentException($"Smart Gallery {label} must not include values for '{condition.Op}'.");
            }

            return;
        }

        var requiredCount = condition.Op == "between" ? 2 : 1;
        if (values.Length < requiredCount || values.Length > MaximumValuesPerCondition
            || (condition.Op is not ("in" or "all") && values.Length != requiredCount))
        {
            throw new ArgumentException(condition.Op == "between"
                ? $"Smart Gallery {label} requires exactly two values."
                : condition.Op is "in" or "all"
                    ? $"Smart Gallery {label} requires between 1 and {MaximumValuesPerCondition} values."
                    : $"Smart Gallery {label} requires exactly one value.");
        }

        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumValueLength))
        {
            throw new ArgumentException($"Smart Gallery {label} contains an empty or overlong value.");
        }

        if (condition.Field == "media_type" && values.Any(value => !LocalMediaKinds.Contains(value)))
        {
            throw new ArgumentException("Smart Gallery media_type values must be image, video, document, audio, or other.");
        }

        if (condition.Field == "orientation" && values.Any(value => !OrientationValues.Contains(value)))
        {
            throw new ArgumentException("Smart Gallery orientation values must be landscape, portrait, or square.");
        }

        if (condition.Field == "favorite" && values.Any(value => !bool.TryParse(value, out _)))
        {
            throw new ArgumentException("Smart Gallery favorite values must be true or false.");
        }

        if (condition.Field == "duration")
        {
            var parsed = new List<double>(values.Length);
            foreach (var value in values)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number) || number < 0)
                {
                    throw new ArgumentException("Smart Gallery duration values must be finite non-negative numbers.");
                }

                parsed.Add(number);
            }
            if (condition.Op == "between" && parsed[0] > parsed[1])
            {
                throw new ArgumentException("Smart Gallery duration between values must be in ascending order.");
            }
        }
        if (condition.Field == "filing_state" && values.Any(value => !FilingStates.Contains(value.Trim().ToLowerInvariant())))
        {
            throw new ArgumentException("Smart Gallery filing_state values must be inbox or filed.");
        }

        if (condition.Field == "document_date" && condition.Op == "within")
        {
            if (!RelativeDateRanges.Contains(values[0].Trim().ToLowerInvariant()))
            {
                throw new ArgumentException($"Smart Gallery document_date 'within' must be one of: {string.Join(", ", RelativeDateRanges)}.");
            }

            condition.Value = values[0].Trim().ToLowerInvariant();
            condition.Values = null;
        }
        else if (condition.Field is "captured_date" or "document_date")
        {
            var parsed = new List<DateOnly>(values.Length);
            foreach (var value in values)
            {
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                {
                    throw new ArgumentException($"Smart Gallery {condition.Field} values must use yyyy-MM-dd.");
                }

                parsed.Add(date);
            }
            if (condition.Op == "between" && parsed[0] > parsed[1])
            {
                throw new ArgumentException($"Smart Gallery {condition.Field} between values must be in ascending order.");
            }
        }
    }

    private static string NormalizeChoice(string? value, string first, string second, string label)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized == first || normalized == second
            ? normalized
            : throw new ArgumentException($"Smart Gallery {label} must be '{first}' or '{second}'.");
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> LocalMediaKinds = Set("image", "video", "document", "audio", "other");
    private static readonly IReadOnlySet<string> FilingStates = Set("inbox", "filed");
    private static readonly IReadOnlySet<string> OrientationValues = Set("landscape", "portrait", "square");
}
