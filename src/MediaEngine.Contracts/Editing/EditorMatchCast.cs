namespace MediaEngine.Contracts.Editing;

/// <summary>How much of a record's cast the Match tab shows (universal editor v7 follow-up): the first few names, so the card stays short.</summary>
public static class EditorMatchCast
{
    /// <summary>The most names a match card carries.</summary>
    public const int Limit = 4;

    /// <summary>The fact name a provider's search result may carry its cast under, names separated by "|".</summary>
    public const string ExtraKey = "cast";

    /// <summary>The first <see cref="Limit"/> distinct, non-blank names of <paramref name="names"/>, trimmed, in the order given.</summary>
    public static IReadOnlyList<string> Lead(IEnumerable<string?> names) =>
        [.. names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(Limit)];

    /// <summary>Reads the cast a search result carried as one "|"-separated text; empty when it carried none.</summary>
    public static IReadOnlyList<string> FromText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : Lead(text.Split('|'));
}
