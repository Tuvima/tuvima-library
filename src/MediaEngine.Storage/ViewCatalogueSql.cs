using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQL pieces shared by the View item repository and the View catalogue repository, so who can reach an item and how
/// names are resolved are decided in one place.
/// </summary>
internal static class ViewCatalogueSql
{
    public const int MaximumNameLength = 120;

    /// <summary>
    /// The one test for "this viewer can reach this item": the item belongs to a library the resolver approved for
    /// the request. Parameters are added as <c>@LibraryId0</c>, <c>@LibraryId1</c>, and so on. An empty list reaches
    /// nothing.
    /// </summary>
    public static string LibraryPredicate(string alias, IReadOnlyCollection<Guid> libraryIds, DynamicParameters parameters)
    {
        if (libraryIds.Count == 0)
        {
            return "0 = 1";
        }

        var index = 0;
        var clauses = new List<string>(libraryIds.Count);
        foreach (var id in libraryIds)
        {
            parameters.Add($"LibraryId{index}", GuidSql.ToBlob(id), System.Data.DbType.Binary);
            clauses.Add($"{alias}.library_id = @LibraryId{index}");
            index++;
        }

        return "(" + string.Join(" OR ", clauses) + ")";
    }

    /// <summary>
    /// The conditions an item must meet for its names to count as visible to the viewer, on top of the library test:
    /// someone else's hidden item stays hidden, and a profile without Documents never learns names that only appear
    /// on documents. Needs the parameters added by <see cref="AddViewer"/>.
    /// </summary>
    public const string ItemVisibleToViewer =
        "li.missing_since IS NULL AND (li.hidden = 0 OR li.owner_profile_id = @viewer) AND (@includeDocuments = 1 OR li.media_kind <> 'document')"
        + " AND (@hasBlockedKinds = 0 OR li.content_kind IS NULL OR li.content_kind NOT IN @blockedKinds)";

    public static void AddViewer(DynamicParameters parameters, ViewCatalogueViewer viewer)
    {
        parameters.Add("viewer", GuidSql.ToBlob(viewer.ProfileId), System.Data.DbType.Binary);
        parameters.Add("includeDocuments", viewer.IncludeDocuments ? 1 : 0);
        var blocked = viewer.BlockedContentKinds ?? [];
        parameters.Add("hasBlockedKinds", blocked.Count == 0 ? 0 : 1);
        // An empty list would make the SQL invalid, so a placeholder stands in while hasBlockedKinds is 0.
        parameters.Add("blockedKinds", blocked.Count == 0 ? new[] { "photos" } : blocked.ToArray());
    }

    /// <summary>
    /// Finds an existing name ignoring letter case in every script. SQLite's NOCASE only folds A-Z, so the stored
    /// names are compared here too; "Ärzte" and "ärzte" are one name.
    /// </summary>
    public static Guid? FindExistingId(
        SqliteConnection connection, SqliteTransaction transaction, string table, string name, Guid? exceptId = null)
    {
        // The table name is one of this repository's own constants, never caller input.
        foreach (var row in connection.Query<NameRow>(
                     $"SELECT id AS Id, name AS Name FROM {table};", transaction: transaction))
        {
            if (row.Id != exceptId && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return row.Id;
            }
        }

        return null;
    }

    private sealed class NameRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>Trims a catalogue name and rejects empty, overlong or control-character names.</summary>
    public static string NormalizeName(string? name, string label)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"A {label} needs a name.", nameof(name));
        }

        if (trimmed.Length > MaximumNameLength)
        {
            throw new ArgumentException($"A {label} name cannot be longer than {MaximumNameLength} characters.", nameof(name));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"A {label} name cannot contain control characters.", nameof(name));
        }

        return trimmed;
    }

    /// <summary>Accepts a #RRGGBB colour or nothing.</summary>
    public static string? NormalizeColour(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour))
        {
            return null;
        }

        var value = colour.Trim();
        if (value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A colour must look like #1A2B3C.", nameof(colour));
        }

        return value.ToUpperInvariant();
    }

    /// <summary>
    /// Finds each tag by name (ignoring case) or adds it. A name that already exists is reused whoever made it:
    /// there is one global list, and attaching by name tells the person nothing they could not type themselves.
    /// </summary>
    public static List<Guid> ResolveTagIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> names,
        Guid? creatorProfileId,
        DateTimeOffset now)
    {
        var ids = new List<Guid>();
        foreach (var name in names
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => NormalizeName(name, "tag"))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(FindOrCreate(connection, transaction, "tags", name, creatorProfileId, now));
        }

        return ids;
    }

    /// <summary>Finds a correspondent or document type by name (ignoring case) or adds it.</summary>
    public static Guid FindOrCreate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string name,
        Guid? creatorProfileId,
        DateTimeOffset now)
    {
        var existing = FindExistingId(connection, transaction, table, name);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        var id = Guid.NewGuid();
        connection.Execute($"""
            INSERT INTO {table} (id, name, created_by_profile_id, created_at)
            VALUES (@id, @name, @creatorProfileId, @now);
            """, new { id, name, creatorProfileId, now }, transaction);
        return id;
    }

    public static void ReplaceTagLinks(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        IEnumerable<Guid> tagIds,
        DateTimeOffset now)
    {
        connection.Execute("DELETE FROM local_item_tag_links WHERE item_id = @itemId;", new { itemId }, transaction);
        foreach (var tagId in tagIds.Distinct())
        {
            connection.Execute("""
                INSERT OR IGNORE INTO local_item_tag_links (item_id, tag_id, added_at)
                VALUES (@itemId, @tagId, @now);
                """, new { itemId, tagId, now }, transaction);
        }
    }

    /// <summary>Escapes the characters LIKE treats as patterns, for use with <c>ESCAPE '~'</c>.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal);
}
