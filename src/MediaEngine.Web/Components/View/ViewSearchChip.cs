namespace MediaEngine.Web.Components.View;

/// <summary>One chosen suggestion in the Photos search bar: its kind ("person", "place", "year", "camera" or "tag") and the text it stands for.</summary>
public sealed record ViewSearchChip(string Kind, string Value);

/// <summary>Turns the chips and typed words into what the timeline asks the Engine for: a year becomes a date range, everything else a search word that must match (all chips combine).</summary>
public static class ViewSearchChips
{
    public static string SearchText(IEnumerable<ViewSearchChip> chips, string? typed) =>
        string.Join(' ', chips.Where(chip => chip.Kind != "year").Select(chip => chip.Value)
            .Append(typed ?? string.Empty).Where(part => !string.IsNullOrWhiteSpace(part)));

    public static (DateTimeOffset? From, DateTimeOffset? To) YearRange(IEnumerable<ViewSearchChip> chips)
    {
        var years = chips.Where(chip => chip.Kind == "year" && int.TryParse(chip.Value, out var year) && year is >= 1 and <= 9999)
            .Select(chip => int.Parse(chip.Value)).ToList();
        return years.Count == 0
            ? (null, null)
            : (new DateTimeOffset(years.Min(), 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(years.Max(), 12, 31, 23, 59, 59, 999, TimeSpan.Zero));
    }
}
