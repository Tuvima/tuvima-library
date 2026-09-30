using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private TvdbScopedMatchCandidatesDto? _tvdbCandidates;
    private TvdbMatchCandidateDto? _selectedTvdbCandidate;
    private string _tvdbSeasonSelection = string.Empty;
    private string _tvdbSeasonTypeSelection = string.Empty;
    private string _tvdbFilter = string.Empty;
    private bool _tvdbSearchPending;
    private bool _tvdbApplyPending;
    private CancellationTokenSource? _tvdbSearchCancellation;
    private TvdbShowOrderPreviewDto? _tvdbShowOrderPreview;
    private bool _tvdbShowOrderPreviewPending;
    private bool _tvdbShowOrderApplyPending;

    protected bool IsTvdbScopedMatching => EditorMediaType == "TV"
        && ActiveScope?.ScopeId is "season" or "episode";

    protected bool CanSelectTvdbCandidates =>
        _tvdbCandidates is not null
        && string.Equals(_tvdbCandidates.SeasonType, _tvdbCandidates.ShowSeasonType, StringComparison.Ordinal)
        && (ActiveScope?.ScopeId != "episode"
        || (_tvdbCandidates.HasConfirmedSeasonMatch
            && _tvdbCandidates.ConfirmedSeasonNumber?.ToString() == _tvdbSeasonSelection));

    protected bool TvdbOrderNeedsShowConfirmation => _tvdbCandidates is { } candidates
        && !string.Equals(candidates.SeasonType, candidates.ShowSeasonType, StringComparison.Ordinal);

    protected string TvdbEpisodeSeasonRequirement =>
        TvdbOrderNeedsShowConfirmation
            ? "Apply this episode order to the show before matching an episode. Review show order below."
            : _tvdbCandidates?.ConfirmedSeasonNumber is { } season
            && _tvdbSeasonSelection != season.ToString()
            ? $"This episode's confirmed match is in provider Season {season}. Select that season here, or rematch the parent Season scope."
            : _tvdbCandidates?.ConfirmedSeasonNumber is { } confirmedSeason
            ? $"Season {confirmedSeason} is confirmed in this order. You can select an episode."
            : "Match the parent season in this episode order before selecting an episode. Use the Season scope above, then return here.";

    protected IReadOnlyList<AppSelectOption> TvdbSeasonOptions =>
        _tvdbCandidates?.AvailableSeasons.Select(number =>
            new AppSelectOption(number.ToString(), number == 0 ? "Specials" : $"Season {number}"))
            .ToList() ?? [];

    protected IReadOnlyList<AppSelectOption> TvdbSeasonTypeOptions =>
        (_tvdbCandidates?.AvailableSeasonTypes ?? ["default"])
            .Select(type => new AppSelectOption(type, TvdbSeasonTypeLabel(type)))
            .ToList();

    protected bool CanReviewTvdbShowOrder =>
        _tvdbCandidates is not null
        && !string.IsNullOrWhiteSpace(_tvdbSeasonTypeSelection)
        && !_tvdbShowOrderPreviewPending;

    protected bool CanApplyTvdbShowOrder =>
        _tvdbShowOrderPreview is { CanApply: true }
        && !string.Equals(_tvdbShowOrderPreview.CurrentSeasonType, _tvdbShowOrderPreview.RequestedSeasonType, StringComparison.Ordinal)
        && !_tvdbShowOrderApplyPending
        && !IsDirty;

    protected IReadOnlyList<TvdbMatchCandidateDto> FilteredTvdbCandidates =>
        _tvdbCandidates?.Candidates.Where(candidate =>
            (ActiveScope?.ScopeId != "season"
             || candidate.SeasonNumber.ToString() == _tvdbSeasonSelection)
            && (string.IsNullOrWhiteSpace(_tvdbFilter)
                || candidate.Title.Contains(_tvdbFilter, StringComparison.OrdinalIgnoreCase)
                || candidate.EpisodeNumber?.ToString() == _tvdbFilter.Trim()))
            .ToList() ?? [];

    protected static string TvdbCandidateNumberLabel(TvdbMatchCandidateDto candidate) =>
        candidate.EpisodeNumber is { } episode
            ? $"S{candidate.SeasonNumber} E{episode}"
            : candidate.SeasonNumber == 0 ? "Specials" : $"Season {candidate.SeasonNumber}";

    protected static string TvdbSeasonTypeLabel(string? seasonType) =>
        (seasonType ?? "default").Trim().ToLowerInvariant() switch
        {
            "official" => "Official order",
            "dvd" => "DVD order",
            "absolute" => "Absolute order",
            _ => "Default order",
        };

    protected static string TvdbCandidateDisplayTitle(TvdbMatchCandidateDto candidate)
    {
        var number = TvdbCandidateNumberLabel(candidate);
        return string.Equals(number, candidate.Title, StringComparison.OrdinalIgnoreCase)
            ? number : $"{number} · {candidate.Title}";
    }

    protected static string TvdbOwnedTargetLabel(TvdbScopedMatchCandidatesDto results) =>
        results.OwnedSeasonNumber is { } season
            ? results.OwnedEpisodeNumber is { } episode ? $"S{season} E{episode}" : $"Season {season}"
            : results.ScopeId == "season" ? "Current season" : "Current episode";

    protected string TvdbSeasonSelectionStatus
    {
        get
        {
            if (_tvdbCandidates?.OwnedSeasonNumber is not { } ownedSeason
                || !int.TryParse(_tvdbSeasonSelection, out var selectedSeason))
            {
                return $"Choose the provider season in the {TvdbSeasonTypeLabel(_tvdbCandidates?.SeasonType).ToLowerInvariant()} that contains this owned item.";
            }

            return selectedSeason == ownedSeason
                ? $"Provider Season {selectedSeason} matches the owned season number in this order."
                : $"Provider Season {selectedSeason} will be aligned to owned Season {ownedSeason} in this order.";
        }
    }

    protected static string TvdbScopedApplyDescription(
        TvdbScopedMatchCandidatesDto results,
        TvdbMatchCandidateDto candidate) =>
        $"TheTVDB {TvdbCandidateNumberLabel(candidate)} will identify your owned {TvdbOwnedTargetLabel(results)}.";

    protected static string TvdbShowOrderImpactLabel(TvdbShowOrderImpactDto impact) =>
        impact.ScopeId switch
        {
            "season" => impact.SeasonNumber is { } season ? $"Season {season}" : "Season",
            "episode" when impact.SeasonNumber is { } season && impact.EpisodeNumber is { } episode => $"S{season} E{episode}",
            "episode" => "Episode",
            _ => impact.ScopeId,
        };

    protected static string TvdbShowOrderImpactMessage(TvdbShowOrderImpactDto impact) =>
        !string.IsNullOrWhiteSpace(impact.Message)
            ? impact.Message
            : impact.IsMapped
                ? "Its existing TheTVDB match stays valid."
                : "This existing match must be rematched before the order can change.";

    protected async Task SearchTvdbScopedMatchesAsync()
    {
        if (!IsTvdbScopedMatching || ActiveScope is null) return;
        _tvdbSearchCancellation?.Cancel();
        _tvdbSearchCancellation?.Dispose();
        var cancel = new CancellationTokenSource();
        _tvdbSearchCancellation = cancel;
        _tvdbSearchPending = true;
        _selectedTvdbCandidate = null;
        try
        {
            var requestedSeason = int.TryParse(_tvdbSeasonSelection, out var number)
                ? number : (int?)null;
            var requestedSeasonType = string.IsNullOrWhiteSpace(_tvdbSeasonTypeSelection)
                ? null
                : _tvdbSeasonTypeSelection;
            var result = await ApiClient.GetTvdbScopedMatchCandidatesAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId, requestedSeason, requestedSeasonType, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _tvdbCandidates = result;
            if (result is null)
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB search failed.", MudBlazor.Severity.Error);
            else
            {
                _tvdbSeasonTypeSelection = result.SeasonType;
                if (string.IsNullOrWhiteSpace(_tvdbSeasonSelection)
                     && ActiveScope.ScopeId == "episode"
                     && result.ConfirmedSeasonNumber is { } confirmedSeason)
                    _tvdbSeasonSelection = confirmedSeason.ToString();
                else if (string.IsNullOrWhiteSpace(_tvdbSeasonSelection)
                     && result.OwnedSeasonNumber is { } ownedSeason
                     && result.AvailableSeasons.Contains(ownedSeason))
                    _tvdbSeasonSelection = result.OwnedSeasonNumber.Value.ToString();
                else if (string.IsNullOrWhiteSpace(_tvdbSeasonSelection) && result.AvailableSeasons.Count > 0)
                    _tvdbSeasonSelection = result.AvailableSeasons[0].ToString();
            }
        }
        finally
        {
            if (ReferenceEquals(_tvdbSearchCancellation, cancel))
            {
                _tvdbSearchPending = false;
                _tvdbSearchCancellation = null;
                cancel.Dispose();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task ChangeTvdbSeasonAsync(string value)
    {
        _tvdbSeasonSelection = value;
        await SearchTvdbScopedMatchesAsync();
    }

    protected async Task ChangeTvdbSeasonTypeAsync(string value)
    {
        _tvdbSeasonTypeSelection = value;
        _tvdbSeasonSelection = string.Empty;
        _tvdbShowOrderPreview = null;
        await SearchTvdbScopedMatchesAsync();
    }

    private void ResetTvdbScopedMatchState()
    {
        _tvdbSearchCancellation?.Cancel();
        _tvdbSearchCancellation?.Dispose();
        _tvdbSearchCancellation = null;
        _tvdbCandidates = null;
        _selectedTvdbCandidate = null;
        _tvdbSeasonSelection = string.Empty;
        _tvdbSeasonTypeSelection = string.Empty;
        _tvdbFilter = string.Empty;
        _tvdbSearchPending = false;
        _tvdbShowOrderPreview = null;
        _tvdbShowOrderPreviewPending = false;
        _tvdbShowOrderApplyPending = false;
    }

    protected async Task PreviewTvdbShowOrderAsync()
    {
        if (!CanReviewTvdbShowOrder) return;
        _tvdbShowOrderPreviewPending = true;
        _tvdbShowOrderPreview = null;
        try
        {
            var preview = await ApiClient.GetTvdbShowOrderPreviewAsync(
                CanonicalEndpointEntityId, _tvdbSeasonTypeSelection);
            _tvdbShowOrderPreview = preview;
            if (preview is null)
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB show order preview could not be loaded.", MudBlazor.Severity.Error);
        }
        finally
        {
            _tvdbShowOrderPreviewPending = false;
        }
    }

    protected async Task ApplyTvdbShowOrderAsync()
    {
        var preview = _tvdbShowOrderPreview;
        if (!CanApplyTvdbShowOrder || preview is null) return;
        _tvdbShowOrderApplyPending = true;
        try
        {
            var result = await ApiClient.ApplyTvdbShowOrderAsync(
                CanonicalEndpointEntityId,
                new ApplyTvdbShowOrderDto(preview.RequestedSeasonType, preview.CurrentRevision));
            if (result is null)
            {
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB show order could not be updated.", MudBlazor.Severity.Error);
                return;
            }

            Snackbar.Add(result.Message, MudBlazor.Severity.Success);
            _tvdbShowOrderPreview = null;
            _tvdbCandidates = null;
            _selectedTvdbCandidate = null;
            await LoadSingleItemAsync(CurrentEntityId, resetEditorState: true,
                preferredScopeId: ActiveScope?.ScopeId);
        }
        finally
        {
            _tvdbShowOrderApplyPending = false;
        }
    }

    protected void SelectTvdbCandidate(TvdbMatchCandidateDto candidate) =>
        _selectedTvdbCandidate = CanSelectTvdbCandidates ? candidate : null;

    protected async Task ApplyTvdbScopedMatchAsync()
    {
        if (ActiveScope is null || _tvdbCandidates is null || _selectedTvdbCandidate is null
            || !CanSelectTvdbCandidates || _tvdbApplyPending || IsDirty) return;
        _tvdbApplyPending = true;
        try
        {
            var result = await ApiClient.ApplyTvdbScopedMatchAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId,
                new ApplyTvdbScopedMatchDto(_selectedTvdbCandidate.Id,
                    _tvdbCandidates.SeriesId, _tvdbCandidates.CurrentRevision, _tvdbCandidates.SeasonType));
            if (result is null)
            {
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB match could not be applied.", MudBlazor.Severity.Error);
                return;
            }
            Snackbar.Add(result.Message, MudBlazor.Severity.Success);
            _tvdbCandidates = null;
            _selectedTvdbCandidate = null;
            await LoadSingleItemAsync(CurrentEntityId, resetEditorState: true,
                preferredScopeId: ActiveScope.ScopeId);
        }
        finally
        {
            _tvdbApplyPending = false;
        }
    }
}
