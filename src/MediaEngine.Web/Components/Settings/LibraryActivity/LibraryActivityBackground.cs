using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Settings.LibraryActivity;

/// <summary>
/// What the quiet Background line says (spec library-activity D29): the Engine's follow-up work with its plain name and count
/// ("Making preview pictures for 12 videos"), then any other background work the menu indicator knows about (AI, enrichment,
/// maintenance). Follow-up work the Engine already named is not listed twice.
/// </summary>
public static class LibraryActivityBackground
{
    /// <param name="engineWork">The Engine's follow-up work, from the Activity snapshot.</param>
    /// <param name="shellItems">The menu indicator's items; ingestion is never background work.</param>
    /// <param name="namedFormat">The words for one named piece of work, with the name, count and noun as {0}, {1} and {2}.</param>
    public static IReadOnlyList<string> Names(
        IEnumerable<IngestionBackgroundWorkDto>? engineWork,
        IEnumerable<ShellActivityItem> shellItems,
        string namedFormat)
    {
        var named = (engineWork ?? []).Where(work => work.Count > 0).ToList();
        var covered = named.Select(work => work.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = named
            .Select(work => string.IsNullOrWhiteSpace(work.Noun)
                ? work.Label
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, namedFormat, work.Label, work.Count, work.Noun))
            .ToList();
        names.AddRange(shellItems
            .Where(item => item.Kind != ShellActivityKind.Ingestion
                && !(item.OperationType is not null && covered.Contains(item.OperationType)))
            .Select(item => item.Label));
        return names.Distinct().ToList();
    }
}
