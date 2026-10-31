using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

/// <summary>What a flow produced: claims for a fetch, candidate items for a search.</summary>
internal sealed record FlowResult(IReadOnlyList<ProviderClaim> Claims, IReadOnlyList<SearchResultItem> Items)
{
    public static readonly FlowResult Empty = new([], []);
}

/// <summary>
/// Runs a parsed provider flow (see <see cref="FlowParser"/>) for one request: calls the
/// provider's named API endpoints through <see cref="ProviderApiClient"/>, keeps the answers in
/// variables, and builds claims or search items. It knows no provider; what to call and how to read
/// the answers is all in the flow.
/// </summary>
internal sealed partial class ProviderFlowRunner(
    ProviderApiClient api,
    string providerName,
    ProviderLookupRequest request,
    Func<string, string?> requestField,
    int limit,
    ILogger logger)
{
    private enum Signal
    {
        None,
        Stop,
        Skip,
        Return,
    }

    private readonly ProviderApiClient _api = api;
    private readonly string _providerName = providerName;
    private readonly ProviderLookupRequest _request = request;
    private readonly Func<string, string?> _requestField = requestField;
    private readonly int _limit = limit;
    private readonly ILogger _logger = logger;
    private readonly Dictionary<string, JsonNode?> _variables = new(StringComparer.Ordinal);
    private readonly List<ProviderClaim> _claims = [];
    private readonly List<SearchResultItem> _items = [];

    public async Task<FlowResult> RunAsync(IReadOnlyList<FlowStep> steps, CancellationToken ct)
    {
        var signal = await RunStepsAsync(steps, ct).ConfigureAwait(false);
        return signal == Signal.Stop ? FlowResult.Empty : new FlowResult(_claims, _items);
    }

    private async Task<Signal> RunStepsAsync(IReadOnlyList<FlowStep> steps, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var signal = await RunStepAsync(step, ct).ConfigureAwait(false);
            if (signal != Signal.None)
            {
                return signal;
            }
        }

        return Signal.None;
    }

    private async Task<Signal> RunStepAsync(FlowStep step, CancellationToken ct)
    {
        switch (step)
        {
            case SetStep set:
                _variables[set.Variable] = Evaluate(set.Value);
                return Signal.None;
            case SliceStep slice:
                _variables[slice.Variable] = Slice(slice);
                return Signal.None;
            case CallStep call:
                var called = await CallAsync(call, ct).ConfigureAwait(false);
                if (called is not null || !call.KeepOnNull)
                {
                    _variables[call.Variable] = called;
                }

                return Signal.None;
            case PagesStep pages:
                var all = await _api.GetAllPagesAsync(_providerName, pages.Endpoint, Arguments(pages.Arguments), ct).ConfigureAwait(false);
                _variables[pages.Variable] = new JsonArray(all.Select(node => (JsonNode?)node.DeepClone()).ToArray());
                return Signal.None;
            case FindStep find:
                _variables[find.Variable] = Find(find);
                return Signal.None;
            case PickStep pick:
                _variables[pick.Variable] = await PickAsync(pick, ct).ConfigureAwait(false);
                return Signal.None;
            case StopStep stop:
                return Holds(stop.Conditions, stop.All) ? Signal.Stop : Signal.None;
            case SkipStep skip:
                return Holds(skip.Conditions, skip.All) ? Signal.Skip : Signal.None;
            case IfStep conditional:
                return await RunStepsAsync(Holds(conditional.Conditions, all: true) ? conditional.Then : conditional.Else, ct).ConfigureAwait(false);
            case EachStep each:
                return await EachAsync(each, ct).ConfigureAwait(false);
            case EmitStep emit:
                return Emit(emit);
            case ClaimsStep claims:
                BuildClaims(claims);
                return Signal.None;
            default:
                return Signal.Return;
        }
    }

    private async Task<Signal> EachAsync(EachStep each, CancellationToken ct)
    {
        var source = Evaluate(each.Source);
        if (source is null)
        {
            return Signal.None;
        }

        var elements = source as JsonArray
            ?? throw new InvalidOperationException($"{each.Location}: the value to loop over is not a list.");
        foreach (var element in elements.Where(element => element is not null).ToList())
        {
            _variables[each.Variable] = element;
            var signal = await RunStepsAsync(each.Body, ct).ConfigureAwait(false);
            if (signal is Signal.Stop or Signal.Return)
            {
                return signal;
            }
        }

        return Signal.None;
    }

    private async Task<JsonNode?> CallAsync(CallStep call, CancellationToken ct)
    {
        try
        {
            return await _api.GetAsync(_providerName, call.Endpoint, Arguments(call.Arguments), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (call.IgnoreErrors && ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Provider}: lookup '{Endpoint}' was unavailable and is skipped", _providerName, call.Endpoint);
            return null;
        }
    }

    private Dictionary<string, string> Arguments(IReadOnlyDictionary<string, FlowExpression> expressions)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expression) in expressions)
        {
            if (TextOf(Evaluate(expression)) is { } text && !string.IsNullOrWhiteSpace(text))
            {
                arguments[name] = text;
            }
        }

        return arguments;
    }

    private JsonNode? Slice(SliceStep slice)
    {
        var source = Evaluate(slice.Source);
        var take = (int)(double.TryParse(TextOf(Evaluate(slice.Take)), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) ? count : 0);
        if (source is null)
        {
            return new JsonArray();
        }

        var array = source as JsonArray
            ?? throw new InvalidOperationException($"{slice.Location}: the value to take from is not a list.");
        return new JsonArray(array.Where(node => node is not null).Take(take).Select(node => (JsonNode?)node!.DeepClone()).ToArray());
    }

    private JsonNode? Find(FindStep find)
    {
        if (Evaluate(find.Source) is not JsonArray array)
        {
            return null;
        }

        return array.FirstOrDefault(node => node is not null && find.Tests.All(test =>
        {
            var actual = TrimmedTextOf(JsonPathEvaluator.Evaluate(node, test.Path));
            var expected = TextOf(Evaluate(test.Expected));
            return test.AsNumber
                ? SameNumber(actual, expected)
                : string.Equals(actual, expected, test.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }))?.DeepClone();
    }

    /// <summary>The trimmed text of a field read from an answer, or nothing when it is blank.</summary>
    private static string? TrimmedTextOf(JsonNode? node) =>
        TextOf(node) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Shared words over all distinct words of two texts, ignoring case and punctuation; 0 when either has none.</summary>
    private static double WordOverlap(string? left, string? right)
    {
        static HashSet<string> Words(string? text) => System.Text.RegularExpressions.Regex
            .Matches((text ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        var a = Words(left);
        var b = Words(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        return a.Intersect(b).Count() / (double)a.Union(b).Count();
    }

    private async Task<JsonNode?> PickAsync(PickStep pick, CancellationToken ct)
    {
        var source = Evaluate(pick.Source);
        var candidates = source is null
            ? []
            : (source as JsonArray ?? throw new InvalidOperationException($"{pick.Location}: the value to pick from is not a list."))
                .Where(node => node is not null).Take(pick.Take).ToList();
        // Exact tests are tried against every candidate before any word-overlap test, so a close name never
        // outranks an exact one further down the list.
        foreach (var fuzzyPass in new[] { false, true })
        {
            foreach (var candidate in candidates)
            {
                _variables["candidate"] = candidate;
                _variables["candidate_id"] = Text(pick.CandidateIdPaths
                    .Select(path => TextOf(JsonPathEvaluator.Evaluate(candidate, path)))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit)));
                foreach (var test in pick.Tests.Where(test => (test.MinWordOverlap is not null) == fuzzyPass))
                {
                    JsonNode? subject = candidate;
                    if (test.Via is not null)
                    {
                        if (!HasValue(_variables["candidate_id"]))
                        {
                            break;
                        }

                        subject = await _api.GetAsync(_providerName, test.Via.Endpoint, Arguments(test.Via.Arguments), ct).ConfigureAwait(false);
                    }

                    var actual = TrimmedTextOf(JsonPathEvaluator.Evaluate(subject, test.Path));
                    var expected = TextOf(Evaluate(test.Expected));
                    if (test.MinWordOverlap is { } minimum
                        ? WordOverlap(actual, expected) >= minimum
                        : string.Equals(actual, expected, test.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        return candidate!.DeepClone();
                    }
                }
            }
        }

        if (!(pick.FallbackToOnlyCandidate && candidates.Count == 1))
        {
            _logger.LogInformation(
                "{Provider}: no search hit was close enough to the requested name; {Count} hit(s), top: {Names}",
                _providerName,
                candidates.Count,
                string.Join(" | ", candidates.Take(5).Select(node => TextOf(JsonPathEvaluator.Evaluate(node, "name")) ?? "?")));
        }

        return pick.FallbackToOnlyCandidate && candidates.Count == 1 ? candidates[0]!.DeepClone() : null;
    }

    private Signal Emit(EmitStep emit)
    {
        string? Trimmed(FlowExpression? expression) =>
            expression is null || TextOf(Evaluate(expression)) is not { } text || string.IsNullOrWhiteSpace(text) ? null : text;

        var extra = emit.Extra.ToDictionary(pair => pair.Key, pair => Trimmed(pair.Value) ?? string.Empty, StringComparer.Ordinal);
        _items.Add(new SearchResultItem(
            Trimmed(emit.Title) ?? string.Empty,
            Trimmed(emit.Author),
            Trimmed(emit.Description),
            Trimmed(emit.Year),
            Trimmed(emit.Thumbnail),
            Trimmed(emit.Id),
            emit.Confidence,
            _providerName,
            emit.ResultType,
            extra));
        return emit.StopAt is not null
            && double.TryParse(TextOf(Evaluate(emit.StopAt)), NumberStyles.Float, CultureInfo.InvariantCulture, out var stopAt)
            && _items.Count >= stopAt
                ? Signal.Return
                : Signal.None;
    }

    private void BuildClaims(ClaimsStep step)
    {
        var root = new JsonObject();
        foreach (var (name, expression) in step.Root)
        {
            root[name] = Evaluate(expression)?.DeepClone();
        }

        var context = new ClaimRuleContext(_providerName, _request.MediaType, new Dictionary<string, string>());
        foreach (var rule in step.Rules)
        {
            ClaimRuleEngine.Apply(_claims, root, rule, step.Add, context);
        }
    }
}
