using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Workers;

public sealed partial class WikidataBridgeWorker
{
    internal async Task ProcessJobAsync(IdentityJob job, CancellationToken ct)
    {
        var reconAdapter = _providers
            .OfType<ReconciliationAdapter>()
            .FirstOrDefault();

        if (reconAdapter is null)
        {
            _logger.LogWarning("No ReconciliationAdapter available — cannot resolve bridge IDs");
            await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.QidNoMatch,
                "No reconciliation adapter configured", ct);
            await TryOrganizeRetainedRetailIdentityAsync(job, ct);
            return;
        }

        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.BridgeSearching, ct: ct);

        if (!Enum.TryParse<MediaType>(job.MediaType, true, out var mediaType))
        {
            mediaType = MediaType.Unknown;
        }

        WorkLineage? lineage = null;
        if (string.Equals(job.EntityType, "MediaAsset", StringComparison.OrdinalIgnoreCase))
        {
            try { lineage = await _workRepo.GetLineageByAssetAsync(job.EntityId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "Wikidata context: lineage lookup failed for asset {EntityId}; using asset-scoped bridge IDs only",
                    job.EntityId);
            }
        }

        // Load context for the single job. Include work-level IDs because
        // retail routes bridge IDs to the asset's own Work or parent Work.
        var contextEntityIds = new HashSet<Guid> { job.EntityId };
        if (lineage is not null)
        {
            contextEntityIds.Add(lineage.TargetForSelfScope);
            contextEntityIds.Add(lineage.TargetForParentScope);
        }

        var allBridgeIds = await _bridgeIdRepo.GetByEntitiesAsync(contextEntityIds.ToList(), ct);
        var allCanonicals = await _canonicalRepo.GetByEntitiesAsync(contextEntityIds.ToList(), ct);
        var bridgeIds = CollectScopedBridgeIdsForResolution(
            job.EntityId,
            mediaType,
            lineage,
            allBridgeIds);
        var canonicals = CollectScopedCanonicalsForResolution(
            job.EntityId,
            lineage,
            allCanonicals);
        bridgeIds = MergeCanonicalBridgeIdsForResolution(
            job.EntityId,
            mediaType,
            lineage,
            bridgeIds,
            canonicals);
        var resolutionScope = ResolveBridgeResolutionScope(mediaType);
        bridgeIds = OrderBridgeIdsForResolution(mediaType, resolutionScope, bridgeIds);

        var bridgeDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wikidataProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bridge in bridgeIds)
        {
            bridgeDict.TryAdd(bridge.IdType, bridge.IdValue);
            var pCode = _bridgeIdHelper.GetPCode(bridge.IdType);
            if (pCode is not null)
            {
                if (string.Equals(bridge.IdType, BridgeIdKeys.TmdbId, StringComparison.OrdinalIgnoreCase)
                    && mediaType == MediaType.TV)
                {
                    pCode = "P4983";
                }

                wikidataProps.TryAdd(bridge.IdType, pCode);
            }
        }

        var (
            titleHint,
            authorHint,
            yearHint,
            albumHint,
            artistHint,
            seriesHint,
            languageHint,
            seasonNumber,
            episodeNumber,
            issueNumber) = BuildLookupHints(
                mediaType,
                canonicals,
                lineage?.TargetForParentScope);

        var ctx = new JobContext(
            Job: job,
            MediaType: mediaType,
            BridgeIds: bridgeIds,
            BridgeDict: bridgeDict,
            WikidataProps: wikidataProps,
            TitleHint: titleHint,
            AuthorHint: authorHint,
            YearHint: yearHint,
            AlbumHint: albumHint,
            ArtistHint: artistHint,
            SeriesHint: seriesHint,
            LanguageHint: languageHint,
            SeasonNumber: seasonNumber,
            EpisodeNumber: episodeNumber,
            IssueNumber: issueNumber);

        // Resolve QID for this single job via the unified facade.
        try
        {
            var result = await reconAdapter.ResolveAsync(
                BuildResolveRequest(ctx, job.Id.ToString()), ct);

            if (result.Found)
            {
                ctx.ResolvedQid = result.WorkQid ?? result.Qid;
                ctx.AdditionalClaims.AddRange(result.Claims);
                ctx.CollectedBridgeIds = result.CollectedBridgeIds;
                ctx.PrimaryBridgeIdType = result.PrimaryBridgeIdType;
                ctx.MatchedBy = result.MatchedBy switch
                {
                    ResolveStrategy.MusicAlbum => "music_album",
                    ResolveStrategy.BridgeId => "bridge_id",
                    ResolveStrategy.TextSearch => "retail_text",
                    _ => null,
                };

                // Persist the resolution method as a canonical value (mirrors batch path).
                if (ctx.MatchedBy is not null)
                {
                    var canonicalMethod = ResolveQidResolutionMethod(ctx.MediaType, ctx.MatchedBy);
                    ctx.AdditionalClaims.Add(new ProviderClaim(
                        MetadataFieldConstants.QidResolutionMethod, canonicalMethod, 1.0));
                }

                MarkComicTextResolvedQidAsSeriesScope(ctx);
                MarkContainerRollupQidScope(ctx);

                // Album rollups must emit a QID claim so lineage-aware scoring can
                // persist the resolved identity on the album Work.
                if (result.MatchedBy == ResolveStrategy.MusicAlbum
                    && !string.IsNullOrWhiteSpace(ctx.ResolvedQid)
                    && !ctx.AdditionalClaims.Any(c => string.Equals(
                        c.Key, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.AdditionalClaims.Add(new ProviderClaim(
                        BridgeIdKeys.WikidataQid, ctx.ResolvedQid, 0.95));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ResolveAsync failed for entity {EntityId}", job.EntityId);
        }

        await TryResolveComicSeriesRollupAsync(ctx, reconAdapter, ct).ConfigureAwait(false);
        await TryResolveSiblingVariantQidAsync(ctx, lineage, ct).ConfigureAwait(false);

        var allCandidates = new List<WikidataBridgeCandidate>();
        await FinaliseJobAsync(ctx, reconAdapter, allCandidates, ct);

        if (allCandidates.Count > 0)
        {
            await _candidateRepo.InsertBatchAsync(allCandidates, ct);
        }
    }

    internal static (
        string? TitleHint,
        string? AuthorHint,
        string? YearHint,
        string? AlbumHint,
        string? ArtistHint,
        string? SeriesHint,
        string? LanguageHint,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? IssueNumber) BuildLookupHints(
        MediaType mediaType,
        IReadOnlyList<CanonicalValue> canonicals,
        Guid? parentScopeEntityId = null)
    {
        static string? GetCanonical(IReadOnlyList<CanonicalValue> values, string key)
        {
            var value = values.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
            return value is null ? null : TextEncodingRepair.RepairMojibake(value);
        }

        static string? GetCanonicalForEntity(IReadOnlyList<CanonicalValue> values, Guid entityId, string key)
        {
            var value = values.FirstOrDefault(c =>
                    c.EntityId == entityId
                    && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            return value is null ? null : TextEncodingRepair.RepairMojibake(value);
        }

        static string? FirstValue(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        var titleHint = GetCanonical(canonicals, MetadataFieldConstants.Title);
        var authorHint = GetCanonical(canonicals, MetadataFieldConstants.Author);
        var yearHint = GetCanonical(canonicals, MetadataFieldConstants.Year);
        var languageHint = GetCanonical(canonicals, MetadataFieldConstants.Language);
        string? albumHint = null;
        string? artistHint = null;
        string? seriesHint = null;
        int? seasonNumber = null;
        int? episodeNumber = null;
        string? issueNumber = null;

        if (mediaType is MediaType.Books or MediaType.Audiobooks)
        {
            authorHint ??= GetCanonical(canonicals, MetadataFieldConstants.Artist);
            seriesHint = GetCanonical(canonicals, MetadataFieldConstants.Series)
                ?? GetCanonical(canonicals, MetadataFieldConstants.Album);
        }
        else if (mediaType == MediaType.TV)
        {
            titleHint = GetCanonical(canonicals, MetadataFieldConstants.ShowName)
                ?? GetCanonical(canonicals, MetadataFieldConstants.Series)
                ?? titleHint;
            seasonNumber = TryParsePositiveOrdinal(GetCanonical(canonicals, MetadataFieldConstants.SeasonNumber));
            episodeNumber = TryParsePositiveOrdinal(GetCanonical(canonicals, MetadataFieldConstants.EpisodeNumber));
        }
        else if (mediaType == MediaType.Comics)
        {
            var parentTitle = parentScopeEntityId.HasValue
                ? GetCanonicalForEntity(canonicals, parentScopeEntityId.Value, MetadataFieldConstants.Title)
                : null;
            seriesHint = GetCanonical(canonicals, MetadataFieldConstants.Series)
                ?? parentTitle;
            issueNumber = FirstValue(
                GetCanonical(canonicals, "issue_number"),
                GetCanonical(canonicals, "issue"),
                GetCanonical(canonicals, MetadataFieldConstants.SeriesPosition));
            if (!string.IsNullOrWhiteSpace(seriesHint))
            {
                titleHint = seriesHint;
            }

            authorHint ??= GetCanonical(canonicals, "writer")
                ?? GetCanonical(canonicals, MetadataFieldConstants.Illustrator);
        }
        else if (mediaType == MediaType.Music)
        {
            albumHint = GetCanonical(canonicals, MetadataFieldConstants.Album);
            artistHint = GetCanonical(canonicals, MetadataFieldConstants.Artist)
                ?? GetCanonical(canonicals, "album_artist")
                ?? GetCanonical(canonicals, MetadataFieldConstants.Composer)
                ?? authorHint;
            authorHint ??= artistHint;
        }

        return (titleHint, authorHint, yearHint, albumHint, artistHint, seriesHint, languageHint, seasonNumber, episodeNumber, issueNumber);
    }

    internal static string? BuildBridgeYearHint(MediaType mediaType, string? seriesHint, string? yearHint)
        => mediaType == MediaType.Comics && !string.IsNullOrWhiteSpace(seriesHint)
            ? null
            : yearHint;

    private WikidataResolveRequest BuildResolveRequest(JobContext ctx, string correlationKey) => new()
    {
        CorrelationKey = correlationKey,
        MediaType = ctx.MediaType,
        ResolutionScope = ResolveBridgeResolutionScope(ctx.MediaType),
        Strategy = ResolveStrategy.Auto,
        BridgeIds = ctx.BridgeDict,
        WikidataProperties = ctx.WikidataProps,
        IsEditionAware = ctx.MediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Music,
        AllowConstrainedTextFallback = ShouldAllowConstrainedTextFallback(ctx),
        AlbumTitle = ctx.AlbumHint,
        Artist = ctx.ArtistHint,
        Title = ctx.TitleHint,
        Author = ctx.AuthorHint,
        Year = BuildBridgeYearHint(ctx.MediaType, ctx.SeriesHint, ctx.YearHint),
        FileLanguage = ctx.LanguageHint,
        SeriesTitle = ctx.SeriesHint,
        SeasonNumber = ctx.MediaType == MediaType.TV ? null : ctx.SeasonNumber,
        EpisodeNumber = ctx.MediaType == MediaType.TV ? null : ctx.EpisodeNumber,
        IssueNumber = ctx.IssueNumber,
    };

    private bool ShouldAllowConstrainedTextFallback(JobContext ctx)
    {
        if (!string.Equals(ctx.Job.State, IdentityJobState.RetailMatched.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ctx.TitleHint))
        {
            return false;
        }

        var policy = LoadBridgeResolutionScope(ResolveBridgeResolutionScope(ctx.MediaType));
        if (policy?.AllowConstrainedTextFallback != true)
        {
            return false;
        }

        return ctx.MediaType switch
        {
            MediaType.Books or MediaType.Audiobooks =>
                !string.IsNullOrWhiteSpace(ctx.AuthorHint)
                || !string.IsNullOrWhiteSpace(ctx.SeriesHint),
            MediaType.Comics => !string.IsNullOrWhiteSpace(ctx.SeriesHint),
            _ => false,
        };
    }

    private static int? TryParsePositiveOrdinal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        var digits = new string(trimmed
            .SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(char.IsDigit)
            .ToArray());

        return int.TryParse(digits, out parsed) && parsed >= 0
            ? parsed
            : null;
    }

    internal static IReadOnlyList<BridgeIdEntry> CollectScopedBridgeIdsForResolution(
        Guid jobEntityId,
        MediaType mediaType,
        WorkLineage? lineage,
        IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>> allBridgeIds)
    {
        var entries = new List<BridgeIdEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddEntries(Guid entityId, Func<string, bool> include)
        {
            if (!allBridgeIds.TryGetValue(entityId, out var entityEntries))
            {
                return;
            }

            foreach (var entry in entityEntries)
            {
                if (IsWikidataEvidence(entry.ProviderId)
                    || string.IsNullOrWhiteSpace(entry.IdType)
                    || string.IsNullOrWhiteSpace(entry.IdValue)
                    || !include(entry.IdType))
                {
                    continue;
                }

                if (seen.Add($"{entry.IdType}\u001f{entry.IdValue}"))
                {
                    entries.Add(entry);
                }
            }
        }

        // Include legacy/current asset-scoped rows unfiltered so in-flight
        // batches can still resolve after a worker restart.
        AddEntries(jobEntityId, _ => true);

        if (lineage is null)
        {
            return entries;
        }

        var selfId = lineage.TargetForSelfScope;
        var parentId = lineage.TargetForParentScope;

        if (selfId == parentId)
        {
            AddEntries(selfId, _ => true);
            return entries;
        }

        AddEntries(selfId, key => !ClaimScopeCatalog.IsParentScoped(key, mediaType));
        AddEntries(parentId, key => ClaimScopeCatalog.IsParentScoped(key, mediaType));
        return entries;
    }

    internal static IReadOnlyList<BridgeIdEntry> MergeCanonicalBridgeIdsForResolution(
        Guid jobEntityId,
        MediaType mediaType,
        WorkLineage? lineage,
        IReadOnlyList<BridgeIdEntry> bridgeIds,
        IReadOnlyList<CanonicalValue> canonicals)
    {
        var entries = bridgeIds.ToList();
        var seen = new HashSet<string>(
            entries.Select(entry => $"{entry.IdType}\u001f{entry.IdValue}"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var canonical in canonicals)
        {
            if (canonical.WinningProviderId == WellKnownProviders.Wikidata
                || string.IsNullOrWhiteSpace(canonical.Key)
                || string.IsNullOrWhiteSpace(canonical.Value)
                || !BridgeIdHelper.IsBridgeId(canonical.Key)
                || !BridgeIdIsInResolutionScope(canonical.EntityId, canonical.Key, jobEntityId, mediaType, lineage))
            {
                continue;
            }

            if (!seen.Add($"{canonical.Key}\u001f{canonical.Value}"))
            {
                continue;
            }

            entries.Add(new BridgeIdEntry
            {
                Id = Guid.NewGuid(),
                EntityId = canonical.EntityId,
                IdType = canonical.Key,
                IdValue = canonical.Value,
                ProviderId = canonical.WinningProviderId?.ToString(),
                CreatedAt = canonical.LastScoredAt,
            });
        }

        return entries;
    }

    // A previous reconciliation output cannot independently corroborate a new one.
    private static bool IsWikidataEvidence(string? provider) =>
        Guid.TryParse(provider, out var id) && id == WellKnownProviders.Wikidata
        || string.Equals(provider, "wikidata", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<BridgeIdEntry> OrderBridgeIdsForResolution(
        MediaType mediaType,
        string resolutionScope,
        IReadOnlyList<BridgeIdEntry> bridgeIds)
    {
        var configuredScope = LoadBridgeResolutionScope(resolutionScope);
        if (configuredScope is { TargetIds.Count: > 0 })
        {
            var targetIds = configuredScope.TargetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return bridgeIds
                .Where(entry => targetIds.Contains(entry.IdType))
                .Select((entry, index) => (Entry: entry, Index: index))
                .OrderBy(item => configuredScope.TargetIds.FindIndex(key =>
                    string.Equals(key, item.Entry.IdType, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(item => item.Index)
                .Select(item => item.Entry)
                .ToList();
        }

        if (bridgeIds.Count <= 1)
        {
            return bridgeIds;
        }

        var priority = BuildBridgeIdPriority(mediaType);
        if (priority.Count == 0)
        {
            return bridgeIds;
        }

        return bridgeIds
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderBy(item => priority.TryGetValue(item.Entry.IdType, out var rank) ? rank : int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Entry)
            .ToList();
    }

    private static string ResolveBridgeResolutionScope(MediaType mediaType) =>
        mediaType == MediaType.Music ? "MusicAlbum" : mediaType.ToString();

    internal static string ResolveQidResolutionMethod(MediaType mediaType, string matchedBy) =>
        mediaType switch
        {
            MediaType.TV => "tv_show_rollup",
            MediaType.Music => "music_album_rollup",
            _ when string.Equals(matchedBy, "bridge_id", StringComparison.OrdinalIgnoreCase) => "bridge",
            _ when string.Equals(matchedBy, "music_album", StringComparison.OrdinalIgnoreCase) => "album",
            _ => matchedBy,
        };

    private static void MarkContainerRollupQidScope(JobContext ctx)
    {
        var scope = ctx.MediaType switch
        {
            MediaType.TV => "show",
            MediaType.Music => "album",
            _ => null,
        };

        if (scope is not null)
        {
            AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.WikidataQidScope, scope, 1.0);
        }
    }

    private BridgeResolutionScopeConfiguration? LoadBridgeResolutionScope(string scope) =>
        _configLoader
            .LoadConfig<ReconciliationProviderConfig>("providers", "wikidata_reconciliation")
            ?.BridgeResolution
            .GetScope(scope);

    private Dictionary<string, int> BuildBridgeIdPriority(MediaType mediaType)
    {
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in GetExecutionSnapshot().Providers)
        {
            if (provider.PreferredBridgeIds is null)
            {
                continue;
            }

            var keys = ResolvePreferredBridgeIds(provider.PreferredBridgeIds, mediaType);
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key) && !priority.ContainsKey(key))
                {
                    priority[key] = priority.Count;
                }
            }
        }

        return priority;
    }

    private static IReadOnlyList<string> ResolvePreferredBridgeIds(
        IReadOnlyDictionary<string, List<string>> preferredBridgeIds,
        MediaType mediaType)
    {
        if (preferredBridgeIds.TryGetValue(mediaType.ToString(), out var direct))
        {
            return direct;
        }

        if (mediaType == MediaType.TV
            && preferredBridgeIds.TryGetValue("TV Shows", out var tvShows))
        {
            return tvShows;
        }

        return [];
    }

    private static bool BridgeIdIsInResolutionScope(
        Guid entityId,
        string key,
        Guid jobEntityId,
        MediaType mediaType,
        WorkLineage? lineage)
    {
        if (entityId == jobEntityId || lineage is null)
        {
            return true;
        }

        var selfId = lineage.TargetForSelfScope;
        var parentId = lineage.TargetForParentScope;

        if (selfId == parentId)
        {
            return entityId == selfId;
        }

        if (entityId == selfId)
        {
            return !ClaimScopeCatalog.IsParentScoped(key, mediaType);
        }

        if (entityId == parentId)
        {
            return ClaimScopeCatalog.IsParentScoped(key, mediaType);
        }

        return false;
    }

    private static IReadOnlyList<CanonicalValue> CollectScopedCanonicalsForResolution(
        Guid jobEntityId,
        WorkLineage? lineage,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> allCanonicals)
    {
        var values = new List<CanonicalValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddValues(Guid entityId)
        {
            if (!allCanonicals.TryGetValue(entityId, out var entityValues))
            {
                return;
            }

            foreach (var value in entityValues)
            {
                if (value.WinningProviderId == WellKnownProviders.Wikidata || string.IsNullOrWhiteSpace(value.Key))
                {
                    continue;
                }

                if (seen.Add($"{value.Key}\u001f{value.Value}"))
                {
                    values.Add(value);
                }
            }
        }

        AddValues(jobEntityId);

        if (lineage is not null)
        {
            AddValues(lineage.TargetForSelfScope);
            AddValues(lineage.TargetForParentScope);
        }

        return values;
    }

    private static string? BuildComicTitleHint(string seriesHint, string? titleHint)
    {
        if (string.IsNullOrWhiteSpace(titleHint))
        {
            return seriesHint;
        }

        if (TitleAlreadyIncludesSeries(titleHint, seriesHint))
        {
            return titleHint;
        }

        return $"{seriesHint} {titleHint}".Trim();
    }

    private static bool TitleAlreadyIncludesSeries(string title, string series)
    {
        var normalizedTitle = RetailTextSimilarity.NormalizeComparableText(title);
        var normalizedSeries = RetailTextSimilarity.NormalizeComparableText(series);

        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return false;
        }

        return normalizedTitle.Equals(normalizedSeries, StringComparison.Ordinal)
            || normalizedTitle.StartsWith(normalizedSeries + " ", StringComparison.Ordinal)
            || normalizedTitle.Contains(" " + normalizedSeries + " ", StringComparison.Ordinal)
            || normalizedTitle.EndsWith(" " + normalizedSeries, StringComparison.Ordinal);
    }

}
