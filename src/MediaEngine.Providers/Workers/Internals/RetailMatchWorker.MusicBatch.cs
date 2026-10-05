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
    private async Task ProcessJobWithRetryAsync(IdentityJob job, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, GetExecutionSnapshot().Hydration.Stage1TimeoutSeconds)));
        try
        {
            await ProcessJobAsync(job, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"Retail identification exceeded the configured {GetExecutionSnapshot().Hydration.Stage1TimeoutSeconds}-second timeout.");
            _logger.LogWarning(timeout, "RetailMatchWorker timed out for job {JobId} (entity {EntityId})", job.Id, job.EntityId);
            await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                _jobRepo,
                job,
                IdentityJobState.Queued,
                timeout,
                GetExecutionSnapshot().Hydration,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RetailMatchWorker failed for job {JobId} (entity {EntityId})",
                job.Id, job.EntityId);
            await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                _jobRepo,
                job,
                IdentityJobState.Queued,
                ex,
                GetExecutionSnapshot().Hydration,
                ct).ConfigureAwait(false);
        }
    }

    private int GetBatchSize() =>
        Math.Max(1, GetExecutionSnapshot().Core.Pipeline.LeaseSizes.Retail);

    private bool ShouldUseAppleMusicAlbumBatch()
    {
        var snapshot = GetExecutionSnapshot();
        var pipeline = snapshot.Pipelines.GetPipelineForMediaType(MediaType.Music);
        var providerConfigs = snapshot.Providers;
        var rankedProviders = pipeline.Providers.Count > 0
            ? pipeline.Providers.OrderBy(p => p.Rank).Select(p => p.Name).ToList()
            : providerConfigs.Select(p => p.Name).ToList();
        var firstEnabledProvider = ProviderExecutionFilter.EnabledProviderNames(
                rankedProviders,
                _providers,
                providerConfigs)
            .FirstOrDefault();
        var firstEnabledEntry = pipeline.Providers
            .OrderBy(p => p.Rank)
            .FirstOrDefault(p => string.Equals(p.Name, firstEnabledProvider, StringComparison.OrdinalIgnoreCase));

        return string.Equals(firstEnabledProvider, "apple_api", StringComparison.OrdinalIgnoreCase)
            && IsIdentityPurpose(firstEnabledEntry?.Purpose);
    }

    // ── Music group processing ──────────────────────────────────────────────

    /// <summary>
    /// Groups Music jobs by album (artist+album) and processes each group with
    /// a single Apple album search + album track lookup instead of per-track calls.
    /// </summary>
    private async Task ProcessMusicBatchAsync(IReadOnlyList<IdentityJob> jobs, CancellationToken ct)
    {
        // Load hints for every job first (one DB call per job, in parallel would be ideal
        // but claim repo may not support concurrent reads — keep sequential to be safe).
        var jobHints = await BuildFileHintsBatchAsync(jobs.Select(job => job.EntityId).ToList(), ct)
            .ConfigureAwait(false);

        // Group by normalised artist+album key.
        var groups = jobs
            .GroupBy(j => BuildAlbumKey(jobHints[j.EntityId]))
            .ToList();

        _logger.LogInformation(
            "Music: grouping {TrackCount} track(s) into {GroupCount} album group(s) for retail match",
            jobs.Count, groups.Count);

        var groupTasks = groups
            .Select(group => _concurrency.RunAsync(
                EnrichmentWorkKind.RetailProvider,
                token => ProcessMusicGroupWithFallbackAsync(group.Key, group.ToList(), jobHints, token),
                ct))
            .ToList();
        await Task.WhenAll(groupTasks).ConfigureAwait(false);
    }

    private async Task ProcessMusicGroupWithFallbackAsync(
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
            await ProcessMusicGroupAsync(groupJobs, jobHints, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"Music album identification exceeded the configured {GetExecutionSnapshot().Hydration.Stage1TimeoutSeconds}-second timeout.");
            foreach (var job in groupJobs)
            {
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
                "Music: album group '{Key}' failed; falling back to per-track search for {Count} job(s)",
                groupKey, groupJobs.Count);

            foreach (var job in groupJobs)
            {
                try { await ProcessJobAsync(job, ct).ConfigureAwait(false); }
                catch (Exception innerEx) when (innerEx is not OperationCanceledException)
                {
                    _logger.LogError(innerEx,
                        "RetailMatchWorker per-track fallback failed for {EntityId}", job.EntityId);
                    await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
                        _jobRepo,
                        job,
                        IdentityJobState.Queued,
                        innerEx,
                        GetExecutionSnapshot().Hydration,
                        ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Processes a group of Music jobs (all from the same album) with a track-first
    /// strategy: search Apple by the representative track to discover the correct
    /// collectionId, fetch the full album, then distribute tracks to all queued jobs.
    /// Falls back to album-name search, then per-track individual search.
    /// </summary>
    private async Task ProcessMusicGroupAsync(
        IReadOnlyList<IdentityJob> groupJobs,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> jobHints,
        CancellationToken ct)
    {
        // Mark all jobs as searching.
        foreach (var job in groupJobs)
        {
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailSearching, ct: ct);
        }

        var hydrationConfig = GetExecutionSnapshot().Hydration;
        var retailAcceptThreshold = hydrationConfig.RetailAutoAcceptThreshold;
        var retailAmbiguousThreshold = hydrationConfig.RetailAmbiguousThreshold;

        var orderedGroupJobs = groupJobs
            .OrderBy(j => TryParseOrdinal(jobHints[j.EntityId].GetValueOrDefault(MetadataFieldConstants.TrackNumber), out var trackNumber)
                ? trackNumber
                : int.MaxValue)
            .ThenBy(j => jobHints[j.EntityId].GetValueOrDefault(MetadataFieldConstants.Title) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var representativeHints = jobHints[orderedGroupJobs[0].EntityId];
        var artist = GetMusicCreatorHint(representativeHints);
        var album = representativeHints.GetValueOrDefault(MetadataFieldConstants.Album);
        var title = representativeHints.GetValueOrDefault(MetadataFieldConstants.Title);
        var (lang, musicCountry, _) = GetConfiguredLocale();
        var country = musicCountry;

        // ── Step 1: Track-first — search by track name to discover the collectionId.
        // A track search returns the exact track + its collectionId, so even when the
        // album name is ambiguous (remastered editions, deluxe versions), the track
        // anchors us to the correct album.
        MediaEngine.Providers.Services.AppleTrackSearchMatch? trackSearchMatch = null;
        string? collectionId = null;
        var resolvedVia = "track search";

        var providerConfigs = GetExecutionSnapshot().Providers;
        var appleProvider = ProviderExecutionFilter.FindEnabledProvider(
            _providers,
            providerConfigs,
            "apple_api");

        if (appleProvider is null)
        {
            _logger.LogInformation(
                "Music: Apple provider is disabled or unavailable; falling back to generic retail matching for {TrackCount} queued track(s)",
                orderedGroupJobs.Count);

            foreach (var job in orderedGroupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "RetailMatchWorker generic music fallback failed for {EntityId}", job.EntityId);
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }

            return;
        }

        if (orderedGroupJobs.Count == 1)
        {
            _logger.LogInformation(
                "Music: searching Apple iTunes by track '{Title}' / '{Artist}' ({TrackCount} queued track(s))",
                title ?? "(unknown)", artist ?? "(unknown artist)", orderedGroupJobs.Count);

            trackSearchMatch = await _appleClient.SearchTrackAsync(artist, title, album, country, lang, ct);

            if (trackSearchMatch is { SingleTrackRelease: true, TitleExact: true, ArtistExact: true })
            {
                var singleJob = orderedGroupJobs[0];
                _logger.LogInformation(
                    "Music: exact single-track Apple hit '{Title}' by '{Artist}' resolved directly via track search for entity {EntityId}",
                    title ?? "(unknown)",
                    artist ?? "(unknown artist)",
                    singleJob.EntityId);

                await ApplyMusicTrackAsync(
                    singleJob,
                    jobHints[singleJob.EntityId],
                    [trackSearchMatch.Track],
                    trackSearchMatch.CollectionId,
                    appleProvider,
                    retailAcceptThreshold,
                    retailAmbiguousThreshold,
                    ct);
                return;
            }

            if (trackSearchMatch is not null
                && IsStrongMusicTrackAlbumAnchor(trackSearchMatch, album))
            {
                collectionId = trackSearchMatch.CollectionId;
            }
            else if (trackSearchMatch is not null)
            {
                _logger.LogInformation(
                    "Music: ignored Apple track search collectionId={CollectionId} for '{Title}' because album '{Album}' was not corroborated (score={AlbumScore:F2}, exact={AlbumExact})",
                    trackSearchMatch.CollectionId,
                    title ?? "(unknown)",
                    album ?? "(unknown album)",
                    trackSearchMatch.AlbumScore,
                    trackSearchMatch.AlbumExact);
            }
        }
        else
        {
            _logger.LogInformation(
                "Music: searching Apple iTunes for album '{Album}' by '{Artist}' using {TrackCount} queued track(s)",
                album ?? "(unknown album)", artist ?? "(unknown artist)", orderedGroupJobs.Count);

            var trackSearchEvidence = new List<MusicGroupTrackSearchEvidence>(orderedGroupJobs.Count);
            foreach (var job in orderedGroupJobs)
            {
                var currentHints = jobHints[job.EntityId];
                var currentTitle = currentHints.GetValueOrDefault(MetadataFieldConstants.Title);
                if (string.IsNullOrWhiteSpace(currentTitle))
                {
                    continue;
                }

                var match = await _appleClient.SearchTrackAsync(artist, currentTitle, album, country, lang, ct);
                if (match is null)
                {
                    continue;
                }

                trackSearchEvidence.Add(new MusicGroupTrackSearchEvidence(job.EntityId, currentTitle, match));
            }

            if (trackSearchEvidence.Count > 0)
            {
                var selection = SelectBestMusicGroupCollection(trackSearchEvidence);
                if (IsStrongMusicGroupCollectionSelection(selection, orderedGroupJobs.Count))
                {
                    collectionId = selection.CollectionId;
                    resolvedVia = "group track consensus";

                    _logger.LogInformation(
                        "Music: selected Apple collectionId={CollectionId} for '{Artist}' / '{Album}' from {SupportCount}/{TrackCount} queued track(s) (albumExact={AlbumExactCount}, score={Score:F2})",
                        selection.CollectionId,
                        artist ?? "(unknown artist)",
                        album ?? "(unknown album)",
                        selection.SupportCount,
                        orderedGroupJobs.Count,
                        selection.AlbumExactCount,
                        selection.TotalScore);
                }
                else
                {
                    _logger.LogInformation(
                        "Music: ignored weak Apple collectionId={CollectionId} for '{Artist}' / '{Album}' from {SupportCount}/{TrackCount} queued track(s); album manifest requires stronger evidence",
                        selection.CollectionId,
                        artist ?? "(unknown artist)",
                        album ?? "(unknown album)",
                        selection.SupportCount,
                        orderedGroupJobs.Count);
                }
            }
        }

        // ── Step 2: Fall back to album-name search if track search failed.
        if (collectionId is null && !string.IsNullOrWhiteSpace(album))
        {
            _logger.LogInformation(
                "Music: track search failed — falling back to album search for '{Album}' by '{Artist}'",
                album, artist ?? "(unknown)");
            collectionId = await _appleClient.SearchAlbumAsync(artist, album, country, lang, ct);
            resolvedVia = "album search";
        }

        if (collectionId is null)
        {
            _logger.LogInformation(
                "Music: no match for '{Title}' / '{Album}' by '{Artist}' on Apple iTunes — falling back to per-track individual search for {TrackCount} job(s)",
                title ?? "(no title)", album ?? "(no album)", artist ?? "(no artist)", orderedGroupJobs.Count);

            // Last resort: process each job individually via ConfigDrivenAdapter.
            foreach (var job in orderedGroupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "RetailMatchWorker per-track fallback failed for {EntityId}", job.EntityId);
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }
            return;
        }

        // ── Step 3: Fetch all tracks for the album via lookup?id={collectionId}&entity=song.
        var allTracks = await _appleClient.FetchAlbumTracksAsync(collectionId, country, lang, ct);

        if (allTracks.Count == 0)
        {
            _logger.LogInformation(
                "RetailMatchWorker: Apple album lookup returned no tracks for collectionId={CollectionId}",
                collectionId);

            foreach (var job in orderedGroupJobs)
            {
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
                }
            }
            return;
        }

        _logger.LogInformation(
            "Music: resolved album via {Strategy} — collectionId={CollectionId}, {TrackCount} tracks from API — distributing to {JobCount} queued track(s)",
            resolvedVia, collectionId, allTracks.Count, orderedGroupJobs.Count);

        var albumLineage = await _workRepo.GetLineageByAssetAsync(orderedGroupJobs[0].EntityId, ct)
            .ConfigureAwait(false);
        if (albumLineage is not null)
        {
            await PersistAppleAlbumManifestAsync(
                    albumLineage,
                    collectionId,
                    album,
                    artist,
                    appleProvider.ProviderId,
                    ct,
                    allTracks)
                .ConfigureAwait(false);
        }

        // ── Step 4: For each job, find the best-matching track and apply its claims.
        foreach (var job in orderedGroupJobs)
        {
            var hints = jobHints[job.EntityId];
            try
            {
                await ApplyMusicTrackAsync(
                    job, hints, allTracks, collectionId,
                    appleProvider, retailAcceptThreshold, retailAmbiguousThreshold, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "RetailMatchWorker: failed to apply track claims to job {JobId} (entity {EntityId})",
                    job.Id, job.EntityId);
                await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.Failed, ex.Message, ct);
            }
        }
    }

    /// <summary>
    /// Finds the best-matching track for the given job from the album track list,
    /// builds claims from that track's data, scores, and transitions the job.
    /// </summary>
    private async Task ApplyMusicTrackAsync(
        IdentityJob job,
        IReadOnlyDictionary<string, string> fileHints,
        IReadOnlyList<JsonNode> allTracks,
        string collectionId,
        IExternalMetadataProvider? appleProvider,
        double retailAcceptThreshold,
        double retailAmbiguousThreshold,
        CancellationToken ct)
    {
        var fileTitle = fileHints.GetValueOrDefault(MetadataFieldConstants.Title);
        var fileTrackNumber = fileHints.GetValueOrDefault(MetadataFieldConstants.TrackNumber);
        var fileDiscNumber = fileHints.GetValueOrDefault("disc_number");

        var hasFileDuration = TryGetDurationSeconds(fileHints, out var fileDurationSeconds);

        // Find the best-matching track by combining title, track number, and duration.
        // Track numbers are helpful corroboration, but they should not overpower a clearly
        // wrong title on compilation albums or alternate editions.
        JsonNode? bestTrack = null;
        double bestMatchScore = -1.0;

        foreach (var track in allTracks)
        {
            var trackNumNode = track["trackNumber"]?.GetValue<long?>() is { } tn ? tn.ToString() : null;
            var discNumNode = track["discNumber"]?.GetValue<long?>() is { } dn ? dn.ToString() : null;
            var trackName = track["trackName"]?.GetValue<string>();
            var candidateHasDurationForMatch = TryGetDurationSeconds(track["trackTimeMillis"]?.GetValue<long?>(), out var candidateDurationSecondsForMatch);
            var trackNumberMatchesForMatch = !string.IsNullOrWhiteSpace(fileTrackNumber)
                && !string.IsNullOrWhiteSpace(trackNumNode)
                && string.Equals(fileTrackNumber.Trim(), trackNumNode.Trim(), StringComparison.Ordinal);
            var discNumberMatchesForMatch = string.IsNullOrWhiteSpace(fileDiscNumber)
                || string.IsNullOrWhiteSpace(discNumNode)
                || string.Equals(fileDiscNumber.Trim(), discNumNode.Trim(), StringComparison.Ordinal);
            var durationCorroboratesForMatch = hasFileDuration
                && candidateHasDurationForMatch
                && DurationsCorroborate(fileDurationSeconds, candidateDurationSecondsForMatch);

            double matchScore = !string.IsNullOrWhiteSpace(fileTitle) && !string.IsNullOrWhiteSpace(trackName)
                ? RetailTextSimilarity.ComputeWordOverlap(fileTitle, trackName)
                : 0.0;

            if (trackNumberMatchesForMatch && discNumberMatchesForMatch)
            {
                matchScore += string.IsNullOrWhiteSpace(fileTitle) ? 0.70 : 0.25;
            }
            else if (!string.IsNullOrWhiteSpace(fileTrackNumber) && !string.IsNullOrWhiteSpace(trackNumNode))
            {
                matchScore -= discNumberMatchesForMatch ? 0.10 : 0.25;
            }

            if (durationCorroboratesForMatch)
            {
                matchScore += 0.15;
            }
            else if (trackNumberMatchesForMatch && !durationCorroboratesForMatch && hasFileDuration && candidateHasDurationForMatch)
            {
                matchScore -= 0.10;
            }

            matchScore = Math.Clamp(matchScore, 0.0, 1.0);

            if (matchScore > bestMatchScore)
            {
                bestMatchScore = matchScore;
                bestTrack = track;
            }
        }

        if (bestTrack is null || bestMatchScore < 0.30)
        {
            // No reasonable track match found — route to no-match.
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailNoMatch, ct: ct);
            await EnrichPeopleWithoutMediaMatchAsync(job.EntityId, ct).ConfigureAwait(false);
            await _outcomeFactory.CreateRetailFailedAsync(
                job.EntityId, job.MediaType, job.IngestionRunId, null, ct);
            var titleHint = fileHints.GetValueOrDefault(MetadataFieldConstants.Title) ?? "(unknown)";
            await _timeline.RecordRetailNoMatchAsync(job.EntityId, titleHint, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Music: no track match (score {Score:F2}) for '{Title}' in album collectionId={CollectionId} (entity {EntityId})",
                bestMatchScore, fileTitle ?? "(unknown)", collectionId, job.EntityId);
            return;
        }

        // Build claims from the matched track node.
        var claims = BuildMusicTrackClaims(bestTrack, collectionId);

        var candidateTitle = bestTrack["trackName"]?.GetValue<string>();
        var candidateAuthor = bestTrack["artistName"]?.GetValue<string>();
        var candidateYear = bestTrack["releaseDate"]?.GetValue<string>()?.Length >= 4
            ? bestTrack["releaseDate"]!.GetValue<string>()![..4]
            : null;
        var candidateTrackCount = bestTrack["trackCount"]?.GetValue<long?>();

        var retailScore = _retailScoring.ScoreCandidate(
            fileHints, candidateTitle, candidateAuthor, candidateYear, MediaType.Music,
            extendedMetadata: BuildCandidateExtendedMetadata(claims));
        var candidateTrackNumber = bestTrack["trackNumber"]?.GetValue<long?>()?.ToString();
        var trackNumberMatches = !string.IsNullOrWhiteSpace(fileTrackNumber)
            && !string.IsNullOrWhiteSpace(candidateTrackNumber)
            && string.Equals(fileTrackNumber.Trim(), candidateTrackNumber.Trim(), StringComparison.Ordinal);
        var hasCandidateDuration = TryGetDurationSeconds(bestTrack["trackTimeMillis"]?.GetValue<long?>(), out var candidateDurationSeconds);
        var durationCorroborates = hasFileDuration
            && hasCandidateDuration
            && DurationsCorroborate(fileDurationSeconds, candidateDurationSeconds);
        var fileAlbum = fileHints.GetValueOrDefault(MetadataFieldConstants.Album);
        var candidateAlbum = bestTrack["collectionName"]?.GetValue<string>();
        var albumCorroborates = !string.IsNullOrWhiteSpace(fileAlbum)
            && MusicAlbumIdentity.IsSameTrackList(fileAlbum, candidateAlbum);
        var yearCorroborates = retailScore.YearScore >= 0.80;
        var singleTrackRelease = candidateTrackCount == 1;
        var strongSingleTrackIdentity = singleTrackRelease
            && retailScore.TitleScore >= 0.95
            && retailScore.AuthorScore >= 0.85;
        var strongCanonicalTrackIdentity = retailScore.TitleScore >= 0.95
            && retailScore.AuthorScore >= 0.85
            && (albumCorroborates || yearCorroborates);
        var decision = _candidateScorer.EvaluateDecision(
            fileHints,
            candidateTitle,
            candidateAuthor,
            candidateYear,
            retailScore,
            retailScore.CompositeScore,
            retailAcceptThreshold,
            retailAmbiguousThreshold,
            "grouped_music",
            autoAcceptCapReasons: trackNumberMatches
                || durationCorroborates
                || strongSingleTrackIdentity
                || strongCanonicalTrackIdentity
                ? null
                : ["requires_track_number_or_duration_corroboration"]);

        var providerId = appleProvider?.ProviderId ?? Guid.Empty;

        var bridgeIdsJson = BuildBridgeIdsJson(claims);

        var candidate = new RetailMatchCandidate
        {
            JobId = job.Id,
            ProviderId = providerId,
            ProviderName = "apple_api",
            ProviderItemId = bestTrack["trackId"]?.GetValue<long?>()?.ToString(),
            Rank = 1,
            Title = candidateTitle ?? "(unknown)",
            Creator = candidateAuthor,
            Year = candidateYear,
            ScoreTotal = decision.FinalScore,
            ScoreBreakdownJson = _candidateScorer.BuildScoreBreakdownJson(
                retailScore,
                decision,
                "grouped_music",
                new Dictionary<string, object?>
                {
                    ["track_match_score"] = Math.Round(bestMatchScore, 4),
                    ["track_number_matches"] = trackNumberMatches,
                    ["duration_corroborates"] = durationCorroborates,
                    ["album_corroborates"] = albumCorroborates,
                    ["year_corroborates"] = yearCorroborates,
                    ["single_track_release"] = singleTrackRelease,
                    ["strong_single_track_identity"] = strongSingleTrackIdentity,
                    ["strong_canonical_track_identity"] = strongCanonicalTrackIdentity,
                    ["file_duration_seconds"] = hasFileDuration ? fileDurationSeconds : null,
                    ["candidate_duration_seconds"] = hasCandidateDuration ? candidateDurationSeconds : null,
                }),
            BridgeIdsJson = bridgeIdsJson,
            ImageUrl = RetailRequestBuilder.BuildAppleCoverUrl(bestTrack["artworkUrl100"]?.GetValue<string>()),
            Outcome = decision.Outcome,
        };

        await _candidateRepo.InsertBatchAsync([candidate], ct);

        if (decision.Outcome != "Rejected")
        {
            // Phase 3c: fetch lineage so parent-scope claims (album, artist,
            // year, cover) mirror onto the album Work in addition to the track.
            WorkLineage? lineage = null;
            try { lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Phase 3c: lineage lookup failed for music track {EntityId} — parent mirror skipped",
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
        }

        if (decision.Outcome == "AutoAccepted")
        {
            await _jobRepo.SetSelectedCandidateAsync(job.Id, candidate.Id, ct);
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct);
            await _timeline.RecordRetailMatchedAsync(
                job.EntityId, "apple_api", 1, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Music: track '{FileTitle}' → '{MatchedTitle}' from Apple iTunes album lookup (score {Score:F2}) [entity {EntityId}]",
                fileTitle ?? "(unknown)", candidateTitle, decision.FinalScore, job.EntityId);

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
                    "Music: post-retail organization failed for entity {EntityId} — pipeline continues",
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
                job.EntityId, "apple_api", 1, job.IngestionRunId, ct);

            _logger.LogInformation(
                "Music: track '{FileTitle}' → '{MatchedTitle}' ambiguous on Apple iTunes (score {Score:F2}, needs review) [entity {EntityId}]",
                fileTitle ?? "(unknown)", candidateTitle, decision.FinalScore, job.EntityId);
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
                "Music: track '{FileTitle}' rejected — score {Score:F2} below thresholds [entity {EntityId}]",
                fileTitle ?? "(unknown)", decision.FinalScore, job.EntityId);
        }
    }

    /// <summary>
    /// Builds <see cref="ProviderClaim"/> list from a raw Apple track JSON node.
    /// Maps the same fields as the <c>apple_api</c> Music field_mappings in config.
    /// </summary>
    private static IReadOnlyList<ProviderClaim> BuildMusicTrackClaims(JsonNode track, string collectionId)
    {
        var claims = new List<ProviderClaim>();

        void Add(string key, string? value, double confidence)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new ProviderClaim(key, value, confidence));
            }
        }

        Add(MetadataFieldConstants.Title, track["trackName"]?.GetValue<string>(), 0.80);
        Add(MetadataFieldConstants.Author, track["artistName"]?.GetValue<string>(), 0.80);
        Add(MetadataFieldConstants.Artist, track["artistName"]?.GetValue<string>(), 0.80);
        Add(MetadataFieldConstants.Album, track["collectionName"]?.GetValue<string>(), 0.85);
        Add(MetadataFieldConstants.Genre, track["primaryGenreName"]?.GetValue<string>(), 0.70);

        var releaseDate = track["releaseDate"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(releaseDate) && releaseDate.Length >= 4)
        {
            Add(MetadataFieldConstants.Year, releaseDate[..4], 0.80);
        }

        Add(MetadataFieldConstants.TrackNumber,
            track["trackNumber"]?.GetValue<long?>()?.ToString(), 0.90);
        Add("disc_number",
            track["discNumber"]?.GetValue<long?>()?.ToString(), 0.90);
        Add("disc_count",
            track["discCount"]?.GetValue<long?>()?.ToString(), 0.90);
        Add("track_count",
            track["trackCount"]?.GetValue<long?>()?.ToString(), 0.90);
        var durationMillis = track["trackTimeMillis"]?.GetValue<long?>();
        if (durationMillis is > 0)
        {
            Add(
                MetadataFieldConstants.DurationField,
                (durationMillis.Value / 60000d).ToString("0.###", CultureInfo.InvariantCulture),
                0.90);
        }

        // Bridge IDs.
        Add(BridgeIdKeys.AppleMusicId,
            track["trackId"]?.GetValue<long?>()?.ToString(), 0.95);
        Add(BridgeIdKeys.AppleMusicCollectionId,
            collectionId, 0.95);
        Add(BridgeIdKeys.AppleArtistId,
            track["artistId"]?.GetValue<long?>()?.ToString(), 0.90);

        // Cover art — scale up from 100px to full-res.
        var artworkUrl = RetailRequestBuilder.BuildAppleCoverUrl(track["artworkUrl100"]?.GetValue<string>());
        if (!string.IsNullOrWhiteSpace(artworkUrl))
        {
            claims.Add(new ProviderClaim(MetadataFieldConstants.CoverUrl, artworkUrl, 0.90));
        }

        return claims;
    }

    // ── TV group processing ──────────────────────────────────────────────────

    /// <summary>
    /// Groups TV jobs by show+season and processes each group with a single
    /// TMDB show search + season episode list call instead of per-episode calls.
    /// </summary>
}
