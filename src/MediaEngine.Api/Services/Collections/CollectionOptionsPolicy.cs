using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Collections;

/// <summary>Where a collection is being read, which decides its default minimum (Collections and lists spec D11).</summary>
public enum CollectionSurface
{
    /// <summary>The collection's own page.</summary>
    CollectionPage,

    /// <summary>A Home or area lane built from the collection.</summary>
    Lane,

    /// <summary>The Collections page. Shows a collection that has any items unless a minimum was chosen.</summary>
    CollectionsPage,
}

/// <summary>
/// The collection options that shape what a read returns: minimum items, "at most N", random order with a daily
/// seed and the seasonal window. Pure functions of the collection and a date, so every read path (collection page,
/// lanes, Collections page) applies them the same way and a test controls the clock by passing the date in.
/// </summary>
public static class CollectionOptionsPolicy
{
    /// <summary>The <c>sort_field</c> value that means "random order".</summary>
    public const string RandomSortField = "random";

    /// <summary>A lane with fewer items than this (and no minimum chosen) is skipped silently.</summary>
    public const int DefaultLaneMinimum = 3;

    /// <summary>The collection page needs at least one item.</summary>
    public const int DefaultPageMinimum = 1;

    /// <summary>True when the collection is ordered at random.</summary>
    public static bool IsRandomOrder(Collection collection) =>
        string.Equals(collection.SortField?.Trim(), RandomSortField, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when there is no seasonal window, or <paramref name="today"/> is inside it (edges included).</summary>
    public static bool IsInSeason(Collection collection, DateOnly today) =>
        collection.SeasonWindow is not { } window || window.Contains(today);

    /// <summary>When an out-of-season collection comes back, or null while it is shown.</summary>
    public static DateOnly? HiddenUntil(Collection collection, DateOnly today) =>
        collection.SeasonWindow is { } window && !window.Contains(today) ? window.NextOpening(today) : null;

    /// <summary>
    /// The fewest items the collection needs on <paramref name="surface"/>: the minimum the person chose, otherwise
    /// the surface's default. The Collections page has no default, so a new empty collection can still be found and filled.
    /// </summary>
    public static int EffectiveMinimum(Collection collection, CollectionSurface surface)
    {
        if (collection.MinItems > 0)
        {
            return collection.MinItems;
        }

        return surface switch
        {
            CollectionSurface.Lane => DefaultLaneMinimum,
            CollectionSurface.CollectionPage => DefaultPageMinimum,
            _ => 0,
        };
    }

    /// <summary>True when <paramref name="itemCount"/> (after any cap) reaches the minimum for <paramref name="surface"/>.</summary>
    public static bool MeetsMinimum(Collection collection, int itemCount, CollectionSurface surface) =>
        itemCount >= EffectiveMinimum(collection, surface);

    /// <summary>The number of items a read returns once "at most N" is applied.</summary>
    public static int CapCount(Collection collection, int itemCount) =>
        collection.MaxItems is { } max and > 0 ? Math.Min(itemCount, max) : itemCount;

    /// <summary>
    /// Applies the collection's order option and cap to a list already in the collection's own order. Random order
    /// ranks every item by a hash of the collection, the day and the item, so the same items come back all day and a
    /// different set the next, and an item added or removed disturbs only its own place. The cap then keeps the first N.
    /// </summary>
    public static IReadOnlyList<T> Arrange<T>(
        Collection collection,
        IReadOnlyList<T> ordered,
        Func<T, Guid> idOf,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(idOf);

        IEnumerable<T> result = ordered;
        if (IsRandomOrder(collection))
        {
            result = ordered
                .Select(item => (Item: item, Rank: DailyRank(collection.Id, today, idOf(item))))
                .OrderBy(entry => entry.Rank)
                .ThenBy(entry => idOf(entry.Item))
                .Select(entry => entry.Item);
        }

        if (collection.MaxItems is { } max and > 0)
        {
            result = result.Take(max);
        }

        return result as IReadOnlyList<T> ?? result.ToList();
    }

    /// <summary>
    /// Applies the options a create or save request carries. A null value leaves that option as it is; zero minimum
    /// returns to the surface default, zero "at most" removes the limit, and empty text on both season edges removes the
    /// window. Returns a plain-English reason when a value is not acceptable (and changes nothing), otherwise null.
    /// </summary>
    public static string? TryApplyRequested(
        Collection collection,
        int? minItems,
        int? maxItems,
        string? seasonFrom,
        string? seasonTo)
    {
        ArgumentNullException.ThrowIfNull(collection);

        if (minItems is < 0)
        {
            return "Minimum items cannot be negative.";
        }

        if (maxItems is < 0)
        {
            return "At most cannot be negative.";
        }

        CollectionSeasonWindow? window = collection.SeasonWindow;
        var changeWindow = seasonFrom is not null || seasonTo is not null;
        if (changeWindow)
        {
            if (!CollectionSeasonWindow.TryCreate(seasonFrom, seasonTo, out window))
            {
                return "Choose both a start and an end day for the seasonal window, for example 10-01 to 11-01.";
            }
        }

        if (minItems is { } min)
        {
            collection.MinItems = min;
        }

        if (maxItems is { } max)
        {
            collection.MaxItems = max == 0 ? null : max;
        }

        if (changeWindow)
        {
            collection.SetSeasonWindow(window);
        }

        return null;
    }

    private static ulong DailyRank(Guid collectionId, DateOnly day, Guid itemId)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{collectionId:N}|{day:yyyy-MM-dd}|{itemId:N}"), hash);
        return BitConverter.ToUInt64(hash);
    }
}
