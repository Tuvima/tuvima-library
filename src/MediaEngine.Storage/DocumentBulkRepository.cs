using System.Globalization;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <inheritdoc cref="IDocumentBulkRepository"/>
public sealed class DocumentBulkRepository(IDatabaseConnection database) : IDocumentBulkRepository
{
    private sealed class SubjectRow
    {
        public Guid Id { get; init; }
        public Guid LibraryId { get; init; }
        public string MediaKind { get; init; } = string.Empty;
        public string ScopeKind { get; init; } = string.Empty;
        public string? FilingState { get; init; }
        public bool Trashed { get; init; }
    }

    public IReadOnlyList<DocumentBulkSubject> Describe(
        ViewCatalogueViewer viewer, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(itemIds);
        ct.ThrowIfCancellationRequested();
        var result = new List<DocumentBulkSubject>(itemIds.Count);
        using var connection = database.CreateConnection();
        foreach (var id in itemIds)
        {
            var row = connection.QuerySingleOrDefault<SubjectRow>(
                """
                SELECT li.id AS Id, li.library_id AS LibraryId, li.media_kind AS MediaKind, li.scope_kind AS ScopeKind,
                       (SELECT dd.filing_state FROM doc_item_details dd WHERE dd.item_id = li.id) AS FilingState,
                       li.trashed_at IS NOT NULL AS Trashed
                  FROM local_items li WHERE li.id = @id;
                """,
                new { id });
            // An id outside the viewer's libraries looks exactly like an id that does not exist.
            var reachable = row is not null && viewer.AccessibleLibraryIds.Contains(row.LibraryId);
            result.Add(new DocumentBulkSubject(
                id,
                reachable,
                reachable && row!.MediaKind == LocalAssetMediaKinds.Document,
                reachable && row!.ScopeKind == "shared",
                reachable && row!.FilingState == ViewDocumentFilingStates.Inbox,
                reachable && row!.Trashed));
        }

        return result;
    }

    public Task<DocumentBulkItemResult> ApplyAsync(
        ViewCatalogueViewer viewer, Guid itemId, DocumentBulkChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(change);
        if (viewer.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("A viewer profile is required.", nameof(viewer));
        }

        var name = string.IsNullOrWhiteSpace(change.Name) ? null : change.Name.Trim();
        var label = change.Kind == DocumentBulkChangeKind.SetCorrespondent ? "correspondent" : "document type";
        if (name is not null && change.Kind is DocumentBulkChangeKind.SetCorrespondent or DocumentBulkChangeKind.SetDocumentType)
        {
            name = ViewCatalogueSql.NormalizeName(name, label);
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!viewer.IncludeDocuments)
            {
                return Refused(itemId, "Documents are not available for this profile.");
            }

            string mediaKind;
            try
            {
                mediaKind = ViewCatalogueRepository.RequireAccessibleItem(connection, transaction, viewer, itemId);
            }
            catch (ViewCatalogueAccessException)
            {
                return Refused(itemId, "This document is not available.");
            }

            if (!string.Equals(mediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal))
            {
                return Refused(itemId, "Only documents can be organised this way.");
            }

            var now = DateTimeOffset.UtcNow;
            var outcome = change.Kind switch
            {
                DocumentBulkChangeKind.AddTags => AddTags(connection, transaction, viewer, itemId, change, now),
                DocumentBulkChangeKind.RemoveTags => RemoveTags(connection, transaction, itemId, change),
                DocumentBulkChangeKind.SetCorrespondent => SetName(connection, transaction, viewer, itemId, name, now,
                    "doc_correspondents", "correspondent_id", DocumentFieldSources.Correspondent),
                DocumentBulkChangeKind.SetDocumentType => SetName(connection, transaction, viewer, itemId, name, now,
                    "doc_types", "doc_type_id", DocumentFieldSources.DocumentType),
                DocumentBulkChangeKind.SetDocumentDate => SetDate(connection, transaction, itemId, change.Date, now),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            if (outcome == DocumentBulkOutcome.Done)
            {
                LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            }

            return new DocumentBulkItemResult(itemId, outcome);
        }, ct);
    }

    private static DocumentBulkItemResult Refused(Guid itemId, string reason) =>
        new(itemId, DocumentBulkOutcome.Refused, reason);

    /// <summary>A document with no organising row yet counts as filed; changing it needs a row to hold the change.</summary>
    private static void EnsureDetails(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, DateTimeOffset now) =>
        connection.Execute("""
            INSERT INTO doc_item_details (item_id, filing_state, updated_at)
            VALUES (@itemId, 'filed', @now)
            ON CONFLICT(item_id) DO NOTHING;
            """, new { itemId, now }, transaction);

    private static DocumentBulkOutcome AddTags(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueViewer viewer, Guid itemId,
        DocumentBulkChange change, DateTimeOffset now)
    {
        var tagIds = ViewCatalogueRepository.ResolveVisibleTagIds(
            connection, transaction, viewer, change.TagNames ?? [], change.TagIds ?? [], now);
        var added = 0;
        foreach (var tagId in tagIds.Distinct())
        {
            added += connection.Execute("""
                INSERT OR IGNORE INTO local_item_tag_links (item_id, tag_id, added_at)
                VALUES (@itemId, @tagId, @now);
                """, new { itemId, tagId, now }, transaction);
        }

        return added == 0 ? DocumentBulkOutcome.Unchanged : DocumentBulkOutcome.Done;
    }

    private static DocumentBulkOutcome RemoveTags(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, DocumentBulkChange change)
    {
        var removed = 0;
        foreach (var tagId in (change.TagIds ?? []).Distinct())
        {
            removed += connection.Execute(
                "DELETE FROM local_item_tag_links WHERE item_id = @itemId AND tag_id = @tagId;",
                new { itemId, tagId }, transaction);
        }

        return removed == 0 ? DocumentBulkOutcome.Unchanged : DocumentBulkOutcome.Done;
    }

    private static DocumentBulkOutcome SetName(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueViewer viewer, Guid itemId,
        string? name, DateTimeOffset now, string table, string column, string slot)
    {
        Guid? newId = name is null ? null : ViewCatalogueSql.FindOrCreate(connection, transaction, table, name, viewer.ProfileId, now);
        var previous = connection.ExecuteScalar<Guid?>(
            $"SELECT {column} FROM doc_item_details WHERE item_id = @itemId;", new { itemId }, transaction);
        if (previous == newId)
        {
            return DocumentBulkOutcome.Unchanged;
        }

        EnsureDetails(connection, transaction, itemId, now);
        connection.Execute($"UPDATE doc_item_details SET {column} = @newId, updated_at = @now WHERE item_id = @itemId;",
            new { itemId, newId, now }, transaction);
        // A person chose this, so an auto-filing rule never replaces it afterwards (the same promise the editor makes).
        DocumentFieldSources.Mark(connection, transaction, itemId, slot, null, DocumentFieldSources.Person);
        return DocumentBulkOutcome.Done;
    }

    private static DocumentBulkOutcome SetDate(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, DateOnly? date, DateTimeOffset now)
    {
        var text = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var previous = connection.ExecuteScalar<string?>(
            "SELECT document_date FROM doc_item_details WHERE item_id = @itemId;", new { itemId }, transaction);
        if (previous == text)
        {
            return DocumentBulkOutcome.Unchanged;
        }

        EnsureDetails(connection, transaction, itemId, now);
        connection.Execute("UPDATE doc_item_details SET document_date = @text, updated_at = @now WHERE item_id = @itemId;",
            new { itemId, text, now }, transaction);
        return DocumentBulkOutcome.Done;
    }
}
