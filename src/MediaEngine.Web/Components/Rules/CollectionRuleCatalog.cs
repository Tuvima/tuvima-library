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
            new("activity", "Your activity", AppMaterialIcons.Outlined.Visibility),
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
        AddField(fields, "media", "release_date", "Release date", "Filter by when it came out: a date range, or the last few days, months or years.",
            AppMaterialIcons.Outlined.Event, RuleOperators.ReleaseDate, new(RuleValueProviderKind.Date));
        AddField(fields, "media", "content_rating", "Age rating", "Filter by the exact rating, such as PG-13 or TV-MA.",
            AppMaterialIcons.Outlined.Shield, [RuleOperators.Is, RuleOperators.IsAny, RuleOperators.IsNot, RuleOperators.Known],
            new(RuleValueProviderKind.CollectionLibrary, "collection.content_rating", SearchPlaceholder: "Search ratings…"));
        AddField(fields, "media", "age_level", "Age rating level", "Filter by the same G, PG, PG-13 and R steps a Kids profile is limited to.",
            AppMaterialIcons.Outlined.Shield,
            [new("lte", "is suitable for up to"), new("gt", "is above"), RuleOperators.Known, RuleOperators.Unknown],
            new(RuleValueProviderKind.StaticOptions, "collection.age_level",
                [new("G", "G · all ages"), new("PG", "PG · kids"), new("PG-13", "PG-13 · teens"), new("R", "R · mature")]));
        AddText(fields, "media", "runtime", "Runtime (minutes)", "Filter by how long a film or episode runs.", AppMaterialIcons.Outlined.Schedule, RuleOperators.Amount);
        AddText(fields, "media", "page_count", "Pages", "Filter by how many pages a book or comic has.", AppMaterialIcons.Outlined.AutoStories, RuleOperators.Amount);
        AddText(fields, "media", "track_count", "Tracks", "Filter by how many tracks an album has.", AppMaterialIcons.Outlined.LibraryBooks, RuleOperators.Amount);
        AddLibrary(fields, "media", "custom_tags", "Tags", "Filter by tags, including your own local tags.", AppMaterialIcons.Outlined.Label);
        AddLibrary(fields, "media", "themes", "Themes", "Filter by the themes Description insights found.", AppMaterialIcons.Outlined.Category);
        AddLibrary(fields, "media", "mood", "Mood", "Filter by the mood Description insights found.", AppMaterialIcons.Outlined.Category);
        AddBoolean(fields, "media", "has_variants", "Has variants");
        AddField(fields, "media", "available_as", "Available as", "Filter by the formats you own: ebook, audiobook or comic.",
            AppMaterialIcons.Outlined.LibraryBooks, [RuleOperators.Is, RuleOperators.IsNot],
            new(RuleValueProviderKind.StaticOptions, "collection.available_as", [new("ebook", "Ebook"), new("audiobook", "Audiobook"), new("comic", "Comic")]));

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
        AddBoolean(fields, "story", "in_universe", "Is in a Universe");
        AddField(fields, "story", "in_collection", "In collection", "Filter by hand-picked collections and playlists the title was added to.",
            AppMaterialIcons.Outlined.BookmarkBorder,
            [RuleOperators.Is, RuleOperators.IsAny, RuleOperators.IsNot, new("known", "is in any collection"), new("unknown", "is in no collection")],
            new(RuleValueProviderKind.CollectionLibrary, "collection.in_collection", SearchPlaceholder: "Search collections…", ResolvesEntities: true));

        AddText(fields, "recognition", "provider_rating", "Provider rating", "Filter by average provider rating.", AppMaterialIcons.Outlined.Star, RuleOperators.Number);
        AddField(fields, "recognition", "your_rating", "Your rating", "Filter by how the profile you are using rated it: Love, Like or Dislike.",
            AppMaterialIcons.Outlined.FavoriteBorder, [RuleOperators.Is, RuleOperators.IsNot],
            new(RuleValueProviderKind.StaticOptions, "collection.your_rating",
                [new("love", "Loved"), new("like", "Liked"), new("dislike", "Disliked"), new("unrated", "Not rated yet")]));
        foreach (var field in new[] { ("award_received", "Award won"), ("award_nominated", "Award nominated"), ("award_family", "Award family"), ("nomination_family", "Nomination family") })
        {
            AddEntity(fields, "recognition", field.Item1, field.Item2, AppMaterialIcons.Outlined.EmojiEvents, "Movies & TV");
        }

        // Your activity: each of these is worked out for the profile looking at the collection.
        AddField(fields, "activity", "watch_state", "Your progress", "Watched, read or listened: unwatched, in progress or finished, for the profile looking.",
            AppMaterialIcons.Outlined.Visibility, [RuleOperators.Is, RuleOperators.IsNot],
            new(RuleValueProviderKind.StaticOptions, "collection.watch_state",
                [new("unwatched", "Unwatched, unread or unheard"), new("in_progress", "In progress"), new("watched", "Watched, read or listened")]));
        AddText(fields, "activity", "play_count", "Play count", "Filter by how many times the profile looking has finished it.", AppMaterialIcons.Outlined.PlayCircle,
            [RuleOperators.Is, RuleOperators.AtLeast, RuleOperators.AtMost, RuleOperators.IsBetween]);
        AddField(fields, "activity", "last_played", "Last played", "Filter by when the profile looking last played, read or listened to it.",
            AppMaterialIcons.Outlined.History,
            [new("within_days", "was within the last (days)"), new("not_within_days", "was not within the last (days)"),
             new("known", "has been played"), new("unknown", "has never been played")],
            new(RuleValueProviderKind.Text));
        AddBoolean(fields, "activity", "on_my_list", "Is on My List");
        AddBoolean(fields, "activity", "favourite", "Is a favourite");

        AddEntity(fields, "production", "filming_location", "Filming location", AppMaterialIcons.Outlined.MovieFilter, "Movies & TV");
        AddText(fields, "library", "added_within_days", "Added within days", "Filter by when an item entered your library.", AppMaterialIcons.Outlined.History, RuleOperators.Number);
        AddField(fields, "library", "added_date", "Added", "Filter by the date an item entered your library.", AppMaterialIcons.Outlined.Event,
            RuleOperators.Date, new(RuleValueProviderKind.Date));
        AddField(fields, "library", "library", "Library", "Filter by which library an item is in.", AppMaterialIcons.Outlined.Inventory2,
            [RuleOperators.Is, RuleOperators.IsAny, RuleOperators.IsNot],
            new(RuleValueProviderKind.CollectionLibrary, "collection.library", SearchPlaceholder: "Search libraries…", ResolvesEntities: true));
        AddText(fields, "library", "file_location", "File location", "Filter by a folder name or path in the file's location.", AppMaterialIcons.Outlined.FolderOpen,
            [RuleOperators.Contains, RuleOperators.NotContains]);
        AddField(fields, "library", "quality_tier", "Quality", "Filter by the picture quality of the file: 4K, HD or SD.", AppMaterialIcons.Outlined.HighQuality,
            [RuleOperators.Is, RuleOperators.IsNot],
            new(RuleValueProviderKind.StaticOptions, "collection.quality_tier", [new("4k", "4K"), new("hd", "HD"), new("sd", "SD")]));
        return fields;
    }

    private static void AddField(List<RuleFieldDefinition> fields, string category, string key, string label, string description, string icon,
        IReadOnlyList<RuleOperatorDefinition> operators, RuleValueProviderDefinition valueProvider) =>
        fields.Add(new(category, key, label, description, icon, operators, valueProvider));

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
