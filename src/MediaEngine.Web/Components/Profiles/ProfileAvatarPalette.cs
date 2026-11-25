namespace MediaEngine.Web.Components.Profiles;

/// <summary>The fixed set of avatar colours a profile can choose from; typed hex colours are gone.</summary>
public static class ProfileAvatarPalette
{
    /// <summary>One swatch: a friendly name for screen readers and its #RRGGBB colour.</summary>
    public sealed record Swatch(string Name, string Hex);

    /// <summary>Twelve swatches. The first is the Tuvima purple; all read well with white or dark lettering.</summary>
    public static readonly IReadOnlyList<Swatch> Swatches =
    [
        new("Tuvima purple", "#8852FC"),
        new("Violet", "#7C4DFF"),
        new("Blue", "#3B82F6"),
        new("Sky", "#22D3EE"),
        new("Teal", "#14B8A6"),
        new("Green", "#81C784"),
        new("Yellow", "#FBBF24"),
        new("Orange", "#FF9F43"),
        new("Red", "#F87171"),
        new("Pink", "#EC4899"),
        new("Lavender", "#B39DDB"),
        new("Slate", "#64748B"),
    ];

    /// <summary>Returns the swatch matching <paramref name="hex"/> (any case), or <see langword="null"/> for an older custom colour.</summary>
    public static Swatch? Find(string? hex) =>
        Swatches.FirstOrDefault(swatch => string.Equals(swatch.Hex, hex, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gives each person in a household a circle colour that differs from everyone before them (profile and household D13), so two
    /// people with the same initial are never identical. A person keeps the swatch they chose unless an earlier person already
    /// has it (or it is not one of the <see cref="Swatches"/>); then they get the first unused swatch, wrapping around after twelve.
    /// Worked out each time the list is shown, in the order the household lists its people; nothing is stored.
    /// </summary>
    /// <param name="chosen">The colour each person has saved, in household order.</param>
    /// <returns>One #RRGGBB colour per person, in the same order.</returns>
    public static IReadOnlyList<string> Distinct(IReadOnlyList<string?> chosen)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(chosen.Count);
        foreach (var color in chosen)
        {
            var swatch = Find(color);
            if (swatch is null || used.Contains(swatch.Hex))
            {
                swatch = Swatches.FirstOrDefault(candidate => !used.Contains(candidate.Hex))
                    ?? Swatches[result.Count % Swatches.Count];
            }

            used.Add(swatch.Hex);
            result.Add(swatch.Hex);
        }

        return result;
    }

    /// <summary>Dark or light lettering, whichever reads better on <paramref name="hex"/> (the same rule the profile picture uses).</summary>
    public static string InkFor(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return "#ffffff";
        }

        var red = (rgb >> 16) & 0xFF;
        var green = (rgb >> 8) & 0xFF;
        var blue = rgb & 0xFF;
        return (0.299 * red) + (0.587 * green) + (0.114 * blue) > 160 ? "#1b1b1f" : "#ffffff";
    }
}
