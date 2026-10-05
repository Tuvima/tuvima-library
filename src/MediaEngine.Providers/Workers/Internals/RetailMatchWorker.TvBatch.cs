using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Workers;

public sealed partial class RetailMatchWorker
{
    private async Task ProcessTvBatchAsync(IReadOnlyList<IdentityJob> jobs, CancellationToken ct)
    {
        // Load hints for every job.
        var jobHints = await BuildFileHintsBatchAsync(jobs.Select(job => job.EntityId).ToList(), ct)
            .ConfigureAwait(false);

        // Group by show_name+season_number key.
        var groups = jobs
            .GroupBy(j => BuildShowSeasonKey(jobHints[j.EntityId]))
            .ToList();

        _logger.LogInformation(
            "TV: grouping {EpisodeCount} episode(s) into {GroupCount} show/season group(s) for retail match",
            jobs.Count, groups.Count);

        var groupTasks = groups
            .Select(group => _concurrency.RunAsync(
                EnrichmentWorkKind.RetailProvider,
                token => ProcessTvGroupWithFallbackAsync(group.Key, group.ToList(), jobHints, token),
                ct))
            .ToList();
        await Task.WhenAll(groupTasks).ConfigureAwait(false);
    }

    private async Task ProcessTvGroupWithFallbackAsync(
        string groupKey,
        IReadOnlyList<IdentityJob> groupJobs,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> jobHints,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, GetExecutionSnapshot().Hydration.Stage1TimeoutSeconds)));
        try
        {
            // TV retail identity is TVDB-only. The legacy grouped TMDB path
            // remains for historical data helpers but must not be invoked for
            // new identification jobs, even when TVDB is not configured.
            foreach (var job in groupJobs)
                await ProcessJobAsync(job, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"TV season identification exceeded the configured {GetExecutionSnapshot().Hydration.Stage1TimeoutSeconds}-second timeout.");
            foreach (var job in groupJobs)
            {
                // A later episode or optional operation must not undo a decision
                // already committed for an earlier member of this group.
                var current = await _jobRepo.GetByIdAsync(job.Id, ct).ConfigureAwait(false);
                if (current is not null && current.State != IdentityJobState.RetailSearching.ToString())
                    continue;
                await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                    _jobRepo,
                    job,
                    IdentityJobState.Queued,
                    timeout,
                    GetExecutionSnapshot().Hydration,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "TV: show/season group '{Key}' failed; falling back to per-episode search for {Count} job(s)",
                groupKey, groupJobs.Count);

            foreach (var job in groupJobs)
            {
                try { await ProcessJobAsync(job, ct).ConfigureAwait(false); }
                catch (Exception innerEx) when (innerEx is not OperationCanceledException)
                {
                    _logger.LogError(innerEx,
                        "RetailMatchWorker per-episode fallback failed for {EntityId}", job.EntityId);
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, innerEx.Message, ct)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Processes a group of TV jobs (same show and season) with a single TMDB
    /// show search + season episode lookup. Each job receives per-episode claims.
    /// </summary>
    private async Task ProcessTvGroupAsync(
        IReadOnlyList<IdentityJob> groupJobs,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> jobHints,
        CancellationToken ct)
    {
        // The generic provider waterfall now starts with TVDB's exact aired
        // show/season/episode lookup. Existing TMDB-only installations retain
        // the established grouped TMDB path below.
        if (_tvdbClient?.IsConfigured() == true
            && ProviderExecutionFilter.IsEnabled("tvdb", GetExecutionSnapshot().Providers))
        {
            foreach (var job in groupJobs)
                await ProcessJobAsync(job, ct).ConfigureAwait(false);
            return;
        }

        foreach (var job in groupJobs)
        {
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailSearching, ct: ct);
        }

        var executionConfig = GetExecutionSnapshot();
        var hydrationConfig = executionConfig.Hydration;
        var retailAcceptThreshold = hydrationConfig.RetailAutoAcceptThreshold;
        var retailAmbiguousThreshold = hydrationConfig.RetailAmbiguousThreshold;

        var providerConfigs = executionConfig.Providers;
        var tmdbConfig = providerConfigs.FirstOrDefault(p =>
            string.Equals(p.Name, "tmdb", StringComparison.OrdinalIgnoreCase));

        var tmdbApiKey = !string.IsNullOrWhiteSpace(tmdbConfig?.HttpClient?.ApiKeyOverride)
            ? tmdbConfig.HttpClient.ApiKeyOverride
            : tmdbConfig?.HttpClient?.ApiKey;
        if (!ProviderExecutionFilter.IsEnabled("tmdb", providerConfigs))
        {
            _logger.LogInformation(
                "RetailMatchWorker: TMDB provider disabled; falling back to generic retail matching for {Count} TV job(s)",
                groupJobs.Count);

            foreach (var job in groupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(tmdbApiKey))
        {
            _logger.LogWarning(
                "RetailMatchWorker: TMDB API key not configured — falling back to per-episode for {Count} jobs",
                groupJobs.Count);

            foreach (var job in groupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }
            return;
        }

        var representativeHints = jobHints[groupJobs[0].EntityId];
        var showName = representativeHints.GetValueOrDefault(MetadataFieldConstants.ShowName)
            ?? representativeHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var seasonStr = representativeHints.GetValueOrDefault(MetadataFieldConstants.SeasonNumber)
            ?? representativeHints.GetValueOrDefault("season");
        // Scan ALL jobs in the group for a year claim — any episode-folder year
        // (e.g. "Shogun (2024)/Season 01/...") is enough to disambiguate the show,
        // even if the representative job's filename had no year.
        int? yearHint = null;
        foreach (var job in groupJobs)
        {
            if (!jobHints.TryGetValue(job.EntityId, out var hints))
            {
                continue;
            }

            var candidate = hints.GetValueOrDefault(MetadataFieldConstants.Year);
            if (int.TryParse(candidate, out var parsedYear) && parsedYear > 1900)
            {
                yearHint = parsedYear;
                break;
            }
        }
        var (lang, _, country) = GetConfiguredLocale();

        // Step 1: Search TMDB for the show to get tv_id.
        _logger.LogInformation(
            "TV: searching TMDB for show '{ShowName}'{YearHint} — {EpisodeCount} episode(s) queued",
            showName ?? "(unknown)",
            yearHint.HasValue ? $" (year={yearHint.Value})" : "",
            groupJobs.Count);
        var showSearch = await _tmdbClient.SearchShowAsync(showName, yearHint, tmdbApiKey, lang, country, ct);
        var tvId = showSearch.TvId;
        var matchedShowName = showSearch.MatchedShowName;

        if (tvId is null)
        {
            _logger.LogInformation(
                "TV: no TMDB show found for '{ShowName}' — falling back to per-episode search for {EpisodeCount} job(s)",
                showName ?? "(unknown)", groupJobs.Count);

            foreach (var job in groupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }
            return;
        }

        var showDetails = await _tmdbClient.FetchShowDetailsAsync(tvId, tmdbApiKey, lang, country, ct);

        // Step 2: Determine unique seasons needed (may be multiple if batch spans seasons).
        var seasonGroups = groupJobs
            .GroupBy(j => jobHints[j.EntityId].GetValueOrDefault(MetadataFieldConstants.SeasonNumber)
                ?? jobHints[j.EntityId].GetValueOrDefault("season")
                ?? string.Empty)
            .ToList();

        // Build a flat episode list across all seasons needed.
        var allEpisodes = new List<(string Season, JsonNode Node)>();
        foreach (var seasonGroup in seasonGroups)
        {
            var season = seasonGroup.Key;
            if (!int.TryParse(season, out var seasonNumber))
            {
                continue;
            }

            var episodes = await _tmdbClient.FetchSeasonEpisodesAsync(tvId, seasonNumber, tmdbApiKey, lang, country, ct);
            foreach (var ep in episodes)
            {
                allEpisodes.Add((season, ep));
            }
        }

        _logger.LogInformation(
            "TV: matched show '{ShowName}' on TMDB (tv_id={TvId}), fetched {EpisodeCount} episode(s) — applying to {JobCount} queued episode(s)",
            showName ?? "—", tvId, allEpisodes.Count, groupJobs.Count);

        var tmdbProvider = _providers.FirstOrDefault(p =>
            string.Equals(p.Name, "tmdb", StringComparison.OrdinalIgnoreCase));

        // Step 3: Match each job to an episode and apply claims.
        foreach (var job in groupJobs)
        {
            var hints = jobHints[job.EntityId];
            try
            {
                await ApplyTvEpisodeAsync(
                    job, hints, allEpisodes, tvId, matchedShowName, showDetails,
                    tmdbProvider, retailAcceptThreshold, retailAmbiguousThreshold, tmdbApiKey, lang, country, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "RetailMatchWorker: failed to apply episode claims to job {JobId} (entity {EntityId})",
                    job.Id, job.EntityId);
                await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
            }
        }
    }

    /// <summary>
    /// Matches a TV job to the best episode in the season list, builds claims, scores, and transitions the job.
    /// </summary>
    private async Task ApplyTvEpisodeAsync(
        IdentityJob job,
        IReadOnlyDictionary<string, string> fileHints,
        IReadOnlyList<(string Season, JsonNode Node)> allEpisodes,
        string tvId,
        string? matchedShowName,
        JsonNode? showDetails,
        IExternalMetadataProvider? tmdbProvider,
        double retailAcceptThreshold,
        double retailAmbiguousThreshold,
        string tmdbApiKey, string language, string country, CancellationToken ct)
    {
        // For TV scoring: prefer episode_title over the generic title claim.
        // VideoProcessor sets title = episode_title when available, but fileHints may
        // still carry the old show-name title for files ingested before the fix.
        // Explicitly preferring episode_title here ensures episode-vs-episode comparison.
        var fileTitle = fileHints.GetValueOrDefault(MetadataFieldConstants.EpisodeTitle)
            ?? fileHints.GetValueOrDefault(MetadataFieldConstants.Title);
        var fileEpisodeNumber = fileHints.GetValueOrDefault(MetadataFieldConstants.EpisodeNumber)
            ?? fileHints.GetValueOrDefault("episode");
        var fileSeason = fileHints.GetValueOrDefault(MetadataFieldConstants.SeasonNumber)
            ?? fileHints.GetValueOrDefault("season")
            ?? "1";

        // Filter to the correct season first.
        var seasonEpisodes = allEpisodes
            .Where(e => string.Equals(e.Season, fileSeason, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Node)
            .ToList();

        if (seasonEpisodes.Count == 0)
        {
            seasonEpisodes = allEpisodes.Select(e => e.Node).ToList(); // Fallback: search all seasons.
        }

        // Match by episode number (preferred), then by title.
        JsonNode? bestEpisode = null;
        double bestMatchScore = -1.0;

        foreach (var ep in seasonEpisodes)
        {
            var epNum = ep["episode_number"]?.GetValue<long?>()?.ToString();
            var epTitle = ep["name"]?.GetValue<string>();

            double matchScore = 0.0;

            if (!string.IsNullOrWhiteSpace(fileEpisodeNumber) && !string.IsNullOrWhiteSpace(epNum)
                && string.Equals(fileEpisodeNumber.Trim(), epNum.Trim(), StringComparison.Ordinal))
            {
                matchScore = 1.0;
            }
            else if (!string.IsNullOrWhiteSpace(fileTitle) && !string.IsNullOrWhiteSpace(epTitle))
            {
                matchScore = RetailTextSimilarity.ComputeWordOverlap(fileTitle, epTitle);
            }

            if (matchScore > bestMatchScore)
            {
                bestMatchScore = matchScore;
                bestEpisode = ep;
            }
        }

        if (bestEpisode is null || bestMatchScore < 0.25)
        {
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch, ct: ct);
            await EnrichPeopleWithoutMediaMatchAsync(job.EntityId, ct).ConfigureAwait(false);
            await _outcomeFactory.CreateRetailFailedAsync(
                job.EntityId, job.MediaType, job.IngestionRunId, null, ct);
            var titleHint = fileHints.GetValueOrDefault(MetadataFieldConstants.Title) ?? "(unknown)";
            await _timeline.RecordRetailNoMatchAsync(job.EntityId, titleHint, job.IngestionRunId, ct);

            _logger.LogInformation(
                "TV: no episode match (score {Score:F2}) for '{Title}' on tv_id={TvId} (entity {EntityId})",
                bestMatchScore, fileTitle ?? "(unknown)", tvId, job.EntityId);
            return;
        }

        var showName = fileHints.GetValueOrDefault(MetadataFieldConstants.ShowName)
            ?? fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var providerShowName = matchedShowName ?? showName;
        var claims = BuildTvShowClaims(showDetails, tvId, providerShowName)
            .Concat(BuildTvEpisodeClaims(bestEpisode, tvId, providerShowName, fileSeason))
            .Concat(BuildTmdbSeasonManifestClaims(seasonEpisodes, tvId, providerShowName, fileSeason))
            .ToList();

        // For retail scoring, the candidate title is the episode title and author/creator
        // is the show name (best available approximation for TV scoring).
        var candidateTitle = bestEpisode["name"]?.GetValue<string>();
        string? candidateAuthor = null;
        var candidateYear = bestEpisode["air_date"]?.GetValue<string>()?.Length >= 4
            ? bestEpisode["air_date"]!.GetValue<string>()![..4]
            : null;

        // ── Structural S/E number signal ─────────────────────────────────────
        // Season+episode number matching is a very strong structural indicator
        // that dwarfs title fuzziness (episode titles may be absent or ambiguous).
        // The bonus is computed here and passed into the scoring service so the
        // composite is produced through a single code path (no manual addition).
        var candidateEpisodeNum = bestEpisode["episode_number"]?.GetValue<long?>()?.ToString();
        var candidateSeasonNum = bestEpisode["season_number"]?.GetValue<long?>()?.ToString()
            ?? bestEpisode["season"]?.GetValue<string>();

        bool seasonMatches = !string.IsNullOrWhiteSpace(fileSeason)
            && !string.IsNullOrWhiteSpace(candidateSeasonNum)
            && string.Equals(fileSeason.Trim(), candidateSeasonNum.Trim(), StringComparison.Ordinal);
        bool episodeMatches = !string.IsNullOrWhiteSpace(fileEpisodeNumber)
            && !string.IsNullOrWhiteSpace(candidateEpisodeNum)
            && string.Equals(fileEpisodeNumber.Trim(), candidateEpisodeNum.Trim(), StringComparison.Ordinal);
        bool showMatches = RetailTextSimilarity.AreEquivalentNames(showName, providerShowName);

        var extended = BuildCandidateExtendedMetadata(claims);
        var retailScore = _retailScoring.ScoreCandidate(
            fileHints, candidateTitle, candidateAuthor, candidateYear, MediaType.TV,
            extendedMetadata: new CandidateExtendedMetadata
            {
                Scope = "episode", Kind = MediaType.TV, Description = extended.Description,
                Series = providerShowName,
                Signals = new Dictionary<string,string>(extended.Signals, StringComparer.OrdinalIgnoreCase)
                {
                    ["show_name"] = providerShowName ?? string.Empty,
                    ["season_number"] = candidateSeasonNum ?? string.Empty,
                    ["episode_number"] = candidateEpisodeNum ?? string.Empty,
                },
            });
        var adjustedComposite = retailScore.CompositeScore;

        var decision = _candidateScorer.EvaluateDecision(
            fileHints,
            candidateTitle,
            candidateAuthor,
            candidateYear,
            retailScore,
            adjustedComposite,
            retailAcceptThreshold,
            retailAmbiguousThreshold,
            "grouped_tv",
            mediaType: MediaType.TV,
            autoAcceptCapReasons: showMatches && seasonMatches && episodeMatches
                ? null
                : ["requires_exact_show_season_episode"]);

        var providerId = tmdbProvider?.ProviderId ?? Guid.Empty;

        var bridgeIdsJson = BuildBridgeIdsJson(claims);

        var candidate = new RetailMatchCandidate
        {
            JobId = job.Id,
            ProviderId = providerId,
            ProviderName = "tmdb",
            ProviderItemId = bestEpisode["id"]?.GetValue<long?>()?.ToString(),
            Rank = 1,
            Title = candidateTitle ?? "(unknown)",
            Creator = candidateAuthor,
            Year = candidateYear,
            ScoreTotal = decision.FinalScore,
            ScoreBreakdownJson = _candidateScorer.BuildScoreBreakdownJson(
                retailScore,
                decision,
                "grouped_tv",
                new Dictionary<string, object?>
                {
                    ["show_matches"] = showMatches,
                    ["season_matches"] = seasonMatches,
                    ["episode_matches"] = episodeMatches,
                }),
            BridgeIdsJson = bridgeIdsJson,
            // Localized TMDB search posters frequently contain embedded title
            // text. Managed TV artwork is selected later from /images using
            // the language-neutral poster policy.
            ImageUrl = null,
            Outcome = decision.Outcome,
        };

        await _candidateRepo.InsertBatchAsync([candidate], ct);

        if (decision.Outcome != "Rejected")
        {
            // Phase 3c: fetch lineage so parent-scope claims (show_name,
            // year, description, cover) mirror onto the show Work in
            // addition to the episode.
            WorkLineage? lineage = null;
            try { lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Phase 3c: lineage lookup failed for TV episode {EntityId} — parent mirror skipped",
                    job.EntityId);
            }

            await ScoringHelper.PersistAndScoreWithLineageAsync(
                job.EntityId, claims, providerId, lineage,
                _claimRepo, _canonicalRepo, _scoringEngine, _configLoader, _providers, ct,
                arrayRepo: _arrayRepo, logger: _logger);

            var bridgeEntries = claims
                .Where(c => BridgeIdHelper.IsBridgeId(c.Key) && !string.IsNullOrWhiteSpace(c.Value))
                .Select(c => new BridgeIdEntry
                {
                    EntityId = ResolveBridgeIdEntityId(lineage, job.EntityId, c.Key),
                    IdType = c.Key,
                    IdValue = c.Value,
                    ProviderId = providerId.ToString(),
                }).ToList();

            if (bridgeEntries.Count > 0)
            {
                await _bridgeIdRepo.UpsertBatchAsync(bridgeEntries, ct);
            }

            if (lineage is not null)
            {
                if (_episodeCredits is not null
                    && int.TryParse(candidateSeasonNum, out var creditSeason)
                    && int.TryParse(candidateEpisodeNum, out var creditEpisode))
                {
                    var evidence = await _tmdbClient.FetchEpisodeCreditsAsync(tvId, creditSeason,
                        creditEpisode, tmdbApiKey, language, country, ct);
                    if (evidence is not null)
                    {
                        await _episodeCredits.ReplaceAsync(new TvEpisodeCredits(lineage.WorkId,
                            lineage.RootParentWorkId, bestEpisode["id"]!.ToString(), creditSeason,
                            creditEpisode, ParseEpisodeCredits(evidence)), ct);
                    }
                }
                await DownloadAndPersistTmdbEpisodeStillAsync(
                    bestEpisode,
                    lineage.TargetForSelfScope,
                    ct);
            }
        }

        if (decision.Outcome == "AutoAccepted")
        {
            await _jobRepo.SetSelectedCandidateAsync(job.Id, candidate.Id, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct);
            await _timeline.RecordRetailMatchedAsync(
                job.EntityId, "tmdb", 1, job.IngestionRunId, ct);

            _logger.LogInformation(
                "TV: '{ShowName}' S{Season}E{Episode} — '{EpisodeTitle}' matched on TMDB (score {Score:F2}) [entity {EntityId}]",
                showName ?? "(unknown)", fileSeason, fileEpisodeNumber ?? "?",
                candidateTitle, decision.FinalScore, job.EntityId);

            try
            {
                await _postPipeline.EvaluateAndOrganizeAsync(
                    job.EntityId, job.Id, wikidataQid: null, job.IngestionRunId, ct)
                    .ConfigureAwait(false);
                if (_coverArtWorker is not null)
                {
                    await _coverArtWorker.DownloadAndPersistAsync(job.EntityId, wikidataQid: null, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception orgEx) when (orgEx is not OperationCanceledException)
            {
                _logger.LogWarning(orgEx,
                    "TV: post-retail organization failed for entity {EntityId} — pipeline continues",
                    job.EntityId);
            }
        }
        else if (decision.Outcome == "Ambiguous")
        {
            await _jobRepo.SetSelectedCandidateAsync(job.Id, candidate.Id, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatchedNeedsReview, ct: ct);
            await _outcomeFactory.CreateRetailAmbiguousAsync(
                job.EntityId, job.MediaType, decision.FinalScore, job.IngestionRunId, null, ct);
            await _timeline.RecordRetailMatchedAsync(
                job.EntityId, "tmdb", 1, job.IngestionRunId, ct);

            _logger.LogInformation(
                "TV: '{ShowName}' S{Season}E{Episode} — '{EpisodeTitle}' ambiguous on TMDB (score {Score:F2}, needs review) [entity {EntityId}]",
                showName ?? "(unknown)", fileSeason, fileEpisodeNumber ?? "?",
                candidateTitle, decision.FinalScore, job.EntityId);
        }
        else
        {
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch, ct: ct);
            await EnrichPeopleWithoutMediaMatchAsync(job.EntityId, ct).ConfigureAwait(false);
            await _outcomeFactory.CreateRetailFailedAsync(
                job.EntityId, job.MediaType, job.IngestionRunId, null, ct);
            var titleHint = fileHints.GetValueOrDefault(MetadataFieldConstants.Title) ?? "(unknown)";
            await _timeline.RecordRetailNoMatchAsync(job.EntityId, titleHint, job.IngestionRunId, ct);

            _logger.LogInformation(
                "TV: '{ShowName}' S{Season}E{Episode} rejected — score {Score:F2} below thresholds [entity {EntityId}]",
                showName ?? "(unknown)", fileSeason, fileEpisodeNumber ?? "?",
                decision.FinalScore, job.EntityId);
        }
    }

    private static string BuildBridgeIdsJson(IEnumerable<ProviderClaim> claims)
    {
        var bridgeIds = claims
            .Where(c => BridgeIdHelper.IsBridgeId(c.Key))
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value,
                StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(bridgeIds);
    }

    /// <summary>
    /// Builds <see cref="ProviderClaim"/> list from a raw TMDB episode JSON node.
    /// Includes show-level bridge ID (tmdb_id for the show) so Stage 2 can bridge to Wikidata.
    /// </summary>
    private static IReadOnlyList<ProviderClaim> BuildTvEpisodeClaims(
        JsonNode episode, string showTvId, string? showName, string season)
    {
        var claims = new List<ProviderClaim>();

        void Add(string key, string? value, double confidence)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new ProviderClaim(key, value, confidence));
            }
        }

        Add(MetadataFieldConstants.EpisodeTitle, episode["name"]?.GetValue<string>(), 0.85);
        // For TV, "title" in the system is typically the episode title.
        Add(MetadataFieldConstants.Title, episode["name"]?.GetValue<string>(), 0.80);
        Add(MetadataFieldConstants.EpisodeDescription, episode["overview"]?.GetValue<string>(), 0.85);
        Add(MetadataFieldConstants.ShowName, showName, 0.85);

        var airDate = episode["air_date"]?.GetValue<string>();
        Add(MetadataFieldConstants.AirDate, airDate, 0.90);

        Add(MetadataFieldConstants.SeasonNumber,
            episode["season_number"]?.GetValue<long?>()?.ToString() ?? season, 0.90);
        Add(MetadataFieldConstants.EpisodeNumber,
            episode["episode_number"]?.GetValue<long?>()?.ToString(), 0.90);

        // The show-level TMDB ID is the critical bridge ID for Stage 2 Wikidata resolution.
        // Episode-level TMDB IDs are available but the show QID is what Wikidata resolves.
        Add(BridgeIdKeys.TmdbId, showTvId, 1.0);
        Add(BridgeIdKeys.TmdbEpisodeId,
            episode["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
                ?? episode["id"]?.GetValue<string>(), 1.0);

        var rating = episode["vote_average"]?.GetValue<double?>()?.ToString("F1");
        if (!string.IsNullOrWhiteSpace(rating))
        {
            Add(MetadataFieldConstants.Rating, rating, 0.80);
        }

        Add(MetadataFieldConstants.Runtime,
            episode["runtime"]?.GetValue<long?>()?.ToString(System.Globalization.CultureInfo.InvariantCulture), 0.90);

        AddTvEpisodeCrewClaims(claims, episode);
        AddTvEpisodeGuestStarClaims(claims, episode);

        return claims;
    }

    internal static IReadOnlyList<TvPersonCredit> ParseEpisodeCredits(JsonNode evidence)
    {
        var credits = new List<TvPersonCredit>();
        foreach (var kind in new[] { "cast", "guest_stars", "crew" })
        {
            foreach (var person in evidence[kind]?.AsArray() ?? [])
            {
                if (person is null || string.IsNullOrWhiteSpace(person["name"]?.ToString()))
                {
                    continue;
                }

                credits.Add(new TvPersonCredit(person["id"]?.ToString() ?? string.Empty,
                    person["credit_id"]?.ToString() ?? string.Empty, person["name"]!.ToString(),
                    kind == "crew" ? person["job"]?.ToString() ?? "Crew" : "Actor",
                    person["character"]?.ToString(), BuildTmdbOriginalImageUrl(person["profile_path"]?.ToString()),
                    person["order"]?.GetValue<int?>() ?? credits.Count));
            }
        }

        return credits.DistinctBy(c => (c.PersonId, c.Job, c.Character)).OrderBy(c => c.Order).ToList();
    }

    private static IReadOnlyList<ProviderClaim> BuildTmdbSeasonManifestClaims(
        IReadOnlyList<JsonNode> episodes,
        string showTvId,
        string? showName,
        string season)
    {
        if (episodes.Count == 0)
        {
            return [];
        }

        var items = episodes
            .Select(episode => new ProviderSequenceManifestItem
            {
                ExternalId = episode["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
                    ?? episode["id"]?.GetValue<string>()
                    ?? string.Empty,
                Title = episode["name"]?.GetValue<string>() ?? "Untitled episode",
                Ordinal = episode["episode_number"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty,
                ReleaseDate = episode["air_date"]?.GetValue<string>(),
                Description = episode["overview"]?.GetValue<string>(),
                Duration = episode["runtime"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ExternalId)
                && !string.IsNullOrWhiteSpace(item.Ordinal))
            .ToList();
        var isAuthoritative = items.Count == episodes.Count
            && items.Select(item => item.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count
            && items.Select(item => item.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count;
        if (items.Count == 0)
        {
            return [];
        }

        var manifest = new ProviderSequenceManifest
        {
            Provider = "tmdb",
            ContainerId = $"tmdb:tv:{showTvId}:season:{season}",
            ContainerLabel = $"{showName ?? "TV Show"} · Season {season}",
            ExternalIdKey = BridgeIdKeys.TmdbEpisodeId,
            MediaType = MediaType.TV.ToString(),
            ContainerKind = "TvSeason",
            ExpectedTotal = episodes.Count,
            ExpectedTotalKind = "episodes",
            IsAuthoritative = isAuthoritative,
            Items = items,
        };

        return
        [
            new ProviderClaim(
                MetadataFieldConstants.SequenceManifestJson,
                JsonSerializer.Serialize(manifest),
                isAuthoritative ? 1.0 : 0.7),
        ];
    }

    private static IReadOnlyList<ProviderClaim> BuildTvShowClaims(
        JsonNode? showDetails, string showTvId, string? fallbackShowName)
    {
        var claims = new List<ProviderClaim>();

        void Add(string key, string? value, double confidence)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new ProviderClaim(key, value, confidence));
            }
        }

        Add(MetadataFieldConstants.ShowName, showDetails?["name"]?.GetValue<string>() ?? fallbackShowName, 0.90);
        Add(MetadataFieldConstants.Title, showDetails?["name"]?.GetValue<string>() ?? fallbackShowName, 0.86);
        Add(MetadataFieldConstants.Description, showDetails?["overview"]?.GetValue<string>(), 0.86);
        Add(MetadataFieldConstants.ShortDescription, showDetails?["overview"]?.GetValue<string>(), 0.84);
        Add(MetadataFieldConstants.Tagline, showDetails?["tagline"]?.GetValue<string>(), ClaimConfidence.ProviderNativeTagline);
        Add(MetadataFieldConstants.Network, showDetails?["networks"]?[0]?["name"]?.GetValue<string>(), 0.85);
        Add(BridgeIdKeys.TmdbId, showTvId, 1.0);
        Add(BridgeIdKeys.ImdbId, showDetails?["external_ids"]?["imdb_id"]?.GetValue<string>(), 0.98);
        Add(BridgeIdKeys.TvdbId,
            showDetails?["external_ids"]?["tvdb_id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? showDetails?["external_ids"]?["tvdb_id"]?.GetValue<string>(),
            0.98);
        Add(BridgeIdKeys.WikidataQid, showDetails?["external_ids"]?["wikidata_id"]?.GetValue<string>(), 0.98);
        Add(MetadataFieldConstants.Rating, showDetails?["vote_average"]?.GetValue<double?>()?.ToString("F1"), 0.80);
        Add("content_rating", ExtractTmdbTvContentRating(showDetails), 0.88);
        Add(MetadataFieldConstants.OriginalLanguage, showDetails?["original_language"]?.GetValue<string>(), 0.85);
        Add(MetadataFieldConstants.SeasonCount,
            showDetails?["number_of_seasons"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture), 0.95);
        Add(MetadataFieldConstants.EpisodeCount,
            showDetails?["number_of_episodes"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture), 0.95);

        var firstAirDate = showDetails?["first_air_date"]?.GetValue<string>();
        Add("first_air_date", firstAirDate, 0.95);
        if (!string.IsNullOrWhiteSpace(firstAirDate) && firstAirDate.Length >= 4)
        {
            Add(MetadataFieldConstants.Year, firstAirDate[..4], 0.85);
        }

        var showStatus = showDetails?["status"]?.GetValue<string>();
        var lastAirDate = showDetails?["last_air_date"]?.GetValue<string>();
        if ((string.Equals(showStatus, "Ended", StringComparison.OrdinalIgnoreCase)
             || string.Equals(showStatus, "Canceled", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(lastAirDate)
            && lastAirDate.Length >= 4)
        {
            Add(MetadataFieldConstants.SeriesEndYear, lastAirDate[..4], 0.85);
        }

        AddTvAggregateCastClaims(claims, showDetails);

        return claims;
    }

    private static void AddTvAggregateCastClaims(List<ProviderClaim> claims, JsonNode? showDetails)
    {
        var castArray = showDetails?["aggregate_credits"]?["cast"]?.AsArray();
        if (castArray is null)
        {
            return;
        }

        foreach (var castNode in castArray
            .Where(node => node is not null)
            .OrderBy(node => node?["order"]?.GetValue<int?>() ?? int.MaxValue)
            .ThenByDescending(node => node?["total_episode_count"]?.GetValue<int?>() ?? 0)
            .ThenBy(node => node?["name"]?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var name = castNode?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            claims.Add(new ProviderClaim(MetadataFieldConstants.CastMember, name, 0.90));
            AddClaimIfPresent(claims, "cast_member_character", ExtractTmdbAggregateCharacterName(castNode), 0.90);
            AddClaimIfPresent(claims, "cast_member_tmdb_id", castNode?["id"]?.ToString(), 0.92);
            AddClaimIfPresent(claims, "cast_member_tmdb_identity",
                castNode?["id"] is null ? null : $"{castNode["id"]}::{name}", 0.92);
            AddClaimIfPresent(claims, "cast_member_profile_url", BuildTmdbOriginalImageUrl(castNode?["profile_path"]?.GetValue<string>()), 0.90);
        }
    }

    private static string? ExtractTmdbAggregateCharacterName(JsonNode? castNode)
    {
        var roles = castNode?["roles"]?.AsArray();
        if (roles is null)
        {
            return null;
        }

        var names = roles
            .Where(role => role is not null)
            .OrderByDescending(role => role?["episode_count"]?.GetValue<int?>() ?? 0)
            .Select(role => role?["character"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        return names.Count == 0 ? null : string.Join(" / ", names);
    }

    private static void AddTvEpisodeCrewClaims(List<ProviderClaim> claims, JsonNode episode)
    {
        var crew = episode["crew"]?.AsArray();
        if (crew is null)
        {
            return;
        }

        foreach (var crewNode in crew.Where(node => node is not null))
        {
            var name = crewNode?["name"]?.GetValue<string>();
            var key = ResolveEpisodeCrewClaimKey(crewNode);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            claims.Add(new ProviderClaim(key, name, 0.88));
            AddClaimIfPresent(claims, $"{key}_tmdb_id", crewNode?["id"]?.ToString(), 0.92);
            AddClaimIfPresent(claims, $"{key}_profile_url", BuildTmdbOriginalImageUrl(crewNode?["profile_path"]?.GetValue<string>()), 0.90);
        }
    }

    private static string? ResolveEpisodeCrewClaimKey(JsonNode? crewNode)
    {
        var job = crewNode?["job"]?.GetValue<string>() ?? string.Empty;
        var department = crewNode?["department"]?.GetValue<string>() ?? string.Empty;

        if (job.Equals("Director", StringComparison.OrdinalIgnoreCase))
        {
            return MetadataFieldConstants.Director;
        }

        if (job.Contains("Writer", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Screenplay", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Teleplay", StringComparison.OrdinalIgnoreCase)
            || department.Equals("Writing", StringComparison.OrdinalIgnoreCase))
        {
            return MetadataFieldConstants.Screenwriter;
        }

        if (job.Contains("Producer", StringComparison.OrdinalIgnoreCase))
        {
            return "producer";
        }

        if (job.Contains("Composer", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Music", StringComparison.OrdinalIgnoreCase))
        {
            return MetadataFieldConstants.Composer;
        }

        return null;
    }

    private static void AddTvEpisodeGuestStarClaims(List<ProviderClaim> claims, JsonNode episode)
    {
        var guestStars = episode["guest_stars"]?.AsArray();
        if (guestStars is null)
        {
            return;
        }

        foreach (var guestNode in guestStars
            .Where(node => node is not null)
            .OrderBy(node => node?["order"]?.GetValue<int?>() ?? int.MaxValue)
            .Take(20))
        {
            var name = guestNode?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            claims.Add(new ProviderClaim(MetadataFieldConstants.GuestStar, name, 0.88));
            AddClaimIfPresent(claims, "guest_star_character", guestNode?["character"]?.GetValue<string>(), 0.88);
            AddClaimIfPresent(claims, "guest_star_tmdb_id", guestNode?["id"]?.ToString(), 0.92);
            AddClaimIfPresent(claims, "guest_star_profile_url", BuildTmdbOriginalImageUrl(guestNode?["profile_path"]?.GetValue<string>()), 0.90);
        }
    }

    private static void AddClaimIfPresent(List<ProviderClaim> claims, string key, string? value, double confidence)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new ProviderClaim(key, value, confidence));
        }
    }

    private static string? BuildTmdbOriginalImageUrl(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : $"https://image.tmdb.org/t/p/original/{path.TrimStart('/')}";

    private static string? ExtractTmdbTvContentRating(JsonNode? showDetails)
    {
        var results = showDetails?["content_ratings"]?["results"]?.AsArray();
        if (results is null)
        {
            return null;
        }

        foreach (var country in new[] { "US", "GB", "CA", "AU" })
        {
            var rating = results.FirstOrDefault(node =>
                string.Equals(node?["iso_3166_1"]?.GetValue<string>(), country, StringComparison.OrdinalIgnoreCase))
                ?["rating"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(rating))
            {
                return rating;
            }
        }

        return null;
    }

    private async Task DownloadAndPersistTmdbEpisodeStillAsync(
        JsonNode episode,
        Guid episodeWorkId,
        CancellationToken ct)
    {
        if (_entityAssetRepo is null || _assetPaths is null)
        {
            return;
        }

        var stillUrl = RetailRequestBuilder.BuildTmdbEpisodeStillUrl(episode["still_path"]?.GetValue<string>());
        if (string.IsNullOrWhiteSpace(stillUrl))
        {
            return;
        }

        await using var downloadLease = await _imageDownloadCoordinator
            .AcquireAsync(stillUrl, ct)
            .ConfigureAwait(false);

        var existingVariants = (await _entityAssetRepo.GetByEntityAsync(
            episodeWorkId.ToString(),
            AssetType.EpisodeStill.ToString(),
            ct)).ToList();

        var userOverride = existingVariants.FirstOrDefault(asset => asset.IsPreferred && asset.IsUserOverride);
        if (userOverride is not null)
        {
            _logger.LogDebug(
                "TV: preserving user-selected episode still for Work {EpisodeWorkId}",
                episodeWorkId);
            return;
        }

        var existingTmdbStill = existingVariants.FirstOrDefault(asset =>
            string.Equals(asset.SourceProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(asset.LocalImagePath)
            && File.Exists(asset.LocalImagePath)
            && !IsLegacyLowResolutionTmdbStill(asset));

        if (existingTmdbStill is not null)
        {
            await _entityAssetRepo.SetPreferredAsync(existingTmdbStill.Id, ct);
            await UpsertPreferredEpisodeStillCanonicalAsync(episodeWorkId, existingTmdbStill, ct);
            return;
        }

        byte[] bytes;
        var sourceCachedPath = _imageCache is null
            ? null
            : await _imageCache.FindBySourceUrlAsync(stillUrl, ct);
        if (!string.IsNullOrWhiteSpace(sourceCachedPath) && File.Exists(sourceCachedPath))
        {
            bytes = await File.ReadAllBytesAsync(sourceCachedPath, ct);
        }
        else
        {
            try
            {
                using var client = _httpFactory.CreateClient("tmdb");
                using var response = await client.GetAsync(stillUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "TV: failed to download TMDB episode still for Work {EpisodeWorkId}",
                    episodeWorkId);
                return;
            }
        }

        if (bytes.Length == 0)
        {
            return;
        }

        var variant = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = episodeWorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = AssetType.EpisodeStill.ToString(),
            ImageUrl = stillUrl,
            LocalImagePath = string.Empty,
            SourceProvider = "tmdb",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Episode",
            IsPreferred = false,
            IsUserOverride = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        variant.LocalImagePath = _assetPaths.GetCentralAssetPath(
            "Work",
            episodeWorkId,
            AssetType.EpisodeStill.ToString(),
            variant.Id,
            InferTmdbStillExtension(stillUrl));

        await PersistTmdbEpisodeStillBytesAsync(bytes, variant.LocalImagePath, stillUrl, ct);
        ArtworkVariantHelper.StampMetadataAndRenditions(variant, _assetPaths);
        await _entityAssetRepo.UpsertAsync(variant, ct);
        await _entityAssetRepo.SetPreferredAsync(variant.Id, ct);
        await UpsertPreferredEpisodeStillCanonicalAsync(episodeWorkId, variant, ct);

        foreach (var stale in existingVariants.Where(asset =>
                     string.Equals(asset.SourceProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
                     && asset.Id != variant.Id
                     && IsLegacyLowResolutionTmdbStill(asset)))
        {
            await _entityAssetRepo.DeleteAsync(stale.Id, ct);
            DeleteManagedEpisodeStillFiles(stale);
        }

        if (_assetExportService is not null)
        {
            await _assetExportService.ReconcileArtworkAsync(
                variant.EntityId,
                variant.EntityType,
                variant.AssetTypeValue,
                ct);
        }

        _logger.LogInformation(
            "TV: downloaded TMDB episode still for Work {EpisodeWorkId} ({Bytes} bytes)",
            episodeWorkId,
            bytes.Length);
    }

    private async Task PersistTmdbEpisodeStillBytesAsync(
        byte[] bytes,
        string destinationPath,
        string sourceUrl,
        CancellationToken ct)
    {
        AssetPathService.EnsureDirectory(destinationPath);

        if (_imageCache is null)
        {
            await BoundedHttpContent.WriteFileAtomicallyAsync(destinationPath, bytes, ct);
            return;
        }

        var hash = Hashing.Sha256Hex(bytes);
        var cachedPath = await _imageCache.FindByHashAsync(hash, ct);
        if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
        {
            if (!string.Equals(cachedPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cachedPath, destinationPath, overwrite: true);
            }
        }
        else
        {
            await BoundedHttpContent.WriteFileAtomicallyAsync(destinationPath, bytes, ct);
        }

        await _imageCache.InsertAsync(hash, destinationPath, sourceUrl, ct);
    }

    private async Task UpsertPreferredEpisodeStillCanonicalAsync(
        Guid episodeWorkId,
        EntityAsset preferredVariant,
        CancellationToken ct)
    {
        await _canonicalRepo.UpsertBatchAsync(
            ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
                episodeWorkId,
                preferredVariant,
                DateTimeOffset.UtcNow),
            ct);
    }

    private static string InferTmdbStillExtension(string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
        {
            var extension = Path.GetExtension(imageUri.AbsolutePath);
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return ".png";
            }
        }

        return ".jpg";
    }

    private static bool IsLegacyLowResolutionTmdbStill(EntityAsset asset)
    {
        var legacySource = string.IsNullOrWhiteSpace(asset.ImageUrl)
                           || asset.ImageUrl.Contains("/w500/", StringComparison.OrdinalIgnoreCase)
                           || asset.ImageUrl.Contains("/w500", StringComparison.OrdinalIgnoreCase);
        var undersized = asset.WidthPx is null or < 1280 || asset.HeightPx is null or < 720;
        return legacySource && undersized;
    }

    private static void DeleteManagedEpisodeStillFiles(EntityAsset asset)
    {
        foreach (var path in new[]
                 {
                     asset.LocalImagePath,
                     asset.LocalImagePathSmall,
                     asset.LocalImagePathMedium,
                     asset.LocalImagePathLarge,
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // The stale database row is already gone. A later central asset
                // cleanup can retry a file that is temporarily held by a reader.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve ingestion progress when an external process has a
                // temporary lock or restrictive ACL on an obsolete rendition.
            }
        }
    }

    // ── Grouping key helpers ─────────────────────────────────────────────────

}
