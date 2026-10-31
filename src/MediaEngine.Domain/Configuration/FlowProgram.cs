namespace MediaEngine.Domain.Configuration;

// The parsed form of a provider flow: expressions, conditions and steps. The parser builds it
// from the JSON of a provider file, the validator checks it, and the Providers library runs it.

public abstract record FlowExpression;

/// <summary>Plain text.</summary>
public sealed record LiteralExpression(string Value) : FlowExpression;

/// <summary>A JSON number written in the file.</summary>
public sealed record NumberExpression(double Value) : FlowExpression;

/// <summary><c>$name</c> or <c>$name.path.to.field</c>.</summary>
public sealed record VariableExpression(string Name, string? Path) : FlowExpression;

/// <summary><c>@request.field</c>.</summary>
public sealed record RequestExpression(string Field) : FlowExpression;

/// <summary><c>@hint.key</c>.</summary>
public sealed record HintExpression(string Key) : FlowExpression;

/// <summary><c>@name</c> (the provider's name) or <c>@limit</c> (the number of results asked for).</summary>
public sealed record BuiltInExpression(string Name) : FlowExpression;

/// <summary>The first of several expressions that has a value.</summary>
public sealed record CoalesceExpression(IReadOnlyList<FlowExpression> Items) : FlowExpression;

/// <summary>The first of several expressions that is not missing; unlike <see cref="CoalesceExpression"/> it keeps a blank text.</summary>
public sealed record FirstNonNullExpression(IReadOnlyList<FlowExpression> Items) : FlowExpression;

/// <summary>The first <c>Count</c> characters of a text, when it has at least that many.</summary>
public sealed record FirstCharsExpression(FlowExpression Source, int Count) : FlowExpression;

/// <summary>Text with <c>{variable.path}</c> placeholders.</summary>
public sealed record FormatExpression(string Template) : FlowExpression;

/// <summary>The first HTTPS address on one of the hosts (or their sub-domains).</summary>
public sealed record UrlOnHostsExpression(IReadOnlyList<FlowExpression> Sources, IReadOnlyList<string> Hosts) : FlowExpression;

/// <summary>The first value that is made of digits only.</summary>
public sealed record DigitsOfExpression(IReadOnlyList<FlowExpression> Sources) : FlowExpression;

/// <summary>
/// Shorter search texts made from a long name: the name is split where it holds " - ", " – " or ": ", and the list
/// holds each pair of neighbouring parts joined by a space, then each single part, longest first. The whole name is
/// not repeated. Empty when the name has only one part.
/// </summary>
public sealed record PartsOfExpression(FlowExpression Source) : FlowExpression;

public sealed record ClampExpression(FlowExpression Source, double Min, double Max) : FlowExpression;

/// <summary>A whole number read from text; no value when it is not one.</summary>
public sealed record IntExpression(FlowExpression Source) : FlowExpression;

/// <summary>A JSON object with the given fields.</summary>
public sealed record ObjectExpression(IReadOnlyDictionary<string, FlowExpression> Fields) : FlowExpression;

public abstract record FlowCondition;

public sealed record BlankCondition(FlowExpression Value) : FlowCondition;

public sealed record PresentCondition(FlowExpression Value) : FlowCondition;

/// <summary>The value is not missing; a blank text still counts.</summary>
public sealed record NotNullCondition(FlowExpression Value) : FlowCondition;

public sealed record EqualsCondition(FlowExpression Left, FlowExpression Right, bool IgnoreCase) : FlowCondition;

/// <summary>Both sides read as whole numbers and are equal.</summary>
public sealed record NumberEqualsCondition(FlowExpression Left, FlowExpression Right) : FlowCondition;

public sealed record AnyCondition(IReadOnlyList<FlowCondition> Conditions) : FlowCondition;

public sealed record AllCondition(IReadOnlyList<FlowCondition> Conditions) : FlowCondition;

public sealed record NotCondition(FlowCondition Condition) : FlowCondition;

/// <summary>One check on an element of a list: its field equals an expression.</summary>
public sealed record FlowElementTest(string Path, FlowExpression Expected, bool IgnoreCase, bool AsNumber);

public abstract record FlowStep(string Location);

public sealed record SetStep(string Location, string Variable, FlowExpression Value) : FlowStep(Location);

public sealed record SliceStep(string Location, string Variable, FlowExpression Source, FlowExpression Take) : FlowStep(Location);

public sealed record CallStep(
    string Location,
    string Endpoint,
    IReadOnlyDictionary<string, FlowExpression> Arguments,
    string Variable,
    bool KeepOnNull,
    bool IgnoreErrors) : FlowStep(Location);

public sealed record PagesStep(
    string Location,
    string Endpoint,
    IReadOnlyDictionary<string, FlowExpression> Arguments,
    string Variable) : FlowStep(Location);

public sealed record FindStep(string Location, string Variable, FlowExpression Source, IReadOnlyList<FlowElementTest> Tests) : FlowStep(Location);

/// <summary>
/// A test of <see cref="PickStep"/>: a field of the candidate, or of a lookup made for it, equals an expression.
/// With <paramref name="MinWordOverlap"/> set, the two texts only have to share that fraction of their words
/// (shared words over all distinct words, ignoring case and punctuation); such tests run after every exact test.
/// </summary>
public sealed record PickTest(
    string Location,
    string Path,
    FlowExpression Expected,
    bool IgnoreCase,
    CallStep? Via,
    double? MinWordOverlap = null);

public sealed record PickStep(
    string Location,
    string Variable,
    FlowExpression Source,
    int Take,
    IReadOnlyList<string> CandidateIdPaths,
    IReadOnlyList<PickTest> Tests,
    bool FallbackToOnlyCandidate) : FlowStep(Location);

/// <summary>Ends the flow with an empty answer when the conditions hold.</summary>
public sealed record StopStep(string Location, IReadOnlyList<FlowCondition> Conditions, bool All) : FlowStep(Location);

/// <summary>Moves to the next element of the enclosing <c>each</c> when the conditions hold.</summary>
public sealed record SkipStep(string Location, IReadOnlyList<FlowCondition> Conditions, bool All) : FlowStep(Location);

public sealed record IfStep(
    string Location,
    IReadOnlyList<FlowCondition> Conditions,
    IReadOnlyList<FlowStep> Then,
    IReadOnlyList<FlowStep> Else) : FlowStep(Location);

public sealed record EachStep(string Location, string Variable, FlowExpression Source, IReadOnlyList<FlowStep> Body) : FlowStep(Location);

public sealed record EmitStep(
    string Location,
    FlowExpression Title,
    FlowExpression? Author,
    FlowExpression? Description,
    FlowExpression? Year,
    FlowExpression? Thumbnail,
    FlowExpression? Id,
    double Confidence,
    string? ResultType,
    IReadOnlyDictionary<string, FlowExpression> Extra,
    FlowExpression? StopAt) : FlowStep(Location);

public sealed record ClaimsStep(
    string Location,
    IReadOnlyDictionary<string, FlowExpression> Root,
    IReadOnlyList<ClaimRuleConfiguration> Rules,
    string Add) : FlowStep(Location);

/// <summary>Ends the flow now and keeps what it produced so far.</summary>
public sealed record ReturnStep(string Location) : FlowStep(Location);

/// <summary>A parsed flow with what the validator needs to know about it.</summary>
public sealed class FlowProgram
{
    public List<FlowStep> Steps { get; } = [];

    /// <summary>Every variable some step assigns, including loop variables.</summary>
    public HashSet<string> Assigned { get; } = new(StringComparer.Ordinal);

    /// <summary>Variables read by expressions, with where they were read.</summary>
    public List<(string Variable, string Location)> Reads { get; } = [];

    /// <summary>Endpoint calls with the argument names given.</summary>
    public List<(string Endpoint, IReadOnlyCollection<string> Arguments, string Location)> Calls { get; } = [];
}
