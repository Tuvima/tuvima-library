using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>
/// One row as text for the before-and-after snapshot: every public readable property in declaration order as
/// <c>name=value</c>, joined by <c> | </c>. Invariant culture, dates as <c>o</c>, Guids as <c>N</c>, null as
/// <c>&lt;null&gt;</c>, collections as <c>[a, b]</c>, nested objects as <c>{name=value; name=value}</c>, newlines as
/// <c>\n</c>. The text is a function of the values only, so equal rows always give equal lines.
/// </summary>
internal static class RowFormatter
{
    public const string Null = "<null>";
    public const string FieldSeparator = " | ";

    private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = [];

    /// <summary>The tie-break columns of the D7 rule, in order. Only those a row type has are used.</summary>
    private static readonly string[] TieKeyNames = ["WorkId", "LibraryId", "AssetId", "EntityId"];

    public static FormattedRow Format(object row)
    {
        var fields = new List<(string Name, string Value)>();
        foreach (var property in PropertiesOf(row.GetType()))
        {
            fields.Add((property.Name, FormatValue(property.GetValue(row))));
        }

        var line = string.Join(FieldSeparator, fields.Select(field => field.Name + "=" + field.Value));
        string? createdAt = null;
        foreach (var (name, value) in fields)
        {
            if (name == "CreatedAt")
            {
                createdAt = value;
            }
        }

        var keys = new List<string>();
        foreach (var keyName in TieKeyNames)
        {
            foreach (var (name, value) in fields)
            {
                if (name == keyName)
                {
                    keys.Add(value);
                }
            }
        }

        return new FormattedRow(line, createdAt, keys);
    }

    public static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return Null;
            case string text:
                return Escape(text);
            case Guid guid:
                return guid.ToString("N", CultureInfo.InvariantCulture);
            case DateTimeOffset moment:
                return moment.ToString("o", CultureInfo.InvariantCulture);
            case DateTime moment:
                return moment.ToString("o", CultureInfo.InvariantCulture);
            case bool flag:
                return flag ? "true" : "false";
            case Enum enumValue:
                return enumValue.ToString();
            case IDictionary dictionary:
                return FormatDictionary(dictionary);
            case IEnumerable enumerable:
                return "[" + string.Join(", ", enumerable.Cast<object?>().Select(FormatValue)) + "]";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder("{");
        var first = true;
        foreach (var property in PropertiesOf(value.GetType()))
        {
            if (!first)
            {
                builder.Append("; ");
            }

            first = false;
            builder.Append(property.Name).Append('=').Append(FormatValue(property.GetValue(value)));
        }

        return builder.Append('}').ToString();
    }

    /// <summary>Escapes line breaks so one row is always one line.</summary>
    private static string Escape(string text) =>
        text.Contains('\n') || text.Contains('\r')
            ? text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            : text;

    private static string FormatDictionary(IDictionary dictionary)
    {
        var entries = new List<(string Key, string Value)>();
        foreach (DictionaryEntry entry in dictionary)
        {
            entries.Add((FormatValue(entry.Key), FormatValue(entry.Value)));
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        return "{" + string.Join(", ", entries.Select(entry => entry.Key + "=" + entry.Value)) + "}";
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        lock (PropertyCache)
        {
            if (!PropertyCache.TryGetValue(type, out var properties))
            {
                // MetadataToken order is declaration order; GetProperties() alone does not promise it.
                properties = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                    .OrderBy(property => property.MetadataToken)];
                PropertyCache[type] = properties;
            }

            return properties;
        }
    }
}

/// <summary>A formatted row plus what the D7 tie rule needs: the added time and the tie-break columns.</summary>
internal sealed record FormattedRow(string Line, string? CreatedAt, IReadOnlyList<string> Keys);
