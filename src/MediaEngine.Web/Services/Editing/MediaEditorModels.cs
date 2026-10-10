using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

public enum SharedMediaEditorMode
{
    Normal,
    Review,
    Batch,
}

public enum MediaEditorIdentityIntent
{
    None,
    EditLocalDetails,
    FixRetailMatch,
    ConfirmRetailMatch,
    FixWikidataMatch,
    ConfirmWikidataMatch,
    MarkWikidataMissing,
    ReviewClassification,
    ConfirmArtwork,
    ResolveWriteback,
}

public sealed class MediaEditorLaunchRequest
{
    public SharedEntityEditorTargetDto? SharedEntityTarget { get; init; }
    public List<Guid> EntityIds { get; init; } = [];
    public Guid? LaunchEntityId { get; init; }
    public string? LaunchEntityKind { get; init; }
    public string? ContainerMode { get; init; }
    public Guid? SelectedCollectionId { get; init; }
    public Guid? ActiveProfileId { get; init; }
    public IReadOnlyList<MediaGroupingViewModel> InitialMediaGroups { get; init; } = [];
    public SequencePlacementViewModel? InitialSequencePlacement { get; init; }
    public SharedMediaEditorMode Mode { get; init; } = SharedMediaEditorMode.Normal;
    public MediaEditorIdentityIntent IdentityIntent { get; init; } = MediaEditorIdentityIntent.None;
    public string? InitialScope { get; init; }
    public string? InitialTab { get; init; }
    public string? InitialCanonicalTargetGroup { get; init; }
    public Guid? ReviewItemId { get; init; }
    public string? ReviewTrigger { get; init; }
    /// <summary>The review item's stored candidates (for "Found as a TV title", the TMDB TV suggestion).</summary>
    public string? ReviewCandidatesJson { get; init; }
    public string? MediaType { get; init; }
    public string? HeaderTitle { get; init; }
    public string? HeaderSubtitle { get; init; }
    public string? CoverUrl { get; init; }
    public Func<Task>? OnArtworkChanged { get; init; }
    public List<MediaEditorPreviewItem> PreviewItems { get; init; } = [];
}

public sealed class MediaEditorPreviewItem
{
    public Guid EntityId { get; init; }
    public string Title { get; init; } = "";
    public string? CoverUrl { get; init; }
    public string? MediaType { get; init; }
}

public sealed class MediaEditorSchema
{
    public required string MediaType { get; init; }
    public required string DefaultTargetGroup { get; init; }
    public required IReadOnlyList<(string Key, string Label)> QuickSearchTargets { get; init; }
    public required IReadOnlyList<MediaEditorFieldGroup> Groups { get; init; }
}

public sealed class MediaEditorFieldGroup
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string TabId { get; init; }
    public required IReadOnlyList<MediaEditorFieldDefinition> Fields { get; init; }
}

public sealed class MediaEditorFieldDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string InputKind { get; init; } = "text";
    public bool SupportsBatch { get; init; } = true;
    public bool IdentityField { get; init; }
    public string? Placeholder { get; init; }
}

public sealed record ReviewEditorTarget(
    string InitialTab,
    string CanonicalTargetGroup,
    string FocusField,
    string Summary,
    MediaEditorIdentityIntent Intent,
    string PrimaryActionLabel);

public static class ReviewTargetResolver
{
    public static ReviewEditorTarget Resolve(string? mediaType, string? reviewTrigger)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        var normalizedTrigger = (reviewTrigger ?? string.Empty).Trim();
        var intent = ResolveIntent(normalizedTrigger);
        var initialTab = ResolveInitialTab(intent);
        var actionLabel = ResolvePrimaryActionLabel(intent);

        if (normalizedType == "Music")
        {
            return new(initialTab, "track", "title", BuildSummary(intent, "track and album"), intent, actionLabel);
        }

        if (normalizedType == "Audiobooks")
        {
            return normalizedTrigger switch
            {
                "QidNoMatch" or "MissingQid" or "WikidataBridgeFailed" or "MultipleQidMatches"
                    => new(initialTab, "narrator", "narrator", BuildSummary(intent, "narrator"), intent, actionLabel),
                _ => new(initialTab, "audiobook_identity", "title", BuildSummary(intent, "audiobook"), intent, actionLabel),
            };
        }

        if (normalizedType == "TV")
        {
            return new(initialTab, "show_episode", "show_name", BuildSummary(intent, "show and episode"), intent, actionLabel);
        }

        if (normalizedType == "Comics")
        {
            return new(initialTab, "issue", "series", BuildSummary(intent, "series and issue"), intent, actionLabel);
        }

        if (normalizedType == "Movies")
        {
            return new(initialTab, "movie_identity", "title", BuildSummary(intent, "movie"), intent, actionLabel);
        }

        return new(initialTab, "book_identity", "title", BuildSummary(intent, "item"), intent, actionLabel);
    }

    public static string NormalizeMediaType(string? mediaType) =>
        (mediaType ?? string.Empty).Trim() switch
        {
            "Book" => "Books",
            "Comic" => "Comics",
            "" => "Books",
            var value => value,
        };

    private static MediaEditorIdentityIntent ResolveIntent(string trigger) =>
        trigger switch
        {
            "RetailMatchAmbiguous" => MediaEditorIdentityIntent.ConfirmRetailMatch,
            "RetailMatchFailed" or "UserFixMatch"
                => MediaEditorIdentityIntent.FixRetailMatch,
            "WikidataBridgeFailed" or "MultipleQidMatches" or "QidNoMatch" or "MissingQid"
                => MediaEditorIdentityIntent.FixWikidataMatch,
            "AmbiguousMediaType" or "RootWatchFolder" => MediaEditorIdentityIntent.ReviewClassification,
            "ArtworkUnconfirmed" => MediaEditorIdentityIntent.ConfirmArtwork,
            "WritebackFailed" => MediaEditorIdentityIntent.ResolveWriteback,
            "PlaceholderTitle" or "StagedUnidentifiable" or "MetadataConflict" or "LowConfidence"
                => MediaEditorIdentityIntent.FixRetailMatch,
            _ => MediaEditorIdentityIntent.FixRetailMatch,
        };

    private static string ResolveInitialTab(MediaEditorIdentityIntent intent) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch or
            MediaEditorIdentityIntent.ConfirmRetailMatch or
            MediaEditorIdentityIntent.FixWikidataMatch or
            MediaEditorIdentityIntent.ConfirmWikidataMatch or
            MediaEditorIdentityIntent.MarkWikidataMissing => "links",
            MediaEditorIdentityIntent.ConfirmArtwork => "artwork",
            MediaEditorIdentityIntent.ResolveWriteback => "file",
            _ => "details",
        };

    private static string ResolvePrimaryActionLabel(MediaEditorIdentityIntent intent) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch => "Find match",
            MediaEditorIdentityIntent.ConfirmRetailMatch => "Confirm Retail Match",
            MediaEditorIdentityIntent.FixWikidataMatch => "Search Wikidata",
            MediaEditorIdentityIntent.ConfirmWikidataMatch => "Choose Wikidata Match",
            MediaEditorIdentityIntent.MarkWikidataMissing => "Mark Provider-Only",
            MediaEditorIdentityIntent.ReviewClassification => "Review classification",
            MediaEditorIdentityIntent.ConfirmArtwork => "Review Artwork",
            MediaEditorIdentityIntent.ResolveWriteback => "Retry Writeback",
            _ => "Review Metadata",
        };

    private static string BuildSummary(MediaEditorIdentityIntent intent, string subject) =>
        intent switch
        {
            MediaEditorIdentityIntent.FixRetailMatch => $"Find the correct retail match for this {subject}.",
            MediaEditorIdentityIntent.ConfirmRetailMatch => $"Confirm the retail match for this {subject}.",
            MediaEditorIdentityIntent.FixWikidataMatch => $"Fix the Wikidata identity for this {subject}.",
            MediaEditorIdentityIntent.MarkWikidataMissing => $"Keep the retail match and mark this {subject} as provider-only.",
            MediaEditorIdentityIntent.ReviewClassification => "Review the classification and metadata; this editor does not change the media type.",
            MediaEditorIdentityIntent.ConfirmArtwork => "Review artwork and choose the preferred assets.",
            MediaEditorIdentityIntent.ResolveWriteback => "Review the file write-back failure and retry or skip it.",
            _ => $"Review the {subject} metadata.",
        };
}

public static class MediaEditorSchemaCatalog
{
    public static MediaEditorSchema Resolve(string? mediaType)
    {
        var normalized = ReviewTargetResolver.NormalizeMediaType(mediaType);
        return normalized switch
        {
            "Music" => Music,
            "Movies" => Movies,
            "TV" => Tv,
            "Audiobooks" => Audiobooks,
            "Comics" => Comics,
            _ => Books,
        };
    }

    public static IReadOnlyList<MediaEditorFieldDefinition> ResolveBatchFields(IEnumerable<string> mediaTypes)
    {
        var normalized = mediaTypes
            .Select(ReviewTargetResolver.NormalizeMediaType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 1)
        {
            return Resolve(normalized[0]).Groups
                .Where(group => group.TabId is "details" or "options" or "sorting")
                .SelectMany(group => group.Fields)
                .Where(field => field.SupportsBatch)
                .DistinctBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return
        [
            new() { Key = "title", Label = "Title" },
            new() { Key = "year", Label = "Year" },
            new() { Key = "genre", Label = "Genre" },
            new() { Key = "custom_tags", Label = "Tags" },
            new() { Key = "language", Label = "Language" },
            new() { Key = "rating", Label = "Rating" },
        ];
    }

    public static IReadOnlyDictionary<string, string> BuildValueMap(
        LibraryItemDetailViewModel? detail,
        IEnumerable<CanonicalFieldDto> canonicals)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isComicItem = detail?.MediaType.Contains("comic", StringComparison.OrdinalIgnoreCase) == true
                          || string.Equals(detail?.MediaType, "Cbz", StringComparison.OrdinalIgnoreCase);

        static string? FindCanonicalValue(IEnumerable<CanonicalFieldDto> source, string key) =>
            source.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        static string? FindDetailCanonicalValue(LibraryItemDetailViewModel source, string key) =>
            source.CanonicalValues
                .FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        static void Add(IDictionary<string, string> target, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                target[key] = string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeDescriptionParagraphs(value)
                    : value.Trim();
            }
        }

        static string NormalizeDescriptionParagraphs(string value)
        {
            var normalized = value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return string.Join("\n\n",
                normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph)));
        }

        foreach (var field in canonicals)
        {
            // A comic issue inherits the parent run's generic description. Keep
            // that series copy out of an item editor and surface only issue text.
            if (isComicItem && string.Equals(field.Key, "description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(values, field.Key, field.Value);
        }

        if (detail is null)
        {
            return values;
        }

        Add(values, "title", detail.Title);
        Add(values, "author", detail.Author);
        Add(values, "director", detail.Director);
        Add(values, "cast_member", detail.Cast);
        Add(values, "language", detail.Language);
        Add(values, "genre", detail.Genre);
        Add(values, "runtime", detail.Runtime);
        if (isComicItem)
        {
            Add(values, "description",
                FindCanonicalValue(canonicals, "issue_description")
                ?? FindCanonicalValue(canonicals, "issue_overview")
                ?? FindDetailCanonicalValue(detail, "issue_description")
                ?? FindDetailCanonicalValue(detail, "issue_overview"));
        }
        else
        {
            Add(values, "description", detail.Description);
        }
        Add(values, "series", detail.Series);
        Add(values, "series_position", detail.SeriesPosition);
        Add(values, "narrator", detail.Narrator);
        Add(values, "rating", detail.Rating);
        Add(values, "wikidata_qid", detail.WikidataQid);
        Add(values, "file_name", detail.FileName);
        Add(values, "file_path", detail.FilePath);
        Add(values, "content_hash", detail.ContentHash);
        Add(values, "artist", FindCanonicalValue(canonicals, "artist"));
        Add(values, "album", FindCanonicalValue(canonicals, "album"));
        Add(values, "album_artist", FindCanonicalValue(canonicals, "album_artist"));
        Add(values, "composer", FindCanonicalValue(canonicals, "composer"));
        Add(values, "track_number", FindCanonicalValue(canonicals, "track_number"));
        Add(values, "disc_number", FindCanonicalValue(canonicals, "disc_number"));
        Add(values, "duration", FindCanonicalValue(canonicals, "duration"));
        Add(values, "lyrics", FindCanonicalValue(canonicals, "lyrics"));
        Add(values, "show_name", detail.ShowName ?? FindCanonicalValue(canonicals, "show_name"));
        Add(values, "season_number", detail.SeasonNumber ?? FindCanonicalValue(canonicals, "season_number"));
        Add(values, "episode_number", detail.EpisodeNumber ?? FindCanonicalValue(canonicals, "episode_number"));
        Add(values, "episode_title", detail.EpisodeTitle ?? FindCanonicalValue(canonicals, "episode_title") ?? detail.Title);
        Add(values, "network", FindCanonicalValue(canonicals, "network"));
        Add(values, "subtitle", FindCanonicalValue(canonicals, "subtitle"));
        Add(values, "publisher", FindCanonicalValue(canonicals, "publisher"));
        Add(values, "studio", FindCanonicalValue(canonicals, "studio"));
        Add(values, "volume", FindCanonicalValue(canonicals, "volume"));
        Add(values, "illustrator", FindCanonicalValue(canonicals, "illustrator"));
        Add(values, "edition", FindCanonicalValue(canonicals, "edition"));
        Add(values, "year", detail.Year ?? FindCanonicalValue(canonicals, "year"));
        Add(values, "original_title", FindCanonicalValue(canonicals, "original_title"));
        Add(values, "sort_title", FindCanonicalValue(canonicals, "sort_title"));
        Add(values, "sort_artist", FindCanonicalValue(canonicals, "sort_artist"));
        Add(values, "sort_album", FindCanonicalValue(canonicals, "sort_album"));
        Add(values, "sort_series", FindCanonicalValue(canonicals, "sort_series"));
        Add(values, "tagline", detail.Tagline ?? FindCanonicalValue(canonicals, "tagline"));
        Add(values, "custom_tags", FindCanonicalValue(canonicals, "custom_tags"));
        return values;
    }

    public static IReadOnlyList<string> GetStrongFieldKeys(string targetGroup) =>
        targetGroup switch
        {
            "album" => ["artist", "album"],
            "artist" => ["artist"],
            "track" => ["artist", "album", "title", "track_number"],
            "movie_identity" => ["title", "year", "director"],
            "show_episode" => ["show_name", "season_number", "episode_number", "episode_title"],
            "show" => ["show_name"],
            "book_identity" => ["title", "author"],
            "audiobook_identity" => ["title", "author", "narrator"],
            "narrator" => ["narrator"],
            "series" => ["series"],
            "issue" => ["title", "series", "series_position"],
            _ => ["title"],
        };

    private static readonly MediaEditorSchema Music = new()
    {
        MediaType = "Music",
        DefaultTargetGroup = "album",
        QuickSearchTargets = [("album", "Album"), ("artist", "Artist"), ("track", "Track")],
        Groups =
        [
            Group("music_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("music_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("music_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort title")),
        ],
    };

    private static readonly MediaEditorSchema Movies = new()
    {
        MediaType = "Movies",
        DefaultTargetGroup = "movie_identity",
        QuickSearchTargets = [("movie_identity", "Movie")],
        Groups =
        [
            Group("movie_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("movie_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("movie_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort Title")),
        ],
    };

    private static readonly MediaEditorSchema Tv = new()
    {
        MediaType = "TV",
        DefaultTargetGroup = "show_episode",
        QuickSearchTargets = [("show_episode", "Show / Episode"), ("show", "Show")],
        Groups =
        [
            Group("tv_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("tv_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("tv_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort title")),
        ],
    };

    private static readonly MediaEditorSchema Books = new()
    {
        MediaType = "Books",
        DefaultTargetGroup = "book_identity",
        QuickSearchTargets = [("book_identity", "Book")],
        Groups =
        [
            Group("book_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("subtitle", "Subtitle"),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("book_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("book_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort title")),
        ],
    };

    private static readonly MediaEditorSchema Audiobooks = new()
    {
        MediaType = "Audiobooks",
        DefaultTargetGroup = "audiobook_identity",
        QuickSearchTargets = [("audiobook_identity", "Audiobook")],
        Groups =
        [
            Group("audiobook_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("subtitle", "Subtitle"),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("audiobook_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("audiobook_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort title")),
        ],
    };

    private static readonly MediaEditorSchema Comics = new()
    {
        MediaType = "Comics",
        DefaultTargetGroup = "issue",
        QuickSearchTargets = [("issue", "Issue")],
        Groups =
        [
            Group("comic_details", "Details", "details",
                Field("title", "Display title", identity: true),
                Field("subtitle", "Subtitle"),
                Field("tagline", "Tagline"),
                Field("genre", "Genres"),
                Field("description", "Description", "textarea", identity: true)),
            Group("comic_options", "Options", "options",
                Field("custom_tags", "Library tags")),
            Group("comic_sorting", "Sorting", "sorting",
                Field("sort_title", "Sort title")),
        ],
    };

    private static MediaEditorFieldDefinition Field(
        string key,
        string label,
        string kind = "text",
        bool batch = true,
        bool identity = false) =>
        new()
        {
            Key = key,
            Label = label,
            InputKind = kind,
            SupportsBatch = batch,
            IdentityField = identity,
        };

    private static MediaEditorFieldGroup Group(
        string id,
        string label,
        string tabId,
        params MediaEditorFieldDefinition[] fields) =>
        new()
        {
            Id = id,
            Label = label,
            TabId = tabId,
            Fields = fields,
        };
}
