using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Rules;

public enum RuleValueProviderKind
{
    Text,
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

    internal static string DefaultIcon => AppMaterialIcons.Outlined.Tune;
}
