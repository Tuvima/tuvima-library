using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// A person's own auto-filing rules. Every read and write is keyed by the owner, so one person can never see, change
/// or reorder another's rules; whose documents a rule covers is decided when it is applied (see
/// <see cref="DocumentFilingRepository"/>).
/// </summary>
public sealed class DocumentRuleRepository(IDatabaseConnection database) : IDocumentRuleRepository
{
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

    private const string RuleColumns = """
        r.id AS Id, r.owner_profile_id AS OwnerProfileId, r.personal_space_id AS PersonalSpaceId, r.name AS Name,
        r.enabled AS Enabled, r.match_mode AS MatchMode, r.sort_order AS SortOrder,
        r.files_automatically AS FilesAutomatically
        """;

    public Task<IReadOnlyList<DocumentRule>> ListOwnedAsync(Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rules = connection.Query<RuleRow>($"""
            SELECT {RuleColumns} FROM doc_rules r WHERE r.owner_profile_id = @ownerProfileId
             ORDER BY CASE WHEN r.personal_space_id IS NULL THEN 1 ELSE 0 END, r.sort_order, r.created_at, r.id;
            """, new { ownerProfileId }).ToList();
        return Task.FromResult<IReadOnlyList<DocumentRule>>(Hydrate(connection, null, rules, ownerProfileId));
    }

    public Task<DocumentRule?> FindOwnedAsync(Guid ownerProfileId, Guid ruleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(Find(connection, null, ownerProfileId, ruleId));
    }

    public Task<DocumentRule> CreateAsync(DocumentRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
            var now = DateTimeOffset.UtcNow;
            var next = connection.ExecuteScalar<long>("""
                SELECT COALESCE(MAX(sort_order), -1) + 1 FROM doc_rules
                 WHERE owner_profile_id = @OwnerProfileId
                   AND ((@PersonalSpaceId IS NULL AND personal_space_id IS NULL) OR personal_space_id = @PersonalSpaceId);
                """, new { rule.OwnerProfileId, rule.PersonalSpaceId }, transaction);
            connection.Execute("""
                INSERT INTO doc_rules
                    (id, owner_profile_id, personal_space_id, name, enabled, match_mode, sort_order,
                     files_automatically, created_at, updated_at)
                VALUES (@id, @OwnerProfileId, @PersonalSpaceId, @Name, @Enabled, @MatchMode, @next,
                        @FilesAutomatically, @now, @now);
                """, new
            {
                id,
                rule.OwnerProfileId,
                rule.PersonalSpaceId,
                rule.Name,
                Enabled = rule.Enabled ? 1 : 0,
                rule.MatchMode,
                next,
                FilesAutomatically = rule.FilesAutomatically ? 1 : 0,
                now,
            }, transaction);
            WriteParts(connection, transaction, id, rule);
            return Find(connection, transaction, rule.OwnerProfileId, id)!;
        }, ct);
    }

    public Task<DocumentRule?> ReplaceAsync(DocumentRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE doc_rules
                   SET name = @Name, enabled = @Enabled, match_mode = @MatchMode,
                       files_automatically = @FilesAutomatically, updated_at = @now
                 WHERE id = @Id AND owner_profile_id = @OwnerProfileId;
                """, new
            {
                rule.Id,
                rule.OwnerProfileId,
                rule.Name,
                Enabled = rule.Enabled ? 1 : 0,
                rule.MatchMode,
                FilesAutomatically = rule.FilesAutomatically ? 1 : 0,
                now = DateTimeOffset.UtcNow,
            }, transaction);
            if (changed == 0)
            {
                return null;
            }

            connection.Execute("DELETE FROM doc_rule_conditions WHERE rule_id = @Id;", new { rule.Id }, transaction);
            connection.Execute("DELETE FROM doc_rule_actions WHERE rule_id = @Id;", new { rule.Id }, transaction);
            WriteParts(connection, transaction, rule.Id, rule);
            return Find(connection, transaction, rule.OwnerProfileId, rule.Id);
        }, ct);
    }

    public Task<bool> DeleteOwnedAsync(Guid ownerProfileId, Guid ruleId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                "DELETE FROM doc_rules WHERE id = @ruleId AND owner_profile_id = @ownerProfileId;",
                new { ruleId, ownerProfileId }, transaction) > 0;
        }, ct);

    public Task<bool> ReorderOwnedAsync(Guid ownerProfileId, IReadOnlyList<Guid> ruleIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // The new order must name every rule the person has, once each, or the order would have gaps and repeats.
            var owned = connection.Query<Guid>(
                "SELECT id FROM doc_rules WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId }, transaction).ToHashSet();
            if (ruleIds.Count != owned.Count || !owned.SetEquals(ruleIds))
            {
                return false;
            }

            var counters = new Dictionary<Guid, int>();
            var seen = new HashSet<Guid>();
            var now = DateTimeOffset.UtcNow;
            foreach (var ruleId in ruleIds)
            {
                if (!seen.Add(ruleId))
                {
                    return false;
                }

                var scope = connection.QuerySingleOrDefault<ScopeRow>("""
                    SELECT 1 AS Found, personal_space_id AS PersonalSpaceId FROM doc_rules
                     WHERE id = @ruleId AND owner_profile_id = @ownerProfileId;
                    """, new { ruleId, ownerProfileId }, transaction);
                if (scope is null)
                {
                    return false;
                }

                var key = scope.PersonalSpaceId ?? Guid.Empty;
                var position = counters.GetValueOrDefault(key);
                counters[key] = position + 1;
                connection.Execute("UPDATE doc_rules SET sort_order = @position, updated_at = @now WHERE id = @ruleId;",
                    new { ruleId, position, now }, transaction);
            }

            return true;
        }, ct);
    }

    private sealed class ScopeRow
    {
        public int Found { get; init; }
        public Guid? PersonalSpaceId { get; init; }
    }

    public Task<IReadOnlyList<DocumentRuleSource>> ListSourcesAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        IReadOnlyList<DocumentRuleSource> sources = connection.Query<DocumentRuleSource>("""
            SELECT id AS Id, name AS Name FROM view_sources
             WHERE library_id = @libraryId AND enabled = 1
             ORDER BY name COLLATE NOCASE, id;
            """, new { libraryId }).ToList();
        return Task.FromResult(sources);
    }

    private static void WriteParts(SqliteConnection connection, SqliteTransaction transaction, Guid ruleId, DocumentRule rule)
    {
        var position = 0;
        foreach (var condition in rule.Conditions)
        {
            connection.Execute("""
                INSERT INTO doc_rule_conditions (rule_id, position, kind, value) VALUES (@ruleId, @position, @Kind, @Value);
                """, new { ruleId, position = position++, condition.Kind, condition.Value }, transaction);
        }

        position = 0;
        foreach (var action in rule.Actions)
        {
            connection.Execute("""
                INSERT INTO doc_rule_actions (rule_id, position, kind, ref_id, text_value)
                VALUES (@ruleId, @position, @Kind, @RefId, @TextValue);
                """, new { ruleId, position = position++, action.Kind, action.RefId, action.TextValue }, transaction);
        }
    }

    private static DocumentRule? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid ownerProfileId, Guid ruleId)
    {
        var row = connection.QuerySingleOrDefault<RuleRow>($"""
            SELECT {RuleColumns} FROM doc_rules r WHERE r.id = @ruleId AND r.owner_profile_id = @ownerProfileId;
            """, new { ruleId, ownerProfileId }, transaction);
        return row is null ? null : Hydrate(connection, transaction, [row], ownerProfileId).Single();
    }

    private static List<DocumentRule> Hydrate(
        SqliteConnection connection, SqliteTransaction? transaction, List<RuleRow> rules, Guid ownerProfileId)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        var conditions = connection.Query<ConditionRow>("""
            SELECT c.rule_id AS RuleId, c.kind AS Kind, c.value AS Value
              FROM doc_rule_conditions c JOIN doc_rules r ON r.id = c.rule_id
             WHERE r.owner_profile_id = @ownerProfileId ORDER BY c.rule_id, c.position;
            """, new { ownerProfileId }, transaction).ToLookup(row => row.RuleId);
        var actions = connection.Query<ActionRow>("""
            SELECT a.rule_id AS RuleId, a.kind AS Kind, a.ref_id AS RefId, a.text_value AS TextValue
              FROM doc_rule_actions a JOIN doc_rules r ON r.id = a.rule_id
             WHERE r.owner_profile_id = @ownerProfileId ORDER BY a.rule_id, a.position;
            """, new { ownerProfileId }, transaction).ToLookup(row => row.RuleId);
        return rules.Select(row => new DocumentRule(
            row.Id, row.OwnerProfileId, row.PersonalSpaceId, row.Name, row.Enabled, row.MatchMode, row.SortOrder,
            row.FilesAutomatically,
            conditions[row.Id].Select(c => new DocumentRuleCondition(c.Kind, c.Value)).ToList(),
            actions[row.Id].Select(a => new DocumentRuleAction(a.Kind, a.RefId, a.TextValue)).ToList())).ToList();
    }
}
