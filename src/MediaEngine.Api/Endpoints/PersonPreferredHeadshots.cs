using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The people whose face is a preferred Headshot picture rather than a path saved on the person. A linked person (alias, band member,
/// group) is shown with the same face as the person's own page, and that page serves the preferred Headshot first, so a link built
/// only from <c>persons.local_headshot_path</c> and <c>persons.headshot_url</c> would show no picture for someone whose face lives
/// only as a Headshot picture. One value per person: the picture's file path, or empty when the picture has no local file.
/// </summary>
internal sealed class PersonPreferredHeadshots
{
    public static readonly PersonPreferredHeadshots None = new(new Dictionary<Guid, string>());

    private readonly IReadOnlyDictionary<Guid, string> _paths;

    private PersonPreferredHeadshots(IReadOnlyDictionary<Guid, string> paths) => _paths = paths;

    /// <summary>Reads the preferred Headshot of each person in one statement.</summary>
    public static async Task<PersonPreferredHeadshots> LoadAsync(IDatabaseConnection db, IEnumerable<Guid> personIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        var blobs = personIds.Distinct().Select(GuidSql.ToBlob).ToArray();
        if (blobs.Length == 0)
        {
            return None;
        }

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<PreferredHeadshotRow>(
            """
            SELECT entity_id AS EntityId, local_image_path AS LocalImagePath
            FROM entity_assets
            WHERE asset_type = 'Headshot' AND is_preferred = 1 AND entity_id IN @ids;
            """,
            new { ids = blobs });
        return From(rows.Select(row => (GuidSql.FromDb(row.EntityId), row.LocalImagePath)));
    }

    /// <summary>The same lookup from preferred Headshot pictures a caller already holds (the Engine's asset repository).</summary>
    public static PersonPreferredHeadshots From(IEnumerable<EntityAsset> preferredHeadshots)
        => From(preferredHeadshots
            .Where(asset => string.Equals(asset.AssetTypeValue, "Headshot", StringComparison.OrdinalIgnoreCase)
                            && Guid.TryParse(asset.EntityId, out _))
            .Select(asset => (Guid.Parse(asset.EntityId), asset.LocalImagePath)));

    private static PersonPreferredHeadshots From(IEnumerable<(Guid PersonId, string? LocalImagePath)> pairs)
    {
        var paths = new Dictionary<Guid, string>();
        foreach (var (personId, localImagePath) in pairs)
        {
            paths.TryAdd(personId, localImagePath ?? string.Empty);
        }

        return new PersonPreferredHeadshots(paths);
    }

    /// <summary>
    /// The face address of a linked person: the usual address when the person has a saved path or remote address, otherwise the address
    /// of their preferred Headshot picture, otherwise null (a clean placeholder).
    /// </summary>
    public string? BuildUrl(Guid personId, string? localHeadshotPath, string? remoteHeadshotUrl)
        => ApiImageUrls.BuildPersonHeadshotUrl(personId, localHeadshotPath, remoteHeadshotUrl)
           ?? (_paths.TryGetValue(personId, out var preferredPath)
               ? ApiImageUrls.BuildPersonHeadshotAddress(personId, preferredPath)
               : null);

    private sealed class PreferredHeadshotRow
    {
        public byte[] EntityId { get; init; } = [];
        public string? LocalImagePath { get; init; }
    }
}
