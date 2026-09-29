using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private TvdbScopedMatchCandidatesDto? _tvdbCandidates;
    private TvdbMatchCandidateDto? _selectedTvdbCandidate;
    private string _tvdbSeasonSelection = string.Empty;
    private string _tvdbFilter = string.Empty;
    private bool _tvdbSearchPending;
    private bool _tvdbApplyPending;
    private CancellationTokenSource? _tvdbSearchCancellation;

    protected bool IsTvdbScopedMatching => EditorMediaType == "TV"
        && ActiveScope?.ScopeId is "season" or "episode";

    protected IReadOnlyList<AppSelectOption> TvdbSeasonOptions =>
        _tvdbCandidates?.AvailableSeasons.Select(number =>
            new AppSelectOption(number.ToString(), number == 0 ? "Specials" : $"Season {number}"))
            .ToList() ?? [];

    protected IReadOnlyList<TvdbMatchCandidateDto> FilteredTvdbCandidates =>
        _tvdbCandidates?.Candidates.Where(candidate =>
            string.IsNullOrWhiteSpace(_tvdbFilter)
            || candidate.Title.Contains(_tvdbFilter, StringComparison.OrdinalIgnoreCase)
            || candidate.EpisodeNumber?.ToString() == _tvdbFilter.Trim())
            .ToList() ?? [];

    protected static string TvdbCandidateNumberLabel(TvdbMatchCandidateDto candidate) =>
        candidate.EpisodeNumber is { } episode
            ? $"S{candidate.SeasonNumber} E{episode}"
            : candidate.SeasonNumber == 0 ? "Specials" : $"Season {candidate.SeasonNumber}";

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
            var result = await ApiClient.GetTvdbScopedMatchCandidatesAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId, requestedSeason, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _tvdbCandidates = result;
            if (result is null)
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB search failed.", MudBlazor.Severity.Error);
            else if (string.IsNullOrWhiteSpace(_tvdbSeasonSelection)
                     && result.OwnedSeasonNumber is { } ownedSeason
                     && result.AvailableSeasons.Contains(ownedSeason))
                _tvdbSeasonSelection = result.OwnedSeasonNumber.Value.ToString();
            else if (string.IsNullOrWhiteSpace(_tvdbSeasonSelection) && result.AvailableSeasons.Count > 0)
                _tvdbSeasonSelection = result.AvailableSeasons[0].ToString();
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

    protected void SelectTvdbCandidate(TvdbMatchCandidateDto candidate) =>
        _selectedTvdbCandidate = candidate;

    protected async Task ApplyTvdbScopedMatchAsync()
    {
        if (ActiveScope is null || _tvdbCandidates is null || _selectedTvdbCandidate is null
            || _tvdbApplyPending || IsDirty) return;
        _tvdbApplyPending = true;
        try
        {
            var result = await ApiClient.ApplyTvdbScopedMatchAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId,
                new ApplyTvdbScopedMatchDto(_selectedTvdbCandidate.Id,
                    _tvdbCandidates.SeriesId, _tvdbCandidates.CurrentRevision));
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
