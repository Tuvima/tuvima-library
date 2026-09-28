using System.Globalization;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private TvTmdbSeasonListDto? _tvSeasons;
    private TvTmdbEpisodeListDto? _tvEpisodes;
    private TvTmdbEpisodeDto? _tvSelectedEpisode;
    private TvTmdbSeasonReviewDto? _tvSeasonReview;
    private readonly HashSet<Guid> _tvReviewSelectedAssets = [];
    private readonly Dictionary<Guid, string> _tvReviewResults = [];
    private bool _tvReviewLoading;
    private bool _tvReviewApplying;
    private int _tvSelectedSeason;
    private string _tvEpisodeFilter = string.Empty;
    private string? _tvLookupError;
    private bool _tvLoadingSeasons;
    private bool _tvLoadingEpisodes;
    private CancellationTokenSource? _tvLookupCts;

    protected bool IsTvEpisodeMatchPicker => EditorMediaType == "TV"
        && string.Equals(ActiveScope?.ScopeId, "episode", StringComparison.OrdinalIgnoreCase)
        && !IsWikidataSearchMode;

    protected bool IsTvSeasonMatchReview => EditorMediaType == "TV"
        && string.Equals(ActiveScope?.ScopeId, "season", StringComparison.OrdinalIgnoreCase);

    protected IReadOnlyList<TvTmdbEpisodeDto> FilteredTvEpisodes =>
        (_tvEpisodes?.Episodes ?? [])
            .Where(episode => string.IsNullOrWhiteSpace(_tvEpisodeFilter)
                || episode.Title.Contains(_tvEpisodeFilter, StringComparison.OrdinalIgnoreCase)
                || episode.Number.ToString(CultureInfo.InvariantCulture).Contains(_tvEpisodeFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private void ResetTvMatchingState()
    {
        _tvLookupCts?.Cancel();
        _tvLookupCts?.Dispose();
        _tvLookupCts = null;
        _tvSeasons = null;
        _tvEpisodes = null;
        _tvSelectedEpisode = null;
        _tvSeasonReview = null;
        _tvReviewSelectedAssets.Clear();
        _tvReviewResults.Clear();
        _tvLookupError = null;
        _tvEpisodeFilter = string.Empty;
        _tvLoadingSeasons = false;
        _tvLoadingEpisodes = false;
    }

    protected async Task LoadTvSeasonsAsync()
    {
        _tvLookupCts?.Cancel();
        _tvLookupCts?.Dispose();
        var cts = new CancellationTokenSource();
        _tvLookupCts = cts;
        _tvLoadingSeasons = true;
        _tvLookupError = null;
        int? initialSeason = null;
        try
        {
            var response = await ApiClient.GetTvTmdbSeasonsAsync(CurrentEntityId, cts.Token);
            if (cts.IsCancellationRequested) return;
            _tvSeasons = response;
            if (response is null)
            {
                _tvLookupError = ApiClient.LastError ?? "TMDB seasons could not be loaded. Retry shortly.";
                return;
            }
            if (response.Seasons.Count == 0)
            {
                _tvLookupError = "TMDB has no seasons listed for this show.";
                return;
            }
            var seasonNode = _navigator?.Nodes.FirstOrDefault(node => node.EntityId == ActiveScope?.FieldEntityId
                && node.NodeKind == "season");
            var suggested = seasonNode?.CompactOrdinalLabel is { Length: > 1 } label
                && int.TryParse(label.AsSpan(1), out var nodeNumber)
                    ? nodeNumber
                    : int.TryParse(GetBaselineValue(MetadataFieldConstants.SeasonNumber), out var parsed) ? parsed : 1;
            _tvSelectedSeason = response.Seasons.Any(season => season.Number == suggested)
                ? suggested : response.Seasons[0].Number;
            initialSeason = _tvSelectedSeason;
        }
        finally
        {
            if (ReferenceEquals(_tvLookupCts, cts))
            {
                _tvLoadingSeasons = false;
                await InvokeAsync(StateHasChanged);
            }
        }
        if (initialSeason.HasValue) await LoadTvEpisodesAsync(initialSeason.Value);
    }

    protected async Task LoadTvEpisodesAsync(int seasonNumber)
    {
        if (_tvSeasons is null || !_tvSeasons.Seasons.Any(season => season.Number == seasonNumber)) return;
        _tvLookupCts?.Cancel();
        _tvLookupCts?.Dispose();
        var cts = new CancellationTokenSource();
        _tvLookupCts = cts;
        _tvSelectedSeason = seasonNumber;
        _tvEpisodes = null;
        _tvSelectedEpisode = null;
        _tvLookupError = null;
        _tvLoadingEpisodes = true;
        TvTmdbEpisodeDto? suggestedEpisode = null;
        try
        {
            var response = await ApiClient.GetTvTmdbEpisodesAsync(CurrentEntityId, seasonNumber, cts.Token);
            if (cts.IsCancellationRequested) return;
            _tvEpisodes = response;
            if (response is null)
                _tvLookupError = ApiClient.LastError ?? "TMDB episodes could not be loaded. Retry shortly.";
            else if (int.TryParse(GetBaselineValue(MetadataFieldConstants.SeasonNumber), out var localSeason)
                && localSeason == seasonNumber
                && int.TryParse(GetBaselineValue(MetadataFieldConstants.EpisodeNumber), out var localEpisode))
                suggestedEpisode = response.Episodes.FirstOrDefault(episode => episode.Number == localEpisode);
        }
        finally
        {
            if (ReferenceEquals(_tvLookupCts, cts))
            {
                _tvLoadingEpisodes = false;
                await InvokeAsync(StateHasChanged);
            }
        }
        if (suggestedEpisode is not null && ReferenceEquals(_tvLookupCts, cts))
            await SelectTvEpisodeAsync(suggestedEpisode);
    }

    protected void SetTvEpisodeFilter(string? value) => _tvEpisodeFilter = value ?? string.Empty;

    protected async Task LoadTvSeasonReviewAsync()
    {
        if (_tvSeasons is null) await LoadTvSeasonsAsync();
        if (_tvSeasons is null) return;
        _tvReviewLoading = true;
        _tvLookupError = null;
        try
        {
            var review = await ApiClient.GetTvTmdbSeasonReviewAsync(CurrentEntityId, _tvSelectedSeason);
            if (review is null)
            {
                _tvLookupError = ApiClient.LastError ?? "This owned season could not be reviewed against TMDB.";
                return;
            }
            _tvSeasonReview = review;
            _tvReviewSelectedAssets.Clear();
            _tvReviewResults.Clear();
            foreach (var row in review.Rows.Where(row => row.CanApply))
                _tvReviewSelectedAssets.Add(row.AssetId);
        }
        finally
        {
            _tvReviewLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected void ToggleTvSeasonReviewRow(Guid assetId)
    {
        if (!_tvReviewSelectedAssets.Add(assetId)) _tvReviewSelectedAssets.Remove(assetId);
    }

    protected async Task ApplyTvSeasonReviewAsync()
    {
        if (_tvSeasonReview is null || _tvReviewApplying || IsDirty) return;
        _tvReviewApplying = true;
        var applied = 0;
        var failed = 0;
        try
        {
            foreach (var row in _tvSeasonReview.Rows.Where(row => row.CanApply
                         && _tvReviewSelectedAssets.Contains(row.AssetId) && row.Episode is not null))
            {
                var episode = row.Episode!;
                _tvReviewResults[row.AssetId] = "Matching…";
                await InvokeAsync(StateHasChanged);
                var response = await ApiClient.ReplaceRetailMatchAsync(row.AssetId, new ReplaceRetailMatchRequestDto
                {
                    TargetKind = "item",
                    TargetFieldGroup = "show_episode",
                    TargetScopeId = "episode",
                    ProviderId = _tvSeasonReview.ProviderId,
                    ProviderName = "tmdb",
                    ProviderItemId = episode.Id,
                    RequiredFields = new(StringComparer.OrdinalIgnoreCase)
                    {
                        [MetadataFieldConstants.ShowName] = _tvSeasonReview.ShowName,
                        [MetadataFieldConstants.SeasonNumber] = _tvSeasonReview.SeasonNumber.ToString(CultureInfo.InvariantCulture),
                        [MetadataFieldConstants.EpisodeNumber] = episode.Number.ToString(CultureInfo.InvariantCulture),
                    },
                    BridgeIds = new(StringComparer.OrdinalIgnoreCase)
                    {
                        [BridgeIdKeys.TmdbId] = _tvSeasonReview.ShowId,
                        [BridgeIdKeys.TmdbEpisodeId] = episode.Id,
                    },
                });
                if (response is null)
                {
                    failed++;
                    _tvReviewResults[row.AssetId] = "Failed; retry this episode individually.";
                }
                else
                {
                    applied++;
                    _tvReviewSelectedAssets.Remove(row.AssetId);
                    _tvReviewResults[row.AssetId] = response.ArtworkMessage ?? "Episode matched; artwork refresh queued.";
                }
            }
            if (applied > 0)
            {
                _hasCommittedChanges = true;
                await RefreshProviderArtworkAsync();
                await NotifyParentArtworkChangedAsync();
            }
            Snackbar.Add($"Season review: {applied} episode(s) matched, {failed} failed, "
                + $"{_tvSeasonReview.Rows.Count - applied - failed} skipped.",
                failed > 0 ? MudBlazor.Severity.Warning : MudBlazor.Severity.Success);
        }
        finally
        {
            _tvReviewApplying = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task SelectTvEpisodeAsync(TvTmdbEpisodeDto episode)
    {
        _tvSelectedEpisode = episode;
        var candidate = BuildTvEpisodeCandidate(episode);
        await SelectCandidateAsync(candidate);
    }

    protected ItemCanonicalRetailCandidateDto? SelectedTvEpisodeCandidate =>
        _tvSelectedEpisode is null ? null : BuildTvEpisodeCandidate(_tvSelectedEpisode);

    private ItemCanonicalRetailCandidateDto BuildTvEpisodeCandidate(TvTmdbEpisodeDto episode)
    {
        var season = _tvEpisodes?.SeasonNumber ?? _tvSelectedSeason;
        var showId = _tvEpisodes?.ShowId ?? _tvSeasons?.ShowId ?? string.Empty;
        var showName = _tvEpisodes?.ShowName ?? _tvSeasons?.ShowName ?? string.Empty;
        var suggested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(episode.Title)) suggested[MetadataFieldConstants.EpisodeTitle] = episode.Title;
        if (episode.RuntimeMinutes is { } runtime) suggested[MetadataFieldConstants.Runtime] = runtime.ToString(CultureInfo.InvariantCulture);
        if (episode.AirDate is { Length: >= 4 }) suggested[MetadataFieldConstants.Year] = episode.AirDate[..4];
        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"tmdb:tv:{showId}:s{season}:e{episode.Number}:{episode.Id}",
            ProviderId = _tvEpisodes?.ProviderId ?? string.Empty,
            ProviderName = "tmdb",
            ProviderItemId = episode.Id,
            Title = episode.Title,
            Description = episode.Overview,
            CoverUrl = episode.StillUrl,
            Year = episode.AirDate is { Length: >= 4 } ? episode.AirDate[..4] : null,
            IsApplicable = true,
            RequiredFields = new(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.ShowName] = showName,
                [MetadataFieldConstants.SeasonNumber] = season.ToString(CultureInfo.InvariantCulture),
                [MetadataFieldConstants.EpisodeNumber] = episode.Number.ToString(CultureInfo.InvariantCulture),
            },
            SuggestedFields = suggested,
            BridgeIds = new(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.TmdbId] = showId,
                [BridgeIdKeys.TmdbEpisodeId] = episode.Id,
            },
        };
    }
}
