using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Read-only assessment of release evidence for one owned music file. Retail
/// MusicBrainz can select an official release from a recording's release list;
/// that contextual choice does not establish the file's exact Edition release.
/// No branch in this repository promotes or merges Edition identity.
/// </summary>
public sealed class MusicEditionReleaseEvidenceRepository(IDatabaseConnection database)
{
    public MusicEditionReleaseAssessment Assess(Guid assetId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<AssetLineageRow>("""
            SELECT a.id AS AssetId, a.edition_id AS EditionId,
                   e.work_id AS WorkId, w.parent_work_id AS AlbumWorkId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned,
                   w.media_type AS MediaType, w.work_kind AS WorkKind,
                   album.media_type AS AlbumMediaType,
                   album.work_kind AS AlbumWorkKind
            FROM media_assets a JOIN editions e ON e.id=a.edition_id
            JOIN works w ON w.id=e.work_id
            LEFT JOIN works album ON album.id=w.parent_work_id
            WHERE a.id=@assetId;
            """, new { assetId });
        if (row is null || row.Status != "Normal" || row.IsOrphaned
            || row.MediaType != "Music" || row.WorkKind != "child"
            || row.AlbumMediaType != "Music" || row.AlbumWorkKind != "parent"
            || row.AlbumWorkId is null)
            return new MusicEditionReleaseAssessment(
                MusicEditionReleaseEvidenceStatus.Unavailable, assetId);

        var editionFileCount = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM media_assets WHERE edition_id=@EditionId;
            """, row);
        if (editionFileCount != 1)
            return Assessment(row, MusicEditionReleaseEvidenceStatus.SharedEdition);
        var workEditionCount = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM editions WHERE work_id=@WorkId;
            """, row);
        if (workEditionCount != 1)
            return Assessment(row, MusicEditionReleaseEvidenceStatus.MultipleEncodes);

        var editionCanonical = connection.Query<string>("""
            SELECT DISTINCT value FROM canonical_values
            WHERE entity_id=@EditionId AND key=@key AND trim(value)<>'';
            """, new { row.EditionId, key = BridgeIdKeys.MusicBrainzReleaseId }).ToArray();
        var editionBridge = connection.Query<string>("""
            SELECT DISTINCT id_value FROM bridge_ids
            WHERE entity_id=@EditionId AND id_type=@key AND trim(id_value)<>'';
            """, new { row.EditionId, key = BridgeIdKeys.MusicBrainzReleaseId }).ToArray();
        if (editionCanonical.Length > 1 || editionBridge.Length > 1
            || editionCanonical.Length == 1 && editionBridge.Length == 1
                && !string.Equals(editionCanonical[0], editionBridge[0],
                    StringComparison.OrdinalIgnoreCase))
            return Assessment(row, MusicEditionReleaseEvidenceStatus.ConflictingEditionIdentity);
        if (editionCanonical.Length == 1 || editionBridge.Length == 1)
        {
            var existing = editionCanonical.FirstOrDefault() ?? editionBridge[0];
            return Guid.TryParse(existing, out _)
                ? Assessment(row, MusicEditionReleaseEvidenceStatus.ExistingEditionIdentity,
                    existing)
                : Assessment(row, MusicEditionReleaseEvidenceStatus.ConflictingEditionIdentity);
        }

        // The chosen retail candidate belongs to this exact asset job, but its
        // release ID can be contextual. Compare sibling candidates solely to
        // surface known album ambiguity; never copy a parent Work ID downward.
        var candidates = connection.Query<SelectedCandidateRow>("""
            SELECT job.entity_id AS AssetId, candidate.provider_name AS ProviderName,
                   candidate.outcome AS Outcome, candidate.bridge_ids_json AS BridgeIdsJson
            FROM works siblingWork
            JOIN editions siblingEdition ON siblingEdition.work_id=siblingWork.id
            JOIN media_assets siblingAsset ON siblingAsset.edition_id=siblingEdition.id
            JOIN identity_jobs job ON job.entity_id=siblingAsset.id
                AND job.entity_type='MediaAsset' AND job.media_type='Music'
            JOIN retail_match_candidates candidate
                ON candidate.id=job.selected_candidate_id AND candidate.job_id=job.id
            WHERE siblingWork.parent_work_id=@AlbumWorkId;
            """, row).ToArray();
        var releaseIds = candidates.Where(candidate =>
                candidate.Outcome == "AutoAccepted"
                && candidate.ProviderName.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => (candidate.AssetId,
                ReleaseId: ReadReleaseId(candidate.BridgeIdsJson)))
            .Where(item => item.ReleaseId is not null).ToArray();
        var own = releaseIds.Where(item => item.AssetId == assetId)
            .Select(item => item.ReleaseId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (own.Length > 1)
            return Assessment(row, MusicEditionReleaseEvidenceStatus.AmbiguousAssetCandidates);
        var distinctAlbumReleases = releaseIds.Select(item => item.ReleaseId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctAlbumReleases.Length > 1)
            return Assessment(row, MusicEditionReleaseEvidenceStatus.MixedAlbumReleases);
        if (own.Length == 1)
            return Assessment(row, MusicEditionReleaseEvidenceStatus.ProviderContextOnly,
                observedProviderReleaseId: own[0]);
        return Assessment(row, MusicEditionReleaseEvidenceStatus.NoExactSourceEvidence);
    }

    private static string? ReadReleaseId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Name.Equals(BridgeIdKeys.MusicBrainzReleaseId,
                        StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    return Guid.TryParse(value, out var releaseId)
                        ? releaseId.ToString("D") : null;
                }
        }
        catch (JsonException) { }
        return null;
    }

    private static MusicEditionReleaseAssessment Assessment(AssetLineageRow row,
        MusicEditionReleaseEvidenceStatus status, string? existingEditionReleaseId = null,
        string? observedProviderReleaseId = null) =>
        new(status, row.AssetId, row.EditionId, row.WorkId, row.AlbumWorkId,
            existingEditionReleaseId, observedProviderReleaseId);

    private sealed class AssetLineageRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? AlbumWorkId { get; set; }
        public string Status { get; set; } = "";
        public bool IsOrphaned { get; set; }
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public string? AlbumMediaType { get; set; }
        public string? AlbumWorkKind { get; set; }
    }

    private sealed class SelectedCandidateRow
    {
        public Guid AssetId { get; set; }
        public string ProviderName { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string? BridgeIdsJson { get; set; }
    }
}

public enum MusicEditionReleaseEvidenceStatus
{
    Unavailable, SharedEdition, MultipleEncodes, ConflictingEditionIdentity,
    ExistingEditionIdentity, MixedAlbumReleases, AmbiguousAssetCandidates,
    ProviderContextOnly, NoExactSourceEvidence
}

public sealed record MusicEditionReleaseAssessment(
    MusicEditionReleaseEvidenceStatus Status,
    Guid AssetId,
    Guid? EditionId = null,
    Guid? WorkId = null,
    Guid? AlbumWorkId = null,
    string? ExistingEditionReleaseId = null,
    string? ObservedProviderReleaseId = null)
{
    public bool CanPromoteToEdition => false;
}
