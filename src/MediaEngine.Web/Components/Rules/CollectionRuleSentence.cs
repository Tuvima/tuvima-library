using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Rules;

/// <summary>
/// The "sentence" shape of a collection's rules (Collections and lists spec D2): media type chips, rules joined by
/// "and", and a "but never when" Exceptions list. It is stored in the ordinary rule groups so the Engine needs nothing
/// new: the chips live in a reserved first group, the rules in one group (all, or any with "Match any of these
/// instead"), and the exceptions in a reserved last group of negated rules. Several groups of any other kind mean the
/// definition needs the Advanced layout.
/// </summary>
internal static class CollectionRuleSentence
{
    public const string MediaGroupId = "media";
    public const string ExceptionsGroupId = "exceptions";
    public const string MediaField = "media_type";

    public static bool IsMediaGroup(CollectionRuleGroupViewModel group) => group.Id == MediaGroupId;
    public static bool IsExceptionsGroup(CollectionRuleGroupViewModel group) => group.Id == ExceptionsGroupId;

    /// <summary>
    /// True when the definition fits the sentence layout: at most one media group (first, media type only), one rules
    /// group and one exceptions group (last, every rule negatable). A definition with any other groups needs Advanced.
    /// </summary>
    public static bool IsPlain(CollectionRuleDefinitionViewModel definition)
    {
        var groups = definition.Groups;
        var rulesGroups = 0;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (IsMediaGroup(group))
            {
                if (index != 0 || group.Conditions.Any(condition => condition.Field != MediaField))
                {
                    return false;
                }
            }
            else if (IsExceptionsGroup(group))
            {
                if (index != groups.Count - 1 || group.MatchMode != "all" || group.Conditions.Any(condition => !IsNegated(condition) && !IsBlankRow(condition)))
                {
                    return false;
                }
            }
            else
            {
                rulesGroups++;
            }
        }

        return rulesGroups <= 1;
    }

    public static CollectionRuleGroupViewModel? MediaGroup(CollectionRuleDefinitionViewModel definition) =>
        definition.Groups.FirstOrDefault(IsMediaGroup);

    public static CollectionRuleGroupViewModel? ExceptionsGroup(CollectionRuleDefinitionViewModel definition) =>
        definition.Groups.FirstOrDefault(IsExceptionsGroup);

    /// <summary>The group holding the include rules; created (after the media group, before the exceptions) when missing.</summary>
    public static CollectionRuleGroupViewModel IncludeGroup(CollectionRuleDefinitionViewModel definition)
    {
        var group = definition.Groups.FirstOrDefault(candidate => !IsMediaGroup(candidate) && !IsExceptionsGroup(candidate));
        if (group is not null)
        {
            return group;
        }

        group = new CollectionRuleGroupViewModel { Conditions = [], JoinWithPrevious = "and" };
        var at = definition.Groups.Count(IsMediaGroup);
        definition.Groups.Insert(at, group);
        return group;
    }

    public static CollectionRuleGroupViewModel EnsureExceptionsGroup(CollectionRuleDefinitionViewModel definition)
    {
        var group = ExceptionsGroup(definition);
        if (group is not null)
        {
            return group;
        }

        IncludeGroup(definition);
        group = new CollectionRuleGroupViewModel { Id = ExceptionsGroupId, Conditions = [], JoinWithPrevious = "and", MatchMode = "all" };
        definition.Groups.Add(group);
        return group;
    }

    /// <summary>The media types ticked as chips, in the order stored.</summary>
    public static IReadOnlyList<string> MediaTypes(CollectionRuleDefinitionViewModel definition)
    {
        var condition = MediaGroup(definition)?.Conditions.FirstOrDefault();
        if (condition is null)
        {
            return [];
        }

        if (condition.Values is { Length: > 0 })
        {
            return condition.Values;
        }

        return string.IsNullOrWhiteSpace(condition.Value) ? [] : [condition.Value];
    }

    /// <summary>Writes the ticked chips into the media group; no chips ("Everything") removes it.</summary>
    public static void SetMediaTypes(CollectionRuleDefinitionViewModel definition, IReadOnlyCollection<string> types)
    {
        var media = MediaGroup(definition);
        if (types.Count == 0)
        {
            if (media is not null)
            {
                definition.Groups.Remove(media);
            }

            SettleJoins(definition);
            return;
        }

        if (media is null)
        {
            media = new CollectionRuleGroupViewModel { Id = MediaGroupId, Conditions = [], MatchMode = "all" };
            definition.Groups.Insert(0, media);
        }

        var ordered = types.ToArray();
        media.Conditions =
        [
            ordered.Length == 1
                ? new CollectionRulePredicateViewModel { Field = MediaField, Op = "eq", Value = ordered[0], DisplayValue = ordered[0] }
                : new CollectionRulePredicateViewModel { Field = MediaField, Op = "in", Values = ordered, DisplayValues = ordered },
        ];
        SettleJoins(definition);
    }

    /// <summary>"Match any of these instead": the rules group matches any rule rather than all of them.</summary>
    public static bool MatchAny(CollectionRuleDefinitionViewModel definition) =>
        definition.Groups.FirstOrDefault(group => !IsMediaGroup(group) && !IsExceptionsGroup(group))?.MatchMode == "any";

    public static void SetMatchAny(CollectionRuleDefinitionViewModel definition, bool any) =>
        IncludeGroup(definition).MatchMode = any ? "any" : "all";

    /// <summary>Every group after the first is joined with "and": chips AND rules AND NOT exceptions.</summary>
    public static void SettleJoins(CollectionRuleDefinitionViewModel definition)
    {
        for (var index = 1; index < definition.Groups.Count; index++)
        {
            var group = definition.Groups[index];
            if (IsMediaGroup(group) || IsExceptionsGroup(group) || definition.Groups.Count(candidate => !IsMediaGroup(candidate) && !IsExceptionsGroup(candidate)) <= 1)
            {
                group.JoinWithPrevious = "and";
            }
        }
    }

    // ── Exceptions ─────────────────────────────────────────────────────────────────────────────────
    // An exception row reads in positive words ("Genre is Horror") under "but never when", and is stored as the
    // opposite rule ("Genre is not Horror"). Fields with no word for the opposite are not offered as exceptions.

    private static readonly Dictionary<string, string> Opposite = new(StringComparer.Ordinal)
    {
        ["eq"] = "neq",
        ["neq"] = "eq",
        ["contains"] = "not_contains",
        ["not_contains"] = "contains",
        ["known"] = "unknown",
        ["unknown"] = "known",
        ["has_any_value"] = "has_no_known_value",
        ["has_no_known_value"] = "has_any_value",
        ["gt"] = "lte",
        ["lte"] = "gt",
        ["gte"] = "lt",
        ["lt"] = "gte",
        ["within_days"] = "not_within_days",
        ["not_within_days"] = "within_days",
    };

    /// <summary>True when a rule row already stands for the opposite of what its words say (it belongs under "but never when").</summary>
    public static bool IsNegated(CollectionRulePredicateViewModel condition) =>
        condition.Op is "neq" or "not_contains" or "unknown" or "has_no_known_value" or "lte" or "lt" or "not_within_days"
        || condition.Op == "eq" && IsBooleanValue(condition.Value, false);

    /// <summary>A row just added whose value is not chosen yet: it fits an exceptions group while it is being filled in.</summary>
    private static bool IsBlankRow(CollectionRulePredicateViewModel condition) =>
        condition.Op == "eq" && string.IsNullOrWhiteSpace(condition.Value) && condition.Values is not { Length: > 0 };

    private static bool IsBooleanValue(string? value, bool expected) =>
        bool.TryParse(value, out var parsed) && parsed == expected;

    /// <summary>True when the field offers a condition with an opposite, so it can be an exception.</summary>
    public static bool CanBeException(RuleFieldDefinition field) => field.Operators.Any(op => ExceptionOperators(field).Contains(op.Key));

    /// <summary>The conditions shown for an exception row: the positive words that have an opposite.</summary>
    public static IReadOnlyList<RuleOperatorDefinition> ExceptionConditions(RuleFieldDefinition field)
    {
        var keys = ExceptionOperators(field);
        return field.Operators.Where(op => keys.Contains(op.Key)).ToList();
    }

    private static HashSet<string> ExceptionOperators(RuleFieldDefinition field)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { "eq", "in", "contains", "known", "unknown", "gt", "gte", "lt", "lte", "within_days", "not_within_days" };
        if (field.ValueProvider.Kind == RuleValueProviderKind.Boolean)
        {
            return ["eq"];
        }
        // "is not" and "does not contain" are already negative: offering them as exceptions would read as a double negative.
        keys.Remove("neq");
        return keys;
    }

    /// <summary>The positive condition an exception row shows for the stored rule.</summary>
    public static string DisplayedCondition(CollectionRulePredicateViewModel stored, RuleFieldDefinition field)
    {
        if (field.ValueProvider.Kind == RuleValueProviderKind.Boolean)
        {
            return "eq";
        }

        if (stored.Op == "neq")
        {
            return stored.Values is { Length: > 1 } ? "in" : "eq";
        }

        return Opposite.TryGetValue(stored.Op, out var positive) ? positive : stored.Op;
    }

    /// <summary>Applies a condition chosen on an exception row, storing its opposite.</summary>
    public static void ApplyDisplayedCondition(CollectionRulePredicateViewModel stored, RuleFieldDefinition field, string positiveOp)
    {
        if (field.ValueProvider.Kind == RuleValueProviderKind.Boolean)
        {
            stored.Op = "eq";
            return;
        }

        stored.Op = positiveOp switch
        {
            "in" => "neq",
            _ => Opposite.GetValueOrDefault(positiveOp, positiveOp),
        };
    }

    /// <summary>The Yes/No an exception row shows for a stored true/false rule (the stored value is the opposite).</summary>
    public static string DisplayedBoolean(string? stored) => IsBooleanValue(stored, true) ? "false" : "true";

    public static string StoredBoolean(string? displayed) => IsBooleanValue(displayed, true) ? "false" : "true";

    // ── Wire shape ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The definition as the Engine stores and evaluates it: a "none of" exception (is not, with several values)
    /// becomes one "is not" rule per value, because the Engine reads a single value for "is not".
    /// </summary>
    public static CollectionRuleDefinitionViewModel ToWire(CollectionRuleDefinitionViewModel definition) => new()
    {
        Version = definition.Version,
        Groups = definition.Groups.Select(group => new CollectionRuleGroupViewModel
        {
            Id = group.Id,
            JoinWithPrevious = group.JoinWithPrevious,
            MatchMode = group.MatchMode,
            Conditions = group.Conditions.SelectMany(condition => ExpandNone(group, condition)).ToList(),
        }).ToList(),
    };

    private static IEnumerable<CollectionRulePredicateViewModel> ExpandNone(CollectionRuleGroupViewModel group, CollectionRulePredicateViewModel condition)
    {
        if (!IsExceptionsGroup(group) || condition.Op != "neq" || condition.Values is not { Length: > 1 } values)
        {
            yield return condition;
            yield break;
        }

        for (var index = 0; index < values.Length; index++)
        {
            yield return new CollectionRulePredicateViewModel
            {
                Field = condition.Field,
                Op = "neq",
                Value = values[index],
                DisplayValue = condition.DisplayValues is { } labels && index < labels.Length ? labels[index] : values[index],
            };
        }
    }

    /// <summary>
    /// Reads a stored definition into the shape the builder edits: "is not" exceptions on one field fold back into one
    /// "none of" row, and a single group whose rules all must match hands its media type rule to the chips.
    /// </summary>
    public static CollectionRuleDefinitionViewModel FromWire(CollectionRuleDefinitionViewModel definition)
    {
        var exceptions = ExceptionsGroup(definition);
        if (exceptions is not null)
        {
            var folded = new List<CollectionRulePredicateViewModel>();
            foreach (var condition in exceptions.Conditions)
            {
                var same = condition.Op == "neq" && condition.Values is null
                    ? folded.FirstOrDefault(existing => existing.Field == condition.Field && existing.Op == "neq")
                    : null;
                if (same is null)
                {
                    folded.Add(condition);
                    continue;
                }

                var values = (same.Values ?? (same.Value is null ? [] : [same.Value])).Append(condition.Value ?? string.Empty).ToArray();
                var labels = (same.DisplayValues ?? (same.DisplayValue is null ? [] : [same.DisplayValue])).Append(condition.DisplayValue ?? condition.Value ?? string.Empty).ToArray();
                same.Values = values;
                same.DisplayValues = labels;
                same.Value = null;
                same.DisplayValue = null;
            }

            exceptions.Conditions = folded;
        }

        if (definition.Groups.Count == 1 && definition.Groups[0].MatchMode == "all" && !IsExceptionsGroup(definition.Groups[0]) && !IsMediaGroup(definition.Groups[0]))
        {
            var only = definition.Groups[0];
            var chip = only.Conditions.FirstOrDefault(condition => condition.Field == MediaField && condition.Op is "eq" or "in");
            if (chip is not null)
            {
                only.Conditions.Remove(chip);
                only.JoinWithPrevious = "and";
                definition.Groups.Insert(0, new CollectionRuleGroupViewModel { Id = MediaGroupId, Conditions = [chip], MatchMode = "all" });
            }
        }

        return definition;
    }

    /// <summary>A rule in words, for hints: "Genre is Horror".</summary>
    public static string Describe(CollectionRulePredicateViewModel condition, RuleBuilderCatalog catalog)
    {
        var field = catalog.Fields.FirstOrDefault(candidate => candidate.Key == condition.Field);
        var op = field?.Operators.FirstOrDefault(candidate => candidate.Key == condition.Op)?.Label ?? condition.Op;
        var value = condition.DisplayValues is { Length: > 0 } labels ? string.Join(", ", labels)
            : condition.Values is { Length: > 0 } values ? string.Join(", ", values)
            : condition.DisplayValue ?? condition.Value;
        return string.IsNullOrWhiteSpace(value)
            ? $"{field?.Label ?? condition.Field} {op}"
            : $"{field?.Label ?? condition.Field} {op} {value}";
    }
}
