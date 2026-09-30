using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private TvdbScopedMatchCandidatesDto? _tvdbCandidates;
    private TvdbMatchCandidateDto? _selectedTvdbCandidate;
    private bool _tvdbSearchPending;
    private bool _tvdbApplyPending;
    private CancellationTokenSource? _tvdbSearchCancellation;
    private TvdbScopedMatchCandidatesDto? _tvdbPlacementSeasons;
    private bool _tvdbPlacementSeasonsPending;
    private bool _resumeEpisodeMatchAfterPlacement;
    private bool _scrollToEpisodePlacement;

    protected bool IsTvdbScopedMatching => EditorMediaType == "TV"
        && ActiveScope?.ScopeId is "season" or "episode";

    protected bool CanSelectTvdbCandidates => _tvdbCandidates is not null;

    protected IReadOnlyList<TvdbMatchCandidateDto> FilteredTvdbCandidates =>
        _tvdbCandidates?.Candidates ?? [];

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
            var result = await ApiClient.GetTvdbScopedMatchCandidatesAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId, seasonNumber: null, ct: cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _tvdbCandidates = result;
            if (result is null)
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB search failed.", MudBlazor.Severity.Error);
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

    private void ResetTvdbScopedMatchState()
    {
        _tvdbSearchCancellation?.Cancel();
        _tvdbSearchCancellation?.Dispose();
        _tvdbSearchCancellation = null;
        _tvdbCandidates = null;
        _selectedTvdbCandidate = null;
        _tvdbSearchPending = false;
        _tvdbPlacementSeasons = null;
        _tvdbPlacementSeasonsPending = false;
    }

    protected async Task OpenEpisodePlacementCorrectionAsync()
    {
        if (EditorMediaType != "TV" || ActiveScope?.ScopeId != "episode") return;
        _resumeEpisodeMatchAfterPlacement = true;
        _scrollToEpisodePlacement = true;
        await OpenDetailsTabAsync();
    }

    protected async Task LoadTvdbPlacementSeasonsAsync()
    {
        if (EditorMediaType != "TV" || ActiveScope?.ScopeId != "episode" || _tvdbPlacementSeasonsPending)
            return;

        var showKey = BuildScopedFieldKey("show_name");
        _selectedMembershipSuggestions.TryGetValue(showKey, out var selectedShow);
        if (selectedShow is not null
            && !string.Equals(selectedShow.ExternalIdKey, "tvdb_id", StringComparison.OrdinalIgnoreCase))
        {
            Snackbar.Add("Match the selected show to TheTVDB before browsing its seasons.", MudBlazor.Severity.Info);
            return;
        }
        var selectedShowId = selectedShow?.ExternalIdValue;
        _tvdbPlacementSeasonsPending = true;
        try
        {
            _tvdbPlacementSeasons = await ApiClient.GetTvdbScopedMatchCandidatesAsync(
                CanonicalEndpointEntityId, "season", seasonNumber: null, seriesId: selectedShowId);
            if (_tvdbPlacementSeasons is null)
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB seasons could not be loaded.", MudBlazor.Severity.Error);
        }
        finally
        {
            _tvdbPlacementSeasonsPending = false;
        }
    }

    protected async Task SelectTvdbPlacementSeasonAsync(TvdbMatchCandidateDto season)
    {
        await OnMembershipFieldInputAsync("season_number", season.SeasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _tvdbPlacementSeasons = null;
    }

    private async Task<bool> ResumeEpisodeMatchAfterPlacementAsync(bool appliedMembershipMove)
    {
        if (!_resumeEpisodeMatchAfterPlacement || !appliedMembershipMove) return false;
        _resumeEpisodeMatchAfterPlacement = false;
        await LoadSingleItemAsync(CurrentEntityId, resetEditorState: true, preferredScopeId: "episode");
        await SelectTabInternalAsync("links");
        await SearchTvdbScopedMatchesAsync();
        return true;
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
