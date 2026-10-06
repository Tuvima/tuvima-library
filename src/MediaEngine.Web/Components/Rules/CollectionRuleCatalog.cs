using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Rules;

public static class CollectionRuleCatalog
{
    public static RuleBuilderCatalog Instance { get; } = new()
    {
        Domain = "collection",
        Eyebrow = "Smart membership",
        Title = "Build rules",
        Description = "Create rule groups and conditions to define which items belong in this collection.",
        DefaultCategory = "media",
        ShowSortControls = true,
        Capabilities = new(PeopleMetadata: true, PlaceMetadata: true),
        Categories =
        [
            new("media", "Media", AppMaterialIcons.Outlined.Movie),
            new("people", "People & organizations", AppMaterialIcons.Outlined.People),
            new("story", "Story & world", AppMaterialIcons.Outlined.Public),
            new("recognition", "Recognition", AppMaterialIcons.Outlined.StarBorder),
            new("production", "Production", AppMaterialIcons.Outlined.MovieFilter),
            new("library", "My library", AppMaterialIcons.Outlined.Inventory2),
        ],
        SortFields =
        [
            new("title", "Title"), new("creator", "Creator"), new("year", "Release year"),
            new("provider_rating", "Provider rating"), new("added_at", "Date added"), new("media_type", "Media type"),
        ],
        Fields = BuildFields(),
    };

    private static IReadOnlyList<RuleFieldDefinition> BuildFields()
    {
        var fields = new List<RuleFieldDefinition>();
        AddLibrary(fields, "media", "media_type", "Media type", "Filter by type of media.", AppMaterialIcons.Outlined.Movie, RuleOperators.Choice);
        AddLibrary(fields, "media", "genre", "Genre", "Filter by genre or category.", AppMaterialIcons.Outlined.LocalOffer);
        AddText(fields, "media", "year", "Year", "Filter by release year.", AppMaterialIcons.Outlined.CalendarToday, RuleOperators.Number);
        AddText(fields, "media", "decade", "Decade", "Filter by release decade.", AppMaterialIcons.Outlined.CalendarToday, [RuleOperators.Is]);
        AddEntity(fields, "media", "language", "Language", AppMaterialIcons.Outlined.Language);
        AddEntity(fields, "media", "original_language", "Original language", AppMaterialIcons.Outlined.Language);
        AddEntity(fields, "media", "country_of_origin", "Country of origin", AppMaterialIcons.Outlined.Language);

        foreach (var field in new[]
        {
            ("person_qid", "Person", "Cross-media"), ("author", "Author", "Books & Comics"), ("narrator", "Narrator", "Audiobooks"),
            ("director", "Director", "Movies & TV"), ("cast_member", "Actor / cast member", "Movies & TV"), ("voice_actor", "Voice actor", "Cross-media"),
            ("artist", "Artist / performer", "Music"), ("composer", "Composer", "Music"), ("screenwriter", "Screenwriter", "Movies & TV"),
            ("illustrator", "Illustrator", "Books & Comics"), ("production_company", "Production company", "Movies & TV"),
            ("publisher", "Publisher", "Books & Comics"), ("record_label", "Record label", "Music"), ("network", "Network / broadcaster", "Movies & TV"),
        })
        {
            AddEntity(fields, "people", field.Item1, field.Item2, AppMaterialIcons.Outlined.People, field.Item3);
        }

        AddLibrary(fields, "story", "series", "Series", "Filter using series metadata.", AppMaterialIcons.Outlined.Public);
        foreach (var field in new[]
        {
            ("wikidata_franchise", "Franchise / universe"), ("based_on", "Based on"), ("narrative_location", "Narrative location"),
            ("set_in_period", "Set in period"), ("main_subject", "Main subject"), ("characters", "Character"), ("fictional_universe", "Universe"),
        })
        {
            AddEntity(fields, "story", field.Item1, field.Item2, AppMaterialIcons.Outlined.Public);
        }

        AddBoolean(fields, "story", "is_adaptation", "Is an adaptation");
        AddBoolean(fields, "story", "source_work_owned", "Source work is owned");
        AddBoolean(fields, "story", "adaptation_owned", "Adaptation is owned");

        AddText(fields, "recognition", "provider_rating", "Provider rating", "Filter by average provider rating.", AppMaterialIcons.Outlined.Star, RuleOperators.Number);
        foreach (var field in new[] { ("award_received", "Award won"), ("award_nominated", "Award nominated"), ("award_family", "Award family"), ("nomination_family", "Nomination family") })
        {
            AddEntity(fields, "recognition", field.Item1, field.Item2, AppMaterialIcons.Outlined.EmojiEvents, "Movies & TV");
        }

        AddEntity(fields, "production", "filming_location", "Filming location", AppMaterialIcons.Outlined.MovieFilter, "Movies & TV");
        AddText(fields, "library", "added_within_days", "Added within days", "Filter by when an item entered your library.", AppMaterialIcons.Outlined.History, RuleOperators.Number);
        return fields;
    }

    private static void AddText(List<RuleFieldDefinition> fields, string category, string key, string label, string description, string icon, IReadOnlyList<RuleOperatorDefinition>? operators = null) =>
        fields.Add(new(category, key, label, description, icon, operators ?? RuleOperators.Text, new(RuleValueProviderKind.Text)));

    private static void AddBoolean(List<RuleFieldDefinition> fields, string category, string key, string label) =>
        fields.Add(new(category, key, label, $"Filter using {label.ToLowerInvariant()} metadata.", RuleOperators.DefaultIcon, RuleOperators.Boolean, new(RuleValueProviderKind.Boolean)));

    private static void AddLibrary(List<RuleFieldDefinition> fields, string category, string key, string label, string description, string icon, IReadOnlyList<RuleOperatorDefinition>? operators = null, string group = "Cross-media") =>
        fields.Add(new(category, key, label, description, icon, operators ?? RuleOperators.LibraryText,
            new(RuleValueProviderKind.CollectionLibrary, $"collection.{key}", OptionGroup: group, SearchPlaceholder: Placeholder(key, label))));

    private static void AddEntity(List<RuleFieldDefinition> fields, string category, string key, string label, string icon, string group = "Cross-media") =>
        fields.Add(new(category, key, label, $"Filter by {label.ToLowerInvariant()}.", icon, RuleOperators.Lookup,
            new(RuleValueProviderKind.CollectionLibrary, $"collection.{key}", OptionGroup: group, SearchPlaceholder: Placeholder(key, label), ResolvesEntities: true)));

    private static string Placeholder(string key, string label) => key switch
    {
        "award_received" or "award_nominated" or "award_family" or "nomination_family" => "Search awards, for example Academy Award…",
        "genre" => "Search genres…",
        "language" or "original_language" => "Search languages…",
        "media_type" => "Search media types…",
        _ => $"Search {label.ToLowerInvariant()}…",
    };
}
