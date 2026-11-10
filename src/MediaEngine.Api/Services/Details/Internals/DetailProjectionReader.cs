using Dapper;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// Owns focused database and repository reads used while composing detail pages.
/// It deliberately returns projection values rather than building view models.
/// </summary>
internal sealed class DetailProjectionReader
{
    private readonly IDatabaseConnection _db;
    private readonly IEntityAssetRepository _entityAssets;

    internal DetailProjectionReader(
        IDatabaseConnection db,
        IEntityAssetRepository entityAssets)
    {
        _db = db;
        _entityAssets = entityAssets;
    }

    internal async Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(
        Guid? profileId,
        CancellationToken ct)
    {
        if (!profileId.HasValue)
        {
            return new HashSet<Guid>();
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT entity_id
            FROM profile_saved_items
            WHERE profile_id = @ProfileId;
            """,
            new { ProfileId = GuidSql.ToBlob(profileId.Value) },
            cancellationToken: ct));

        return rows
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    internal async Task<string?> LoadPersonWikipediaUrlAsync(Guid personId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT value
            FROM canonical_values
            WHERE entity_id = @personId
              AND key = 'wikipedia_url'
              AND value IS NOT NULL
              AND TRIM(value) <> ''
            ORDER BY last_scored_at DESC
            LIMIT 1;
            """,
            new { personId },
            cancellationToken: ct));
    }

    internal async Task<string?> LoadPersonShortDescriptionAsync(
        Guid personId,
        string? qid,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var description = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT value
            FROM canonical_values
            WHERE entity_id = @personId
              AND key = 'short_description'
              AND value IS NOT NULL
              AND TRIM(value) <> ''
            ORDER BY last_scored_at DESC
            LIMIT 1;
            """,
            new { personId },
            cancellationToken: ct));

        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        if (string.IsNullOrWhiteSpace(qid))
        {
            return null;
        }

        var labelDescription = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT description
            FROM qid_labels
            WHERE qid = @qid
              AND description IS NOT NULL
              AND TRIM(description) <> ''
            LIMIT 1;
            """,
            new { qid },
            cancellationToken: ct));

        return LooksLikeWikidataShortDescription(labelDescription)
            ? labelDescription
            : null;
    }

    internal async Task<string?> LoadManagedWorkCoverUrlAsync(
        Guid entityId,
        DetailEntityType entityType,
        string? canonicalArtworkUrl,
        CancellationToken ct)
    {
        var preferred = await _entityAssets.GetPreferredAsync(entityId.ToString("D"), "CoverArt", ct);

        if (preferred is not null)
        {
            return $"/stream/artwork/{preferred.Id:D}";
        }

        return string.IsNullOrWhiteSpace(canonicalArtworkUrl)
            ? null
            : $"/stream/entity/{ToDetailRouteEntityType(entityType)}/{entityId:D}/cover";
    }

    private static bool LooksLikeWikidataShortDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 220
            && !trimmed.Contains('\n')
            && !trimmed.Contains(". ", StringComparison.Ordinal);
    }

    private static string ToDetailRouteEntityType(DetailEntityType entityType)
        => entityType.ToString().Replace("Tv", "tv-", StringComparison.Ordinal).ToLowerInvariant();
}
