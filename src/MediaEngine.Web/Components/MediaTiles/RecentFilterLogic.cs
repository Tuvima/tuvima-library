namespace MediaEngine.Web.Components.MediaTiles;

/// <summary>
/// Rules for the Recently Added toggle pills. A scope is "all" or any of watch, read, listen and view joined by commas
/// (the Engine reads the same text). Pills are toggles: several can be on at once, and nothing on means everything.
/// </summary>
public static class RecentFilterLogic
{
    private static readonly string[] Order = ["watch", "read", "listen", "view"];

    public static string Label(string key) => key switch
    {
        "all" => "All",
        "watch" => "Watch",
        "read" => "Read",
        "listen" => "Listen",
        _ => "View",
    };

    /// <summary>The types that have a pill, in display order. One type or none means nothing to filter, so no pills.</summary>
    public static IReadOnlyList<string> VisibleTypes(IEnumerable<string>? available)
    {
        var types = Order.Where(type => available?.Contains(type) == true).ToList();
        return types.Count < 2 ? [] : types;
    }

    public static HashSet<string> Selected(string? scope)
    {
        var parts = (scope ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Where(part => Order.Contains(part)).ToHashSet(StringComparer.Ordinal);
    }

    public static bool IsAll(string? scope) => Selected(scope).Count == 0;

    /// <summary>The scope after the person taps a pill.</summary>
    public static string Toggle(string? scope, string key, IReadOnlyList<string> visibleTypes)
    {
        if (key == "all")
        {
            return "all";
        }

        var selected = Selected(scope);
        if (!selected.Remove(key))
        {
            selected.Add(key);
        }

        // Every pill on is the same as none on.
        if (selected.Count == 0 || visibleTypes.All(selected.Contains))
        {
            return "all";
        }

        return string.Join(',', Order.Where(selected.Contains));
    }
}
