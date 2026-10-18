using System.Linq.Expressions;
using System.Reflection;

namespace MediaEngine.Storage.Benchmarks.Ef;

/// <summary>
/// Copies a row of one type into another type by matching public property names, compiled once into a single
/// member-initialiser expression (no per-row reflection). A source <c>T?</c> feeding a target <c>T</c> becomes
/// <c>value ?? default</c>, the result Dapper produces for a NULL column.
/// </summary>
internal static class RowMapper<TFrom, TTo>
    where TFrom : class
    where TTo : class, new()
{
    private static readonly Func<TFrom, TTo> Map = Compile();

    public static TTo Convert(TFrom from) => Map(from);

    private static Func<TFrom, TTo> Compile()
    {
        var source = Expression.Parameter(typeof(TFrom), "from");
        var bindings = new List<MemberBinding>();
        foreach (var target in typeof(TTo).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!target.CanWrite)
            {
                continue;
            }

            var origin = typeof(TFrom).GetProperty(target.Name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{typeof(TFrom).Name} has no property '{target.Name}' to map into {typeof(TTo).Name}.");

            Expression value = Expression.Property(source, origin);
            if (origin.PropertyType != target.PropertyType)
            {
                value = Nullable.GetUnderlyingType(origin.PropertyType) == target.PropertyType
                    ? Expression.Coalesce(value, Expression.Default(target.PropertyType))
                    : throw new InvalidOperationException($"Cannot map {origin.PropertyType.Name} to {target.PropertyType.Name} for '{target.Name}'.");
            }

            bindings.Add(Expression.Bind(target, value));
        }

        return Expression.Lambda<Func<TFrom, TTo>>(
            Expression.MemberInit(Expression.New(typeof(TTo)), bindings),
            source).Compile();
    }
}
