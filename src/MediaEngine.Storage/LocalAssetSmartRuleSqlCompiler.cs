using System.Data;
using System.Globalization;
using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Storage;

/// <summary>
/// Compiles validated Smart Gallery rules into a SQL predicate. Every SQL
/// identifier and operator originates in this file; rule values are always
/// Dapper parameters.
/// </summary>
public static class LocalAssetSmartRuleSqlCompiler
{
    /// <param name="today">
    /// The day relative document-date ranges ("last 30 days", "this year") are worked out from. Defaults to today in UTC;
    /// tests pass a fixed day.
    /// </param>
    public static LocalAssetSmartRuleSql Compile(CollectionRuleDefinition definition, DateOnly? today = null)
    {
        ViewSmartGalleryRules.Validate(definition);
        var compiler = new Compiler(today ?? DateOnly.FromDateTime(DateTime.UtcNow), vaultScope: false);
        return compiler.Compile(definition);
    }

    /// <summary>
    /// Compiles a Vault gallery rule (Videos spec D44): the same rule document and the same compiler, limited to the fields the Vault
    /// allows (<see cref="ViewSmartGalleryRules.VaultFields"/>), with tags read from the Vault's own tags and never from the shared list.
    /// </summary>
    public static LocalAssetSmartRuleSql CompileForVault(CollectionRuleDefinition definition, DateOnly? today = null)
    {
        ViewSmartGalleryRules.ValidateForVault(definition);
        var compiler = new Compiler(today ?? DateOnly.FromDateTime(DateTime.UtcNow), vaultScope: true);
        return compiler.Compile(definition);
    }

    private sealed class Compiler(DateOnly today, bool vaultScope)
    {
        private readonly DynamicParameters _parameters = new();
        private int _parameterIndex;

        public LocalAssetSmartRuleSql Compile(CollectionRuleDefinition definition)
        {
            var groups = definition.Groups.Select(CompileGroup).ToArray();
            var predicate = groups[0];
            for (var index = 1; index < groups.Length; index++)
            {
                var join = definition.Groups[index].JoinWithPrevious == "and" ? "AND" : "OR";
                predicate = $"({predicate} {join} {groups[index]})";
            }
            return new LocalAssetSmartRuleSql(predicate, _parameters);
        }

        private string CompileGroup(CollectionRuleGroup group)
        {
            var join = group.MatchMode == "all" ? " AND " : " OR ";
            return $"({string.Join(join, group.Conditions.Select(CompileCondition))})";
        }

        private string CompileCondition(CollectionRulePredicate condition) => vaultScope && !ViewSmartGalleryRules.VaultFields.Contains(condition.Field)
            ? throw new ArgumentException($"Vault gallery rules cannot use the field '{condition.Field}'.")
            : CompileAllowedCondition(condition);

        private string CompileAllowedCondition(CollectionRulePredicate condition) => condition.Field switch
        {
            "media_type" => TextScalar(condition, "li.media_kind", "li.media_kind IS NOT NULL"),
            "file_type" => FileType(condition),
            "orientation" => Orientation(condition),
            "duration" => Numeric(condition, "lm.duration_seconds"),
            "captured_date" => CapturedDate(condition),
            "people" => RelatedText(condition,
                "SELECT 1 FROM local_item_annotations lia WHERE lia.item_id = li.id AND trim(lia.source) <> '' AND (lia.annotation_kind IN ('person_name', 'named_person', 'face_name') OR (lia.annotation_kind IN ('person_identity', 'face_identity') AND lia.reviewed_at IS NOT NULL))",
                "lia.annotation_value"),
            "place" => Place(condition),
            "device" => Device(condition),
            "tags" => Tags(condition),
            "correspondent" => DocumentName(condition, "doc_correspondents", "dcn", "correspondent_id"),
            "document_type" => DocumentName(condition, "doc_types", "dtn", "doc_type_id"),
            "document_date" => DocumentDate(condition),
            "filing_state" => FilingState(condition),
            "text_contains" => TextContains(condition),
            "favorite" => Boolean(condition, "li.favorite"),
            "owner" => Owner(condition),
            "source" => Source(condition),
            _ when ViewSmartGalleryRules.TryParseCustomField(condition.Field, out var fieldId) => CustomField(condition, fieldId),
            _ => throw new ArgumentException($"Unsupported Smart Gallery field '{condition.Field}'."),
        };

        /// <summary>Tags match by id (what a saved view stores) or by name. "all" needs every listed tag.</summary>
        private string Tags(CollectionRulePredicate condition)
        {
            // A Vault gallery reads the Vault's own tags; a View gallery reads the shared list. Neither can see the other's.
            var relation = vaultScope
                ? "SELECT 1 FROM vault_image_tags lit JOIN vault_tags tg ON tg.id = lit.tag_id WHERE lit.item_id = li.id"
                : "SELECT 1 FROM local_item_tag_links lit JOIN tags tg ON tg.id = lit.tag_id WHERE lit.item_id = li.id";
            string Match(string value, bool contains)
            {
                if (Guid.TryParse(value, out var id))
                {
                    return $"tg.id = {AddGuid(id)}";
                }

                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"instr(lower(tg.name), {parameter}) > 0"
                    : $"tg.name = {parameter} COLLATE NOCASE";
            }

            if (condition.Op != "all")
            {
                return ApplyRelatedOperation(condition, relation, Match);
            }

            var each = condition.GetEffectiveValues().Select(value => $"EXISTS ({relation} AND ({Match(value, false)}))");
            return $"({string.Join(" AND ", each)})";
        }

        /// <summary>A document's correspondent or type, by id (what a saved view stores) or by name.</summary>
        private string DocumentName(CollectionRulePredicate condition, string table, string alias, string column)
        {
            var relation = $"SELECT 1 FROM doc_item_details dd_{alias} JOIN {table} {alias} ON {alias}.id = dd_{alias}.{column} WHERE dd_{alias}.item_id = li.id";
            string Match(string value, bool contains)
            {
                if (Guid.TryParse(value, out var id))
                {
                    return $"{alias}.id = {AddGuid(id)}";
                }

                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"instr(lower({alias}.name), {parameter}) > 0"
                    : $"{alias}.name = {parameter} COLLATE NOCASE";
            }
            return ApplyRelatedOperation(condition, relation, Match);
        }

        /// <summary>
        /// The document's own date, or the day it was added when it has none (the same day the Documents date filter
        /// uses). Known and unknown look only at a date a person or the file really gave.
        /// </summary>
        private string DocumentDate(CollectionRulePredicate condition)
        {
            const string expression = "COALESCE((SELECT dds.document_date FROM doc_item_details dds WHERE dds.item_id = li.id), substr(COALESCE(li.captured_at, li.created_at), 1, 10))";
            const string known = "EXISTS (SELECT 1 FROM doc_item_details ddk WHERE ddk.item_id = li.id AND ddk.document_date IS NOT NULL)";
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, known);
            }

            if (condition.Op == "within")
            {
                var (from, to) = ViewSmartGalleryRules.ResolveRelativeRange(condition.GetEffectiveValues()[0].Trim().ToLowerInvariant(), today);
                return $"({expression} BETWEEN {Add(Iso(from))} AND {Add(Iso(to))})";
            }

            var values = condition.GetEffectiveValues()
                .Select(value => Iso(DateOnly.Parse(value, CultureInfo.InvariantCulture))).ToArray();
            return condition.Op == "between"
                ? $"({expression} BETWEEN {Add(values[0])} AND {Add(values[1])})"
                : $"({expression} {SqlComparison(condition.Op)} {Add(values[0])})";
        }

        private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Documents with no row count as filed, like the Inbox does.</summary>
        private string FilingState(CollectionRulePredicate condition)
        {
            const string expression = "COALESCE((SELECT dfs.filing_state FROM doc_item_details dfs WHERE dfs.item_id = li.id), 'filed')";
            var value = Add(condition.GetEffectiveValues()[0].Trim().ToLowerInvariant());
            return condition.Op == "neq" ? $"{expression} <> {value}" : $"{expression} = {value}";
        }

        /// <summary>The words read from the document. Private text: it only ever narrows the owner's own view.</summary>
        private string TextContains(CollectionRulePredicate condition)
        {
            var parameter = Add(condition.GetEffectiveValues()[0].Trim().ToLowerInvariant());
            return $"EXISTS (SELECT 1 FROM local_item_metadata tcm WHERE tcm.item_id = li.id AND instr(lower(COALESCE(tcm.document_text, '')), {parameter}) > 0)";
        }

        /// <summary>One custom field: equals, contains or a number/date range, judged by the field's own kind.</summary>
        private string CustomField(CollectionRulePredicate condition, Guid fieldId)
        {
            var id = AddGuid(fieldId);
            var relation = $"SELECT 1 FROM doc_field_values cfv JOIN doc_field_definitions cfd ON cfd.id = cfv.field_id WHERE cfv.item_id = li.id AND cfv.field_id = {id}";
            const string hasValue = "(cfv.text_value IS NOT NULL OR cfv.number_value IS NOT NULL OR cfv.date_value IS NOT NULL OR cfv.bool_value IS NOT NULL)";
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, $"EXISTS ({relation} AND {hasValue})");
            }

            var values = condition.GetEffectiveValues();
            string Compare(string op, string value)
            {
                var number = Add(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d);
                var text = Add(value.Trim());
                var lowered = Add(value.Trim().ToLowerInvariant());
                var yes = Add(value.Trim().ToLowerInvariant() is "true" or "1" or "yes" ? 1 : 0);
                var sql = SqlComparison(op);
                return op == "eq"
                    ? $"(CASE cfd.kind WHEN 'number' THEN cfv.number_value {sql} {number} WHEN 'money' THEN cfv.number_value {sql} {number} WHEN 'date' THEN cfv.date_value {sql} {text} WHEN 'bool' THEN cfv.bool_value {sql} {yes} ELSE lower(cfv.text_value) {sql} {lowered} END)"
                    : $"(CASE cfd.kind WHEN 'number' THEN cfv.number_value {sql} {number} WHEN 'money' THEN cfv.number_value {sql} {number} WHEN 'date' THEN cfv.date_value {sql} {text} ELSE 0 END)";
            }

            string match;
            if (condition.Op == "contains")
            {
                match = $"instr(lower(COALESCE(cfv.text_value, '')), {Add(values[0].Trim().ToLowerInvariant())}) > 0";
            }
            else if (condition.Op == "between")
            {
                match = $"({Compare("gte", values[0])} AND {Compare("lte", values[1])})";
            }
            else
            {
                match = Compare(condition.Op == "neq" ? "eq" : condition.Op, values[0]);
            }

            var anyMatch = $"EXISTS ({relation} AND {match})";
            return condition.Op == "neq" ? $"(EXISTS ({relation} AND {hasValue}) AND NOT {anyMatch})" : anyMatch;
        }

        private string FileType(CollectionRulePredicate condition)
        {
            const string known = "li.primary_mime_type IS NOT NULL AND trim(li.primary_mime_type) <> ''";
            string Match(string value, bool contains)
            {
                var parameter = Add(value.Trim().ToLowerInvariant());
                var mime = contains
                    ? $"instr(lower(li.primary_mime_type), {parameter}) > 0"
                    : $"li.primary_mime_type = {parameter} COLLATE NOCASE";
                var extension = contains
                    ? $"instr(lower(lf.extension), {parameter}) > 0"
                    : $"(lf.extension = {parameter} COLLATE NOCASE OR ltrim(lf.extension, '.') = ltrim({parameter}, '.') COLLATE NOCASE)";
                return $"({mime} OR EXISTS (SELECT 1 FROM local_item_files lif JOIN local_files lf ON lf.id = lif.file_id WHERE lif.item_id = li.id AND {extension}))";
            }
            return ApplyTextOperation(condition, known, Match);
        }

        private string Orientation(CollectionRulePredicate condition)
        {
            const string known = "lm.width IS NOT NULL AND lm.height IS NOT NULL AND lm.width > 0 AND lm.height > 0";
            string Match(string value, bool _) => value.Trim().ToLowerInvariant() switch
            {
                "landscape" => "lm.width > lm.height",
                "portrait" => "lm.height > lm.width",
                "square" => "lm.width = lm.height",
                _ => throw new ArgumentException($"Unsupported orientation '{value}'."),
            };
            return ApplyTextOperation(condition, known, Match);
        }

        private string CapturedDate(CollectionRulePredicate condition)
        {
            const string expression = "date(li.captured_at)";
            const string known = "li.captured_at IS NOT NULL";
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, known);
            }

            var values = condition.GetEffectiveValues()
                .Select(value => DateOnly.Parse(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToArray();
            if (condition.Op == "between")
            {
                return $"({known} AND {expression} BETWEEN {Add(values[0])} AND {Add(values[1])})";
            }

            var op = SqlComparison(condition.Op);
            return $"({known} AND {expression} {op} {Add(values[0])})";
        }

        private string Numeric(CollectionRulePredicate condition, string expression)
        {
            var known = $"{expression} IS NOT NULL";
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, known);
            }

            var values = condition.GetEffectiveValues()
                .Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (condition.Op == "between")
            {
                return $"({known} AND {expression} BETWEEN {Add(values[0])} AND {Add(values[1])})";
            }

            return $"({known} AND {expression} {SqlComparison(condition.Op)} {Add(values[0])})";
        }

        private string Place(CollectionRulePredicate condition)
        {
            const string known = "((lm.location_name IS NOT NULL AND trim(lm.location_name) <> '') OR (lm.latitude IS NOT NULL AND lm.longitude IS NOT NULL))";
            string Match(string value, bool contains)
            {
                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"instr(lower(COALESCE(lm.location_name, '')), {parameter}) > 0"
                    : $"lm.location_name = {parameter} COLLATE NOCASE";
            }
            return ApplyTextOperation(condition, known, Match);
        }

        private string Device(CollectionRulePredicate condition)
        {
            const string relation = "SELECT 1 FROM local_item_files lif JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id JOIN view_devices vd ON vd.id = lfs.device_id WHERE lif.item_id = li.id";
            string Match(string value, bool contains)
            {
                if (Guid.TryParse(value, out var id))
                {
                    return $"vd.id = {AddGuid(id)}";
                }

                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"(instr(lower(vd.name), {parameter}) > 0 OR instr(lower(COALESCE(vd.make, '')), {parameter}) > 0 OR instr(lower(COALESCE(vd.model, '')), {parameter}) > 0)"
                    : $"(vd.name = {parameter} COLLATE NOCASE OR vd.client_device_id = {parameter} COLLATE NOCASE OR vd.model = {parameter} COLLATE NOCASE)";
            }
            return ApplyRelatedOperation(condition, relation, Match);
        }

        private string Source(CollectionRulePredicate condition)
        {
            const string relation = "SELECT 1 FROM local_item_files lif JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id JOIN view_sources vs ON vs.id = lfs.source_id WHERE lif.item_id = li.id";
            string Match(string value, bool contains)
            {
                if (Guid.TryParse(value, out var id))
                {
                    return $"vs.id = {AddGuid(id)}";
                }

                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"(instr(lower(vs.name), {parameter}) > 0 OR instr(lower(COALESCE(vs.source_key, '')), {parameter}) > 0)"
                    : $"(vs.name = {parameter} COLLATE NOCASE OR vs.source_key = {parameter} COLLATE NOCASE)";
            }
            return ApplyRelatedOperation(condition, relation, Match);
        }

        private string Owner(CollectionRulePredicate condition)
        {
            const string known = "li.owner_profile_id IS NOT NULL";
            string Match(string value, bool contains)
            {
                if (Guid.TryParse(value, out var id))
                {
                    return $"li.owner_profile_id = {AddGuid(id)}";
                }

                var parameter = Add(value.Trim().ToLowerInvariant());
                var comparison = contains
                    ? $"instr(lower(p.display_name), {parameter}) > 0"
                    : $"p.display_name = {parameter} COLLATE NOCASE";
                return $"EXISTS (SELECT 1 FROM profiles p WHERE p.id = li.owner_profile_id AND {comparison})";
            }
            return ApplyTextOperation(condition, known, Match);
        }

        private string Boolean(CollectionRulePredicate condition, string expression)
        {
            const string known = "1 = 1";
            if (condition.Op == "known")
            {
                return known;
            }

            if (condition.Op == "unknown")
            {
                return "0 = 1";
            }

            var expected = bool.Parse(condition.GetEffectiveValues()[0]) ? 1 : 0;
            var match = $"{expression} = {Add(expected)}";
            return condition.Op == "neq" ? $"({known} AND NOT ({match}))" : match;
        }

        private string TextScalar(CollectionRulePredicate condition, string expression, string known)
        {
            string Match(string value, bool contains)
            {
                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"instr(lower({expression}), {parameter}) > 0"
                    : $"{expression} = {parameter} COLLATE NOCASE";
            }
            return ApplyTextOperation(condition, known, Match);
        }

        private string RelatedText(CollectionRulePredicate condition, string relation, string valueExpression)
        {
            string Match(string value, bool contains)
            {
                var parameter = Add(value.Trim().ToLowerInvariant());
                return contains
                    ? $"instr(lower({valueExpression}), {parameter}) > 0"
                    : $"{valueExpression} = {parameter} COLLATE NOCASE";
            }
            return ApplyRelatedOperation(condition, relation, Match);
        }

        private string ApplyRelatedOperation(CollectionRulePredicate condition, string relation,
            Func<string, bool, string> match)
        {
            var known = $"EXISTS ({relation})";
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, known);
            }

            var matches = condition.GetEffectiveValues().Select(value => match(value, condition.Op == "contains"));
            var anyMatch = $"EXISTS ({relation} AND ({string.Join(" OR ", matches)}))";
            return condition.Op == "neq" ? $"({known} AND NOT ({anyMatch}))" : anyMatch;
        }

        private static string KnownUnknown(string op, string known) =>
            op == "known" ? $"({known})" : $"NOT ({known})";

        private string ApplyTextOperation(CollectionRulePredicate condition, string known,
            Func<string, bool, string> match)
        {
            if (condition.Op is "known" or "unknown")
            {
                return KnownUnknown(condition.Op, known);
            }

            var matches = condition.GetEffectiveValues().Select(value => match(value, condition.Op == "contains"));
            var anyMatch = $"({string.Join(" OR ", matches)})";
            return condition.Op == "neq" ? $"({known} AND NOT {anyMatch})" : anyMatch;
        }

        private static string SqlComparison(string op) => op switch
        {
            "eq" => "=",
            "neq" => "<>",
            "gt" => ">",
            "lt" => "<",
            "gte" => ">=",
            "lte" => "<=",
            _ => throw new ArgumentException($"Unsupported comparison operator '{op}'."),
        };

        private string Add(object value, DbType? type = null)
        {
            var name = $"ViewRule{_parameterIndex++}";
            _parameters.Add(name, value, type);
            return $"@{name}";
        }

        private string AddGuid(Guid value) => Add(GuidSql.ToBlob(value), DbType.Binary);
    }
}

public sealed record LocalAssetSmartRuleSql(string Predicate, DynamicParameters Parameters);
