using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>What the Quick Facts read found: the facts, and the paths of the files behind them (so the Engine can say whether a folder is read-only).</summary>
public sealed record ItemQuickFactsRead(ItemQuickFactsDto Facts, IReadOnlyList<string> FilePaths);

/// <summary>
/// The totals the universal editor's Quick Facts need (v4 spec D4, section 5): owned of total children, file count, size and runtime
/// across everything under an item, and the counts of a person, Series, curated collection or Universe. Everything comes from rows that
/// are already stored; no file is ever opened.
/// </summary>
public sealed class ItemQuickFactsRepository(IDatabaseConnection db)
{
    private const int MaxDepth = 6;

    // A recursive walk from the root titles down through their children. Roots arrive as one JSON array of hex ids.
    private const string TreeCte = """
        WITH RECURSIVE tree(id, parent_id, depth) AS (
            SELECT w.id, w.parent_work_id, 0 FROM works w WHERE w.id IN (SELECT unhex(value) FROM json_each(@roots))
            UNION ALL
            SELECT c.id, c.parent_work_id, t.depth + 1 FROM works c JOIN tree t ON c.parent_work_id = t.id WHERE t.depth < @maxDepth
        )
        """;

    public Task<ItemQuickFactsRead?> GetAsync(Guid id, CancellationToken ct) =>
        db.ExecuteReadAsync<ItemQuickFactsRead?>((connection, transaction, token) =>
        {
            var blob = GuidSql.ToBlob(id);
            var launch = connection.QuerySingleOrDefault<WorkRow>(new CommandDefinition("""
                SELECT w.id AS WorkId FROM works w WHERE w.id = @id
                UNION ALL SELECT e.work_id FROM editions e WHERE e.id = @id
                UNION ALL SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id
                LIMIT 1;
                """, new { id = blob }, transaction, cancellationToken: token));
            if (launch is not null)
            {
                return ForWorks(connection, transaction, token, id, ItemQuickFactsKinds.Work, [launch.WorkId], includeChildren: true);
            }

            if (connection.QuerySingleOrDefault<int?>(new CommandDefinition(
                    "SELECT 1 FROM persons WHERE id = @id;", new { id = blob }, transaction, cancellationToken: token)) is not null)
            {
                return ForPerson(connection, transaction, token, id, blob);
            }

            if (connection.QuerySingleOrDefault<int?>(new CommandDefinition(
                    "SELECT 1 FROM fictional_entities WHERE id = @id;", new { id = blob }, transaction, cancellationToken: token)) is not null)
            {
                return ForThing(connection, transaction, token, id, blob);
            }

            var collection = connection.QuerySingleOrDefault<CollectionRow>(new CommandDefinition("""
                SELECT c.collection_type AS CollectionType, c.membership_mode AS MembershipMode, c.owner_kind AS OwnerKind,
                       p.display_name AS OwnerName
                FROM collections c LEFT JOIN profiles p ON p.id = c.profile_id
                WHERE c.id = @id;
                """, new { id = blob }, transaction, cancellationToken: token));
            return collection is null ? null : ForCollection(connection, transaction, token, id, blob, collection);
        }, ct);

    private static ItemQuickFactsRead ForPerson(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken ct, Guid id, byte[] blob)
    {
        var roles = connection.Query<string>(new CommandDefinition(
            "SELECT role FROM person_roles WHERE person_id = @id ORDER BY role;", new { id = blob }, transaction, cancellationToken: ct)).ToList();
        // Files in a Vault library are never counted: the editor's totals follow the same Vault rule as every other people count.
        var works = connection.QuerySingle<int>(new CommandDefinition($"""
            SELECT COUNT(DISTINCT e.work_id)
            FROM person_media_links pml
            JOIN media_assets ma ON ma.id = pml.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            WHERE pml.person_id = @id AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND {VaultSql.NotVaultAsset("ma.id")};
            """, new { id = blob }, transaction, cancellationToken: ct));
        return new ItemQuickFactsRead(
            new ItemQuickFactsDto { Id = id, Kind = ItemQuickFactsKinds.Person, ItemCount = works, Roles = roles },
            []);
    }

    // A character, place, faction or other thing of a Universe: in how many of the Works it appears (the Works themselves are what the
    // appearance links name; a link kept only as extra lore does not count). It owns no files of its own.
    private static ItemQuickFactsRead ForThing(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken ct, Guid id, byte[] blob)
    {
        var works = connection.QuerySingle<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT work_qid) FROM fictional_entity_work_links WHERE entity_id = @id AND is_supplemental = 0;",
            new { id = blob }, transaction, cancellationToken: ct));
        return new ItemQuickFactsRead(new ItemQuickFactsDto { Id = id, Kind = ItemQuickFactsKinds.Thing, ItemCount = works }, []);
    }

    private static ItemQuickFactsRead ForCollection(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken ct,
        Guid id, byte[] blob, CollectionRow collection)
    {
        var isUniverse = string.Equals(collection.CollectionType, "Universe", StringComparison.OrdinalIgnoreCase);
        var isSeries = string.Equals(collection.CollectionType, "Series", StringComparison.OrdinalIgnoreCase);
        var isSmart = string.Equals(collection.MembershipMode, "Smart", StringComparison.OrdinalIgnoreCase)
            && !isUniverse && !isSeries;

        // The titles at the top of the grouping: a Series' books, a Universe's films, shows and books, or what was put in a collection.
        string membersSql;
        if (isUniverse)
        {
            membersSql = """
                SELECT w.id AS Id, w.media_type AS MediaType,
                       CASE WHEN w.ownership = 'Owned' AND w.is_catalog_only = 0 THEN 1 ELSE 0 END AS Owned
                FROM works w
                WHERE w.parent_work_id IS NULL
                  AND w.collection_id IN (SELECT c.id FROM collections c WHERE c.id = @id OR c.universe_id = @id OR c.parent_collection_id = @id);
                """;
        }
        else if (isSeries)
        {
            membersSql = """
                SELECT w.id AS Id, w.media_type AS MediaType,
                       CASE WHEN w.ownership = 'Owned' AND w.is_catalog_only = 0 THEN 1 ELSE 0 END AS Owned
                FROM works w WHERE w.parent_work_id IS NULL AND w.collection_id = @id;
                """;
        }
        else
        {
            membersSql = """
                SELECT w.id AS Id, w.media_type AS MediaType,
                       CASE WHEN w.ownership = 'Owned' AND w.is_catalog_only = 0 THEN 1 ELSE 0 END AS Owned
                FROM collection_items ci JOIN works w ON w.id = ci.work_id WHERE ci.collection_id = @id;
                """;
        }

        var members = connection.Query<MemberRow>(new CommandDefinition(membersSql, new { id = blob }, transaction, cancellationToken: ct)).ToList();
        var owned = members.Where(member => member.Owned == 1).ToList();
        var read = ForWorks(connection, transaction, ct, id, ItemQuickFactsKinds.Collection, owned.Select(member => member.Id).ToList(), includeChildren: false);
        var counts = isUniverse || isSeries
            ? owned.GroupBy(member => KindLabel(member.MediaType))
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ItemQuickFactCountDto { Label = group.Key, Count = group.Count() })
                .ToList()
            : [];
        var facts = new ItemQuickFactsDto
        {
            Id = id,
            Kind = ItemQuickFactsKinds.Collection,
            FileCount = read.Facts.FileCount,
            SizeBytes = read.Facts.SizeBytes,
            RuntimeSeconds = read.Facts.RuntimeSeconds,
            BestQuality = read.Facts.BestQuality,
            ItemCount = isSmart ? null : isUniverse || isSeries ? owned.Count : members.Count,
            Owner = !isUniverse && !isSeries
                ? string.Equals(collection.OwnerKind, "Profile", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(collection.OwnerName)
                    ? collection.OwnerName
                    : "Library"
                : null,
            CountsByKind = counts,
        };
        return new ItemQuickFactsRead(facts, read.FilePaths);
    }

    private static ItemQuickFactsRead ForWorks(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken ct,
        Guid id, string kind, IReadOnlyList<Guid> roots, bool includeChildren)
    {
        if (roots.Count == 0)
        {
            return new ItemQuickFactsRead(new ItemQuickFactsDto { Id = id, Kind = kind }, []);
        }

        var parameters = new { roots = GuidSql.ToJsonHexArray(roots), maxDepth = MaxDepth };
        var tree = includeChildren
            ? connection.Query<TreeRow>(new CommandDefinition(TreeCte + """

                SELECT t.id AS Id, t.parent_id AS ParentId, t.depth AS Depth,
                       CASE WHEN w.ownership = 'Owned' AND w.is_catalog_only = 0 THEN 1 ELSE 0 END AS Owned
                FROM tree t JOIN works w ON w.id = t.id;
                """, parameters, transaction, cancellationToken: ct)).ToList()
            : [];

        var assets = connection.Query<AssetRow>(new CommandDefinition(TreeCte + """

            SELECT ma.file_path_root AS FilePath, pic.file_size AS InspectedFileSize, pic.duration_secs AS DurationSeconds,
                   pic.metadata_json AS InspectionJson,
                   (SELECT fhc.size_bytes FROM file_hash_cache fhc WHERE fhc.sha256 = ma.content_hash LIMIT 1) AS HashedFileSize
            FROM tree t
            JOIN editions e ON e.work_id = t.id
            JOIN media_assets ma ON ma.edition_id = e.id
            LEFT JOIN playback_inspection_cache pic ON pic.asset_id = ma.id AND pic.source_hash = ma.content_hash
            WHERE ma.status = 'Normal' AND ma.is_orphaned = 0 AND ma.rendition_purpose = 'Original';
            """, parameters, transaction, cancellationToken: ct)).ToList();

        long size = 0;
        var anySize = false;
        double runtime = 0;
        var anyRuntime = false;
        ItemQuality? best = null;
        foreach (var asset in assets)
        {
            var bytes = asset.InspectedFileSize is > 0 ? asset.InspectedFileSize : asset.HashedFileSize is > 0 ? asset.HashedFileSize : null;
            if (bytes is { } known)
            {
                size += known;
                anySize = true;
            }

            if (asset.DurationSeconds is > 0)
            {
                runtime += asset.DurationSeconds.Value;
                anyRuntime = true;
            }

            var quality = ItemQuality.From(ReadProbe(asset.InspectionJson));
            if (quality is not null && (best is null || quality.Rank > best.Rank))
            {
                best = quality;
            }
        }

        var directOwned = 0;
        var directTotal = 0;
        var leafOwned = 0;
        var leafTotal = 0;
        if (includeChildren && roots.Count == 1)
        {
            var rootId = roots[0];
            var parents = tree.Where(row => row.ParentId is not null).Select(row => row.ParentId!.Value).ToHashSet();
            foreach (var row in tree.Where(row => row.Depth > 0))
            {
                if (row.ParentId == rootId)
                {
                    directTotal++;
                    directOwned += row.Owned;
                }

                if (!parents.Contains(row.Id))
                {
                    leafTotal++;
                    leafOwned += row.Owned;
                }
            }
        }

        var facts = new ItemQuickFactsDto
        {
            Id = id,
            Kind = kind,
            FileCount = assets.Count,
            SizeBytes = anySize ? size : null,
            RuntimeSeconds = anyRuntime ? runtime : null,
            BestQuality = best?.Label,
            DirectOwned = directOwned,
            DirectTotal = directTotal,
            LeafOwned = leafOwned,
            LeafTotal = leafTotal,
        };
        return new ItemQuickFactsRead(facts, assets.Select(asset => asset.FilePath).ToList());
    }

    private static string KindLabel(string mediaType) => mediaType switch
    {
        "Movies" => "Films",
        "TV" => "Shows",
        "Music" => "Music",
        "Books" => "Books",
        "Audiobooks" => "Audiobooks",
        "Comics" => "Comics",
        "Videos" => "Videos",
        _ => mediaType,
    };

    private static MediaProbeResult? ReadProbe(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MediaProbeResult>(json);
        }
        catch (JsonException)
        {
            // A damaged cache row only hides this optional fact; the next inspection rewrites it.
            return null;
        }
    }

    private sealed class WorkRow
    {
        public Guid WorkId { get; init; }
    }

    private sealed class CollectionRow
    {
        public string CollectionType { get; init; } = string.Empty;
        public string? MembershipMode { get; init; }
        public string? OwnerKind { get; init; }
        public string? OwnerName { get; init; }
    }

    private sealed class MemberRow
    {
        public Guid Id { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public int Owned { get; init; }
    }

    private sealed class TreeRow
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public int Depth { get; init; }
        public int Owned { get; init; }
    }

    private sealed class AssetRow
    {
        public string FilePath { get; init; } = string.Empty;
        public long? InspectedFileSize { get; init; }
        public double? DurationSeconds { get; init; }
        public string? InspectionJson { get; init; }
        public long? HashedFileSize { get; init; }
    }
}

/// <summary>One file's picture or sound quality as a short label, with a rank so the best of several files can be picked.</summary>
public sealed record ItemQuality(string Label, long Rank)
{
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { "flac", "alac", "wav", "pcm_s16le", "pcm_s24le", "aiff" };

    public static ItemQuality? From(MediaProbeResult? probe)
    {
        if (probe is null)
        {
            return null;
        }

        if (probe.Height is > 0 || probe.Width is > 0)
        {
            var height = probe.Height ?? 0;
            var width = probe.Width ?? 0;
            var label = height >= 2000 || width >= 3600 ? "4K"
                : height >= 1000 || width >= 1800 ? "1080p"
                : height >= 700 || width >= 1200 ? "720p"
                : "SD";
            var hdr = string.Equals(probe.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(probe.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
            return new ItemQuality(hdr ? $"{label} HDR" : label, Math.Max(height, 1) * 10L + (hdr ? 1 : 0) + 1_000_000L);
        }

        if (!string.IsNullOrWhiteSpace(probe.AudioCodec))
        {
            var codec = probe.AudioCodec.Trim();
            if (Lossless.Contains(codec) || codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase))
            {
                var name = codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) ? "WAV" : codec.ToUpperInvariant();
                return new ItemQuality(name, 100_000);
            }

            var kbps = probe.AudioBitrate is > 0 ? probe.AudioBitrate.Value : 0;
            return new ItemQuality(kbps > 0 ? $"{codec.ToUpperInvariant()} {kbps} kbps" : codec.ToUpperInvariant(), kbps);
        }

        return null;
    }
}
