using MediaEngine.Web.Components.Import;
using MediaEngine.Web.Components.Rules;

namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    // ── The New collection flow (Collections and lists spec 4b): how it fills up, build it, name and show it ───────────

    private enum FillWay { PickTitles, SetRules, FromList, Pack }

    // The card words live in the strings file; each card names its two keys.
    private record WayCard(FillWay Way, string TitleKey, string TextKey, string Icon, bool Available);

    private static readonly WayCard[] WayCards =
    [
        new(FillWay.PickTitles, "CollectionFlow_WayPickTitles", "CollectionFlow_WayPickTitlesText", AppMaterialIcons.Outlined.PlaylistAdd, true),
        new(FillWay.SetRules, "CollectionFlow_WaySetRules", "CollectionFlow_WaySetRulesText", AppMaterialIcons.Outlined.Tune, true),
        new(FillWay.FromList, "CollectionFlow_WayFromList", "CollectionFlow_WayFromListText", AppMaterialIcons.Outlined.UploadFile, true),
        new(FillWay.Pack, "CollectionFlow_WayPack", "CollectionFlow_WayPackText", AppMaterialIcons.Outlined.AutoAwesome, true),
    ];

    private FillWay _way = FillWay.SetRules;
    private readonly ListImportDraft _listDraft = new();
    private bool _isListCollection;
    private int _step = 1;
    private bool _advanced;
    private bool _changingWay;
    private bool _showAllPreview;
    private string? _strictestRule;
    private string? _suggestedName;
    private CollectionRuleBuilder? _ruleBuilder;

    private bool IsCreateFlow => EditingCollection is null;
    private IReadOnlyList<CollectionMediaChips.MediaChipType> MediaChoices => IsPlaylistDialog ? CollectionMediaChips.PlaylistTypes : CollectionMediaChips.CollectionTypes;
    private bool HasUsableRules => _ruleDefinition.AllConditions.Any(IsCompleteRule);

    private string StepLabel(int step) => step switch
    {
        1 => L["CollectionFlow_StepHowItFills"].Value,
        2 => _way switch
        {
            FillWay.PickTitles => L["CollectionFlow_WayPickTitles"].Value,
            FillWay.SetRules => L["CollectionFlow_WaySetRules"].Value,
            FillWay.FromList => L["CollectionFlow_WayFromList"].Value,
            _ => L["CollectionFlow_StepPickPack"].Value,
        },
        _ => L["CollectionFlow_StepNameAndShow"].Value,
    };

    // A web list whose site did not answer has nothing to match yet; it is saved and Library tries again (spec 4h), so it can go on.
    private bool ListReady => _listDraft.HasList && (_listDraft.OwnedCount > 0 || _listDraft.Current?.WillTryAgain == true);

    private bool NextBlocked => _step == 2 && (_way switch
    {
        FillWay.SetRules => !HasUsableRules,
        FillWay.FromList => !ListReady,
        FillWay.Pack => _selectedPacks.Count == 0 || _packsLoading,
        _ => IsEmptyCuratedCollection,
    });

    // The footer carries no summary line (mock), so what Next is waiting for is said in the step itself.
    private string BuildStepHint => _way switch
    {
        FillWay.Pack when _selectedPacks.Count == 0 => L["CollectionFlow_TickPackToContinue"].Value,
        FillWay.Pack => PackSelectionSummary,
        _ when IsEmptyCuratedCollection => EmptyCuratedCollectionMessage,
        _ => L["CollectionFlow_ItemsStaged", _manualItems.Count].Value,
    };

    private async Task ChooseWayAsync(FillWay way)
    {
        var card = WayCards.First(candidate => candidate.Way == way);
        if (!card.Available)
            return;

        _way = way;
        var rules = way == FillWay.SetRules;
        if (_rulesEnabled != rules)
        {
            _rulesEnabled = rules;
            _previewResult = null;
        }

        await Task.CompletedTask;
    }

    private async Task NextAsync()
    {
        ClearNotice();
        if (_step == 1)
        {
            _step = 2;
            if (_way == FillWay.PickTitles)
            {
                _showItemPicker = true;
                await EnsureLibraryItemsLoadedAsync();
            }
            else if (_way == FillWay.Pack)
            {
                await LoadPacksAsync();
            }

            return;
        }

        if (_step == 2 && !NextBlocked)
        {
            SuggestName();
            _step = 3;
        }
    }

    private void Back()
    {
        ClearNotice();
        if (_step > 1)
            _step--;
    }

    private Task OnMediaTypesChosenAsync(IReadOnlyList<string> types)
    {
        CollectionRuleSentence.SetMediaTypes(_ruleDefinition, types);
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnAdvancedChangedAsync(bool advanced)
    {
        _advanced = advanced;
        return Task.CompletedTask;
    }

    private Task OnOrderRandomChangedAsync(bool random)
    {
        _randomOrder = random;
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnMaxItemsChosenAsync(int value)
    {
        _maxItems = Math.Max(0, value);
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task OnStrictestRuleChangedAsync(string? rule)
    {
        _strictestRule = rule;
        return InvokeAsync(StateHasChanged);
    }

    // The name offered on the last step: "Movies directed by Denis Villeneuve". Anything the person typed is kept.
    private void SuggestName()
    {
        var suggestion = _way == FillWay.SetRules
            ? NameFromRules()
            : _way == FillWay.FromList && _listDraft.Current is { } listPreview
                ? (_listDraft.Shelves.Count > 1 ? listPreview.SourceLabel.Replace(" export", string.Empty, StringComparison.Ordinal) : listPreview.SuggestedName)
                : null;
        if (suggestion is null)
            return;
        if (string.IsNullOrWhiteSpace(_name) || _name == _suggestedName)
            _name = suggestion;
        _suggestedName = suggestion;
    }

    private string? NameFromRules()
    {
        var types = CollectionRuleSentence.MediaTypes(_ruleDefinition);
        var noun = types.Count == 0
            ? "Titles"
            : string.Join(" and ", types.Select(type => type switch { "TV" => "TV shows", _ => type }));
        var rule = CollectionRuleSentence.IncludeGroup(_ruleDefinition).Conditions
            .FirstOrDefault(condition => condition.Op is "eq" or "in" && IsCompleteRule(condition));
        if (rule is null)
            return types.Count == 0 ? null : noun;

        var who = string.Join(" or ", rule.DisplayValues is { Length: > 0 } labels ? labels : rule.Values is { Length: > 0 } values ? values : [rule.DisplayValue ?? rule.Value ?? string.Empty]);
        return rule.Field switch
        {
            "director" => $"{noun} directed by {who}",
            "author" or "artist" => $"{noun} by {who}",
            "cast_member" => $"{noun} with {who}",
            "narrator" => $"{noun} narrated by {who}",
            "composer" => $"{noun} composed by {who}",
            "genre" => $"{who} {noun.ToLowerInvariant()}",
            _ => $"{noun}: {CollectionRuleSentence.Describe(rule, CollectionRuleCatalog.Instance)}",
        };
    }

    // Changing the way in from the Contents tab warns first when it would throw away work: the rules, or the picked titles.
    private async Task ChangeWayAsync(FillWay way)
    {
        var card = WayCards.First(candidate => candidate.Way == way);
        if (!card.Available || way == FillWay.Pack || (way == FillWay.SetRules) == _rulesEnabled)
        {
            _changingWay = false;
            return;
        }

        if (way == FillWay.SetRules)
        {
            if (_manualItems.Count > 0 && await AppConfirmDialog.ShowAsync(
                    Dialogs,
                    "Switch to rules?",
                    "The titles you picked are not used once the collection fills up from rules. They are removed when you save.",
                    primaryLabel: "Switch to rules",
                    secondaryLabel: "Keep my titles",
                    primaryDestructive: true,
                    tone: AppUiTone.Warning) != AppConfirmResult.Primary)
            {
                return;
            }
        }
        else if (HasUsableRules && await AppConfirmDialog.ShowAsync(
                     Dialogs,
                     "Switch to picking titles?",
                     "The rules are removed when you save, and you choose every title yourself.",
                     primaryLabel: "Switch to picking",
                     secondaryLabel: "Keep my rules",
                     primaryDestructive: true,
                     tone: AppUiTone.Warning) != AppConfirmResult.Primary)
        {
            return;
        }

        _way = way;
        _changingWay = false;
        await SetMembershipModeAsync(way == FillWay.SetRules);
    }
}
