using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Rules;

/// <summary>How a rule builder lays out its rules.</summary>
public enum RuleBuilderLayout
{
    /// <summary>Rule groups, each with its own all/any, joined with AND/OR (the Advanced layout).</summary>
    Groups,

    /// <summary>Rules joined by "and", with a "but never when" Exceptions list and one "match any" switch.</summary>
    Sentence,
}

public enum RuleValueProviderKind
{
    Text,
    Date,
    Boolean,
    StaticOptions,
    CollectionLibrary,
    DeferredLookup,
}

public sealed record RuleOptionDefinition(string Value, string Label);

public sealed record RuleOperatorDefinition(string Key, string Label);

public sealed record RuleValueProviderDefinition(
    RuleValueProviderKind Kind,
    string? ProviderKey = null,
    IReadOnlyList<RuleOptionDefinition>? Options = null,
    string OptionGroup = "Cross-media",
    string? SearchPlaceholder = null,
    bool ResolvesEntities = false);

public sealed record RuleFieldDefinition(
    string Category,
    string Key,
    string Label,
    string Description,
    string Icon,
    IReadOnlyList<RuleOperatorDefinition> Operators,
    RuleValueProviderDefinition ValueProvider,
    string? RequiredCapability = null);

public sealed record RuleCategoryDefinition(string Key, string Label, string Icon);

public sealed record RuleSortDefinition(string Key, string Label);

public sealed record RuleBuilderCapabilities(
    bool PeopleMetadata = false,
    bool PlaceMetadata = false,
    bool FaceRecognition = false,
    bool SemanticSearch = false,
    bool Ocr = false);

public sealed class RuleBuilderCatalog
{
    public required string Domain { get; init; }
    public required string Eyebrow { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string DefaultCategory { get; init; }
    public required IReadOnlyList<RuleCategoryDefinition> Categories { get; init; }
    public required IReadOnlyList<RuleFieldDefinition> Fields { get; init; }
    public IReadOnlyList<RuleSortDefinition> SortFields { get; init; } = [];
    public bool ShowSortControls { get; init; }
    public RuleBuilderCapabilities Capabilities { get; init; } = new();

    public RuleFieldDefinition Field(string key) => Fields.First(field => field.Key == key);
}

public sealed record RuleBuilderPreviewRequest(
    global::MediaEngine.Web.Models.ViewDTOs.CollectionRuleDefinitionViewModel Definition,
    string? SortField,
    string SortDirection,
    string? SecondarySortField,
    string? SecondarySortDirection,
    int Limit = 15);

internal static class RuleOperators
{
    internal static readonly RuleOperatorDefinition Is = new("eq", "is");
    internal static readonly RuleOperatorDefinition IsNot = new("neq", "is not");
    internal static readonly RuleOperatorDefinition IsAny = new("in", "is any of");
    internal static readonly RuleOperatorDefinition Contains = new("contains", "contains");
    internal static readonly RuleOperatorDefinition Known = new("known", "is known");
    internal static readonly RuleOperatorDefinition Unknown = new("unknown", "is unknown");
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Boolean = [Is];
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Choice = [Is, IsAny, IsNot];
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Lookup = [Is, IsAny, IsNot, Known, Unknown];
    internal static readonly IReadOnlyList<RuleOperatorDefinition> LibraryText = [Is, IsAny, IsNot, Contains, Known];
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Text = [Is, IsNot, Contains, Known];
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Number =
    [
        Is, new("gt", "greater than"), new("lt", "less than"), new("between", "between"),
        new("gte", "at least"), new("lte", "at most"),
    ];

    // Sentence-style conditions for collection rules: each reads as "<field> <condition> <value>".
    internal static readonly RuleOperatorDefinition IsBefore = new("lt", "is before");
    internal static readonly RuleOperatorDefinition IsAfter = new("gt", "is after");
    internal static readonly RuleOperatorDefinition IsBetween = new("between", "is between");
    internal static readonly RuleOperatorDefinition AtLeast = new("gte", "at least");
    internal static readonly RuleOperatorDefinition AtMost = new("lte", "at most");
    internal static readonly RuleOperatorDefinition NotContains = new("not_contains", "does not contain");

    /// <summary>A count or amount: "is 3", "at least 7.5", "at most 120", "is between 90 and 120".</summary>
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Amount = [Is, AtLeast, AtMost, IsBetween, Known, Unknown];

    /// <summary>A calendar date: "is before 1990-01-01", "is after …", "is between … and …".</summary>
    internal static readonly IReadOnlyList<RuleOperatorDefinition> Date = [IsBefore, IsAfter, IsBetween];

    /// <summary>Release date also reads relative to today: "is within the last 6 months".</summary>
    internal static readonly IReadOnlyList<RuleOperatorDefinition> ReleaseDate =
    [
        new("in_last_days", "is within the last (days)"), new("in_last_months", "is within the last (months)"),
        new("in_last_years", "is within the last (years)"), IsBefore, IsAfter, IsBetween, Known, Unknown,
    ];

    /// <summary>True for conditions whose value is a whole number of days, months or years rather than text or a date.</summary>
    internal static bool TakesWholeNumber(string op) =>
        op is "in_last_days" or "in_last_months" or "in_last_years" or "within_days" or "not_within_days";

    /// <summary>True for conditions that need two values (a start and an end).</summary>
    internal static bool TakesRange(string op) => op == "between";

    internal static string DefaultIcon => AppMaterialIcons.Outlined.Tune;
}
