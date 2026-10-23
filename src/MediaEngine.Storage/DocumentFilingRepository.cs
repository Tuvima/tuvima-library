using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Auto-filing rules, Inbox suggestions and filing from the Inbox. Rules are only ever read for the document's own
/// library, and every value they write goes through the same field checks as the editor. Suggestions are built only
/// from documents in the same library, so they cannot repeat a name from someone else's private documents.
/// </summary>
public sealed class DocumentFilingRepository(IDatabaseConnection database) : IDocumentFilingRepository
{
    /// <summary>The longest stretch of document text a rule or the suggester looks at.</summary>
    public const int MaximumTextCharacters = 200_000;

    private sealed class SubjectRow
    {
        public Guid ItemId { get; init; }
        public Guid LibraryId { get; init; }
        public Guid? PersonalSpaceId { get; init; }
        public Guid? OwnerProfileId { get; init; }
        public string ScopeKind { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string FilingState { get; init; } = ViewDocumentFilingStates.Filed;
        public string FileTime { get; init; } = string.Empty;
    }

    private sealed class FileSourceRow
    {
        public string Path { get; init; } = string.Empty;
        public Guid? SourceId { get; init; }
    }

    private sealed class RuleRow
    {
        public Guid Id { get; init; }
        public Guid OwnerProfileId { get; init; }
        public Guid? PersonalSpaceId { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public string MatchMode { get; init; } = "all";
        public int SortOrder { get; init; }
        public bool FilesAutomatically { get; init; }
    }

    private sealed class ConditionRow
    {
        public Guid RuleId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    private sealed class ActionRow
    {
        public Guid RuleId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public Guid? RefId { get; init; }
        public string? TextValue { get; init; }
    }

    private sealed class DetailsRow
    {
        public Guid? CorrespondentId { get; init; }
        public Guid? DocTypeId { get; init; }
        public string? DocumentDate { get; init; }
        public string FilingState { get; init; } = ViewDocumentFilingStates.Filed;
        public string? SuggestionsJson { get; init; }
    }

    private sealed class SimilarRow
    {
        public Guid ItemId { get; init; }
        public string? Correspondent { get; init; }
        public string? DocumentType { get; init; }
    }

    private sealed class FieldValueRow
    {
        public string? TextValue { get; init; }
        public double? NumberValue { get; init; }
        public string? DateValue { get; init; }
        public int? BoolValue { get; init; }
    }

    // ---------------------------------------------------------------- subjects

    public Task<DocumentRuleSubject?> GetSubjectAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<SubjectRow>($"{SubjectSelect} WHERE li.id = @itemId AND li.archived_at IS NULL AND li.trashed_at IS NULL AND li.media_kind = 'document';",
            new { itemId }).ToList();
        return Task.FromResult(rows.Count == 0 ? null : ToSubjects(connection, rows).Single());
    }

    public Task<IReadOnlyList<DocumentRuleSubject>> GetSubjectsPageAsync(
        ViewCatalogueViewer viewer, Guid libraryId, int skip, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ct.ThrowIfCancellationRequested();
        // Only the viewer's own approved libraries, never a library named by the caller alone, and nothing at all when
        // the profile may not use Documents.
        if (!viewer.IncludeDocuments || !viewer.AccessibleLibraryIds.Contains(libraryId))
        {
            return Task.FromResult<IReadOnlyList<DocumentRuleSubject>>([]);
        }

        using var connection = database.CreateConnection();
        var rows = connection.Query<SubjectRow>($"""
            {SubjectSelect}
             WHERE li.library_id = @libraryId AND li.media_kind = 'document'
               AND li.archived_at IS NULL AND li.trashed_at IS NULL
               AND (li.hidden = 0 OR li.owner_profile_id = @viewer)
             ORDER BY COALESCE(li.captured_at, li.created_at) DESC, li.id DESC
             LIMIT @take OFFSET @skip;
            """, new { libraryId, viewer = viewer.ProfileId, take = Math.Clamp(take, 1, 500), skip = Math.Max(0, skip) }).ToList();
        return Task.FromResult<IReadOnlyList<DocumentRuleSubject>>(ToSubjects(connection, rows));
    }

    private static readonly string SubjectSelect = $"""
        SELECT li.id AS ItemId, li.library_id AS LibraryId, li.personal_space_id AS PersonalSpaceId,
               li.owner_profile_id AS OwnerProfileId, li.scope_kind AS ScopeKind,
               COALESCE(li.title, '') AS Title, li.primary_file_name AS FileName,
               substr(lm.document_text, 1, {MaximumTextCharacters}) AS Text,
               COALESCE(dd.filing_state, 'filed') AS FilingState,
               COALESCE(li.captured_at, li.created_at) AS FileTime
          FROM local_items li
          LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
          LEFT JOIN doc_item_details dd ON dd.item_id = li.id
        """;

    private static List<DocumentRuleSubject> ToSubjects(SqliteConnection connection, List<SubjectRow> rows)
    {
        var subjects = new List<DocumentRuleSubject>(rows.Count);
        foreach (var row in rows)
        {
            var files = connection.Query<FileSourceRow>("""
                SELECT lfs.file_path AS Path, lfs.source_id AS SourceId
                  FROM local_item_files lif
                  JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = @LibraryId
                 WHERE lif.item_id = @ItemId AND lif.role = 'primary'
                 ORDER BY lfs.indexed_at, lfs.id;
                """, new { row.ItemId, row.LibraryId }).ToList();
            var folder = files.Count == 0 ? string.Empty : Path.GetDirectoryName(files[0].Path) ?? string.Empty;
            subjects.Add(new DocumentRuleSubject(
                row.ItemId, row.LibraryId, row.PersonalSpaceId, row.OwnerProfileId,
                string.Equals(row.ScopeKind, "shared", StringComparison.Ordinal),
                row.Title, row.FileName, folder, string.IsNullOrWhiteSpace(row.Text) ? null : row.Text,
                files.Where(file => file.SourceId.HasValue).Select(file => file.SourceId!.Value).Distinct().ToList(),
                row.FilingState, ParseTime(row.FileTime)));
        }

        return subjects;
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed : DateTimeOffset.UtcNow;

    // ------------------------------------------------------------------- rules

    public Task<IReadOnlyList<DocumentRule>> GetApplicableRulesAsync(DocumentRuleSubject subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        // A personal rule covers exactly its owner's Personal Space. A Shared Library rule only counts while its maker
        // is still an enabled household administrator (not a child profile) of the household that owns that library.
        var rules = connection.Query<RuleRow>("""
            SELECT r.id AS Id, r.owner_profile_id AS OwnerProfileId, r.personal_space_id AS PersonalSpaceId,
                   r.name AS Name, r.enabled AS Enabled, r.match_mode AS MatchMode, r.sort_order AS SortOrder,
                   r.files_automatically AS FilesAutomatically
              FROM doc_rules r
             WHERE r.enabled = 1
               AND ((@isShared = 0 AND r.personal_space_id = @spaceId AND r.owner_profile_id = @owner)
                 OR (@isShared = 1 AND r.personal_space_id IS NULL AND EXISTS (
                        SELECT 1
                          FROM view_shared_library sl
                          JOIN profiles p ON p.id = r.owner_profile_id AND p.household_id = sl.household_id
                                         AND p.role <> 'RestrictedProfile' AND p.is_household_admin = 1
                          JOIN accounts a ON a.profile_id = p.id AND a.is_enabled = 1
                         WHERE sl.library_id = @libraryId)))
             ORDER BY r.sort_order, r.created_at, r.id;
            """, new
        {
            isShared = subject.IsShared ? 1 : 0,
            spaceId = subject.PersonalSpaceId,
            owner = subject.OwnerProfileId,
            libraryId = subject.LibraryId,
        }).ToList();
        if (rules.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DocumentRule>>([]);
        }

        var ids = rules.Select(rule => rule.Id).ToHashSet();
        var conditions = new Dictionary<Guid, List<DocumentRuleCondition>>();
        var actions = new Dictionary<Guid, List<DocumentRuleAction>>();
        foreach (var id in ids)
        {
            conditions[id] = connection.Query<ConditionRow>("""
                SELECT rule_id AS RuleId, kind AS Kind, value AS Value FROM doc_rule_conditions
                 WHERE rule_id = @id ORDER BY position;
                """, new { id }).Select(row => new DocumentRuleCondition(row.Kind, row.Value)).ToList();
            actions[id] = connection.Query<ActionRow>("""
                SELECT rule_id AS RuleId, kind AS Kind, ref_id AS RefId, text_value AS TextValue FROM doc_rule_actions
                 WHERE rule_id = @id ORDER BY position;
                """, new { id }).Select(row => new DocumentRuleAction(row.Kind, row.RefId, row.TextValue)).ToList();
        }

        return Task.FromResult<IReadOnlyList<DocumentRule>>(rules.Select(row => new DocumentRule(
            row.Id, row.OwnerProfileId, row.PersonalSpaceId, row.Name, row.Enabled, row.MatchMode, row.SortOrder,
            row.FilesAutomatically, conditions[row.Id], actions[row.Id])).ToList());
    }

    public Task<DocumentRuleApplyOutcome> ApplyPlanAsync(Guid itemId, DocumentRulePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var mediaKind = connection.ExecuteScalar<string?>(
                "SELECT media_kind FROM local_items WHERE id = @itemId AND trashed_at IS NULL;", new { itemId }, transaction);
            if (!string.Equals(mediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal))
            {
                return new DocumentRuleApplyOutcome(false, false);
            }

            var details = connection.QuerySingleOrDefault<DetailsRow>("""
                SELECT correspondent_id AS CorrespondentId, doc_type_id AS DocTypeId, document_date AS DocumentDate,
                       filing_state AS FilingState, suggestions_json AS SuggestionsJson
                  FROM doc_item_details WHERE item_id = @itemId;
                """, new { itemId }, transaction);
            var hasRow = details is not null;
            var state = details?.FilingState ?? ViewDocumentFilingStates.Filed;
            var now = DateTimeOffset.UtcNow;
            var changed = false;

            void EnsureRow()
            {
                if (hasRow)
                {
                    return;
                }

                // A document with no row was added before filing existed: it stays filed.
                connection.Execute("""
                    INSERT INTO doc_item_details (item_id, filing_state, updated_at) VALUES (@itemId, 'filed', @now);
                    """, new { itemId, now }, transaction);
                hasRow = true;
            }

            foreach (var tagId in plan.TagIds.Distinct())
            {
                if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tags WHERE id = @tagId;", new { tagId }, transaction) == 0)
                {
                    continue;
                }

                changed |= connection.Execute("""
                    INSERT OR IGNORE INTO local_item_tag_links (item_id, tag_id, added_at) VALUES (@itemId, @tagId, @now);
                    """, new { itemId, tagId, now }, transaction) > 0;
            }

            if (plan.CorrespondentId is { } correspondentId
                && connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM doc_correspondents WHERE id = @correspondentId;", new { correspondentId }, transaction) != 0
                && MayWrite(details?.CorrespondentId is not null,
                    DocumentFieldSources.Get(connection, transaction, itemId, DocumentFieldSources.Correspondent, null))
                && details?.CorrespondentId != correspondentId)
            {
                EnsureRow();
                connection.Execute("UPDATE doc_item_details SET correspondent_id = @correspondentId WHERE item_id = @itemId;",
                    new { itemId, correspondentId }, transaction);
                DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.Correspondent, null, DocumentFieldSources.Rule);
                changed = true;
            }

            if (plan.DocumentTypeId is { } typeId
                && connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM doc_types WHERE id = @typeId;", new { typeId }, transaction) != 0
                && MayWrite(details?.DocTypeId is not null,
                    DocumentFieldSources.Get(connection, transaction, itemId, DocumentFieldSources.DocumentType, null))
                && details?.DocTypeId != typeId)
            {
                EnsureRow();
                connection.Execute("UPDATE doc_item_details SET doc_type_id = @typeId WHERE item_id = @itemId;",
                    new { itemId, typeId }, transaction);
                DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.DocumentType, null, DocumentFieldSources.Rule);
                changed = true;
            }

            foreach (var field in plan.Fields)
            {
                changed |= ApplyField(connection, transaction, itemId, field);
            }

            var filed = state == ViewDocumentFilingStates.Filed;
            if (plan.FileNow && state == ViewDocumentFilingStates.Inbox)
            {
                connection.Execute("""
                    UPDATE doc_item_details SET filing_state = 'filed', suggestions_json = NULL WHERE item_id = @itemId;
                    """, new { itemId }, transaction);
                filed = true;
                changed = true;
            }

            if (changed)
            {
                connection.Execute("UPDATE doc_item_details SET updated_at = @now WHERE item_id = @itemId;",
                    new { itemId, now }, transaction);
                LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            }

            return new DocumentRuleApplyOutcome(changed, filed);
        }, ct);
    }

    /// <summary>A rule may fill an empty value or replace one a rule set; it never replaces a person's.</summary>
    private static bool MayWrite(bool hasValue, string? setBy) => setBy switch
    {
        DocumentFieldSources.Person => false,
        DocumentFieldSources.Rule => true,
        _ => !hasValue,
    };

    private static bool ApplyField(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, DocumentRuleFieldValue field)
    {
        var kind = connection.ExecuteScalar<string?>(
            "SELECT kind FROM doc_field_definitions WHERE id = @FieldId;", new { field.FieldId }, transaction);
        if (kind is null)
        {
            return false;
        }

        var write = DocumentRuleFieldValues.TryCreateWrite(field.FieldId, kind, field.Text, out _);
        if (write is null)
        {
            return false;
        }

        List<ViewCatalogueRepository.FieldRow> rows;
        try
        {
            rows = ViewCatalogueRepository.ValidateFieldWrites(connection, transaction, null, [write]);
        }
        catch (Exception exception) when (exception is ArgumentException or ViewCatalogueAccessException)
        {
            // The value no longer fits the field (for example a choice that was removed): the rule skips it.
            return false;
        }

        if (rows.Count == 0)
        {
            return false;
        }

        var row = rows[0];
        var existing = connection.QuerySingleOrDefault<FieldValueRow>("""
            SELECT text_value AS TextValue, number_value AS NumberValue, date_value AS DateValue, bool_value AS BoolValue
              FROM doc_field_values WHERE item_id = @itemId AND field_id = @FieldId;
            """, new { itemId, row.FieldId }, transaction);
        var setBy = DocumentFieldSources.Get(connection, transaction, itemId, DocumentFieldSources.Field, row.FieldId);
        if (!MayWrite(existing is not null, setBy))
        {
            return false;
        }

        var same = existing is not null
            && existing.TextValue == row.Text && existing.NumberValue == row.Number
            && existing.DateValue == row.Date && existing.BoolValue == row.Flag;
        if (same && setBy == DocumentFieldSources.Rule)
        {
            return false;
        }

        connection.Execute("DELETE FROM doc_field_values WHERE item_id = @itemId AND field_id = @FieldId;",
            new { itemId, row.FieldId }, transaction);
        connection.Execute("""
            INSERT INTO doc_field_values (item_id, field_id, text_value, number_value, date_value, bool_value)
            VALUES (@itemId, @FieldId, @Text, @Number, @Date, @Flag);
            """, new { itemId, row.FieldId, row.Text, row.Number, row.Date, row.Flag }, transaction);
        DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.Field, row.FieldId, DocumentFieldSources.Rule);
        return !same;
    }

    // ------------------------------------------------------------- suggestions

    public Task SaveSuggestionsAsync(Guid itemId, ViewDocumentSuggestionsDto? suggestions, CancellationToken ct = default)
    {
        var json = suggestions is null ? null : JsonSerializer.Serialize(suggestions);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE doc_item_details SET suggestions_json = @json
                 WHERE item_id = @itemId AND filing_state = 'inbox';
                """, new { itemId, json }, transaction);
        }, ct);
    }

    public Task<int> CountOrganisedDocumentsAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM local_items li
              LEFT JOIN doc_item_details dd ON dd.item_id = li.id
             WHERE {CorpusPredicate};
            """, new { libraryId }));
    }

    public Task<IReadOnlyDictionary<string, int>> GetWordDocumentCountsAsync(
        Guid libraryId, IReadOnlyCollection<string> words, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        using var connection = database.CreateConnection();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in words.Where(IsSearchableWord).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var count = connection.ExecuteScalar<int>($"""
                SELECT COUNT(*)
                  FROM local_item_search
                  JOIN local_item_search_keys k ON k.rowid = local_item_search.rowid
                  JOIN local_items li ON li.id = k.item_id
                  LEFT JOIN doc_item_details dd ON dd.item_id = li.id
                 WHERE local_item_search MATCH @expression AND {CorpusPredicate};
                """, new { libraryId, expression = $"document_text : \"{word}\"" });
            if (count > 0)
            {
                counts[word] = count;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
    }

    public Task<IReadOnlyList<SimilarFiledDocument>> FindSimilarDocumentsAsync(
        Guid libraryId, Guid excludeItemId, IReadOnlyCollection<string> words, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        ct.ThrowIfCancellationRequested();
        var usable = words.Where(IsSearchableWord).Distinct(StringComparer.Ordinal).ToList();
        if (usable.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SimilarFiledDocument>>([]);
        }

        var expression = "document_text : (" + string.Join(" OR ", usable.Select(word => $"\"{word}\"")) + ")";
        using var connection = database.CreateConnection();
        var rows = connection.Query<SimilarRow>($"""
            SELECT li.id AS ItemId, dc.name AS Correspondent, dt.name AS DocumentType
              FROM local_item_search
              JOIN local_item_search_keys k ON k.rowid = local_item_search.rowid
              JOIN local_items li ON li.id = k.item_id
              LEFT JOIN doc_item_details dd ON dd.item_id = li.id
              LEFT JOIN doc_correspondents dc ON dc.id = dd.correspondent_id
              LEFT JOIN doc_types dt ON dt.id = dd.doc_type_id
             WHERE local_item_search MATCH @expression AND li.id <> @excludeItemId AND {CorpusPredicate}
             ORDER BY bm25(local_item_search), li.id
             LIMIT @take;
            """, new { libraryId, excludeItemId, expression, take = Math.Clamp(take, 1, 20) }).ToList();
        var result = new List<SimilarFiledDocument>(rows.Count);
        foreach (var row in rows)
        {
            var tags = connection.Query<string>("""
                SELECT tg.name FROM local_item_tag_links tl JOIN tags tg ON tg.id = tl.tag_id
                 WHERE tl.item_id = @ItemId ORDER BY tg.name COLLATE NOCASE;
                """, new { row.ItemId }).ToList();
            result.Add(new SimilarFiledDocument(row.ItemId, row.Correspondent, row.DocumentType, tags));
        }

        return Task.FromResult<IReadOnlyList<SimilarFiledDocument>>(result);
    }

    public Task<DocumentAmountField?> FindAmountFieldAsync(Guid libraryId, Guid? ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(FindAmountField(connection, null, libraryId, ownerProfileId));
    }

    /// <summary>
    /// Names are unique ignoring case, so at most one field is called Amount. It only counts when the owner could see
    /// it anyway: they made it, or it is in use on a document in their library that they may see.
    /// </summary>
    private static DocumentAmountField? FindAmountField(
        SqliteConnection connection, SqliteTransaction? transaction, Guid libraryId, Guid? ownerProfileId) =>
        connection.QuerySingleOrDefault<DocumentAmountField>("""
            SELECT f.id AS FieldId, f.kind AS Kind FROM doc_field_definitions f
             WHERE f.name = 'Amount' COLLATE NOCASE AND f.kind IN ('money', 'number')
               AND (f.created_by_profile_id = @ownerProfileId
                    OR EXISTS (SELECT 1 FROM doc_field_values v JOIN local_items li ON li.id = v.item_id
                                WHERE v.field_id = f.id AND li.library_id = @libraryId
                                  AND (li.hidden = 0 OR li.owner_profile_id = @ownerProfileId)));
            """, new { libraryId, ownerProfileId }, transaction);

    /// <summary>Filed, organised, visible documents of the library: the ones a suggestion may learn from.</summary>
    private const string CorpusPredicate = """
        li.library_id = @libraryId AND li.media_kind = 'document'
        AND li.archived_at IS NULL AND li.trashed_at IS NULL AND li.hidden = 0
        AND COALESCE(dd.filing_state, 'filed') = 'filed'
        AND (dd.correspondent_id IS NOT NULL OR dd.doc_type_id IS NOT NULL
             OR EXISTS (SELECT 1 FROM local_item_tag_links tl WHERE tl.item_id = li.id))
        """;

    /// <summary>Words go into a quoted phrase, so only letters and digits are allowed.</summary>
    private static bool IsSearchableWord(string word) =>
        word.Length > 0 && word.All(char.IsLetterOrDigit);

    // ------------------------------------------------------------------- inbox

    public int CountInbox(ViewCatalogueViewer viewer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ct.ThrowIfCancellationRequested();
        if (!viewer.IncludeDocuments)
        {
            return 0;
        }

        var parameters = new DynamicParameters();
        var libraries = ViewCatalogueSql.LibraryPredicate("li", viewer.AccessibleLibraryIds, parameters);
        using var connection = database.CreateConnection();
        return connection.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM local_items li
              JOIN doc_item_details dd ON dd.item_id = li.id
             WHERE {libraries} AND li.media_kind = 'document' AND dd.filing_state = 'inbox'
               AND li.archived_at IS NULL AND li.trashed_at IS NULL AND li.hidden = 0;
            """, parameters);
    }

    public Task<bool> FileFromInboxAsync(ViewCatalogueViewer viewer, Guid itemId, FileInboxWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(write);
        var correspondent = string.IsNullOrWhiteSpace(write.Correspondent)
            ? null : ViewCatalogueSql.NormalizeName(write.Correspondent, "correspondent");
        var documentType = string.IsNullOrWhiteSpace(write.DocumentType)
            ? null : ViewCatalogueSql.NormalizeName(write.DocumentType, "document type");
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!viewer.IncludeDocuments)
            {
                return false;
            }

            string mediaKind;
            try
            {
                mediaKind = ViewCatalogueRepository.RequireAccessibleItem(connection, transaction, viewer, itemId);
            }
            catch (ViewCatalogueAccessException)
            {
                // The item is missing or outside the viewer's libraries; the two look the same to the caller.
                return false;
            }

            if (!string.Equals(mediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal))
            {
                return false;
            }

            var details = connection.QuerySingleOrDefault<DetailsRow>("""
                SELECT correspondent_id AS CorrespondentId, doc_type_id AS DocTypeId, document_date AS DocumentDate,
                       filing_state AS FilingState, suggestions_json AS SuggestionsJson
                  FROM doc_item_details WHERE item_id = @itemId;
                """, new { itemId }, transaction);
            if (details is null || details.FilingState != ViewDocumentFilingStates.Inbox)
            {
                // Already filed (a second tap, or another device got there first): nothing to do.
                return true;
            }

            // What the person was shown wins over what is stored now: suggestions can be worked out again while a card is open.
            var suggestions = write.AcceptSuggestions ? write.Shown ?? ReadSuggestions(details.SuggestionsJson) : null;
            var now = DateTimeOffset.UtcNow;

            var date = write.DocumentDate
                ?? (details.DocumentDate is null && suggestions is { DocumentDate: { } suggested, DateFromFile: false }
                    ? suggested : (DateOnly?)null);
            if (date is { } chosenDate)
            {
                connection.Execute("UPDATE doc_item_details SET document_date = @date WHERE item_id = @itemId;",
                    new { itemId, date = chosenDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, transaction);
            }

            var correspondentName = correspondent
                ?? (details.CorrespondentId is null ? CleanName(suggestions?.Correspondent, "correspondent") : null);
            if (correspondentName is not null)
            {
                var id = ViewCatalogueSql.FindOrCreate(connection, transaction, "doc_correspondents", correspondentName, viewer.ProfileId, now);
                connection.Execute("UPDATE doc_item_details SET correspondent_id = @id WHERE item_id = @itemId;",
                    new { itemId, id }, transaction);
                DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.Correspondent, null, DocumentFieldSources.Person);
            }

            var typeName = documentType
                ?? (details.DocTypeId is null ? CleanName(suggestions?.DocumentType, "document type") : null);
            if (typeName is not null)
            {
                var id = ViewCatalogueSql.FindOrCreate(connection, transaction, "doc_types", typeName, viewer.ProfileId, now);
                connection.Execute("UPDATE doc_item_details SET doc_type_id = @id WHERE item_id = @itemId;",
                    new { itemId, id }, transaction);
                DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.DocumentType, null, DocumentFieldSources.Person);
            }

            // Tags are added to the ones it has; the person's own list replaces the suggested one.
            var tagNames = write.Tags ?? suggestions?.Tags ?? [];
            foreach (var tagId in ViewCatalogueSql.ResolveTagIds(
                         connection, transaction, tagNames.Where(name => !string.IsNullOrWhiteSpace(name)), viewer.ProfileId, now))
            {
                connection.Execute("""
                    INSERT OR IGNORE INTO local_item_tag_links (item_id, tag_id, added_at) VALUES (@itemId, @tagId, @now);
                    """, new { itemId, tagId, now }, transaction);
            }

            if (suggestions?.Amount is { } amount)
            {
                WriteSuggestedAmount(connection, transaction, itemId, viewer.ProfileId, amount, suggestions.Currency);
            }

            connection.Execute("""
                UPDATE doc_item_details SET filing_state = 'filed', suggestions_json = NULL, updated_at = @now
                 WHERE item_id = @itemId;
                """, new { itemId, now }, transaction);
            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            return true;
        }, ct);
    }

    private static string? CleanName(string? name, string label) =>
        string.IsNullOrWhiteSpace(name) ? null : ViewCatalogueSql.NormalizeName(name, label);

    /// <summary>
    /// A suggested amount goes into the custom field named Amount, and only when that field exists, can hold it, and
    /// has no value the person (or a rule) put there already.
    /// </summary>
    private static void WriteSuggestedAmount(
        SqliteConnection connection, SqliteTransaction transaction, Guid itemId, Guid ownerProfileId, double amount, string? currency)
    {
        var libraryId = connection.ExecuteScalar<Guid>("SELECT library_id FROM local_items WHERE id = @itemId;", new { itemId }, transaction);
        var field = FindAmountField(connection, transaction, libraryId, ownerProfileId);
        if (field is null
            || connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM doc_field_values WHERE item_id = @itemId AND field_id = @FieldId;",
                new { itemId, field.FieldId }, transaction) != 0)
        {
            return;
        }

        ViewDocumentFieldWrite? write = field.Kind == ViewDocumentFieldKinds.Money
            ? (string.IsNullOrWhiteSpace(currency) ? null : new ViewDocumentFieldWrite(field.FieldId, Text: currency, Number: amount))
            : new ViewDocumentFieldWrite(field.FieldId, Number: amount);
        if (write is null)
        {
            return;
        }

        try
        {
            var rows = ViewCatalogueRepository.ValidateFieldWrites(connection, transaction, null, [write]);
            foreach (var row in rows)
            {
                connection.Execute("""
                    INSERT INTO doc_field_values (item_id, field_id, text_value, number_value, date_value, bool_value)
                    VALUES (@itemId, @FieldId, @Text, @Number, @Date, @Flag);
                    """, new { itemId, row.FieldId, row.Text, row.Number, row.Date, row.Flag }, transaction);
                DocumentFieldSources.Mark(connection, transaction, itemId, DocumentFieldSources.Field, row.FieldId, DocumentFieldSources.Person);
            }
        }
        catch (ArgumentException)
        {
            // A currency code that is not three letters: the amount is simply not filled in.
        }
    }

    internal static ViewDocumentSuggestionsDto? ReadSuggestions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ViewDocumentSuggestionsDto>(json);
        }
        catch (JsonException)
        {
            // Suggestions are a convenience we wrote ourselves; unreadable ones are treated as not worked out yet.
            return null;
        }
    }

    public Task<int> CountDocumentsAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM local_items
             WHERE library_id = @libraryId AND media_kind = 'document' AND trashed_at IS NULL;
            """, new { libraryId }));
    }

    public Task<IReadOnlyList<Guid>> GetDocumentIdsAsync(Guid libraryId, Guid? afterItemId, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var limit = Math.Clamp(take, 1, 1000);
        IReadOnlyList<Guid> ids = afterItemId is { } after
            ? connection.Query<Guid>("""
                SELECT id FROM local_items
                 WHERE library_id = @libraryId AND media_kind = 'document' AND trashed_at IS NULL AND id > @after
                 ORDER BY id LIMIT @limit;
                """, new { libraryId, after, limit }).ToList()
            : connection.Query<Guid>("""
                SELECT id FROM local_items
                 WHERE library_id = @libraryId AND media_kind = 'document' AND trashed_at IS NULL
                 ORDER BY id LIMIT @limit;
                """, new { libraryId, limit }).ToList();
        return Task.FromResult(ids);
    }
}
