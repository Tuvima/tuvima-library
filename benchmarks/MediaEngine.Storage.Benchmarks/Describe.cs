using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MediaEngine.Storage.Benchmarks;

/// <summary>
/// Turns result objects into stable one-line texts so <c>verify</c> can compare every mapped field between the
/// production Dapper result and each EF variant, and report the first differing row.
/// </summary>
internal static class Describe
{
    private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = [];

    public static string Row(object? value)
    {
        var builder = new StringBuilder();
        Append(builder, value);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                builder.Append('"').Append(text).Append('"');
                return;
            case DateTimeOffset moment:
                builder.Append(moment.ToString("o", CultureInfo.InvariantCulture));
                return;
            case IFormattable formattable when value.GetType().IsPrimitive || value is Guid || value is decimal:
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IDictionary dictionary:
                AppendDictionary(builder, dictionary);
                return;
            case IEnumerable enumerable:
                builder.Append('[');
                foreach (var item in enumerable)
                {
                    Append(builder, item);
                    builder.Append(',');
                }

                builder.Append(']');
                return;
        }

        var type = value.GetType();
        if (type.IsEnum || type.IsPrimitive)
        {
            builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        builder.Append('{');
        foreach (var property in PropertiesOf(type))
        {
            builder.Append(property.Name).Append('=');
            Append(builder, property.GetValue(value));
            builder.Append(';');
        }

        builder.Append('}');
    }

    private static void AppendDictionary(StringBuilder builder, IDictionary dictionary)
    {
        var entries = new List<(string Key, object? Value)>();
        foreach (DictionaryEntry entry in dictionary)
        {
            entries.Add((Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value));
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        builder.Append('{');
        foreach (var (key, entryValue) in entries)
        {
            builder.Append(key).Append(':');
            Append(builder, entryValue);
            builder.Append(',');
        }

        builder.Append('}');
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        lock (PropertyCache)
        {
            if (!PropertyCache.TryGetValue(type, out var properties))
            {
                properties = type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetIndexParameters().Length == 0)
                    .ToArray();
                PropertyCache[type] = properties;
            }

            return properties;
        }
    }
}
