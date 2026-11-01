using System.Globalization;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using static MediaEngine.Api.Models.CollectionPackCatalog;

namespace MediaEngine.Api.Services.Collections.Packs;

/// <summary>Which media types a pack may use. Empty means everything; a playlist flow passes Music.</summary>
public sealed class PackScope
{
    private static readonly string[] Known = ["Movies", "TV", "Books", "Audiobooks", "Music", "Comics", "Videos"];

    public static PackScope Everything { get; } = new([]);

    private PackScope(IReadOnlyList<string> mediaTypes) => MediaTypes = mediaTypes;

    public IReadOnlyList<string> MediaTypes { get; }

    /// <summary>Keeps the media types the Library knows, spelled as it spells them; anything else is dropped.</summary>
    public static PackScope From(IEnumerable<string>? mediaTypes) =>
        new((mediaTypes ?? [])
            .Select(type => Known.FirstOrDefault(known => string.Equals(known, type?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(type => type is not null)
            .Select(type => type!)
            .Distinct(StringComparer.Ordinal)
            .ToList());

    public bool Allows(string mediaType) =>
        MediaTypes.Count == 0 || MediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);

    public bool AllowsAny(params string[] mediaTypes) => mediaTypes.Any(Allows);

    /// <summary>The given media types that the scope allows.</summary>
    public IReadOnlyList<string> Narrow(IReadOnlyList<string> mediaTypes) =>
        MediaTypes.Count == 0 ? mediaTypes : mediaTypes.Where(Allows).ToList();

    /// <summary>The chips a pack puts on its rules: nothing for "Everything", otherwise the scope's media types.</summary>
    public CollectionRuleGroup? Chips() => MediaChips(MediaTypes);
}

/// <summary>A collection a pack would create, with how many titles the library has for it today.</summary>
public sealed record PlannedCollection(
    PackEntry Entry,
    int ItemCount,
    IReadOnlyDictionary<string, int> MediaTypeCounts);

/// <summary>A pack counted against the library: only the collections the library supports are in it.</summary>
public sealed record PackPlan(
    string Id,
    string Name,
    string? Description,
    bool CreatesSet,
    IReadOnlyList<PlannedCollection> Collections,
    bool UsesPerProfileFields,
    IReadOnlyList<string>? LeftOut = null);

/// <summary>
/// Works out what each pack would create from the library as it is now (Collections and lists spec D14, 4e): a
/// collection is offered only when the library has at least its minimum number of titles for it, so a pack never
/// creates an empty row. Counts use the same rule evaluation, visibility and display collapsing as the editor's
/// preview, evaluated for the profile asking.
/// </summary>
public sealed class CollectionPackPlanner(
    ICollectionBrowseReadService browse,
    ICollectionMediaLookupReadService lookup,
    CollectionCatalogReadService catalog,
    IDisplayProjectionReadService display)
{
    private sealed record Area(string Verb, string PrimaryArea, string[] MediaTypes);

    private static readonly Area[] Areas =
    [
        new("to watch", "Watch", ["Movies", "TV"]),
        new("to read", "Read", ["Books", "Comics"]),
        new("to listen to", "Listen", ["Music", "Audiobooks"]),
    ];

    /// <summary>The award collections, matched by the words in the award's name among the awards the library already stores.</summary>
    private sealed record AwardSpec(string Name, string[] Words, string[] NotWords);

    private static readonly AwardSpec[] AwardSpecs =
    [
        new("Oscar Best Picture winners", ["academy award", "best picture"], []),
        new("Emmy Outstanding Drama Series winners", ["emmy", "outstanding drama series"], []),
        new("Emmy Outstanding Comedy Series winners", ["emmy", "outstanding comedy series"], []),
        new("Hugo Award for Best Novel winners", ["hugo award", "best novel"], []),
        new("Nebula Award for Best Novel winners", ["nebula award", "best novel"], []),
        new("Booker Prize winners", ["booker prize"], ["international"]),
        new("Pulitzer Prize for Fiction winners", ["pulitzer prize for fiction"], []),
        new("Grammy Album of the Year winners", ["grammy award", "album of the year"], []),
        new("Eisner Award winners", ["eisner award"], []),
    ];

    private sealed record PersonKind(string Field, string NameFormat, string[] MediaTypes, string SortField, string SortDirection, string PrimaryArea);

    private static readonly PersonKind[] PersonKinds =
    [
        new("director", "Directed by {0}", ["Movies", "TV"], "year", "asc", "Watch"),
        new("author", "Written by {0}", ["Books", "Audiobooks", "Comics"], "year", "asc", "Read"),
        new("artist", "Music by {0}", ["Music"], "title", "asc", "Listen"),
    ];

    /// <summary>Every built-in starter pack, counted for the given profile.</summary>
    public async Task<IReadOnlyList<PackPlan>> PlanStartersAsync(PackScope scope, Guid? viewerProfileId, CancellationToken ct)
    {
        var counter = await CreateCounterAsync(viewerProfileId, ct).ConfigureAwait(false);
        var plans = new List<PackPlan>();
        foreach (var starter in Starters)
        {
            plans.Add(await PlanStarterAsync(starter, scope, counter, ct).ConfigureAwait(false));
        }

        return plans;
    }

    /// <summary>One built-in starter pack by id, or null when there is no such pack.</summary>
    public async Task<PackPlan?> PlanStarterAsync(string packId, PackScope scope, Guid? viewerProfileId, CancellationToken ct)
    {
        var starter = Starters.FirstOrDefault(candidate => string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase));
        if (starter is null)
        {
            return null;
        }

        var counter = await CreateCounterAsync(viewerProfileId, ct).ConfigureAwait(false);
        return await PlanStarterAsync(starter, scope, counter, ct).ConfigureAwait(false);
    }

    /// <summary>A pack from a definition (a pack file): each entry is kept only if the library has enough titles for it.</summary>
    public async Task<PackPlan> PlanBlueprintAsync(PackBlueprint blueprint, Guid? viewerProfileId, CancellationToken ct)
    {
        var counter = await CreateCounterAsync(viewerProfileId, ct).ConfigureAwait(false);
        var kept = new List<PlannedCollection>();
        var leftOut = new List<string>();
        foreach (var entry in blueprint.Entries)
        {
            if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
            {
                kept.Add(planned);
            }
            else
            {
                leftOut.Add(entry.Name);
            }
        }

        return ToPlan(blueprint.Id, blueprint.Name, blueprint.Description, blueprint.CreatesSet, kept) with { LeftOut = leftOut };
    }

    private async Task<PackPlan> PlanStarterAsync(StarterPack starter, PackScope scope, Counter counter, CancellationToken ct)
    {
        var collections = starter.Id switch
        {
            DecadesId => await DecadesAsync(scope, counter, ct).ConfigureAwait(false),
            GenresId => await GenresAsync(scope, counter, ct).ConfigureAwait(false),
            AwardsId => await AwardsAsync(scope, counter, ct).ConfigureAwait(false),
            PeopleId => await PeopleAsync(scope, counter, ct).ConfigureAwait(false),
            BasedOnBookId => await BasedOnBookAsync(scope, counter, ct).ConfigureAwait(false),
            SeasonalId => await SeasonalAsync(scope, counter, ct).ConfigureAwait(false),
            PickUpAgainId => await PickUpAgainAsync(scope, counter, ct).ConfigureAwait(false),
            _ => [],
        };
        return ToPlan(starter.Id, starter.Name, starter.Description, starter.CreatesSet, collections);
    }

    private static PackPlan ToPlan(string id, string name, string? description, bool createsSet, IReadOnlyList<PlannedCollection> collections) =>
        new(id, name, description, createsSet, collections,
            collections.Any(planned => CollectionRuleEvaluator.UsesPerProfileFields(planned.Entry.Rules)));

    // ── The packs ────────────────────────────────────────────────────────────────────────────────

    private async Task<List<PlannedCollection>> DecadesAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var result = new List<PlannedCollection>();
        var lastDecade = DateTime.UtcNow.Year / 10 * 10;
        for (var decade = 1900; decade <= lastDecade; decade += 10)
        {
            var label = $"{decade.ToString(CultureInfo.InvariantCulture)}s";
            var entry = new PackEntry(
                label,
                null,
                Definition(scope.Chips(), Rules(false, Rule("decade", "eq", label))),
                "year",
                "asc");
            if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
            {
                result.Add(planned);
            }
        }

        return result;
    }

    private async Task<List<PlannedCollection>> GenresAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var genres = (await browse.GetFieldValuesAsync("genre", null, 300, ct).ConfigureAwait(false))
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(genre => genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var found = new List<PlannedCollection>();
        foreach (var genre in genres)
        {
            ct.ThrowIfCancellationRequested();
            var (total, byMedia) = await counter.CountAsync(Definition(Rules(false, Rule("genre", "eq", genre))), ct).ConfigureAwait(false);
            if (total < MinimumItems)
            {
                continue;
            }

            foreach (var area in Areas)
            {
                var types = scope.Narrow(area.MediaTypes);
                if (types.Count == 0)
                {
                    continue;
                }

                var inArea = types.ToDictionary(type => type, type => CountFor(byMedia, type), StringComparer.Ordinal);
                var count = inArea.Values.Sum();
                if (count < MinimumItems)
                {
                    continue;
                }

                var entry = new PackEntry(
                    $"{Capitalise(genre)} {area.Verb}",
                    null,
                    Definition(MediaChips(types), Rules(false, Rule("genre", "eq", genre))),
                    "title",
                    "asc",
                    PrimaryArea: area.PrimaryArea);
                found.Add(new PlannedCollection(entry, count, inArea.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }

        return found
            .OrderByDescending(planned => planned.ItemCount)
            .Take(MostCollectionsPerList)
            .OrderBy(planned => planned.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<List<PlannedCollection>> AwardsAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var awards = await browse.GetEntityFieldValuesAsync("award_received", null, 500, ct).ConfigureAwait(false);
        var result = new List<PlannedCollection>();
        foreach (var spec in AwardSpecs)
        {
            var matches = awards
                .Where(award => spec.Words.All(word => award.Label.Contains(word, StringComparison.OrdinalIgnoreCase))
                    && !spec.NotWords.Any(word => award.Label.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            var entry = new PackEntry(
                spec.Name,
                null,
                Definition(scope.Chips(), Rules(false, RuleIn("award_received", matches.Select(award => award.Value).ToList(), matches.Select(award => award.Label).ToList()))),
                "year",
                "desc");
            if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
            {
                result.Add(planned);
            }
        }

        return result;
    }

    private async Task<List<PlannedCollection>> PeopleAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var result = new List<PlannedCollection>();
        foreach (var kind in PersonKinds)
        {
            var types = scope.Narrow(kind.MediaTypes);
            if (types.Count == 0)
            {
                continue;
            }

            var people = await browse.GetEntityFieldValuesAsync(kind.Field, null, 500, ct).ConfigureAwait(false);
            var perKind = new List<PlannedCollection>();
            // The value list is ordered by how many files a person has, so the first people are the ones to count.
            foreach (var person in people.Where(person => person.LocalCount >= MinimumItems).Take(MostCollectionsPerList * 2))
            {
                var entry = new PackEntry(
                    string.Format(CultureInfo.CurrentCulture, kind.NameFormat, person.Label),
                    null,
                    Definition(scope.Chips(), Rules(false, Rule(kind.Field, "eq", person.Value, person.Label))),
                    kind.SortField,
                    kind.SortDirection,
                    PrimaryArea: kind.PrimaryArea);
                if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
                {
                    perKind.Add(planned);
                }
            }

            result.AddRange(perKind.OrderByDescending(planned => planned.ItemCount).Take(MostCollectionsPerList));
        }

        return result;
    }

    private async Task<List<PlannedCollection>> BasedOnBookAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        if (!scope.AllowsAny("Movies", "TV"))
        {
            return [];
        }

        var entry = new PackEntry(
            "Based on a book you own",
            "Films and shows adapted from a book that is also in your library.",
            Definition(Rules(false, Rule("source_work_owned", "eq", "true", "Yes"))),
            "title",
            "asc");
        return await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned ? [planned] : [];
    }

    private async Task<List<PlannedCollection>> SeasonalAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var halloween = await SubjectsAsync("Halloween", ["halloween"], ct).ConfigureAwait(false);
        var festive = (await SubjectsAsync("Christmas", ["christmas"], ct).ConfigureAwait(false))
            .Concat(await SubjectsAsync("New Year", ["new year"], ct).ConfigureAwait(false))
            .DistinctBy(subject => subject.Value)
            .ToList();

        var entries = new[]
        {
            new PackEntry(
                "Halloween",
                "Horror and Halloween titles, shown from 1 October to 1 November.",
                Definition(scope.Chips(), SeasonRules([("genre", "horror")], halloween)),
                "title",
                "asc",
                SeasonFrom: "10-01",
                SeasonTo: "11-01"),
            new PackEntry(
                "Festive",
                "Christmas and holiday titles, shown from 1 December to 2 January.",
                Definition(scope.Chips(), SeasonRules([("genre", "christmas"), ("genre", "holiday")], festive)),
                "title",
                "asc",
                SeasonFrom: "12-01",
                SeasonTo: "01-02"),
        };

        var result = new List<PlannedCollection>();
        foreach (var entry in entries)
        {
            if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
            {
                result.Add(planned);
            }
        }

        return result;
    }

    private static CollectionRuleGroup SeasonRules(
        IReadOnlyList<(string Field, string Contains)> genreWords,
        IReadOnlyList<CollectionRuleValueDto> subjects)
    {
        var rules = genreWords.Select(word => Rule(word.Field, "contains", word.Contains)).ToList();
        if (subjects.Count > 0)
        {
            rules.Add(RuleIn("main_subject", subjects.Select(subject => subject.Value).ToList(), subjects.Select(subject => subject.Label).ToList()));
        }

        return Rules(true, rules.ToArray());
    }

    private async Task<List<CollectionRuleValueDto>> SubjectsAsync(string query, string[] words, CancellationToken ct) =>
        (await browse.GetEntityFieldValuesAsync("main_subject", query, 20, ct).ConfigureAwait(false))
            .Where(subject => words.Any(word => subject.Label.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private async Task<List<PlannedCollection>> PickUpAgainAsync(PackScope scope, Counter counter, CancellationToken ct)
    {
        var entries = new List<PackEntry>
        {
            new(
                "Unwatched classics",
                "Highly rated titles you have not started.",
                Definition(
                    scope.Chips(),
                    Rules(false,
                        Rule("watch_state", "eq", "unwatched", "Unwatched, unread or unheard"),
                        Rule("provider_rating", "gte", "7.5"))),
                "provider_rating",
                "desc"),
        };

        if (scope.Allows("Books"))
        {
            entries.Add(new PackEntry(
                "Unread on the shelf",
                "Books you have not opened, added to your library over a year ago.",
                Definition(
                    MediaChips(["Books"]),
                    Rules(false, Rule("watch_state", "eq", "unwatched", "Unwatched, unread or unheard")),
                    // Stored as the opposite of what it reads: "never when added within 365 days".
                    Exceptions(Rule("added_within_days", "neq", "365"))),
                "added_at",
                "asc",
                PrimaryArea: "Read"));
        }

        var short_ = ShortAndSweet(scope);
        if (short_ is not null)
        {
            entries.Add(short_);
        }

        var result = new List<PlannedCollection>();
        foreach (var entry in entries)
        {
            if (await CountedAsync(entry, counter, ct).ConfigureAwait(false) is { } planned)
            {
                result.Add(planned);
            }
        }

        return result;
    }

    private static PackEntry? ShortAndSweet(PackScope scope)
    {
        var films = scope.Allows("Movies")
            ? Rules(false, Rule("media_type", "eq", "Movies"), Rule("runtime", "lt", "100"))
            : null;
        var books = scope.Allows("Books")
            ? Rules(false, Rule("media_type", "eq", "Books"), Rule("page_count", "lt", "250"))
            : null;
        var rules = (films, books) switch
        {
            (not null, not null) => Either(films, books),
            (not null, null) => Definition(films),
            (null, not null) => Definition(books),
            _ => null,
        };
        return rules is null
            ? null
            : new PackEntry("Short and sweet", "Films under 100 minutes and books under 250 pages.", rules, "title", "asc");
    }

    // ── Counting ─────────────────────────────────────────────────────────────────────────────────

    private async Task<PlannedCollection?> CountedAsync(PackEntry entry, Counter counter, CancellationToken ct)
    {
        var (total, byMedia) = await counter.CountAsync(entry.Rules, ct).ConfigureAwait(false);
        return total >= entry.RequiredItems ? new PlannedCollection(entry, total, byMedia) : null;
    }

    private static int CountFor(IReadOnlyDictionary<string, int> byMedia, string mediaType) =>
        byMedia.Where(pair => string.Equals(pair.Key, mediaType, StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value);

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private async Task<Counter> CreateCounterAsync(Guid? viewerProfileId, CancellationToken ct) =>
        new(browse, lookup, catalog, await display.LoadVisibleWorkIdsAsync(ct).ConfigureAwait(false), viewerProfileId);

    /// <summary>The same count the editor's preview shows: rule matches the person may see, collapsed to what a tile stands for.</summary>
    private sealed class Counter(
        ICollectionBrowseReadService browse,
        ICollectionMediaLookupReadService lookup,
        CollectionCatalogReadService catalog,
        IReadOnlySet<Guid> visible,
        Guid? viewerProfileId)
    {
        public async Task<(int Total, IReadOnlyDictionary<string, int> ByMedia)> CountAsync(CollectionRuleDefinition rules, CancellationToken ct)
        {
            if (rules.AllConditions.Count == 0)
            {
                return (0, new Dictionary<string, int>());
            }

            var matched = browse.EvaluateRules(rules, "title", "asc", 0, null, null, null, viewerProfileId)
                .Where(visible.Contains)
                .ToList();
            if (matched.Count == 0)
            {
                return (0, new Dictionary<string, int>());
            }

            var displayIds = await catalog.GetDisplayWorkIdsAsync(matched, ct).ConfigureAwait(false);
            var byMedia = await lookup.CountMediaTypesAsync(displayIds, ct).ConfigureAwait(false);
            return (displayIds.Count, byMedia);
        }
    }
}
