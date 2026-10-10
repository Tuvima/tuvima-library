using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<List<SequenceContainerOptionViewModel>> ResolveLinkedManifestSequenceContainerOptionsAsync(
        Guid workId,
        DetailEntityType entityType,
        string mediaType,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT smi.series_qid AS ContainerId,
                   smi.collection_id AS CollectionId,
                   CAST(COALESCE(NULLIF(h.series_label, ''), NULLIF(c.display_name, ''), NULLIF(smi.parent_collection_label, ''), smi.series_qid) AS TEXT) AS ContainerTitle,
                   COALESCE(
                       MAX(COALESCE(
                           CAST(json_extract(h.api_metadata_json, '$.expectedTotal') AS INTEGER),
                           CAST(json_extract(h.api_metadata_json, '$.expected_total') AS INTEGER))),
                       COUNT(*)) AS ItemCount,
                   MIN(CASE WHEN smi.parsed_ordinal IS NOT NULL OR NULLIF(smi.raw_ordinal, '') IS NOT NULL THEN 0 ELSE 1 END) AS HasOrderingRank
            FROM series_manifest_items smi
            LEFT JOIN series_manifest_hydrations h ON h.series_qid = smi.series_qid
            LEFT JOIN collections c ON c.id = smi.collection_id
            WHERE smi.linked_work_id = @workId
              AND NULLIF(smi.series_qid, '') IS NOT NULL
              AND smi.membership_scope IN ('MainSequence', 'Supplementary', 'Unpositioned')
              AND COALESCE(CAST(json_extract(h.api_metadata_json, '$.containerKind') AS TEXT), 'OrderedSeries') NOT IN ('Franchise', 'Universe', 'WikimediaList', 'PublisherOrProductionList')
            GROUP BY smi.series_qid
            ORDER BY HasOrderingRank, ItemCount DESC, ContainerTitle;
            """,
            new { workId },
            cancellationToken: ct));

        var mediaScope = SeriesMediaFilter(entityType, mediaType);
        var options = new List<SequenceContainerOptionViewModel>();
        foreach (var row in rows)
        {
            var collectionId = StringValue(row.CollectionId);
            var containerId = StringValue(row.ContainerId);
            IReadOnlyList<SeriesManifestItemRecord> manifestItems = string.IsNullOrWhiteSpace(containerId)
                ? Array.Empty<SeriesManifestItemRecord>()
                : await _seriesManifests.GetItemsBySeriesQidAsync(containerId, ct);
            var memberFingerprint = BuildManifestMemberFingerprint(manifestItems);
            IReadOnlyList<string> collectionAliases = new[] { collectionId, memberFingerprint }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
            AddSequenceContainerOption(
                options,
                containerId,
                FormatSequenceContainerTitle(StringValue(row.ContainerTitle)),
                mediaScope,
                sourceContainerId: StringValue(row.ContainerId),
                equivalentContainerIds: collectionAliases);
        }

        return options;
    }

    private static string? BuildManifestMemberFingerprint(IReadOnlyList<SeriesManifestItemRecord> items)
    {
        var positionedMainMembers = items
            .Where(item => string.Equals(item.MembershipScope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Title = NormalizeSeriesTitle(item.ItemLabel),
                Position = ManifestSourcePosition(item),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) && item.Position.HasValue)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (positionedMainMembers.Count < 2)
        {
            return null;
        }

        var identity = string.Join('|', positionedMainMembers.Select(item =>
            $"{item.Position!.Value.ToString("0.####", CultureInfo.InvariantCulture)}:{item.Title}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"sequence-fingerprint:{hash}";
    }

    private static IReadOnlyList<SequenceContainerOptionViewModel> ResolveSequenceContainerOptions(LibraryItemDetail detail, DetailEntityType entityType)
    {
        var mediaScope = SeriesMediaFilter(entityType, detail.MediaType);
        var options = new List<SequenceContainerOptionViewModel>();
        var seriesTitle = FirstText(detail.Series, GetDetailCanonicalValue(detail, MetadataFieldConstants.Series));
        var defaultContainerId = NormalizeSequenceContainerId(GetDetailCanonicalValue(detail, "default_sequence_container_id"));
        var defaultContainerTitle = GetDetailCanonicalValue(detail, "default_sequence_container_label");

        AddSequenceContainerOption(options, defaultContainerId, defaultContainerTitle, mediaScope, sourceContainerId: defaultContainerId);
        AddSequenceContainerOptionFromCanonicalQid(options, GetDetailCanonicalValue(detail, "series_qid"), seriesTitle, mediaScope);
        AddSequenceContainerOptionFromCanonicalQid(options, GetDetailCanonicalValue(detail, "part_of_the_series_qid"), seriesTitle, mediaScope);
        AddSequenceContainerOptionFromCanonicalQid(options, GetDetailCanonicalValue(detail, "part_of_series_qid"), seriesTitle, mediaScope);

        if (options.Count == 0 && !string.IsNullOrWhiteSpace(seriesTitle))
        {
            AddSequenceContainerOption(options, seriesTitle, seriesTitle, mediaScope, sourceContainerId: null);
        }

        return options;
    }

    private static void AddSequenceContainerOptionFromCanonicalQid(List<SequenceContainerOptionViewModel> options, string? rawQidValue, string? title, string mediaScope)
    {
        var parsed = ParseQidLabel(rawQidValue);
        AddSequenceContainerOption(options, parsed.Qid, FirstText(title, parsed.Label), mediaScope, sourceContainerId: parsed.Qid);
    }

    private static void AddSequenceContainerOption(
        List<SequenceContainerOptionViewModel> options,
        string? containerId,
        string? title,
        string mediaScope,
        string? sourceContainerId = null,
        IReadOnlyList<string>? equivalentContainerIds = null)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return;
        }

        var normalizedContainerId = NormalizeSequenceContainerId(containerId) ?? containerId.Trim();
        var normalizedSourceContainerId = NormalizeSequenceContainerId(sourceContainerId) ?? sourceContainerId?.Trim();
        var candidate = new SequenceContainerOptionViewModel
        {
            ContainerId = normalizedContainerId,
            SourceContainerId = normalizedSourceContainerId,
            ContainerTitle = FormatSequenceContainerTitle(FirstText(title, containerId)) ?? "Series",
            MediaScope = mediaScope,
            EquivalentContainerIds = BuildSequenceContainerAliases(
                normalizedContainerId,
                normalizedSourceContainerId,
                equivalentContainerIds),
        };

        var existingIndex = options.FindIndex(option => ShouldMergeSequenceContainerOptions(option, candidate));
        if (existingIndex >= 0)
        {
            options[existingIndex] = MergeSequenceContainerOptions(options[existingIndex], candidate);
            return;
        }

        options.Add(candidate);
    }

    private static IReadOnlyList<SequenceContainerOptionViewModel> DeduplicateSequenceContainerOptions(
        IReadOnlyList<SequenceContainerOptionViewModel> options)
    {
        if (options.Count <= 1)
        {
            return options;
        }

        var distinct = new List<SequenceContainerOptionViewModel>();
        foreach (var option in options)
        {
            var existingIndex = distinct.FindIndex(existing => ShouldMergeSequenceContainerOptions(existing, option));
            if (existingIndex >= 0)
            {
                distinct[existingIndex] = MergeSequenceContainerOptions(distinct[existingIndex], option);
                continue;
            }

            distinct.Add(option);
        }

        return distinct;
    }

    private static IReadOnlyList<string> BuildSequenceContainerAliases(
        string? containerId,
        string? sourceContainerId,
        IReadOnlyList<string>? extraIds)
    {
        var aliases = new List<string>();
        AddSequenceContainerAlias(aliases, containerId);
        AddSequenceContainerAlias(aliases, sourceContainerId);
        if (extraIds is not null)
        {
            foreach (var id in extraIds)
            {
                AddSequenceContainerAlias(aliases, id);
            }
        }

        return aliases;
    }

    private static void AddSequenceContainerAlias(List<string> aliases, string? value)
    {
        var normalized = NormalizeSequenceContainerId(value) ?? value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || aliases.Any(alias => SequenceContainerIdEquals(alias, normalized)))
        {
            return;
        }

        aliases.Add(normalized);
    }

    private static bool ShouldMergeSequenceContainerOptions(
        SequenceContainerOptionViewModel existing,
        SequenceContainerOptionViewModel candidate)
    {
        if (SequenceContainerOptionMatches(existing, candidate.ContainerId)
            || SequenceContainerOptionMatches(existing, candidate.SourceContainerId)
            || candidate.EquivalentContainerIds.Any(alias => SequenceContainerOptionMatches(existing, alias)))
        {
            return true;
        }

        return false;
    }

    private static SequenceContainerOptionViewModel MergeSequenceContainerOptions(
        SequenceContainerOptionViewModel existing,
        SequenceContainerOptionViewModel candidate)
    {
        var aliases = BuildSequenceContainerAliases(
            PreferRoutableContainerId(existing.ContainerId, candidate.ContainerId),
            PreferSourceContainerId(existing, candidate),
            existing.EquivalentContainerIds.Concat(candidate.EquivalentContainerIds).ToList());

        return new SequenceContainerOptionViewModel
        {
            ContainerId = PreferRoutableContainerId(existing.ContainerId, candidate.ContainerId),
            SourceContainerId = PreferSourceContainerId(existing, candidate),
            ContainerTitle = PreferSequenceContainerTitle(existing, candidate),
            MediaScope = FirstText(existing.MediaScope, candidate.MediaScope),
            IsSelected = existing.IsSelected || candidate.IsSelected,
            IsDefault = existing.IsDefault || candidate.IsDefault,
            EquivalentContainerIds = aliases,
        };
    }

    private static string PreferRoutableContainerId(string existingId, string candidateId)
        => Guid.TryParse(existingId, out _)
            ? existingId
            : Guid.TryParse(candidateId, out _)
                ? candidateId
                : existingId;

    private static string? PreferSourceContainerId(
        SequenceContainerOptionViewModel existing,
        SequenceContainerOptionViewModel candidate)
    {
        var ids = new[]
        {
            existing.SourceContainerId,
            candidate.SourceContainerId,
            existing.ContainerId,
            candidate.ContainerId,
        };

        return ids.FirstOrDefault(IsWikidataQid)
            ?? ids.FirstOrDefault(IsProviderSequenceContainerId)
            ?? ids.FirstOrDefault(id => !Guid.TryParse(id, out _));
    }

    private static string PreferSequenceContainerTitle(
        SequenceContainerOptionViewModel existing,
        SequenceContainerOptionViewModel candidate)
    {
        if (IsWikidataQid(candidate.ContainerId)
            && IsProviderSequenceContainerId(existing.ContainerId)
            && !string.IsNullOrWhiteSpace(candidate.ContainerTitle))
        {
            return candidate.ContainerTitle;
        }

        if (Guid.TryParse(existing.ContainerId, out _)
            && IsManifestBackedSequenceContainerId(candidate.ContainerId)
            && !string.IsNullOrWhiteSpace(candidate.ContainerTitle))
        {
            return candidate.ContainerTitle;
        }

        if (string.IsNullOrWhiteSpace(existing.ContainerTitle) || IsSequenceContainerIdLike(existing.ContainerTitle))
        {
            return candidate.ContainerTitle;
        }

        return existing.ContainerTitle;
    }

    private static bool SequenceContainerOptionMatches(SequenceContainerOptionViewModel? option, string? containerId)
    {
        if (option is null || string.IsNullOrWhiteSpace(containerId))
        {
            return false;
        }

        return SequenceContainerIdEquals(option.ContainerId, containerId)
            || SequenceContainerIdEquals(option.SourceContainerId, containerId)
            || option.EquivalentContainerIds.Any(alias => SequenceContainerIdEquals(alias, containerId));
    }

    private static bool IsLocalOrProviderBackedSequenceContainer(SequenceContainerOptionViewModel option)
        => Guid.TryParse(option.ContainerId, out _)
           || Guid.TryParse(option.SourceContainerId, out _)
           || IsProviderSequenceContainerId(option.ContainerId)
           || IsProviderSequenceContainerId(option.SourceContainerId)
           || option.EquivalentContainerIds.Any(alias => Guid.TryParse(alias, out _) || IsProviderSequenceContainerId(alias));

    private static bool IsProviderBackedSequenceContainer(SequenceContainerOptionViewModel option)
        => IsProviderSequenceContainerId(option.ContainerId)
           || IsProviderSequenceContainerId(option.SourceContainerId)
           || option.EquivalentContainerIds.Any(IsProviderSequenceContainerId);

    private static bool IsComicSequenceEntity(DetailEntityType entityType)
        => entityType is DetailEntityType.ComicIssue or DetailEntityType.ComicSeries;

    private static bool IsWikidataOnlySequenceContainer(SequenceContainerOptionViewModel option)
    {
        var identities = new[] { option.ContainerId, option.SourceContainerId }
            .Concat(option.EquivalentContainerIds)
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .ToList();
        return identities.Any(IsWikidataQid)
            && !identities.Any(identity => Guid.TryParse(identity, out _) || IsProviderSequenceContainerId(identity));
    }

    private static List<SequenceContainerOptionViewModel> PreferWikidataLinkedSequenceContainers(
        IReadOnlyList<SequenceContainerOptionViewModel> options)
    {
        var wikidataOptions = options
            .Where(option => IsWikidataQid(option.SourceContainerId) || IsWikidataQid(option.ContainerId))
            .ToList();
        if (wikidataOptions.Count == 0)
        {
            return options.ToList();
        }

        return options
            .Where(option =>
                IsWikidataQid(option.SourceContainerId)
                || IsWikidataQid(option.ContainerId)
                || wikidataOptions.Any(wikidata => ShouldMergeSequenceContainerOptions(option, wikidata)))
            .ToList();
    }

    private static bool IsTitleOnlySequenceContainerOption(SequenceContainerOptionViewModel option)
        => string.IsNullOrWhiteSpace(option.SourceContainerId)
           && !Guid.TryParse(option.ContainerId, out _)
           && !IsManifestBackedSequenceContainerId(option.ContainerId);

    private static bool IsManifestBackedSequenceContainerId(string? containerId)
        => IsWikidataQid(containerId) || IsProviderSequenceContainerId(containerId);

    private static bool IsProviderSequenceContainerId(string? containerId)
        => !string.IsNullOrWhiteSpace(containerId)
           && containerId.Contains(':', StringComparison.Ordinal)
           && !IsWikidataQid(containerId);

    private static bool IsSequenceContainerIdLike(string? value)
        => IsWikidataQid(value)
           || Guid.TryParse(value, out _)
           || IsProviderSequenceContainerId(value);

    private async Task<SequenceContainerOptionViewModel?> ResolveLocalSequenceContainerOptionAsync(Guid workId, DetailEntityType entityType, string mediaType, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            """
            WITH current_lineage AS (
                SELECT COALESCE(current_grandparent.id, current_parent.id, current_work.id) AS RootWorkId,
                       current_work.parent_work_id AS ParentWorkId,
                       current_work.collection_id AS CollectionId
                FROM works current_work
                LEFT JOIN works current_parent ON current_parent.id = current_work.parent_work_id
                LEFT JOIN works current_grandparent ON current_grandparent.id = current_parent.parent_work_id
                WHERE current_work.id = @workId
            )
            SELECT CAST(COALESCE(
                (SELECT display_name FROM collections c WHERE c.id = current.CollectionId LIMIT 1),
                (SELECT value FROM canonical_values WHERE entity_id = current.RootWorkId AND key = 'series' LIMIT 1),
                -- A root's own title only names a container when the work actually has a
                -- parent (episode -> show, track -> album). A standalone work is not a
                -- one-item series of itself.
                (SELECT value FROM canonical_values WHERE entity_id = current.RootWorkId AND key = 'title'
                    AND current.ParentWorkId IS NOT NULL LIMIT 1)
            ) AS TEXT) AS SeriesTitle,
            current.CollectionId AS CollectionId,
            CAST((SELECT wikidata_qid FROM collections c WHERE c.id = current.CollectionId LIMIT 1) AS TEXT) AS SeriesQid,
            CAST((SELECT rule_hash FROM collections c WHERE c.id = current.CollectionId LIMIT 1) AS TEXT) AS ProviderKey
            FROM current_lineage current;
            """,
            new { workId },
            cancellationToken: ct));

        var title = StringValue(row?.SeriesTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var qid = ExtractQid(StringValue(row?.SeriesQid));
        var providerKey = StringValue(row?.ProviderKey);
        var collectionId = StringValue(row?.CollectionId);
        return new SequenceContainerOptionViewModel
        {
            ContainerId = collectionId ?? qid ?? title,
            SourceContainerId = FirstText(qid, providerKey),
            ContainerTitle = title,
            MediaScope = SeriesMediaFilter(entityType, mediaType),
            EquivalentContainerIds = BuildSequenceContainerAliases(
                collectionId,
                qid,
                string.IsNullOrWhiteSpace(providerKey) ? Array.Empty<string>() : [providerKey]),
        };
    }

    private static string? GetDetailCanonicalValue(LibraryItemDetail detail, string key)
        => detail.CanonicalValues.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static DateTimeOffset? GetCanonicalLastScoredAt(LibraryItemDetail detail, string key)
        => detail.CanonicalValues.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.LastScoredAt;

    private static string? GetCanonicalProviderId(LibraryItemDetail detail, string key)
        => detail.CanonicalValues.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.WinningProviderId;

}
