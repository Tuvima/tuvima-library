using MediaEngine.Contracts.Metadata;
using System.Globalization;

namespace MediaEngine.Web.Services.Editing;

public sealed record MediaEditorDetailsFieldPresentation(
    string Key,
    string OverrideKey,
    string Label,
    string Value,
    string Icon,
    string InputKind,
    string Provenance,
    string? SourceLabel,
    string? ProviderName,
    bool CanOverride,
    bool CanRevert,
    bool IsChips,
    int Tier,
    string RawValue,
    string DisplayValue,
    bool IsUserLocked,
    string? SourceScopeId = null,
    IReadOnlyList<MediaEditorDetailsLinkedValue>? LinkedValues = null);

public sealed record MediaEditorDetailsLinkedValue(string Label, string? Href);
public sealed record MediaEditorDetailsSummaryFact(string Label, string Value);
public sealed record MediaEditorDetailsFilePresentation(string EditionLabel, string FileName, string TechnicalLabel);

public sealed record MediaEditorDetailsPresentation
{
    public IReadOnlyList<MediaEditorDetailsFieldPresentation> Facts { get; init; } = [];
    public MediaEditorDetailsFieldPresentation? Heading { get; init; }
    public IReadOnlyList<MediaEditorDetailsFieldPresentation> Fields { get; init; } = [];
    public IReadOnlyList<MediaEditorDetailsFieldPresentation> AdditionalFields { get; init; } = [];
    public IReadOnlyList<MediaEditorDetailsSummaryFact> LibrarySummary { get; init; } = [];
    public IReadOnlyList<MediaEditorDetailsFilePresentation> Files { get; init; } = [];
    public int? OwnedArtworkCount { get; init; }
    public bool CanAddTags { get; init; }
    public MediaEditorDetailsFieldPresentation? Synopsis { get; init; }
    public string ImageShape { get; init; } = "portrait";
    public string ImageWidthClass { get; init; } = "sme-details-image--portrait";
}

/// <summary>Builds the read-first Details body from the exact active scope snapshot.</summary>
public static class MediaEditorDetailsPresenter
{
    private sealed record FieldSpec(string Key, string Label, string Icon, string InputKind = "text", int Tier = 2, bool Chips = false);
    private sealed record FieldValue(
        string Value,
        string Provenance,
        string? SourceLabel,
        string? ProviderName,
        bool Locked,
        bool Revertable,
        string? SourceScopeId = null,
        IReadOnlyList<MediaEditorDetailsLinkedValue>? LinkedValues = null);

    private static IReadOnlyDictionary<string, FieldSpec[]> ScopeFields => new Dictionary<string, FieldSpec[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Movies|movie"] = [Title, OriginalTitle, Spec("release_date", "Release date", "CalendarToday", "date", 1), Year, Runtime, Rating, ContentRating, Language,
            Description, Tagline, Genres, Tags, Spec("director", "Director", "Movie", chips: true), Spec("screenwriter", "Writers", "EditNote", chips: true), Spec("cast_member", "Cast", "People", chips: true),
            Spec("composer", "Composer", "MusicNote", chips: true), Country, ProductionCompany, Spec("series", "Series", "CollectionsBookmark", tier: 3), Spec("franchise", "Franchise", "Hub", tier: 3)],
        ["Movies|item"] = [Title, OriginalTitle, Spec("release_date", "Release date", "CalendarToday", "date", 1), Year, Runtime, Rating, ContentRating, Language, Description, Tagline, Genres, Tags,
            Spec("director", "Director", "Movie", chips: true), Spec("screenwriter", "Writers", "EditNote", chips: true), Spec("cast_member", "Cast", "People", chips: true), Spec("composer", "Composer", "MusicNote", chips: true), Country, ProductionCompany],
        ["TV|series"] = [Spec("show_name", "Title", "Title", tier: 1), OriginalTitle, Spec("first_air_date", "Premiere", "CalendarToday", "date", 1), Spec("last_air_date", "Finale", "Event", "date", 1), Spec("status", "Status", "LiveTv", "text", 1),
            Runtime, Rating, ContentRating, Language, Description, Tagline, Genres, Tags,
            Spec("network", "Network", "Tv", chips: true), Country, ProductionCompany, Spec("creator", "Creators", "People", chips: true), Spec("cast_member", "Main cast", "People", chips: true)],
        ["TV|season"] = [Spec("season_title", "Season", "ViewAgenda", tier: 1), Spec("season_number", "Season number", "Tag", "number", 1), Spec("first_air_date", "Premiere", "CalendarToday", "date", 1), Spec("last_air_date", "Finale", "Event", "date", 1), Rating, Description, Genres, ContentRating, Language, Spec("network", "Network", "Tv", chips: true), Spec("cast_member", "Cast", "People", chips: true)],
        ["TV|episode"] = [Spec("episode_title", "Title", "Title", tier: 1), OriginalTitle, Spec("season_number", "Season", "ViewAgenda", "number", 1), Spec("episode_number", "Episode", "LiveTv", "number", 1), Spec("absolute_episode_number", "Absolute episode", "Tag", "number", 1), Spec("air_date", "Air date", "CalendarToday", "date", 1), Runtime, Rating, ContentRating, Language,
            Spec("episode_description", "Synopsis", "Description", "textarea", 2), Genres, Tags, Spec("network", "Network", "Tv", chips: true), Spec("director", "Director", "Movie", chips: true), Spec("screenwriter", "Writers", "EditNote", chips: true), Spec("cast_member", "Cast", "People", chips: true), Spec("guest_star", "Guest cast", "People", chips: true)],
        ["Music|album"] = [Spec("album", "Title", "Album", tier: 1), Spec("album_artist", "Album artist", "Person", chips: true, tier: 1), Spec("release_date", "Release date", "CalendarToday", "date", 1), Year, Spec("track_count", "Tracks", "QueueMusic", "number", 1), Spec("disc_count", "Discs", "Album", "number", 1), Duration, Rating, Language, Genres, Tags, Spec("record_label", "Label", "Label", tier: 2), Description, Spec("artist", "Credits", "People", chips: true)],
        ["Music|track"] = [Title, Spec("artist", "Artist", "Person", chips: true, tier: 1), Spec("track_number", "Track", "Tag", "number", 1), Spec("disc_number", "Disc", "Album", "number", 1), Duration, Rating, Language, Spec("album", "Album", "Album", tier: 1), Spec("album_artist", "Album artist", "Person", chips: true), Genres, Spec("record_label", "Label", "Label"), Spec("release_date", "Release date", "CalendarToday", "date"), Spec("composer", "Composer", "MusicNote", chips: true)],
        ["Books|book"] = [Title, OriginalTitle, Spec("original_publication_date", "Original publication", "CalendarToday", "date", 1), Spec("original_publication_year", "Publication year", "CalendarToday", "number", 1), Rating, Language, Description, Genres, Tags, Spec("series", "Series", "CollectionsBookmark", tier: 1), Spec("series_position", "Position", "Tag", "text", 1), Spec("author", "Author", "Person", chips: true), Publisher, Spec("publication_date", "Edition publication date", "CalendarToday", "date", 3), Spec("isbn", "ISBN", "Numbers", tier: 3), Spec("asin", "ASIN", "Numbers", tier: 3), Spec("page_count", "Edition pages", "MenuBook", "number", 3), Spec("edition_format", "Edition format", "Book", tier: 3)],
        ["Books|series"] = [Spec("series", "Title", "Title", tier: 1), Spec("series_start_year", "Start year", "CalendarToday", "number", 1), Spec("series_end_year", "End year", "Event", "number", 1), Spec("work_count", "Owned books", "MenuBook", "number", 1), Language, Description, Genres, Tags, Spec("author", "Authors", "Person", chips: true)],
        ["Audiobooks|audiobook"] = [Title, Spec("author", "Author", "Person", chips: true, tier: 1), Spec("narrator", "Narrator", "RecordVoiceOver", chips: true, tier: 1), Spec("release_date", "Release date", "CalendarToday", "date", 1), Year, Duration, Rating, Language, Description, Genres, Tags, Spec("series", "Series", "CollectionsBookmark", tier: 1), Spec("series_position", "Position", "Tag", "text", 1), Publisher, Spec("isbn", "ISBN", "Numbers", tier: 3), Spec("asin", "ASIN", "Numbers", tier: 3)],
        ["Audiobooks|series"] = [Spec("series", "Title", "Title", tier: 1), Spec("series_start_year", "Start year", "CalendarToday", "number", 1), Spec("series_end_year", "End year", "Event", "number", 1), Spec("work_count", "Owned audiobooks", "Headphones", "number", 1), Description, Genres, Spec("author", "Authors", "Person", chips: true)],
        ["Comics|issue"] = [Spec("issue_title", "Title", "Title", tier: 1), Spec("issue_number", "Issue", "Tag", "text", 1), Spec("publication_date", "Publication date", "CalendarToday", "date", 1), Spec("year", "Year", "CalendarToday", "number", 1), Spec("page_count", "Pages", "MenuBook", "number", 1), Rating, Language,
            Spec("issue_description", "Synopsis", "Description", "textarea"), Spec("series", "Series / run", "CollectionsBookmark", tier: 1), Publisher, Genres, Tags, Spec("author", "Writer", "Person", chips: true), Spec("illustrator", "Illustrator", "Brush", chips: true)],
        ["Comics|series"] = [Spec("series", "Title", "Title", tier: 1), Publisher, Spec("series_start_year", "Start year", "CalendarToday", "number", 1), Spec("series_end_year", "End year", "Event", "number", 1), Spec("issue_count", "Owned issues", "MenuBook", "number", 1), Language, Description, Genres, Tags, Spec("author", "Writers", "Person", chips: true), Spec("illustrator", "Artists", "Brush", chips: true), Spec("franchise", "Universe", "Hub", tier: 3)],
    };

    private static readonly HashSet<string> SafeOverrideFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "show_name", "episode_title", "season_title", "issue_title", "album", "series", "original_title", "release_date", "year", "original_release_date", "original_release_year", "original_publication_date", "original_publication_year",
        "publication_date", "publication_year", "air_date", "last_air_date", "first_air_date", "runtime", "duration", "rating", "content_rating", "language",
        "description", "episode_description", "issue_description", "tagline", "genre", "custom_tags", "series_position", "issue_number", "track_number", "disc_number", "season_number", "episode_number", "page_count"
    };

    private static readonly HashSet<string> MultiValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "genre", "custom_tags", "director", "screenwriter", "cast_member", "guest_star", "creator", "artist", "album_artist", "author", "narrator", "composer", "illustrator", "performer"
    };
    private static readonly HashSet<string> SemicolonSeparatedArrayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "director", "screenwriter", "cast_member", "guest_star", "creator", "artist", "album_artist", "author", "narrator", "composer", "illustrator", "performer"
    };
    private static readonly HashSet<string> SearchableValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "network", "publisher", "country_of_origin", "series", "franchise"
    };

    public static MediaEditorDetailsPresentation Build(
        MediaEditorScopeDto activeScope,
        IReadOnlyDictionary<string, MediaEditorScopeDto> scopesById,
        string? mediaType,
        IReadOnlyDictionary<string, string>? draft = null,
        IReadOnlyDictionary<string, string>? profileOverrides = null,
        Guid? preferredFieldOwnerEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(activeScope);
        ArgumentNullException.ThrowIfNull(scopesById);

        var type = NormalizeType(mediaType);
        var scopeId = NormalizeScope(activeScope.ScopeId, type);
        if (!ScopeFields.TryGetValue($"{type}|{scopeId}", out var specs))
        {
            specs = ResolveFallback(type, scopeId);
        }

        var snapshot = activeScope.FieldSnapshot;
        var parent = !string.IsNullOrWhiteSpace(snapshot.ParentFieldScopeId)
                     && scopesById.TryGetValue(snapshot.ParentFieldScopeId, out var parentScope)
            ? parentScope
            : null;
        var allowed = snapshot.AllowedOverrideKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inheritable = ResolveInheritableKeys(type, scopeId);
        var present = new List<MediaEditorDetailsFieldPresentation>();
        MediaEditorDetailsFieldPresentation? synopsis = null;

        foreach (var spec in specs)
        {
            var fieldKey = spec.Key;
            var overrideKey = ResolveOverrideKey(fieldKey);
            var resolved = Resolve(fieldKey, overrideKey, snapshot, parent, scopesById, type, inheritable, draft, profileOverrides, preferredFieldOwnerEntityId);
            if (resolved is null)
            {
                foreach (var alias in ResolveAliases(spec.Key, type, scopeId))
                {
                    fieldKey = alias;
                    overrideKey = ResolveOverrideKey(fieldKey);
                    resolved = Resolve(fieldKey, overrideKey, snapshot, parent, scopesById, type, inheritable, draft, profileOverrides, preferredFieldOwnerEntityId);
                    if (resolved is not null)
                    {
                        break;
                    }
                }
            }
            if (resolved is null || string.IsNullOrWhiteSpace(resolved.Value))
            {
                continue;
            }

            var isReadOnlyEditionFact = type == "Books" && scopeId == "book"
                                        && string.Equals(resolved.SourceLabel, "Edition", StringComparison.OrdinalIgnoreCase)
                                        && (fieldKey is "publisher" or "isbn" or "asin" or "page_count" or "publication_date" or "edition_format");
            var editable = resolved.Provenance != "Inherited" && !isReadOnlyEditionFact && SafeOverrideFields.Contains(overrideKey)
                           && allowed.Contains(overrideKey)
                           && activeScope.CanEditFields;
            var displayValue = FormatDisplayValue(fieldKey, resolved.Value);
            var field = new MediaEditorDetailsFieldPresentation(
                fieldKey, overrideKey, spec.Label, displayValue, spec.Icon, spec.InputKind, resolved.Provenance,
                resolved.SourceLabel, resolved.ProviderName, editable, editable && resolved.Revertable,
                spec.Chips || MultiValueKeys.Contains(fieldKey) || resolved.LinkedValues is { Count: > 1 },
                spec.Tier, resolved.Value, displayValue, resolved.Locked, resolved.SourceScopeId, resolved.LinkedValues);

            if (spec.Key is "description" or "episode_description" or "issue_description")
            {
                synopsis ??= field;
                continue;
            }

            present.Add(field);
        }

        if (type == "Music" && scopeId == "album")
        {
            var albumArtist = present.FirstOrDefault(field => string.Equals(field.Label, "Album artist", StringComparison.OrdinalIgnoreCase));
            if (albumArtist is not null)
            {
                present.RemoveAll(field => string.Equals(field.Label, "Credits", StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(field.RawValue, albumArtist.RawValue, StringComparison.Ordinal));
            }
        }

        var heading = present.FirstOrDefault(field => string.Equals(field.Label, "Title", StringComparison.OrdinalIgnoreCase));
        var factKeys = ResolveCompactFactKeys(type, scopeId);
        var facts = present.Where(field => factKeys.Contains(field.Key)).ToList();
        var fields = present.Where(field => field.Tier <= 2 && !string.Equals(field.Label, "Title", StringComparison.OrdinalIgnoreCase)).ToList();
        var additional = present.Where(field => field.Tier >= 3).ToList();
        var shape = ResolveImageShape(type, scopeId);
        return new()
        {
            Facts = facts,
            Heading = heading,
            Fields = fields,
            AdditionalFields = additional,
            Synopsis = synopsis,
            CanAddTags = activeScope.CanEditFields
                         && allowed.Contains("custom_tags", StringComparer.OrdinalIgnoreCase)
                         && !present.Any(field => string.Equals(field.Key, "custom_tags", StringComparison.OrdinalIgnoreCase)),
            ImageShape = shape,
            ImageWidthClass = shape switch
            {
                "landscape" => "sme-details-image--landscape",
                "square" => "sme-details-image--square",
                _ => "sme-details-image--portrait",
            },
        };
    }

    private static HashSet<string> ResolveCompactFactKeys(string mediaType, string scopeId)
    {
        IEnumerable<string> keys = (mediaType, scopeId) switch
        {
            ("Movies", _) => ["release_date", "year", "runtime", "rating", "content_rating"],
            ("TV", "series") => ["first_air_date", "last_air_date", "status", "rating", "content_rating"],
            ("TV", "season") => ["season_number", "first_air_date", "last_air_date", "rating"],
            ("TV", "episode") => ["air_date", "runtime", "rating", "content_rating"],
            ("Music", "album") => ["release_date", "year", "track_count", "disc_count", "duration", "rating"],
            ("Music", "track") => ["track_number", "disc_number", "duration", "rating"],
            ("Books", "book") => ["original_publication_date", "original_publication_year", "rating", "language", "series_position"],
            ("Audiobooks", "audiobook") => ["release_date", "year", "duration", "rating", "language"],
            ("Comics", "issue") => ["issue_number", "publication_date", "year", "page_count", "rating"],
            _ => [],
        };
        return keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static FieldValue? Resolve(
        string key,
        string overrideKey,
        MediaEditorScopeFieldSnapshotDto current,
        MediaEditorScopeDto? parent,
        IReadOnlyDictionary<string, MediaEditorScopeDto> scopesById,
        string mediaType,
        HashSet<string> inheritable,
        IReadOnlyDictionary<string, string>? draft,
        IReadOnlyDictionary<string, string>? profile,
        Guid? preferredFieldOwnerEntityId)
    {
        if (TryGetEither(draft, overrideKey, key, out var value))
        {
            return new(value, "Override", "Unsaved change", null, false, false, LinkedValues: BuildDisplayValueLinks(key, value, mediaType));
        }
        if (!string.Equals(overrideKey, "custom_tags", StringComparison.OrdinalIgnoreCase)
            && TryGetEither(profile, overrideKey, key, out value))
        {
            return new(value, "Override", "This profile", null, false, true, LinkedValues: BuildDisplayValueLinks(key, value, mediaType));
        }
        if (string.Equals(overrideKey, "custom_tags", StringComparison.OrdinalIgnoreCase))
        {
            var tagOverride = current.DisplayOverrides.FirstOrDefault(pair => KeyEquals(pair.Key, overrideKey));
            if (!string.IsNullOrWhiteSpace(tagOverride.Key))
            {
                return new(tagOverride.Value.Trim(), "Override", "Library override", null, false, true,
                        LinkedValues: BuildDisplayValueLinks(key, tagOverride.Value, mediaType));
            }
        }
        if (TryGetEither(current.DisplayOverrides, overrideKey, key, out value))
        {
            return new(value, "Override", "Library override", null, false, true, LinkedValues: BuildDisplayValueLinks(key, value, mediaType));
        }

        var ownArray = SelectCanonicalArray(current.CanonicalArrays, key, preferredFieldOwnerEntityId);
        if (ownArray is not null)
        {
            return new(FormatCanonicalArray(key, ownArray), "Canonical", ResolveOwnerLabel(ownArray.OwnerEntityKind), null, false, false,
                    LinkedValues: BuildCanonicalArrayLinks(key, ownArray, mediaType));
        }

        var own = SelectCanonicalField(current.CanonicalFields, key, preferredFieldOwnerEntityId);
        if (own is not null)
        {
            return new(own.Value.Trim(), "Canonical", ResolveOwnerLabel(own), own.ProviderName, own.IsUserLocked, false,
                    LinkedValues: BuildCanonicalScalarLinks(key, own.Value, mediaType));
        }

        var currentParent = parent;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (currentParent is not null && inheritable.Contains(key) && visited.Add(currentParent.ScopeId))
        {
            if (TryGet(currentParent.FieldSnapshot.DisplayOverrides, key, out value))
            {
                return new(value, "Inherited", currentParent.Label, null, false, false, currentParent.ScopeId,
                        BuildDisplayValueLinks(key, value, mediaType));
            }
            var inheritedArray = SelectCanonicalArray(currentParent.FieldSnapshot.CanonicalArrays, key, preferredFieldOwnerEntityId: null);
            if (inheritedArray is not null)
            {
                return new(FormatCanonicalArray(key, inheritedArray), "Inherited", currentParent.Label, null, false, false, currentParent.ScopeId,
                        BuildCanonicalArrayLinks(key, inheritedArray, mediaType));
            }
            var inherited = SelectCanonicalField(currentParent.FieldSnapshot.CanonicalFields, key, preferredFieldOwnerEntityId: null);
            if (inherited is not null)
            {
                return new(inherited.Value.Trim(), "Inherited", currentParent.Label, inherited.ProviderName, inherited.IsUserLocked, false, currentParent.ScopeId,
                        BuildCanonicalScalarLinks(key, inherited.Value, mediaType));
            }
            currentParent = !string.IsNullOrWhiteSpace(currentParent.FieldSnapshot.ParentFieldScopeId)
                            && scopesById.TryGetValue(currentParent.FieldSnapshot.ParentFieldScopeId, out var nextParent)
                ? nextParent
                : null;
        }

        return null;
    }

    private static MediaEditorScopedFieldArrayDto? SelectCanonicalArray(
        IEnumerable<MediaEditorScopedFieldArrayDto> arrays,
        string key,
        Guid? preferredFieldOwnerEntityId)
    {
        var matching = arrays.Where(array => KeyEquals(array.Key, key)
                                             && array.Entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Value))).ToList();
        var workValue = matching.FirstOrDefault(array => string.Equals(array.OwnerEntityKind, "Work", StringComparison.OrdinalIgnoreCase));
        if (workValue is not null)
        {
            return workValue;
        }
        if (preferredFieldOwnerEntityId is { } preferred)
        {
            var exact = matching.FirstOrDefault(array => array.OwnerEntityId == preferred);
            if (exact is not null)
            {
                return exact;
            }
        }

        var editions = matching.Where(array => string.Equals(array.OwnerEntityKind, "Edition", StringComparison.OrdinalIgnoreCase))
            .GroupBy(array => array.OwnerEntityId).ToList();
        if (editions.Count == 1)
        {
            return editions[0].First();
        }

        var assets = matching.Where(array => string.Equals(array.OwnerEntityKind, "MediaAsset", StringComparison.OrdinalIgnoreCase))
            .GroupBy(array => array.OwnerEntityId).ToList();
        return assets.Count == 1 ? assets[0].First() : null;
    }

    private static string FormatCanonicalArray(string key, MediaEditorScopedFieldArrayDto array)
    {
        var values = array.Entries.OrderBy(entry => entry.Ordinal)
            .Select(entry => entry.Value?.Trim())
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var separator = SemicolonSeparatedArrayKeys.Contains(key) ? "; " : ", ";
        return string.Join(separator, values);
    }

    private static IReadOnlyList<MediaEditorDetailsLinkedValue>? BuildCanonicalArrayLinks(
        string key,
        MediaEditorScopedFieldArrayDto array,
        string mediaType)
    {
        return array.Entries.OrderBy(entry => entry.Ordinal)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => new MediaEditorDetailsLinkedValue(
                entry.Value.Trim(),
                CanLinkCanonicalValue(key)
                    ? BuildCanonicalValueHref(key, entry.Value.Trim(), mediaType, entry.LocalPersonId)
                    : null))
            .ToList();
    }

    private static bool CanLinkCanonicalValue(string key) =>
        SemicolonSeparatedArrayKeys.Contains(key)
        || string.Equals(key, "genre", StringComparison.OrdinalIgnoreCase)
        || SearchableValueKeys.Contains(key);

    private static IReadOnlyList<MediaEditorDetailsLinkedValue>? BuildCanonicalScalarLinks(string key, string value, string mediaType)
    {
        return BuildDisplayValueLinks(key, value, mediaType);
    }

    private static IReadOnlyList<MediaEditorDetailsLinkedValue>? BuildDisplayValueLinks(string key, string value, string mediaType)
    {
        // Contributor values remain plain text unless a canonical array entry
        // carries a server-verified LocalPersonId.
        if (SemicolonSeparatedArrayKeys.Contains(key))
        {
            return null;
        }

        if (string.Equals(key, "genre", StringComparison.OrdinalIgnoreCase))
        {
            return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(label => new MediaEditorDetailsLinkedValue(label, BuildCanonicalValueHref(key, label, mediaType, personId: null)))
                .ToList();
        }

        if (SearchableValueKeys.Contains(key) && !string.IsNullOrWhiteSpace(value))
        {
            var label = value.Trim();
            return [new(label, BuildCanonicalValueHref(key, label, mediaType, personId: null))];
        }

        return null;
    }

    private static string? BuildCanonicalValueHref(string key, string label, string mediaType, Guid? personId)
    {
        if (SemicolonSeparatedArrayKeys.Contains(key))
        {
            return personId is { } id
                ? $"/details/person/{id:D}"
                : null;
        }

        if (string.Equals(key, "genre", StringComparison.OrdinalIgnoreCase))
        {
            var route = mediaType switch
            {
                "Movies" => "/watch/movies",
                "TV" => "/watch/tv",
                "Music" => "/listen/music",
                "Books" => "/read/books",
                "Audiobooks" => "/listen/audiobooks",
                "Comics" => "/read/comics",
                _ => "/search",
            };
            return route == "/search"
                ? $"/search?q={Uri.EscapeDataString(label)}"
                : $"{route}?genre={Uri.EscapeDataString(label)}";
        }

        return $"/search?q={Uri.EscapeDataString(label)}";
    }

    private static MediaEditorScopedFieldValueDto? SelectCanonicalField(
        IEnumerable<MediaEditorScopedFieldValueDto> fields,
        string key,
        Guid? preferredFieldOwnerEntityId)
    {
        var matching = fields.Where(field => KeyEquals(field.Key, key) && !string.IsNullOrWhiteSpace(field.Value)).ToList();
        var workValue = matching.FirstOrDefault(field => string.Equals(field.OwnerEntityKind, "Work", StringComparison.OrdinalIgnoreCase));
        if (workValue is not null)
        {
            return workValue;
        }
        if (preferredFieldOwnerEntityId is { } preferred)
        {
            var exact = matching.FirstOrDefault(field => field.OwnerEntityId == preferred);
            if (exact is not null)
            {
                return exact;
            }
        }

        var editions = matching.Where(field => string.Equals(field.OwnerEntityKind, "Edition", StringComparison.OrdinalIgnoreCase))
            .GroupBy(field => field.OwnerEntityId).ToList();
        if (editions.Count == 1)
        {
            return editions[0].First();
        }

        var assets = matching.Where(field => string.Equals(field.OwnerEntityKind, "MediaAsset", StringComparison.OrdinalIgnoreCase))
            .GroupBy(field => field.OwnerEntityId).ToList();
        return assets.Count == 1 ? assets[0].First() : null;
    }

    private static bool TryGetEither(IReadOnlyDictionary<string, string>? values, string primaryKey, string fallbackKey, out string value) =>
        TryGet(values, primaryKey, out value) || TryGet(values, fallbackKey, out value);

    private static string ResolveOverrideKey(string sourceKey) => sourceKey.ToLowerInvariant() switch
    {
        "show_name" or "episode_title" or "season_title" or "issue_title" or "album" => "title",
        "episode_description" or "issue_description" => "description",
        _ => sourceKey,
    };

    private static HashSet<string> ResolveInheritableKeys(string mediaType, string scopeId)
    {
        IEnumerable<string> keys = (mediaType, scopeId) switch
        {
            ("TV", "season" or "episode") => ["genre", "language", "content_rating", "network"],
            ("Music", "track") => ["genre", "language", "album", "album_artist", "record_label", "release_date"],
            ("Books", "book") => ["genre", "language", "series"],
            ("Audiobooks", "audiobook") => ["genre", "language", "series"],
            ("Comics", "issue") => ["genre", "language", "series", "publisher"],
            _ => [],
        };
        return keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolveAliases(string key, string mediaType, string scopeId) =>
        (mediaType, scopeId, key) switch
        {
            ("Music", "album", "album_artist") => ["artist"],
            ("TV", "series", "show_name") => ["title"],
            ("TV", "season", "season_title") => ["title"],
            ("TV", "episode", "episode_title") => ["title"],
            ("Comics", "issue", "issue_title") => ["title"],
            _ => [],
        };

    private static string? ResolveOwnerLabel(MediaEditorScopedFieldValueDto field)
    {
        var ownerKind = field.OwnerEntityKind?.Trim();
        return ownerKind?.ToLowerInvariant() switch
        {
            "edition" => "Edition",
            "asset" or "mediaasset" => "File",
            "work" => "Work",
            _ => null,
        };
    }

    private static string? ResolveOwnerLabel(string? ownerKind) => ownerKind?.Trim().ToLowerInvariant() switch
    {
        "edition" => "Edition",
        "asset" or "mediaasset" => "File",
        "work" => "Work",
        _ => null,
    };

    private static string FormatDisplayValue(string key, string value)
    {
        if (key is "air_date" or "release_date" or "publication_date" or "original_publication_date" or "first_air_date" or "last_air_date"
            && TryParseCalendarDate(value, out var date))
        {
            return date.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
        }

        if (key is "runtime" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        if (key is "track_count" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tracks))
        {
            return tracks == 1 ? "1 track" : $"{tracks} tracks";
        }

        if (key is "track_number" or "disc_number" or "season_number"
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
        {
            var label = key switch
            {
                "track_number" => "Track",
                "disc_number" => "Disc",
                _ => "Season",
            };
            return $"{label} {position.ToString(CultureInfo.InvariantCulture)}";
        }

        if (key is "series_position" && !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("Position ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Position {value}";
        }

        if (key is "issue_number" && !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("Issue ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Issue {value}";
        }

        return value;
    }

    private static bool TryParseCalendarDate(string value, out DateOnly date)
    {
        // Metadata dates represent calendar days. For ISO timestamps, use the
        // authored date prefix instead of converting the instant to local time.
        if (value.Length >= 10
            && value[4] == '-'
            && value[7] == '-'
            && (value.Length == 10 || value[10] is 'T' or 't' or ' ')
            && DateOnly.TryParseExact(value.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryGet(IReadOnlyDictionary<string, string>? values, string key, out string value)
    {
        value = string.Empty;
        if (values is null)
        {
            return false;
        }
        var pair = values.FirstOrDefault(entry => KeyEquals(entry.Key, key));
        if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
        {
            return false;
        }
        value = pair.Value.Trim();
        return true;
    }

    private static bool KeyEquals(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => "Movies",
        "tv" or "television" or "show" => "TV",
        "music" or "album" => "Music",
        "book" or "books" => "Books",
        "audiobook" or "audiobooks" => "Audiobooks",
        "comic" or "comics" => "Comics",
        _ => "Books",
    };

    private static string NormalizeScope(string? scope, string type) => (scope ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "item" when type == "Movies" => "item",
        "movie" or "movie_identity" => "movie",
        "tv_show" or "show" => "series",
        "book_identity" or "book" when type == "Books" => "book",
        "audiobook_identity" or "audiobook" when type == "Audiobooks" => "audiobook",
        "comic_issue" or "issue" when type == "Comics" => "issue",
        "music_album" or "album" when type == "Music" => "album",
        "track" when type == "Music" => "track",
        "episode" or "season" or "series" => (scope ?? string.Empty).Trim().ToLowerInvariant(),
        _ => (scope ?? string.Empty).Trim().ToLowerInvariant(),
    };

    private static FieldSpec[] ResolveFallback(string type, string scope)
    {
        var itemScope = type switch
        {
            "Movies" => "item",
            "TV" => "episode",
            "Music" => "track",
            "Books" => "book",
            "Audiobooks" => "audiobook",
            "Comics" => "issue",
            _ => "book",
        };
        return ScopeFields.TryGetValue($"{type}|{scope}", out var exact)
            ? exact
            : ScopeFields.TryGetValue($"{type}|{itemScope}", out var fallback) ? fallback : [Title, Description, Genres, Tags];
    }

    public static string ResolveImageShape(string? mediaType, string? scopeId)
    {
        var type = NormalizeType(mediaType);
        var scope = NormalizeScope(scopeId, type);
        if (type == "TV" && scope == "episode")
        {
            return "landscape";
        }
        if (type == "Music")
        {
            return "square";
        }
        return "portrait";
    }

    private static FieldSpec Spec(string key, string label, string icon, string input = "text", int tier = 2, bool chips = false) => new(key, label, icon, input, tier, chips);
    private static readonly FieldSpec Title = Spec("title", "Title", "Title", tier: 1);
    private static readonly FieldSpec OriginalTitle = Spec("original_title", "Original title", "Title", tier: 3);
    private static readonly FieldSpec Year = Spec("year", "Year", "CalendarToday", "number", 1);
    private static readonly FieldSpec Runtime = Spec("runtime", "Runtime", "Schedule", tier: 1);
    private static readonly FieldSpec Duration = Spec("duration", "Duration", "Schedule", tier: 1);
    private static readonly FieldSpec Rating = Spec("rating", "Rating", "Star", "rating", 1);
    private static readonly FieldSpec ContentRating = Spec("content_rating", "Content rating", "VerifiedUser", tier: 1);
    private static readonly FieldSpec Language = Spec("language", "Language", "Language", tier: 1);
    private static readonly FieldSpec Description = Spec("description", "Synopsis", "Description", "textarea");
    private static readonly FieldSpec Tagline = Spec("tagline", "Tagline", "FormatQuote");
    private static readonly FieldSpec Genres = Spec("genre", "Genres", "Category", chips: true);
    private static readonly FieldSpec Tags = Spec("custom_tags", "Tags", "Label", chips: true);
    private static readonly FieldSpec Country = Spec("country_of_origin", "Country", "Public");
    private static readonly FieldSpec ProductionCompany = Spec("production_company", "Production company", "Business");
    private static readonly FieldSpec Publisher = Spec("publisher", "Publisher", "Business");
}
