using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private async Task SetMembershipModeAsync(bool rulesEnabled)
    {
        if (_rulesEnabled == rulesEnabled)
            return;

        _rulesEnabled = rulesEnabled;
        _previewResult = null;
        MarkDirty();

        if (rulesEnabled)
        {
            if (_ruleDefinition.AllConditions.Any(IsCompleteRule))
                await RunPreview();
            return;
        }

        await LoadManualItemsAsync();
    }

    private Task OnRuleDefinitionChanged(CollectionRuleDefinitionViewModel definition)
    {
        _ruleDefinition = definition;
        return Task.CompletedTask;
    }

    private Task OnRuleBuilderPreviewChanged(CollectionPreviewResult? preview)
    {
        _previewResult = preview;
        return Task.CompletedTask;
    }

    private Task OnRuleBuilderChanged()
    {
        MarkDirty();
        return Task.CompletedTask;
    }

    private async Task RunPreview()
    {
        if (!_rulesEnabled)
        {
            _previewResult = null;
            return;
        }

        var validRules = _ruleDefinition.AllConditions.Where(IsCompleteRule).ToList();

        if (validRules.Count == 0)
        {
            _previewResult = null;
            StateHasChanged();
            return;
        }

        _previewResult = await ApiClient.PreviewCollectionRulesAsync(
            CollectionRuleSentence.ToWire(_ruleDefinition),
            _sortField,
            _sortDirection,
            limit: 60,
            secondarySortField: _secondarySortField,
            secondarySortDirection: _secondarySortDirection);
        StateHasChanged();
    }

    private static string FormatPreviewMeta(CollectionResolvedItemViewModel item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Creator))
            parts.Add(item.Creator);
        if (!string.IsNullOrWhiteSpace(item.Year))
            parts.Add(item.Year);
        return parts.Count == 0 ? "Matching item" : string.Join("  -  ", parts);
    }

    private static string FormatMatchSummary(CollectionPreviewResult preview)
    {
        if (preview.Count == 0)
            return "No items match these rules.";

        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mediaType, count) in preview.MediaTypeCounts)
        {
            var key = NormalizeMediaType(mediaType);
            normalized[key] = normalized.GetValueOrDefault(key) + count;
        }

        var ordered = new[] { "movie", "TV show", "book", "comic", "music item", "audiobook", "item" };
        var parts = ordered
            .Where(normalized.ContainsKey)
            .Select(key => FormatMediaCount(key, normalized[key]))
            .ToList();

        return parts.Count == 0
            ? $"{preview.Count} {(preview.Count == 1 ? "item" : "items")} matched"
            : $"{string.Join(" · ", parts)} matched";
    }

    private static string NormalizeMediaType(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" or "films" => "movie",
        "tv" or "television" or "tv show" or "tv shows" or "series" => "TV show",
        "book" or "books" => "book",
        "comic" or "comics" => "comic",
        "music" or "album" or "albums" => "music item",
        "audiobook" or "audiobooks" => "audiobook",
        _ => "item",
    };

    private static string FormatMediaCount(string label, int count) =>
        $"{count} {label}{(count == 1 ? string.Empty : "s")}";

    private static bool IsValueFreeOperator(string op) =>
        op is "known" or "unknown" or "has_any_value" or "has_no_known_value";

    private static bool IsCompleteRule(CollectionRulePredicateViewModel rule) =>
        !string.IsNullOrWhiteSpace(rule.Field)
        && (IsValueFreeOperator(rule.Op) || !string.IsNullOrWhiteSpace(rule.Value) || rule.Values?.Length > 0);

    private CollectionRuleDefinitionViewModel BuildPersistableDefinition() => CollectionRuleSentence.ToWire(new()
    {
        Version = _ruleDefinition.Version,
        Groups = _ruleDefinition.Groups
            .Select(group => new CollectionRuleGroupViewModel
            {
                Id = group.Id,
                JoinWithPrevious = group.JoinWithPrevious,
                MatchMode = group.MatchMode,
                Conditions = group.Conditions.Where(IsCompleteRule).ToList(),
            })
            .Where(group => group.Conditions.Count > 0)
            .ToList(),
    });

    private static readonly Dictionary<string, string> MediaTypeColors = new(StringComparer.Ordinal)
    {
        ["movie"] = "var(--tl-media-movies)", ["TV show"] = "var(--tl-media-tv)", ["book"] = "var(--tl-media-books)",
        ["audiobook"] = "var(--tl-media-audiobooks)", ["music item"] = "var(--tl-media-music)", ["comic"] = "var(--tl-media-comics)",
    };

    private static IEnumerable<(string Label, int Count, string Color)> PreviewTypeCounts(CollectionPreviewResult preview)
    {
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mediaType, count) in preview.MediaTypeCounts)
        {
            var key = NormalizeMediaType(mediaType);
            normalized[key] = normalized.GetValueOrDefault(key) + count;
        }

        return normalized.Select(pair => (pair.Key, pair.Value, MediaTypeColors.GetValueOrDefault(pair.Key, "var(--tl-media-unknown)")));
    }
}
