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
    public static LocalAssetSmartRuleSql Compile(CollectionRuleDefinition definition)
    {
        ViewSmartGalleryRules.Validate(definition);
        var compiler = new Compiler();
        return compiler.Compile(definition);
    }

    private sealed class Compiler
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

        private string CompileCondition(CollectionRulePredicate condition) => condition.Field switch
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
            "tags" => RelatedText(condition,
                "SELECT 1 FROM local_item_tag_links lit JOIN tags tg ON tg.id = lit.tag_id WHERE lit.item_id = li.id", "tg.name"),
            "favorite" => Boolean(condition, "li.favorite"),
            "owner" => Owner(condition),
            "source" => Source(condition),
            _ => throw new ArgumentException($"Unsupported Smart Gallery field '{condition.Field}'."),
        };

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
