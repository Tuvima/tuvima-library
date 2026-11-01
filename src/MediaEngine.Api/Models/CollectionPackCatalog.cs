using MediaEngine.Api.Services.Collections;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Models;

/// <summary>
/// One collection a pack would create (Collections and lists spec D14): a name, the rules, how it is ordered, and the
/// optional seasonal window. It carries no titles and nothing about any person's library.
/// </summary>
public sealed record PackEntry(
    string Name,
    string? Description,
    CollectionRuleDefinition Rules,
    string SortField,
    string SortDirection,
    string? IconName = null,
    string? SecondarySortField = null,
    string? SecondarySortDirection = null,
    int MinItems = 0,
    int MaxItems = 0,
    string? SeasonFrom = null,
    string? SeasonTo = null,
    string PrimaryArea = "Mixed",
    string? ArtworkHint = CollectionPackCatalog.StackArtworkHint)
{
    /// <summary>How many titles the library needs before the pack creates this collection: the entry's own minimum, otherwise the lane default.</summary>
    public int RequiredItems => MinItems > 0 ? MinItems : CollectionPackCatalog.MinimumItems;
}

/// <summary>A pack ready to count and create: the built-in starter packs and a pack file both become one of these.</summary>
public sealed record PackBlueprint(
    string Id,
    string Name,
    string? Description,
    bool CreatesSet,
    IReadOnlyList<PackEntry> Entries);

/// <summary>A built-in starter pack as the picker lists it.</summary>
public sealed record StarterPack(string Id, string Name, string Description, bool CreatesSet);

/// <summary>
/// The built-in starter packs (spec 4e) and the small rule-building helpers every pack uses. The packs themselves are
/// generated from the person's own library by <see cref="Services.Collections.Packs.CollectionPackPlanner"/>; nothing
/// is downloaded.
/// </summary>
public static class CollectionPackCatalog
{
    public const string DecadesId = "decades";
    public const string GenresId = "genres";
    public const string AwardsId = "award-winners";
    public const string PeopleId = "people-you-collect";
    public const string BasedOnBookId = "based-on-a-book";
    public const string SeasonalId = "seasonal";
    public const string PickUpAgainId = "pick-up-again";

    /// <summary>The artwork hint for the automatic stack of covers, the default for every collection.</summary>
    public const string StackArtworkHint = "stack";

    /// <summary>The fewest titles a collection needs before a pack creates it: the same minimum a lane uses.</summary>
    public const int MinimumItems = CollectionOptionsPolicy.DefaultLaneMinimum;

    /// <summary>The most collections one pack creates from a long list (genres, people), the biggest first.</summary>
    public const int MostCollectionsPerList = 30;

    /// <summary>The most entries a pack file may hold.</summary>
    public const int MostFileEntries = 200;

    public static IReadOnlyList<StarterPack> Starters { get; } =
    [
        new(DecadesId, "Decades", "One collection for each decade your library has enough titles from, from the 1960s to the 2020s.", true),
        new(GenresId, "Genres", "One collection for each genre you own enough of, kept apart for watching, reading and listening.", true),
        new(AwardsId, "Award winners", "Oscars Best Picture, Emmy drama and comedy, Hugo, Nebula, Booker, Pulitzer, Grammy and Eisner winners you own.", true),
        new(PeopleId, "Directors, authors and artists you collect", "One collection for each director, author or artist with at least three titles in your library.", true),
        new(BasedOnBookId, "Based on a book you own", "Films and shows adapted from a book that is also in your library.", false),
        new(SeasonalId, "Seasonal", "Halloween from 1 October and Festive from 1 December, shown only in their season.", true),
        new(PickUpAgainId, "Pick up again", "Unwatched classics, unread books that have sat on the shelf, and short films and books.", true),
    ];

    // The names of the reserved first and last rule groups the Dashboard's sentence builder reads (media chips, and
    // "but never when" exceptions). Packs write them so their rules open in the plain sentence layout.
    private const string MediaGroupId = "media";
    private const string ExceptionsGroupId = "exceptions";

    public static CollectionRulePredicate Rule(string field, string op, string value, string? display = null) =>
        new() { Field = field, Op = op, Value = value, DisplayValue = display ?? value };

    public static CollectionRulePredicate RuleIn(string field, IReadOnlyList<string> values, IReadOnlyList<string>? displays = null) =>
        values.Count == 1
            ? Rule(field, "eq", values[0], displays?[0])
            : new CollectionRulePredicate
            {
                Field = field,
                Op = "in",
                Values = values.ToArray(),
                DisplayValues = (displays ?? values).ToArray(),
            };

    /// <summary>The media type chips: one rule in the reserved first group. Null for "Everything".</summary>
    public static CollectionRuleGroup? MediaChips(IReadOnlyList<string> mediaTypes) =>
        mediaTypes.Count == 0
            ? null
            : new CollectionRuleGroup
            {
                Id = MediaGroupId,
                JoinWithPrevious = "and",
                MatchMode = "all",
                Conditions = [RuleIn("media_type", mediaTypes)],
            };

    /// <summary>A group of rules, all of which must hold (or any of them, with <paramref name="any"/>).</summary>
    public static CollectionRuleGroup Rules(bool any, params CollectionRulePredicate[] rules) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            JoinWithPrevious = "and",
            MatchMode = any ? "any" : "all",
            Conditions = rules.ToList(),
        };

    /// <summary>The "but never when" group; every rule in it is stored as the opposite of what it reads.</summary>
    public static CollectionRuleGroup Exceptions(params CollectionRulePredicate[] storedRules) =>
        new()
        {
            Id = ExceptionsGroupId,
            JoinWithPrevious = "and",
            MatchMode = "all",
            Conditions = storedRules.ToList(),
        };

    /// <summary>Joins groups with "and" (the first needs no join); a group built with another join is kept as it is.</summary>
    public static CollectionRuleDefinition Definition(params CollectionRuleGroup?[] groups) =>
        new() { Groups = groups.Where(group => group is not null).Select(group => group!).ToList() };

    /// <summary>Two media-specific alternatives: the second group joins the first with "or" ("short films or short books").</summary>
    public static CollectionRuleDefinition Either(CollectionRuleGroup first, CollectionRuleGroup second)
    {
        second.JoinWithPrevious = "or";
        return new CollectionRuleDefinition { Groups = [first, second] };
    }
}
