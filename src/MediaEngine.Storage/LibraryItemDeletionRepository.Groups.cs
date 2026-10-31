using System.Globalization;
using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

// Delete a show, season, album or Series: turning a group id into the titles the library owns inside it.
public sealed partial class LibraryItemDeletionRepository
{
    private const string TitleKeys = "'episode_title', 'title', 'track_title', 'show_name', 'album'";

    public Task<LibraryItemDeletionGroup?> ResolveGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (groupId == Guid.Empty)
        {
            return Task.FromResult<LibraryItemDeletionGroup?>(null);
        }

        using var connection = db.CreateConnection();
        var root = connection.QueryFirstOrDefault<GroupRootRow>(new CommandDefinition($"""
            -- readiness: a group opened by id for the delete preview
            SELECT w.id AS Id, w.work_kind AS Kind, w.parent_work_id AS ParentId, w.media_type AS MediaType, w.ordinal AS Ordinal,
                   (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ({TitleKeys}) AND NULLIF(cv.value, '') IS NOT NULL
                    ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 WHEN 'album' THEN 2 ELSE 3 END LIMIT 1) AS Title
            FROM works w
            WHERE w.id = @id AND {VaultSql.NotVaultEntity("w.id")};
            """,
            new { id = GuidSql.ToBlob(groupId) },
            cancellationToken: ct));

        if (root is not null)
        {
            if (!string.Equals(root.Kind, "parent", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<LibraryItemDeletionGroup?>(null);
            }

            var rows = LoadTree(connection, groupId, fromCollection: false, ct);
            var mediaType = root.MediaType ?? string.Empty;
            var isTv = mediaType.Equals("TV", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("Videos", StringComparison.OrdinalIgnoreCase);
            var isSeason = isTv && root.ParentId is not null;
            var kind = isTv ? (isSeason ? "season" : "show")
                : mediaType.Equals("Music", StringComparison.OrdinalIgnoreCase) ? "album"
                : "series";
            var title = isSeason
                ? SeasonLabel(root.Ordinal, root.Title)
                : string.IsNullOrWhiteSpace(root.Title) ? "Untitled" : root.Title.Trim();

            string? parentTitle = null;
            var parentOwned = 0;
            if (isSeason && root.ParentId is { } parentId)
            {
                parentOwned = LoadTree(connection, parentId, fromCollection: false, ct).Count(IsOwnedTitle);
                parentTitle = connection.QueryFirstOrDefault<string>(new CommandDefinition($"""
                    SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = @id AND cv.key IN ({TitleKeys}) AND NULLIF(cv.value, '') IS NOT NULL
                    ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 ELSE 2 END LIMIT 1;
                    """, new { id = GuidSql.ToBlob(parentId) }, cancellationToken: ct));
            }

            return Task.FromResult<LibraryItemDeletionGroup?>(Build(groupId, kind, title, mediaType, rows, parentTitle, parentOwned, isSeason ? root.ParentId : null));
        }

        var collection = connection.QueryFirstOrDefault<GroupRootRow>(new CommandDefinition("""
            -- readiness: a Series opened by id for the delete preview
            SELECT c.id AS Id, 'collection' AS Kind, NULL AS ParentId, COALESCE(c.media_type, '') AS MediaType, NULL AS Ordinal, c.display_name AS Title
            FROM collections c
            WHERE c.id = @id AND c.collection_type IN ('Series', 'ContentGroup') AND c.profile_id IS NULL;
            """,
            new { id = GuidSql.ToBlob(groupId) },
            cancellationToken: ct));
        if (collection is null)
        {
            return Task.FromResult<LibraryItemDeletionGroup?>(null);
        }

        var members = LoadTree(connection, groupId, fromCollection: true, ct);
        var memberType = members.Select(row => row.MediaType).FirstOrDefault(type => !string.IsNullOrEmpty(type))
            ?? collection.MediaType
            ?? string.Empty;
        return Task.FromResult<LibraryItemDeletionGroup?>(Build(
            groupId, "series", string.IsNullOrWhiteSpace(collection.Title) ? "Untitled Series" : collection.Title.Trim(), memberType, members, null, 0));
    }

    private static List<TreeRow> LoadTree(System.Data.IDbConnection connection, Guid rootId, bool fromCollection, CancellationToken ct)
    {
        var seed = fromCollection
            ? "SELECT w.id FROM works w WHERE w.collection_id = @root -- readiness: members of a Series opened by id for the delete preview"
            : "SELECT @root";
        return connection.Query<TreeRow>(new CommandDefinition($"""
            -- readiness: the titles under a group opened by id for the delete preview
            WITH RECURSIVE tree(id) AS (
                {seed}
                UNION
                SELECT w.id FROM works w JOIN tree t ON w.parent_work_id = t.id
            )
            SELECT w.id AS Id, w.parent_work_id AS ParentId, w.work_kind AS Kind, w.is_catalog_only AS IsCatalogOnly,
                   w.ownership AS Ownership, w.ordinal AS Ordinal, w.media_type AS MediaType,
                   CASE WHEN w.work_kind = 'parent' THEN
                       (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ({TitleKeys}) AND NULLIF(cv.value, '') IS NOT NULL
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 WHEN 'album' THEN 2 ELSE 3 END LIMIT 1)
                   END AS Title
            FROM works w
            WHERE w.id IN (SELECT id FROM tree)
              AND {VaultSql.NotVaultEntity("w.id")}
            ORDER BY w.ordinal_sort, w.ordinal, w.id;
            """,
            new { root = GuidSql.ToBlob(rootId) },
            cancellationToken: ct)).ToList();
    }

    private static bool IsTitle(TreeRow row) =>
        !string.Equals(row.Kind, "parent", StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnedTitle(TreeRow row) =>
        IsTitle(row)
        && row.IsCatalogOnly == 0
        && !string.Equals(row.Kind, "catalog", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(row.Ownership, "Unowned", StringComparison.OrdinalIgnoreCase);

    private static LibraryItemDeletionGroup Build(
        Guid groupId, string kind, string title, string mediaType, List<TreeRow> rows, string? parentTitle, int parentOwned, Guid? parentGroupId = null)
    {
        var byId = rows.ToDictionary(row => row.Id);
        var owned = rows.Where(IsOwnedTitle).ToList();
        var notOwned = rows.Count(row => IsTitle(row) && !IsOwnedTitle(row));

        // A part of the group (a season) is the parent Work under the group's own root that holds the title.
        var subGroups = new List<LibraryItemDeletionSubGroup>();
        if (!string.Equals(kind, "series", StringComparison.Ordinal))
        {
            var perParent = rows.Where(IsTitle)
                .Where(row => row.ParentId is { } parent && parent != groupId && byId.ContainsKey(parent))
                .GroupBy(row => row.ParentId!.Value);
            foreach (var part in perParent)
            {
                var ownedHere = part.Count(IsOwnedTitle);
                if (ownedHere == 0)
                {
                    continue;
                }

                var parentRow = byId[part.Key];
                subGroups.Add(new LibraryItemDeletionSubGroup(
                    part.Key, SeasonLabel(parentRow.Ordinal, parentRow.Title), ownedHere, part.Count()));
            }
        }

        return new LibraryItemDeletionGroup(
            groupId,
            kind,
            title,
            mediaType,
            [.. owned.Select(row => row.Id)],
            notOwned,
            [.. subGroups.OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase)],
            parentTitle,
            parentOwned,
            parentGroupId);
    }

    private static string SeasonLabel(long? ordinal, string? title)
    {
        if (ordinal is { } number)
        {
            return number == 0 ? "Specials" : $"Season {number.ToString(CultureInfo.InvariantCulture)}";
        }

        return string.IsNullOrWhiteSpace(title) ? "Season" : title.Trim();
    }

    private sealed class GroupRootRow
    {
        public Guid Id { get; init; }
        public string? Kind { get; init; }
        public Guid? ParentId { get; init; }
        public string? MediaType { get; init; }
        public long? Ordinal { get; init; }
        public string? Title { get; init; }
    }

    private sealed class TreeRow
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public string? Kind { get; init; }
        public long IsCatalogOnly { get; init; }
        public string? Ownership { get; init; }
        public long? Ordinal { get; init; }
        public string? MediaType { get; init; }
        public string? Title { get; init; }
    }
}
