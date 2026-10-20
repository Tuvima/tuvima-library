using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private TvdbScopedMatchCandidatesDto? _tvdbCandidates;
    private TvdbMatchCandidateDto? _selectedTvdbCandidate;
    private bool _tvdbSearchPending;
    private string? _tvdbSearchError;
    private bool _tvdbApplyPending;
    private CancellationTokenSource? _tvdbSearchCancellation;
    private bool _tvdbCrossShowSearch;

    protected bool IsTvdbScopedMatching => EditorMediaType == "TV"
        && (ActiveScope?.ScopeId is "season" or "episode")
        && !_tvdbCrossShowSearch;

    protected void BeginTvdbCrossShowSearch()
    {
        if (EditorMediaType != "TV" || ActiveScope?.ScopeId != "episode")
        {
            return;
        }
        _tvdbCrossShowSearch = true;
        _tvdbCandidates = null;
        _selectedTvdbCandidate = null;
        ResetMatchSearchState();
        _matchActionStatus = null;
    }

    protected async Task ReturnToTvdbScopedSearch()
    {
        _tvdbCrossShowSearch = false;
        ResetMatchSearchState();
        _retailMovePreview = null;
        _pendingRetailMoveCandidate = null;
        _tvdbLocalFilter = string.Empty;
        await SearchTvdbScopedMatchesAsync();
    }

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
        if (!IsTvdbScopedMatching || ActiveScope is null || _tvdbSearchPending)
        {
            return;
        }
        _tvdbSearchCancellation?.Cancel();
        _tvdbSearchCancellation?.Dispose();
        var cancel = new CancellationTokenSource();
        _tvdbSearchCancellation = cancel;
        _tvdbSearchPending = true;
        _tvdbSearchError = null;
        _selectedTvdbCandidate = null;
        try
        {
            var result = await ApiClient.GetTvdbScopedMatchCandidatesAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId, seasonNumber: null, ct: cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            _tvdbCandidates = result;
            if (result is null)
            {
                _tvdbSearchError = string.IsNullOrWhiteSpace(ApiClient.LastError)
                        ? "TheTVDB candidates could not be loaded."
                        : ApiClient.LastError;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Scope changes and retries cancel the previous request without surfacing an error.
        }
        catch (Exception)
        {
            if (!cancel.IsCancellationRequested)
            {
                _tvdbSearchError = string.IsNullOrWhiteSpace(ApiClient.LastError)
                        ? "TheTVDB candidates could not be loaded. Try again."
                        : ApiClient.LastError;
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

    private void ResetTvdbScopedMatchState()
    {
        _tvdbSearchCancellation?.Cancel();
        _tvdbSearchCancellation?.Dispose();
        _tvdbSearchCancellation = null;
        _tvdbCandidates = null;
        _tvdbSearchError = null;
        _selectedTvdbCandidate = null;
        _tvdbSearchPending = false;
        _tvdbCrossShowSearch = false;
    }

    protected void SelectTvdbCandidate(TvdbMatchCandidateDto candidate) =>
        _selectedTvdbCandidate = CanSelectTvdbCandidates ? candidate : null;

    protected async Task ApplyTvdbScopedMatchAsync()
    {
        if (ActiveScope is null || _tvdbCandidates is null || _selectedTvdbCandidate is null
            || !CanSelectTvdbCandidates || _tvdbApplyPending || IsDirty)
        {
            return;
        }
        _tvdbApplyPending = true;
        try
        {
            var result = await ApiClient.ApplyTvdbScopedMatchAsync(
                CanonicalEndpointEntityId, ActiveScope.ScopeId,
                new ApplyTvdbScopedMatchDto(_selectedTvdbCandidate.Id,
                    _tvdbCandidates.SeriesId, _tvdbCandidates.CurrentRevision, _tvdbCandidates.SeasonType));
            if (result is null)
            {
                Snackbar.Add(ApiClient.LastError ?? "TheTVDB match could not be applied.", AppSeverity.Error);
                return;
            }
            Snackbar.Add(result.Message, AppSeverity.Success);
            _tvdbCandidates = null;
            _tvdbSearchError = null;
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
