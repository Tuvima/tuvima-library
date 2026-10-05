using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Workers;

public sealed partial class WikidataBridgeWorker
{
    /// <summary>
    /// Runs an entity-scoped, read-only preview through the exact Stage 2 resolver used by
    /// automatic ingestion. The editor may override evidence, but it does not use the
    /// broader standalone Wikidata search path or a separate acceptance policy.
    /// </summary>
    internal async Task<SearchUniverseResult> PreviewCandidatesAsync(
        Guid entityId,
        string requestedMediaType,
        string? queryOverride,
        IReadOnlyDictionary<string, string>? evidenceOverrides,
        int maxCandidates,
        CancellationToken ct = default)
    {
        var reconAdapter = _providers.OfType<ReconciliationAdapter>().FirstOrDefault();
        if (reconAdapter is null)
        {
            return new SearchUniverseResult([], queryOverride ?? string.Empty, requestedMediaType);
        }

        var persistedJob = await _jobRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        var job = new IdentityJob
        {
            Id = persistedJob?.Id ?? Guid.NewGuid(),
            EntityId = entityId,
            EntityType = persistedJob?.EntityType ?? "MediaAsset",
            MediaType = string.IsNullOrWhiteSpace(requestedMediaType)
                ? persistedJob?.MediaType ?? MediaType.Unknown.ToString()
                : requestedMediaType,
            // A preview represents the Stage 2 hand-off after a retail candidate exists.
            // This preserves the automatic constrained-text policy even for a completed job.
            State = IdentityJobState.RetailMatched.ToString(),
            SelectedCandidateId = persistedJob?.SelectedCandidateId,
        };

        if (!Enum.TryParse<MediaType>(job.MediaType, true, out var mediaType))
        {
            mediaType = MediaType.Unknown;
        }

        WorkLineage? lineage = null;
        if (string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
        {
            lineage = await _workRepo.GetLineageByAssetAsync(entityId, ct).ConfigureAwait(false);
        }

        var contextEntityIds = new HashSet<Guid> { entityId };
        if (lineage is not null)
        {
            contextEntityIds.Add(lineage.TargetForSelfScope);
            contextEntityIds.Add(lineage.TargetForParentScope);
        }

        var ids = contextEntityIds.ToList();
        var allBridgeIds = await _bridgeIdRepo.GetByEntitiesAsync(ids, ct).ConfigureAwait(false);
        var allCanonicals = await _canonicalRepo.GetByEntitiesAsync(ids, ct).ConfigureAwait(false);
        var bridgeIds = CollectScopedBridgeIdsForResolution(entityId, mediaType, lineage, allBridgeIds);
        var canonicals = CollectScopedCanonicalsForResolution(entityId, lineage, allCanonicals);
        bridgeIds = MergeCanonicalBridgeIdsForResolution(entityId, mediaType, lineage, bridgeIds, canonicals);
        var resolutionScope = ResolveBridgeResolutionScope(mediaType);
        bridgeIds = OrderBridgeIdsForResolution(mediaType, resolutionScope, bridgeIds);

        var bridgeDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wikidataProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in bridgeIds)
        {
            bridgeDict.TryAdd(bridge.IdType, bridge.IdValue);
            AddWikidataProperty(mediaType, bridge.IdType, wikidataProps);
        }

        if (evidenceOverrides is { Count: > 0 })
        {
            foreach (var (key, value) in evidenceOverrides)
            {
                if (string.IsNullOrWhiteSpace(value) || _bridgeIdHelper.GetPCode(key) is null)
                {
                    continue;
                }

                bridgeDict[key] = value.Trim();
                AddWikidataProperty(mediaType, key, wikidataProps);
            }
        }

        var hints = BuildLookupHints(mediaType, canonicals, lineage?.TargetForParentScope);
        var title = FirstOverride(evidenceOverrides, MetadataFieldConstants.Title, MetadataFieldConstants.ShowName)
            ?? hints.TitleHint;
        var author = FirstOverride(evidenceOverrides, MetadataFieldConstants.Author) ?? hints.AuthorHint;
        var year = FirstOverride(evidenceOverrides, MetadataFieldConstants.Year) ?? hints.YearHint;
        var album = FirstOverride(evidenceOverrides, MetadataFieldConstants.Album) ?? hints.AlbumHint;
        var artist = FirstOverride(evidenceOverrides, MetadataFieldConstants.Artist) ?? hints.ArtistHint;
        var series = FirstOverride(evidenceOverrides, MetadataFieldConstants.Series, MetadataFieldConstants.ShowName)
            ?? hints.SeriesHint;
        var language = FirstOverride(evidenceOverrides, MetadataFieldConstants.Language) ?? hints.LanguageHint;

        if (!string.IsNullOrWhiteSpace(queryOverride))
        {
            if (mediaType == MediaType.Music && !string.IsNullOrWhiteSpace(album))
            {
                album = queryOverride.Trim();
            }
            else
            {
                title = queryOverride.Trim();
            }
        }

        var ctx = new JobContext(
            job,
            mediaType,
            bridgeIds,
            bridgeDict,
            wikidataProps,
            title,
            author,
            year,
            album,
            artist,
            series,
            language,
            hints.SeasonNumber,
            hints.EpisodeNumber,
            hints.IssueNumber);

        var result = await reconAdapter.ResolveAsync(
            BuildResolveRequest(ctx, $"preview:{entityId:N}"), ct).ConfigureAwait(false);

        var ranked = result.RankedBridgeCandidates
            .GroupBy(candidate => candidate.Qid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(Math.Clamp(maxCandidates, 1, 10))
            .ToList();

        if (result.Found && !string.IsNullOrWhiteSpace(result.WorkQid ?? result.Qid)
            && ranked.All(candidate => !string.Equals(
                candidate.Qid, result.WorkQid ?? result.Qid, StringComparison.OrdinalIgnoreCase)))
        {
            ranked.Insert(0, new Tuvima.Wikidata.BridgeCandidate
            {
                Qid = result.WorkQid ?? result.Qid!,
                Confidence = 1.0,
                CollectedBridgeIds = result.CollectedBridgeIds,
            });
        }

        var localEvidence = BuildPreviewEvidence(title, author, year, evidenceOverrides);
        var candidates = new List<UniverseCandidate>();
        foreach (var candidate in ranked.Take(Math.Clamp(maxCandidates, 1, 10)))
        {
            // Fetch the candidate QID itself. When Stage 2 selected an edition and rolled it
            // up to a work, result.Claims can still describe the edition transition rather
            // than the canonical work shown by the editor.
            var claims = await reconAdapter.FetchAsync(new ProviderLookupRequest
            {
                EntityId = entityId,
                EntityType = EntityType.MediaAsset,
                MediaType = mediaType,
                PreResolvedQid = candidate.Qid,
                Title = title,
                Author = author,
                Year = year,
                FileLanguage = language,
            }, ct).ConfigureAwait(false);

            var fields = claims
                .Where(claim => !string.IsNullOrWhiteSpace(claim.Key) && !string.IsNullOrWhiteSpace(claim.Value))
                .GroupBy(claim => claim.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            var candidateTitle = fields.GetValueOrDefault(MetadataFieldConstants.Title)
                ?? candidate.Label
                ?? candidate.Qid;
            var candidateAuthor = fields.GetValueOrDefault(MetadataFieldConstants.Author)
                ?? fields.GetValueOrDefault(MetadataFieldConstants.Artist)
                ?? fields.GetValueOrDefault(MetadataFieldConstants.Director);
            var candidateYear = fields.GetValueOrDefault(MetadataFieldConstants.Year)
                ?? fields.GetValueOrDefault("original_release_year")
                ?? fields.GetValueOrDefault("edition_release_year");

            var universe = new UniverseCandidate
            {
                Qid = candidate.Qid,
                Label = candidateTitle,
                Description = fields.GetValueOrDefault(MetadataFieldConstants.Description) ?? candidate.Description,
                InstanceOf = fields.GetValueOrDefault("instance_of")
                    ?? candidate.EntityTypes.FirstOrDefault(),
                Year = candidateYear,
                Author = candidateAuthor,
                Director = fields.GetValueOrDefault(MetadataFieldConstants.Director),
                ResolutionTier = candidate.MatchedBridgeIdType is null ? "title_search" : "bridge",
                Confidence = candidate.Confidence,
                BridgeIds = candidate.CollectedBridgeIds,
                MediaType = mediaType.ToString(),
                MediaTypeMetadata = fields,
            };

            if (_retailMatchScoring is not null && !string.IsNullOrWhiteSpace(title))
            {
                universe.MatchScores = ToPreviewFieldMatches(
                    _retailMatchScoring.ScoreCandidate(
                        localEvidence, candidateTitle, candidateAuthor, candidateYear, mediaType,
                        extendedMetadata: new CandidateExtendedMetadata { Signals = fields }));
            }

            candidates.Add(universe);
        }

        return new SearchUniverseResult(
            candidates.OrderByDescending(candidate => candidate.Confidence).ToList(),
            queryOverride ?? title ?? string.Empty,
            mediaType.ToString());
    }

    private void AddWikidataProperty(
        MediaType mediaType,
        string bridgeIdType,
        IDictionary<string, string> properties)
    {
        var pCode = _bridgeIdHelper.GetPCode(bridgeIdType);
        if (pCode is null)
        {
            return;
        }

        if (mediaType == MediaType.TV
            && string.Equals(bridgeIdType, BridgeIdKeys.TmdbId, StringComparison.OrdinalIgnoreCase))
        {
            pCode = "P4983";
        }

        properties[bridgeIdType] = pCode;
    }

    private static string? FirstOverride(
        IReadOnlyDictionary<string, string>? overrides,
        params string[] keys)
    {
        if (overrides is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (overrides.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static Dictionary<string, string> BuildPreviewEvidence(
        string? title,
        string? author,
        string? year,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var evidence = overrides is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(title))
        {
            evidence[MetadataFieldConstants.Title] = title;
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            evidence[MetadataFieldConstants.Author] = author;
        }

        if (!string.IsNullOrWhiteSpace(year))
        {
            evidence[MetadataFieldConstants.Year] = year;
        }

        return evidence;
    }

    private static FieldMatchResult ToPreviewFieldMatches(FieldMatchScores scores) => new()
    {
        TitleScore = scores.TitleScore,
        AuthorScore = scores.AuthorScore,
        YearScore = scores.YearScore,
        FormatScore = scores.FormatScore,
        CompositeScore = scores.CompositeScore,
        CoverScore = scores.CoverArtScore,
        FieldScores = scores.FieldScores,
        TitleVerdict = ToPreviewVerdict(scores.TitleScore),
        AuthorVerdict = scores.AuthorScore < 0 ? FieldMatchVerdict.NotAvailable : ToPreviewVerdict(scores.AuthorScore),
        YearVerdict = scores.YearScore < 0 ? FieldMatchVerdict.NotAvailable : ToPreviewVerdict(scores.YearScore),
        FormatVerdict = ToPreviewVerdict(scores.FormatScore),
        CoverVerdict = scores.CoverArtScore < 0 ? FieldMatchVerdict.NotAvailable : ToPreviewVerdict(scores.CoverArtScore),
    };

    private static FieldMatchVerdict ToPreviewVerdict(double score) => score switch
    {
        >= 0.95 => FieldMatchVerdict.Exact,
        >= 0.70 => FieldMatchVerdict.Close,
        _ => FieldMatchVerdict.Mismatch,
    };
}
