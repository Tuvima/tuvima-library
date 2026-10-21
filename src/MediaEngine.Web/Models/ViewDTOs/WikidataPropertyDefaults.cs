namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Shared static property data for Wikidata universe properties.
/// Used by Wikidata configuration and property-editing surfaces.
/// to eliminate duplication of hardcoded property lists.
/// </summary>
public static class WikidataPropertyDefaults
{
    /// <summary>A single Wikidata property used by the Universe knowledge model.</summary>
    public sealed record WikidataPropertyInfo(
        string PCode, string ClaimKey, string Category,
        string EntityScope, double Confidence, bool IsBridge, bool Enabled,
        string StageApplicability = "Media Match");

    /// <summary>A bridge identifier entry for QID cross-referencing.</summary>
    public sealed record BridgeEntry(string PCode, string RequestField);

    /// <summary>Returns all default Wikidata properties across all categories.</summary>
    public static List<WikidataPropertyInfo> GetAllProperties() =>
    [
        // Stage 1: Work Identity
        new("P31",   "instance_of",       "Stage 1: Work Identity", "Work",   0.9,  false, true),
        new("P1476", "title",             "Stage 1: Work Identity", "Work",   0.9,  false, true),
        new("P577",  "year",              "Stage 1: Work Identity", "Work",   0.9,  false, true),
        new("P407",  "language",          "Stage 1: Work Identity", "Work",   0.85, false, true),
        new("P495",  "country_of_origin", "Stage 1: Work Identity", "Work",   0.85, false, true),
        new("P123",  "publisher",         "Stage 1: Work Identity", "Work",   0.85, false, true),
        new("P136",  "genre",             "Stage 1: Work Identity", "Work",   0.8,  false, true),
        new("P629",  "edition_or_translation_of", "Stage 1: Work Identity", "Work", 0.9, false, true),
        new("P825",  "adaptation_of",     "Stage 1: Work Identity", "Work",   0.8,  false, true),

        // Stage 1: Series & Franchise
        new("P179",  "series",            "Stage 1: Series & Franchise", "Work",   0.9,  false, true),
        new("P1545", "series_position",   "Stage 1: Series & Franchise", "Work",   0.9,  false, true),
        new("P8345", "franchise",         "Stage 1: Series & Franchise", "Work",   0.9,  false, true),
        new("P155",  "preceded_by",       "Stage 1: Series & Franchise", "Work",   0.8,  false, true),
        new("P156",  "followed_by",       "Stage 1: Series & Franchise", "Work",   0.8,  false, true),

        // Stage 1: Creative Credits
        new("P50",   "author",        "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P110",  "illustrator",   "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P57",   "director",      "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P161",  "cast_member",   "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P987",  "narrator",      "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P725",  "voice_actor",   "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P58",   "screenwriter",  "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P86",   "composer",      "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P175",  "performer",     "Stage 1: Creative Credits", "Work",   0.9,  false, true),
        new("P2093", "author_name_string",  "Stage 1: Creative Credits", "Work", 0.7,  false, true),
        new("P655",  "translator",          "Stage 1: Creative Credits", "Work", 0.85, false, true),
        new("P1431", "executive_producer",  "Stage 1: Creative Credits", "Work", 0.85, false, true),

        // Stage 1: Story & Narrative
        new("P674",  "characters",         "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P840",  "narrative_location",  "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P921",  "main_subject",        "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P1434", "fictional_universe",  "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P144",  "based_on",            "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P4969", "derivative_work",     "Stage 1: Story & Narrative", "Work", 0.8, false, true),
        new("P4584", "first_appearance",    "Stage 1: Story & Narrative", "Work", 0.8, false, true),

        // Stage 1: Bridges — Books
        new("P6395", "apple_books_id", "Stage 1: Bridges — Books", "Work", 1.0, true, true),
        new("P212",  "isbn",           "Stage 1: Bridges — Books", "Work", 1.0, true, true),
        new("P1566", "asin",           "Stage 1: Bridges — Books", "Work", 1.0, true, true),
        new("P2969", "goodreads_id",   "Stage 1: Bridges — Books", "Work", 1.0, true, true),
        new("P244",  "loc_id",         "Stage 1: Bridges — Books", "Work", 1.0, true, true),
        new("P648",  "openlibrary_id", "Stage 1: Bridges — Books", "Work", 1.0, true, true),

        // Stage 1: Bridges — Movies/TV
        new("P4947", "tmdb_movie_id",  "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P4983", "tmdb_tv_id",     "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P345",  "imdb_id",        "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P9385", "justwatch_id",   "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P1712", "metacritic_id",  "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P6127", "letterboxd_id",  "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P2638", "tvcom_id",       "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),
        new("P4835", "tvdb_id",        "Stage 1: Bridges — Movies/TV", "Work", 1.0, true, true),

        // Stage 1: Bridges — Comics/Anime
        new("P3589",  "gcd_series_id", "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),
        new("P11308", "gcd_issue_id",  "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),
        new("P5905",  "comicvine_id",  "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),
        new("P4084",  "mal_anime_id",  "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),
        new("P4087",  "mal_manga_id",  "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),
        new("P11736", "anilist_id",   "Stage 1: Bridges — Comics/Anime", "Work", 1.0, true, true),

        // Stage 1: Bridges — Music/Audio
        new("P434",  "musicbrainz_artist_id",  "Stage 1: Bridges — Music/Audio", "Both", 1.0, true, true),
        new("P436",  "musicbrainz_release_id", "Stage 1: Bridges — Music/Audio", "Work", 1.0, true, true),
        new("P1902", "spotify_id",             "Stage 1: Bridges — Music/Audio", "Work", 1.0, true, true),
        new("P1953", "discogs_id",             "Stage 1: Bridges — Music/Audio", "Work", 1.0, true, true),
        new("P3398", "audible_id",             "Stage 1: Bridges — Music/Audio", "Work", 1.0, true, true),
        new("P982",  "musicbrainz_release_group_id", "Stage 1: Bridges — Music/Audio", "Work", 1.0, true, true),

        // Stage 1: Physical/Technical
        new("P1680", "subtitle",        "Stage 1: Physical/Technical", "Work", 0.85, false, true),
        new("P2047", "duration",        "Stage 1: Physical/Technical", "Work", 0.85, false, true),
        new("P1104", "page_count",      "Stage 1: Physical/Technical", "Work", 0.85, false, true),
        new("P1657", "maturity_rating", "Stage 1: Physical/Technical", "Work", 0.8,  false, true),
        new("P433",  "issue_number",    "Stage 1: Physical/Technical", "Work", 0.85, false, true),
        new("P478",  "volume_number",   "Stage 1: Physical/Technical", "Work", 0.85, false, true),

        // Stage 1: Bridges — Games
        new("P5792", "igdb_id",     "Stage 1: Bridges — Games", "Work", 1.0, true, true),
        new("P9968", "rawg_id",     "Stage 1: Bridges — Games", "Work", 1.0, true, true),
        new("P1735", "steam_appid", "Stage 1: Bridges — Games", "Work", 1.0, true, true),

        // Stage 1: Social Proof
        new("P166", "awards_received", "Stage 1: Social Proof", "Work", 0.85, false, true),

        // Person Enrichment
        new("P18",   "headshot_url",  "Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P106",  "occupation",    "Person Enrichment", "Person", 0.85, false, true, "Person Lookup"),
        new("P800",  "notable_work",  "Person Enrichment", "Person", 0.85, false, true, "Person Lookup"),
        new("P569",  "date_of_birth", "Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P570",  "date_of_death", "Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P19",   "place_of_birth","Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P20",   "place_of_death","Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P27",   "citizenship",   "Person Enrichment", "Person", 0.9,  false, true, "Person Lookup"),
        new("P742",  "pseudonym",     "Person Enrichment", "Person", 0.85, false, true, "Person Lookup"),
        new("P1773", "attributed_to", "Person Enrichment", "Person", 0.85, false, true, "Person Lookup"),
        new("P1813", "short_name",    "Person Enrichment", "Person", 0.8,  false, true, "Person Lookup"),

        // Person: Social Links
        new("P2003", "instagram", "Person: Social Links", "Person", 1.0, true, true, "Person Lookup"),
        new("P2002", "twitter",   "Person: Social Links", "Person", 1.0, true, true, "Person Lookup"),
        new("P7085", "tiktok",    "Person: Social Links", "Person", 1.0, true, true, "Person Lookup"),
        new("P4033", "mastodon",  "Person: Social Links", "Person", 1.0, true, true, "Person Lookup"),
        new("P856",  "website",   "Person: Social Links", "Person", 1.0, true, true, "Person Lookup"),

        // Universe: Character
        new("P1080", "narrative_universe", "Universe: Character", "Character", 0.85, false, true, "Universe Graph"),
        new("P1441", "present_in_work", "Universe: Character", "Character", 0.85, false, true, "Universe Graph"),
        new("P4584", "first_appearance", "Universe: Character", "Character", 0.85, false, true, "Universe Graph"),
        new("P170",  "creator",         "Universe: Character", "Character", 0.85, false, true, "Universe Graph"),
        new("P21",   "gender",          "Universe: Character", "Character", 0.9,  false, true, "Universe Graph"),

        // Universe: Character Relationships
        new("P22",   "father",    "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P25",   "mother",    "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P26",   "spouse",    "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P3373", "sibling",   "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P40",   "child",     "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P1344", "participant_in", "Universe: Character Relationships", "Character", 0.8,  false, true, "Universe Graph"),
        new("P3342", "significant_person", "Universe: Character Relationships", "Character", 0.8,  false, true, "Universe Graph"),
        new("P7047", "enemy",     "Universe: Character Relationships", "Character", 0.8,  false, true, "Universe Graph"),
        new("P1066", "student_of","Universe: Character Relationships", "Character", 0.8,  false, true, "Universe Graph"),
        new("P463",  "member_of", "Universe: Character Relationships", "Character", 0.85, false, true, "Universe Graph"),
        new("P551",  "residence", "Universe: Character Relationships", "Character", 0.8,  false, true, "Universe Graph"),

        // Universe: Location
        new("P131", "located_in",          "Universe: Location", "Location", 0.85, false, true, "Universe Graph"),
        new("P361", "part_of",             "Universe: Location", "Location", 0.85, false, true, "Universe Graph"),
        new("P625", "coordinate_location", "Universe: Location", "Location", 0.8,  false, true, "Universe Graph"),

        // Universe: Organization
        new("P527", "has_parts",           "Universe: Organization", "Organization", 0.85, false, true, "Universe Graph"),
        new("P169", "chief_executive_officer", "Universe: Organization", "Organization", 0.85, false, true, "Universe Graph"),
        new("P749", "parent_organization", "Universe: Organization", "Organization", 0.85, false, true, "Universe Graph"),

        // Universe: Event
        new("P1080", "narrative_universe", "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P1441", "present_in_work",    "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P585",  "point_in_time",      "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P580",  "start_time",         "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P582",  "end_time",           "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P276",  "event_location",     "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P710",  "participant",        "Universe: Event", "Event", 0.85, false, true, "Universe Graph"),
        new("P828",  "cause",              "Universe: Event", "Event", 0.8,  false, true, "Universe Graph"),
        new("P1542", "effect",             "Universe: Event", "Event", 0.8,  false, true, "Universe Graph"),

        // Universe: Object
        new("P1080", "narrative_universe", "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P1441", "present_in_work",    "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P4584", "first_appearance",   "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P170",  "creator",            "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P361",  "part_of",            "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P527",  "has_parts",          "Universe: Object", "Object", 0.85, false, true, "Universe Graph"),
        new("P5800", "narrative_role",     "Universe: Object", "Object", 0.8,  false, true, "Universe Graph"),
    ];

    /// <summary>Returns bridge identifier entries used for QID cross-referencing.</summary>
    public static List<BridgeEntry> GetBridgeEntries() =>
    [
        new("P212",  "isbn"),
        new("P6395", "apple_books_id"),
        new("P3398", "audible_id"),
        new("P4947", "tmdb_movie_id"),
        new("P4983", "tmdb_tv_id"),
        new("P345",  "imdb_id"),
        new("P5905", "comicvine_id"),
        new("P434",  "musicbrainz_artist_id"),
        new("P436",  "musicbrainz_release_id"),
        new("P1566", "asin"),
        new("P648",   "openlibrary_id"),
        new("P4835",  "tvdb_id"),
        new("P11736", "anilist_id"),
        new("P982",   "musicbrainz_release_group_id"),
        new("P5792",  "igdb_id"),
        new("P9968",  "rawg_id"),
        new("P1735",  "steam_appid"),
    ];

    /// <summary>Returns human-readable label for a bridge P-code.</summary>
    public static string GetBridgeLabel(string pCode) => pCode switch
    {
        "P6395" => "Apple Books ID",
        "P3398" => "Audible ID",
        "P4947" => "TMDB Movie ID",
        "P4983" => "TMDB TV ID",
        "P345" => "IMDb ID",
        "P1566" => "ASIN",
        "P212" => "ISBN-13",
        "P5905" => "Comic Vine ID",
        "P434" => "MusicBrainz Artist ID",
        "P436" => "MusicBrainz Release ID",
        "P648" => "Open Library ID",
        "P4835" => "TVDB ID",
        "P11736" => "AniList ID",
        "P982" => "MusicBrainz Release Group ID",
        "P5792" => "IGDB ID",
        "P9968" => "RAWG ID",
        "P1735" => "Steam App ID",
        _ => pCode,
    };

    /// <summary>Returns the target provider name for a bridge P-code.</summary>
    public static string GetTargetProvider(string pCode) => pCode switch
    {
        "P6395" => "Apple Books",
        "P3398" => "Audnexus",
        "P4947" => "TMDB",
        "P4983" => "TMDB",
        "P345" => "IMDb",
        "P1566" => "Amazon",
        "P212" => "Open Library",
        "P5905" => "Comic Vine",
        "P434" => "MusicBrainz",
        "P436" => "MusicBrainz",
        "P648" => "Open Library",
        "P4835" => "TVDB",
        "P11736" => "AniList",
        "P982" => "MusicBrainz",
        "P5792" => "IGDB",
        "P9968" => "RAWG",
        "P1735" => "Steam",
        _ => "Unknown",
    };

    /// <summary>Returns category sort order.</summary>
    public static int CategoryOrder(string category) => category switch
    {
        "Stage 1: Work Identity" => 0,
        "Stage 1: Series & Franchise" => 1,
        "Stage 1: Creative Credits" => 2,
        "Stage 1: Story & Narrative" => 3,
        "Stage 1: Physical/Technical" => 4,
        "Stage 1: Bridges — Books" => 5,
        "Stage 1: Bridges — Movies/TV" => 6,
        "Stage 1: Bridges — Comics/Anime" => 7,
        "Stage 1: Bridges — Music/Audio" => 8,
        "Stage 1: Bridges — Games" => 9,
        "Stage 1: Social Proof" => 10,
        "Person Enrichment" => 11,
        "Person: Social Links" => 12,
        "Universe: Character" => 13,
        "Universe: Character Relationships" => 14,
        "Universe: Location" => 15,
        "Universe: Organization" => 16,
        "Universe: Event" => 17,
        "Universe: Object" => 18,
        _ => 99,
    };

    /// <summary>Returns the category icon for display.</summary>
    public static string GetCategoryIcon(string category) => category switch
    {
        "Stage 1: Work Identity" => "identification",
        "Stage 1: Series & Franchise" => "collections_bookmark",
        "Stage 1: Creative Credits" => "people",
        "Stage 1: Story & Narrative" => "auto_stories",
        "Stage 1: Physical/Technical" => "straighten",
        "Stage 1: Bridges — Books" => "menu_book",
        "Stage 1: Bridges — Movies/TV" => "movie",
        "Stage 1: Bridges — Comics/Anime" => "comic_bubble",
        "Stage 1: Bridges — Music/Audio" => "music_note",
        "Stage 1: Bridges — Games" => "sports_esports",
        "Stage 1: Social Proof" => "emoji_events",
        "Person Enrichment" => "person",
        "Person: Social Links" => "share",
        "Universe: Character" => "face",
        "Universe: Character Relationships" => "group_work",
        "Universe: Location" => "place",
        "Universe: Organization" => "corporate_fare",
        "Universe: Event" => "event",
        "Universe: Object" => "category",
        _ => "category",
    };
}
