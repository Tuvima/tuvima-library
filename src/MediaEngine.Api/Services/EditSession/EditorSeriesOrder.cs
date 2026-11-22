using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Puts a Series' members in order for the universal editor and says which rule placed each one (spec <c>group-pages-and-tiles</c> D5, D6, Q54).
/// The sort is the Library's one Series ordering (<see cref="SeriesOrdering"/>): the number the Series gives a member, then its publication date,
/// then its title. The rule named for a member is the first of those that applies to it, so the editor shows what the Engine did, never a guess.
/// </summary>
public static class EditorSeriesOrder
{
    /// <summary>The canonical keys that can say when a member was published, best first.</summary>
    private static readonly string[] DateKeys =
    [
        MetadataFieldConstants.PublicationDate,
        MetadataFieldConstants.OriginalPublicationDate,
        MetadataFieldConstants.OriginalReleaseDate,
        MetadataFieldConstants.Year,
    ];

    /// <summary>What the ordering reads about one member.</summary>
    public readonly record struct Entry(Guid Id, string Title, int? Number, string? MediaType, string? Date);

    /// <summary>Reads a work's ordering facts: its title, its own number in the Series, its media type and its publication date or year.</summary>
    public static Entry EntryOf(Work work)
    {
        ArgumentNullException.ThrowIfNull(work);
        string? Value(string key) => work.CanonicalValues
            .Where(value => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Value)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim();

        return new Entry(
            work.Id,
            Value(MetadataFieldConstants.Title) ?? string.Empty,
            work.Ordinal,
            work.MediaType.ToString(),
            DateKeys.Select(Value).FirstOrDefault(text => text is not null));
    }

    /// <summary>The members that have a title, in the Series' order, each naming the rule that placed it.</summary>
    public static List<EditorSubjectMemberDto> Place(IEnumerable<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var named = entries.Where(entry => entry.Title.Length > 0);
        return
        [
            .. SeriesOrdering.Order(named, InputOf).Select(entry => new EditorSubjectMemberDto
            {
                Id = entry.Id,
                Title = entry.Title,
                Number = entry.Number,
                MediaType = entry.MediaType,
                Year = YearOf(entry.Date),
                PlacedBy = RuleOf(entry),
            }),
        ];
    }

    /// <summary>The first rule that applies to the member: it has a number, else a publication date, else it is placed by its title alone.</summary>
    public static string RuleOf(Entry entry) =>
        entry.Number.HasValue ? EditorSeriesOrderRuleKeys.Number
        : !string.IsNullOrWhiteSpace(entry.Date) ? EditorSeriesOrderRuleKeys.Date
        : EditorSeriesOrderRuleKeys.Title;

    private static SeriesOrderInput InputOf(Entry entry) =>
        new(Scope: null, ManifestOrdinal: null, WorkOrdinalSort: entry.Number, PublicationDate: entry.Date, Title: entry.Title);

    // The date as stored is ISO or just a year; the detail line shows the year.
    private static string? YearOf(string? date) =>
        date is { Length: >= 4 } && date.AsSpan(0, 4).IndexOfAnyExceptInRange('0', '9') < 0 ? date[..4] : null;
}
