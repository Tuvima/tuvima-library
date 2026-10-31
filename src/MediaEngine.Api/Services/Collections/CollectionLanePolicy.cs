using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Collections;

/// <summary>
/// Where a collection can be shown as a lane (Collections and lists spec D9): Home and the Watch, Read and Listen
/// pages. The Collections page always lists a collection, so it has no switch. A lane's stored location is the
/// placement store's <c>location</c>; the short area key is what screens and requests use.
/// </summary>
public static class CollectionLanePolicy
{
    public const string HomeLocation = "home";
    public const string WatchLocation = "area:watch";
    public const string ReadLocation = "area:read";
    public const string ListenLocation = "area:listen";

    /// <summary>Every location a lane can have, in the order the editor lists them.</summary>
    public static readonly IReadOnlyList<string> Locations = [HomeLocation, WatchLocation, ReadLocation, ListenLocation];

    /// <summary>The most tiles a lane shows before its View all tile.</summary>
    public const int LaneItemLimit = 20;

    private static int _version;

    /// <summary>Changes whenever a lane choice is saved, so a cached lane list is never served after the change.</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>Called after a placement or a profile's hide was saved.</summary>
    public static void MarkChanged() => Interlocked.Increment(ref _version);

    /// <summary>"home", "watch", "read" or "listen" to the stored location; null for anything else.</summary>
    public static string? LocationFor(string? areaKey) => areaKey?.Trim().ToLowerInvariant() switch
    {
        "home" => HomeLocation,
        "watch" => WatchLocation,
        "read" => ReadLocation,
        "listen" => ListenLocation,
        _ => null,
    };

    /// <summary>
    /// Turns the area keys of a "Show on" request into stored locations, each once, in the editor's order. False when
    /// any key is not one of home, watch, read or listen.
    /// </summary>
    public static bool TryParseShowOn(IEnumerable<string>? areaKeys, out IReadOnlyList<string> locations)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in areaKeys ?? [])
        {
            if (LocationFor(key) is not { } location)
            {
                locations = [];
                return false;
            }

            wanted.Add(location);
        }

        locations = Locations.Where(wanted.Contains).ToList();
        return true;
    }

    /// <summary>The short key for a stored location ("area:read" becomes "read"); null for a location that is not a lane.</summary>
    public static string? AreaKeyFor(string? location) => location switch
    {
        HomeLocation => "home",
        WatchLocation => "watch",
        ReadLocation => "read",
        ListenLocation => "listen",
        _ => null,
    };

    /// <summary>The location of the lanes on the Watch, Read or Listen page.</summary>
    public static string AreaLocation(string lane) => $"area:{lane.Trim().ToLowerInvariant()}";

    /// <summary>
    /// True when everyone sees the collection (Library-owned or shared with everyone): its lane belongs to the shared,
    /// administrator-ordered list. Anything else is a profile's own (or selected-profile) collection and is listed after
    /// the shared lanes.
    /// </summary>
    public static bool IsShared(Collection collection) =>
        collection.OwnerKind == ContainerOwnerKind.Library || collection.Audience == ContainerAudience.Everyone;
}
