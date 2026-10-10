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
}
