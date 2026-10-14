using System.Globalization;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// The global tag, correspondent, document type and custom field lists. Names are unique ignoring case across the
/// whole install, but what a person can <em>see</em> is decided here on every read: a name is visible when it is in
/// use on an item in one of the viewer's approved libraries (the same library test the item listings use) or the
/// viewer created it. Nothing in this class lists a name that only appears on someone else's private item.
/// </summary>
public sealed class ViewCatalogueRepository(IDatabaseConnection database) : IViewCatalogueRepository
{
    private const int MaximumFieldChoices = 100;
    private const int MaximumNotesLength = 8000;
    private const int MaximumArchiveNumberLength = 120;
    private const int MaximumTitleLength = 300;
    private const int MaximumTextValueLength = 1000;

    private sealed record KindSpec(string Table, string Label, string UsageFrom, string UsageKey, string ColourColumn, string KindColumn);

    private static KindSpec Spec(ViewCatalogueKind kind) => kind switch
    {
        ViewCatalogueKind.Tag => new("tags", "tag",
            "local_item_tag_links x JOIN local_items li ON li.id = x.item_id", "x.tag_id", "e.colour", "NULL"),
        ViewCatalogueKind.Correspondent => new("doc_correspondents", "correspondent",
            "doc_item_details x JOIN local_items li ON li.id = x.item_id", "x.correspondent_id", "e.colour", "NULL"),
        ViewCatalogueKind.DocumentType => new("doc_types", "document type",
            "doc_item_details x JOIN local_items li ON li.id = x.item_id", "x.doc_type_id", "NULL", "NULL"),
        ViewCatalogueKind.Field => new("doc_field_definitions", "field",
            "doc_field_values x JOIN local_items li ON li.id = x.item_id", "x.field_id", "NULL", "e.kind"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class EntryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Colour { get; init; }
        public Guid? CreatedBy { get; init; }
        public string? FieldKind { get; init; }
        public long Usage { get; init; }
    }

    private sealed class ItemAccessRow
    {
        public Guid LibraryId { get; init; }
        public string MediaKind { get; init; } = string.Empty;
    }

    private sealed class ChoiceRow
    {
        public Guid FieldId { get; init; }
        public string Value { get; init; } = string.Empty;
    }

    public ViewCatalogueSnapshot GetCatalogue(
        ViewCatalogueViewer viewer, IReadOnlyCollection<string>? mediaKinds = null, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var tags = ReadEntries(connection, null, ViewCatalogueKind.Tag, viewer, mediaKinds, null);
        var correspondents = ReadEntries(connection, null, ViewCatalogueKind.Correspondent, viewer, mediaKinds, null);
        var types = ReadEntries(connection, null, ViewCatalogueKind.DocumentType, viewer, mediaKinds, null);
        var fields = ReadEntries(connection, null, ViewCatalogueKind.Field, viewer, mediaKinds, null);
        return new ViewCatalogueSnapshot(tags, correspondents, types, fields);
    }

    public ViewCatalogueEntry? FindEntry(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return ReadEntries(connection, null, kind, viewer, null, id).SingleOrDefault();
    }

    public IReadOnlyList<string> GetTagSuggestions(
        ViewCatalogueViewer viewer, string? search, int limit, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        ct.ThrowIfCancellationRequested();
        var take = Math.Clamp(limit, 1, 100);
        var pattern = string.IsNullOrWhiteSpace(search) ? null : "%" + ViewCatalogueSql.EscapeLike(search.Trim()) + "%";
        var parameters = new DynamicParameters();
        var libraries = ViewCatalogueSql.LibraryPredicate("li", viewer.AccessibleLibraryIds, parameters);
        ViewCatalogueSql.AddViewer(parameters, viewer);
        parameters.Add("pattern", pattern);
        parameters.Add("take", take);
        using var connection = database.CreateConnection();
        // Visible catalogue tags weigh by how many items carry them; the saved Library tags are the viewer's own.
        return connection.Query<string>(new CommandDefinition($"""
            WITH terms(value, weight) AS (
                SELECT e.name,
                       (SELECT COUNT(*) FROM local_item_tag_links x JOIN local_items li ON li.id = x.item_id
                         WHERE x.tag_id = e.id AND {libraries} AND {ViewCatalogueSql.ItemVisibleToViewer})
                  FROM tags e
                 WHERE e.created_by_profile_id = @viewer
                    OR EXISTS (SELECT 1 FROM local_item_tag_links x JOIN local_items li ON li.id = x.item_id
                                WHERE x.tag_id = e.id AND {libraries} AND {ViewCatalogueSql.ItemVisibleToViewer})
                UNION ALL
                SELECT json.value, 1 FROM profile_work_preferences pref, json_each(pref.local_tags_json) json
                 WHERE pref.profile_id = @viewer AND json_valid(pref.local_tags_json)
                UNION ALL
                SELECT json.value, 1 FROM profile_person_preferences pref, json_each(pref.local_tags_json) json
                 WHERE pref.profile_id = @viewer AND json_valid(pref.local_tags_json)
            )
            SELECT TRIM(value)
              FROM terms
             WHERE TRIM(value) <> '' AND (@pattern IS NULL OR value LIKE @pattern ESCAPE '~' COLLATE NOCASE)
             GROUP BY LOWER(TRIM(value))
             ORDER BY SUM(weight) DESC, value COLLATE NOCASE
             LIMIT @take;
            """, parameters, cancellationToken: ct)).ToList();
    }

    public Task<ViewCatalogueEntry> CreateEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, string name, string? colour, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        if (kind == ViewCatalogueKind.Field)
        {
            throw new ArgumentException("Custom fields are created with CreateFieldAsync.", nameof(kind));
        }

        var spec = Spec(kind);
        var normalizedName = ViewCatalogueSql.NormalizeName(name, spec.Label);
        var normalizedColour = spec.ColourColumn == "NULL" ? null : ViewCatalogueSql.NormalizeColour(colour);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            EnsureNameFree(connection, transaction, spec, normalizedName, null);
            var id = Guid.NewGuid();
            if (spec.ColourColumn == "NULL")
            {
                connection.Execute($"""
                    INSERT INTO {spec.Table} (id, name, created_by_profile_id, created_at)
                    VALUES (@id, @normalizedName, @profileId, @now);
                    """, new { id, normalizedName, profileId = viewer.ProfileId, now = DateTimeOffset.UtcNow }, transaction);
            }
            else
            {
                connection.Execute($"""
                    INSERT INTO {spec.Table} (id, name, colour, created_by_profile_id, created_at)
                    VALUES (@id, @normalizedName, @normalizedColour, @profileId, @now);
                    """, new { id, normalizedName, normalizedColour, profileId = viewer.ProfileId, now = DateTimeOffset.UtcNow }, transaction);
            }

            return ReadEntries(connection, transaction, kind, viewer, null, id).Single();
        }, ct);
    }

    public Task<ViewCatalogueEntry> CreateFieldAsync(
        ViewCatalogueViewer viewer, string name, string fieldKind, IReadOnlyList<string>? choices, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        var spec = Spec(ViewCatalogueKind.Field);
        var normalizedName = ViewCatalogueSql.NormalizeName(name, spec.Label);
        var kind = (fieldKind ?? string.Empty).Trim().ToLowerInvariant();
        if (!ViewDocumentFieldKinds.All.Contains(kind))
        {
            throw new ArgumentException($"A field must be one of: {string.Join(", ", ViewDocumentFieldKinds.All)}.", nameof(fieldKind));
        }

        var normalizedChoices = NormalizeChoices(kind, choices);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            EnsureNameFree(connection, transaction, spec, normalizedName, null);
            var id = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO doc_field_definitions (id, name, kind, created_by_profile_id, created_at)
                VALUES (@id, @normalizedName, @kind, @profileId, @now);
                """, new { id, normalizedName, kind, profileId = viewer.ProfileId, now = DateTimeOffset.UtcNow }, transaction);
            WriteChoices(connection, transaction, id, normalizedChoices);
            return ReadEntries(connection, transaction, ViewCatalogueKind.Field, viewer, null, id).Single();
        }, ct);
    }

    public Task<ViewCatalogueEntry> UpdateEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, string name, string? colour, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        if (kind == ViewCatalogueKind.Field)
        {
            throw new ArgumentException("Custom fields are changed with UpdateFieldAsync.", nameof(kind));
        }

        var spec = Spec(kind);
        var normalizedName = ViewCatalogueSql.NormalizeName(name, spec.Label);
        var normalizedColour = spec.ColourColumn == "NULL" ? null : ViewCatalogueSql.NormalizeColour(colour);
        // No colour sent keeps the colour as it is; an empty one clears it.
        var keepColour = spec.ColourColumn == "NULL" || colour is null;
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireVisible(connection, transaction, kind, id, viewer);
            EnsureNameFree(connection, transaction, spec, normalizedName, id);
            if (keepColour)
            {
                connection.Execute($"UPDATE {spec.Table} SET name = @normalizedName WHERE id = @id;",
                    new { id, normalizedName }, transaction);
            }
            else
            {
                connection.Execute($"UPDATE {spec.Table} SET name = @normalizedName, colour = @normalizedColour WHERE id = @id;",
                    new { id, normalizedName, normalizedColour }, transaction);
            }

            RebuildCarriers(connection, transaction, kind, id);
            return ReadEntries(connection, transaction, kind, viewer, null, id).Single();
        }, ct);
    }

    public Task<ViewCatalogueEntry> UpdateFieldAsync(
        ViewCatalogueViewer viewer, Guid id, string name, IReadOnlyList<string>? choices, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        var spec = Spec(ViewCatalogueKind.Field);
        var normalizedName = ViewCatalogueSql.NormalizeName(name, spec.Label);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireVisible(connection, transaction, ViewCatalogueKind.Field, id, viewer);
            EnsureNameFree(connection, transaction, spec, normalizedName, id);
            var kind = connection.ExecuteScalar<string>(
                "SELECT kind FROM doc_field_definitions WHERE id = @id;", new { id }, transaction)!;
            var normalizedChoices = NormalizeChoices(kind, choices);
            connection.Execute("UPDATE doc_field_definitions SET name = @normalizedName WHERE id = @id;",
                new { id, normalizedName }, transaction);
            if (kind == ViewDocumentFieldKinds.Choice)
            {
                WriteChoices(connection, transaction, id, normalizedChoices);
                // A value whose choice was taken away no longer means anything, so it goes with it.
                // Matching ignores case, so only changing a choice's letter case keeps every value, in its new spelling.
                connection.Execute("""
                    UPDATE doc_field_values
                       SET text_value = (SELECT c.value FROM doc_field_choices c
                                          WHERE c.field_id = doc_field_values.field_id
                                            AND c.value = doc_field_values.text_value COLLATE NOCASE)
                     WHERE field_id = @id AND text_value IS NOT NULL
                       AND EXISTS (SELECT 1 FROM doc_field_choices c
                                    WHERE c.field_id = @id AND c.value = doc_field_values.text_value COLLATE NOCASE);
                    DELETE FROM doc_field_values
                     WHERE field_id = @id
                       AND (text_value IS NULL
                            OR NOT EXISTS (SELECT 1 FROM doc_field_choices c
                                            WHERE c.field_id = @id AND c.value = doc_field_values.text_value COLLATE NOCASE));
                    """, new { id }, transaction);
            }

            RebuildCarriers(connection, transaction, ViewCatalogueKind.Field, id);
            return ReadEntries(connection, transaction, ViewCatalogueKind.Field, viewer, null, id).Single();
        }, ct);
    }

    public Task<bool> DeleteEntryAsync(
        ViewCatalogueViewer viewer, ViewCatalogueKind kind, Guid id, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        var spec = Spec(kind);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsVisible(connection, transaction, kind, id, viewer))
            {
                return false;
            }

            var carriers = CarrierItemIds(connection, transaction, kind, id);
            // Links, details and values go with the entry (cascade or cleared); the items themselves are untouched.
            connection.Execute($"DELETE FROM {spec.Table} WHERE id = @id;", new { id }, transaction);
            foreach (var itemId in carriers)
            {
                LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            }

            return true;
        }, ct);
    }

    public Task ReplaceItemTagsAsync(
        ViewCatalogueViewer viewer, Guid itemId, IReadOnlyCollection<string> tagNames,
        IReadOnlyCollection<Guid> tagIds, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        ArgumentNullException.ThrowIfNull(tagNames);
        ArgumentNullException.ThrowIfNull(tagIds);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireAccessibleItem(connection, transaction, viewer, itemId);
            var now = DateTimeOffset.UtcNow;
            var ids = ResolveVisibleTagIds(connection, transaction, viewer, tagNames, tagIds, now);
            ViewCatalogueSql.ReplaceTagLinks(connection, transaction, itemId, ids, now);
            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
        }, ct);
    }

    public Task<bool> UpdateDocumentDetailsAsync(
        ViewCatalogueViewer viewer, Guid itemId, ViewDocumentDetailsWrite write, CancellationToken ct = default)
    {
        ValidateViewer(viewer);
        ArgumentNullException.ThrowIfNull(write);
        var title = string.IsNullOrWhiteSpace(write.Title) ? null : write.Title.Trim();
        if (title?.Length > MaximumTitleLength)
        {
            throw new ArgumentException($"A title cannot be longer than {MaximumTitleLength} characters.", nameof(write));
        }

        var archiveNumber = string.IsNullOrWhiteSpace(write.ArchiveNumber) ? null : write.ArchiveNumber.Trim();
        if (archiveNumber?.Length > MaximumArchiveNumberLength)
        {
            throw new ArgumentException($"An archive number cannot be longer than {MaximumArchiveNumberLength} characters.", nameof(write));
        }

        var notes = string.IsNullOrWhiteSpace(write.Notes) ? null : write.Notes.Trim();
        if (notes?.Length > MaximumNotesLength)
        {
            throw new ArgumentException($"Notes cannot be longer than {MaximumNotesLength} characters.", nameof(write));
        }

        var requestedState = string.IsNullOrWhiteSpace(write.FilingState) ? null : write.FilingState.Trim().ToLowerInvariant();
        if (requestedState is not null && requestedState is not (ViewDocumentFilingStates.Inbox or ViewDocumentFilingStates.Filed))
        {
            throw new ArgumentException("Filing state must be inbox or filed.", nameof(write));
        }

        var correspondent = string.IsNullOrWhiteSpace(write.Correspondent)
            ? null : ViewCatalogueSql.NormalizeName(write.Correspondent, "correspondent");
        var documentType = string.IsNullOrWhiteSpace(write.DocumentType)
            ? null : ViewCatalogueSql.NormalizeName(write.DocumentType, "document type");
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var mediaKind = RequireAccessibleItem(connection, transaction, viewer, itemId);
            if (!string.Equals(mediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal))
            {
                throw new ArgumentException("Only documents have organising details.");
            }

            var now = DateTimeOffset.UtcNow;
            var fieldRows = write.Fields is null ? null : ValidateFieldWrites(connection, transaction, viewer, write.Fields);
            var tagIds = ResolveVisibleTagIds(connection, transaction, viewer, write.TagNames, write.TagIds, now);
            var correspondentId = correspondent is null
                ? (Guid?)null : ViewCatalogueSql.FindOrCreate(connection, transaction, "doc_correspondents", correspondent, viewer.ProfileId, now);
            var typeId = documentType is null
                ? (Guid?)null : ViewCatalogueSql.FindOrCreate(connection, transaction, "doc_types", documentType, viewer.ProfileId, now);
            var state = requestedState
                ?? connection.ExecuteScalar<string?>(
                    "SELECT filing_state FROM doc_item_details WHERE item_id = @itemId;", new { itemId }, transaction)
                ?? ViewDocumentFilingStates.Filed;

            connection.Execute("UPDATE local_items SET title = @title, updated_at = @now WHERE id = @itemId;",
                new { itemId, title, now }, transaction);
            connection.Execute("""
                INSERT INTO doc_item_details
                    (item_id, document_date, correspondent_id, doc_type_id, archive_number, notes, filing_state, updated_at)
                VALUES (@itemId, @date, @correspondentId, @typeId, @archiveNumber, @notes, @state, @now)
                ON CONFLICT(item_id) DO UPDATE SET
                    document_date = excluded.document_date,
                    correspondent_id = excluded.correspondent_id,
                    doc_type_id = excluded.doc_type_id,
                    archive_number = excluded.archive_number,
                    notes = excluded.notes,
                    filing_state = excluded.filing_state,
                    updated_at = excluded.updated_at;
                """, new
            {
                itemId,
                date = write.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                correspondentId,
                typeId,
                archiveNumber,
                notes,
                state,
                now,
            }, transaction);
            ViewCatalogueSql.ReplaceTagLinks(connection, transaction, itemId, tagIds, now);
            if (fieldRows is not null)
            {
                connection.Execute("DELETE FROM doc_field_values WHERE item_id = @itemId;", new { itemId }, transaction);
                foreach (var row in fieldRows)
                {
                    connection.Execute("""
                        INSERT INTO doc_field_values (item_id, field_id, text_value, number_value, date_value, bool_value)
                        VALUES (@itemId, @FieldId, @Text, @Number, @Date, @Flag);
                        """, new { itemId, row.FieldId, row.Text, row.Number, row.Date, row.Flag }, transaction);
                }
            }

            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            return true;
        }, ct);
    }

    private sealed record FieldRow(Guid FieldId, string? Text, double? Number, string? Date, int? Flag);

    /// <summary>
    /// Checks every submitted value against its field definition (kind, choice list, money code) and that the viewer
    /// may see the field. Values with nothing in them are dropped, which clears the field.
    /// </summary>
    private static List<FieldRow> ValidateFieldWrites(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueViewer viewer,
        IReadOnlyCollection<ViewDocumentFieldWrite> writes)
    {
        var rows = new List<FieldRow>();
        var seen = new HashSet<Guid>();
        foreach (var write in writes ?? [])
        {
            if (!seen.Add(write.FieldId))
            {
                throw new ArgumentException("A field can only be given once.");
            }

            if (!IsVisible(connection, transaction, ViewCatalogueKind.Field, write.FieldId, viewer))
            {
                throw new ViewCatalogueAccessException("That field is not available.");
            }

            var kind = connection.ExecuteScalar<string>(
                "SELECT kind FROM doc_field_definitions WHERE id = @FieldId;", new { write.FieldId }, transaction)!;
            var text = string.IsNullOrWhiteSpace(write.Text) ? null : write.Text.Trim();
            if (text?.Length > MaximumTextValueLength)
            {
                throw new ArgumentException($"A field value cannot be longer than {MaximumTextValueLength} characters.");
            }

            switch (kind)
            {
                case ViewDocumentFieldKinds.Text:
                    if (text is not null)
                    {
                        rows.Add(new FieldRow(write.FieldId, text, null, null, null));
                    }

                    break;
                case ViewDocumentFieldKinds.Choice:
                    if (text is not null)
                    {
                        var canonical = connection.QuerySingleOrDefault<string?>(
                            "SELECT value FROM doc_field_choices WHERE field_id = @FieldId AND value = @text COLLATE NOCASE;",
                            new { write.FieldId, text }, transaction)
                            ?? throw new ArgumentException($"'{text}' is not one of the choices for this field.");
                        rows.Add(new FieldRow(write.FieldId, canonical, null, null, null));
                    }

                    break;
                case ViewDocumentFieldKinds.Number:
                    if (write.Number is { } number)
                    {
                        rows.Add(new FieldRow(write.FieldId, null, FiniteNumber(number), null, null));
                    }

                    break;
                case ViewDocumentFieldKinds.Money:
                    if (write.Number is { } amount)
                    {
                        var code = text?.ToUpperInvariant();
                        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
                        {
                            throw new ArgumentException("An amount needs a three-letter currency code such as EUR.");
                        }

                        rows.Add(new FieldRow(write.FieldId, code, FiniteNumber(amount), null, null));
                    }

                    break;
                case ViewDocumentFieldKinds.Date:
                    if (write.Date is { } date)
                    {
                        rows.Add(new FieldRow(write.FieldId, null, null, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null));
                    }

                    break;
                case ViewDocumentFieldKinds.Bool:
                    if (write.Flag is { } flag)
                    {
                        rows.Add(new FieldRow(write.FieldId, null, null, null, flag ? 1 : 0));
                    }

                    break;
            }
        }

        return rows;
    }

    private static double FiniteNumber(double value) =>
        double.IsFinite(value) ? value : throw new ArgumentException("A number field needs a real number.");

    private static List<Guid> ResolveVisibleTagIds(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueViewer viewer,
        IEnumerable<string> names, IEnumerable<Guid> ids, DateTimeOffset now)
    {
        var result = ViewCatalogueSql.ResolveTagIds(connection, transaction, names, viewer.ProfileId, now);
        foreach (var id in ids.Distinct())
        {
            // Guessing the id of a tag from someone else's private item gets the same answer as an unknown id.
            if (!IsVisible(connection, transaction, ViewCatalogueKind.Tag, id, viewer))
            {
                throw new ViewCatalogueAccessException("That tag is not available.");
            }

            result.Add(id);
        }

        return result;
    }

    /// <summary>Returns the item's media kind, or throws when it does not exist or is outside the viewer's libraries.</summary>
    private static string RequireAccessibleItem(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueViewer viewer, Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item ID is required.", nameof(itemId));
        }

        var item = connection.QuerySingleOrDefault<ItemAccessRow>(
            "SELECT library_id AS LibraryId, media_kind AS MediaKind FROM local_items WHERE id = @itemId;",
            new { itemId }, transaction);
        if (item is null)
        {
            throw new ViewCatalogueAccessException("That item is not available.");
        }

        if (!viewer.AccessibleLibraryIds.Contains(item.LibraryId))
        {
            throw new ViewCatalogueAccessException("That item is not available.");
        }

        return item.MediaKind;
    }

    private static void EnsureNameFree(
        SqliteConnection connection, SqliteTransaction transaction, KindSpec spec, string name, Guid? exceptId)
    {
        if (ViewCatalogueSql.FindExistingId(connection, transaction, spec.Table, name, exceptId).HasValue)
        {
            throw new ViewCatalogueConflictException($"A {spec.Label} named '{name}' already exists.");
        }
    }

    private static IReadOnlyList<string> NormalizeChoices(string kind, IReadOnlyList<string>? choices)
    {
        if (kind != ViewDocumentFieldKinds.Choice)
        {
            return [];
        }

        var normalized = (choices ?? [])
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => ViewCatalogueSql.NormalizeName(choice, "choice"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            throw new ArgumentException("A choice field needs at least one choice.");
        }

        if (normalized.Count > MaximumFieldChoices)
        {
            throw new ArgumentException($"A choice field can have at most {MaximumFieldChoices} choices.");
        }

        return normalized;
    }

    private static void WriteChoices(
        SqliteConnection connection, SqliteTransaction transaction, Guid fieldId, IReadOnlyList<string> choices)
    {
        connection.Execute("DELETE FROM doc_field_choices WHERE field_id = @fieldId;", new { fieldId }, transaction);
        for (var index = 0; index < choices.Count; index++)
        {
            connection.Execute("""
                INSERT INTO doc_field_choices (field_id, value, sort_order) VALUES (@fieldId, @value, @index);
                """, new { fieldId, value = choices[index], index }, transaction);
        }
    }

    private static List<Guid> CarrierItemIds(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueKind kind, Guid id)
    {
        var spec = Spec(kind);
        // The usage table and key are this class's own constants, never caller input.
        return connection.Query<Guid>(
            $"SELECT DISTINCT x.item_id FROM {spec.UsageFrom} WHERE {spec.UsageKey} = @id;", new { id }, transaction).ToList();
    }

    private static void RebuildCarriers(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueKind kind, Guid id)
    {
        foreach (var itemId in CarrierItemIds(connection, transaction, kind, id))
        {
            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
        }
    }

    private static bool IsVisible(
        SqliteConnection connection, SqliteTransaction? transaction, ViewCatalogueKind kind, Guid id, ViewCatalogueViewer viewer) =>
        ReadEntries(connection, transaction, kind, viewer, null, id).Count != 0;

    private static void RequireVisible(
        SqliteConnection connection, SqliteTransaction transaction, ViewCatalogueKind kind, Guid id, ViewCatalogueViewer viewer)
    {
        if (!IsVisible(connection, transaction, kind, id, viewer))
        {
            throw new ViewCatalogueAccessException($"That {Spec(kind).Label} is not available.");
        }
    }

    /// <summary>
    /// The one visibility query. An entry is visible to the viewer when they created it, or it is carried by an item
    /// in one of the libraries the resolver approved (<see cref="ViewCatalogueSql.LibraryPredicate"/>). The usage
    /// count only covers active, unhidden items the viewer can reach, optionally of the given media kinds.
    /// </summary>
    private static List<ViewCatalogueEntry> ReadEntries(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ViewCatalogueKind kind,
        ViewCatalogueViewer viewer,
        IReadOnlyCollection<string>? mediaKinds,
        Guid? onlyId)
    {
        var spec = Spec(kind);
        var parameters = new DynamicParameters();
        var libraries = ViewCatalogueSql.LibraryPredicate("li", viewer.AccessibleLibraryIds, parameters);
        var kinds = (mediaKinds ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant()).Distinct().ToArray();
        ViewCatalogueSql.AddViewer(parameters, viewer);
        parameters.Add("hasKinds", kinds.Length == 0 ? 0 : 1);
        parameters.Add("kinds", kinds.Length == 0 ? new[] { LocalAssetMediaKinds.Other } : kinds);
        parameters.Add("onlyId", onlyId.HasValue ? GuidSql.ToBlob(onlyId.Value) : null, System.Data.DbType.Binary);
        var rows = connection.Query<EntryRow>($"""
            SELECT e.id AS Id, e.name AS Name, {spec.ColourColumn} AS Colour,
                   e.created_by_profile_id AS CreatedBy, {spec.KindColumn} AS FieldKind,
                   (SELECT COUNT(DISTINCT li.id) FROM {spec.UsageFrom}
                     WHERE {spec.UsageKey} = e.id AND {libraries} AND {ViewCatalogueSql.ItemVisibleToViewer}
                       AND li.archived_at IS NULL AND li.trashed_at IS NULL AND li.hidden = 0
                       AND (@hasKinds = 0 OR li.media_kind IN @kinds)) AS Usage
              FROM {spec.Table} e
             WHERE (@onlyId IS NULL OR e.id = @onlyId)
               AND (e.created_by_profile_id = @viewer
                    OR EXISTS (SELECT 1 FROM {spec.UsageFrom}
                                WHERE {spec.UsageKey} = e.id AND {libraries} AND {ViewCatalogueSql.ItemVisibleToViewer}))
             ORDER BY e.name COLLATE NOCASE;
            """, parameters, transaction).ToList();
        var choices = kind == ViewCatalogueKind.Field && rows.Count != 0
            ? connection.Query<ChoiceRow>("""
                SELECT field_id AS FieldId, value AS Value FROM doc_field_choices ORDER BY field_id, sort_order, value;
                """, transaction: transaction).ToLookup(row => row.FieldId, row => row.Value)
            : null;
        return rows.Select(row => new ViewCatalogueEntry(
            row.Id, kind, row.Name, row.Colour, row.FieldKind,
            choices is null ? [] : choices[row.Id].ToList(),
            row.CreatedBy, checked((int)row.Usage))).ToList();
    }

    private static void ValidateViewer(ViewCatalogueViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("A viewer profile is required.", nameof(viewer));
        }

        ArgumentNullException.ThrowIfNull(viewer.AccessibleLibraryIds);
    }
}
