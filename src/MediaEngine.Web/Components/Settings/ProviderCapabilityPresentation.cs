using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Settings;

public sealed record ProviderCapabilityDefinition(
    string Id,
    string Label,
    string Description,
    string Icon,
    string Category,
    IReadOnlySet<string> FieldKeys,
    bool IsIdentity = false);

public static class ProviderCapabilityPresentation
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static readonly IReadOnlyList<ProviderCapabilityDefinition> All =
    [
        new(ProviderCapabilityId.Identity, "Identity", "Retail matching and bridge identifiers.", AppMaterialIcons.Outlined.Fingerprint, "Other", EmptyFields(), true),
        new(ProviderCapabilityId.Metadata, "Metadata", "Titles, descriptions, release facts, and core details.", AppMaterialIcons.Outlined.Description, "Other", Fields(
            "title", "subtitle", "description", "short_description", "year", "genre", "runtime", "duration", "language", "original_language",
            "content_rating", "publisher", "studio", "production_company", "network", "tagline", "album", "disc_number", "disc_count",
            "track_number", "track_count", "issue_number", "issue_title", "issue_description", "issue_source_url", "series_start_year")),
        new(ProviderCapabilityId.Artwork, "Artwork", "Covers, posters, backdrops, logos, and supporting art.", AppMaterialIcons.Outlined.Image, "Artwork", Fields(
            "cover", "poster", "backdrop", "background", "logo", "image", "headshot_url", "studio_logo_url", "network_logo_url")),
        new(ProviderCapabilityId.Lyrics, "Lyrics", "Plain and synchronized lyrics.", AppMaterialIcons.Outlined.Lyrics, "Audio", Fields("lyrics", "synced_lyrics", "lrc")),
        new(ProviderCapabilityId.Subtitles, "Subtitles", "Subtitles, captions, and timed text.", AppMaterialIcons.Outlined.Subtitles, "Video", Fields("subtitles", "captions", "webvtt", "srt")),
        new(ProviderCapabilityId.Ratings, "Ratings", "Ratings, vote counts, and classifications.", AppMaterialIcons.Outlined.StarOutline, "Other", Fields(
            "rating", "vote_count", "vote_average")),
        new(ProviderCapabilityId.People, "People", "Creators, performers, biographies, photos, and credits.", AppMaterialIcons.Outlined.PeopleOutline, "Relationships", Fields(
            "author", "artist", "album_artist", "director", "cast_member", "cast_member_character", "illustrator", "narrator", "composer", "performer")),
        new(ProviderCapabilityId.Relationships, "Relationships", "Series, collections, seasons, volumes, and canonical links.", AppMaterialIcons.Outlined.AccountTree, "Relationships", Fields(
            "series", "series_position", "sequence_total", "sequence_total_scope", "sequence_format", "sequence_manifest_json", "show_name",
            "season_number", "episode_number", "episode_count", "tmdb_collection_id", "tmdb_collection_name", "franchise", "fictional_universe")),
        new(ProviderCapabilityId.Other, "Other", "Additional provider-specific contributions.", AppMaterialIcons.Outlined.MoreHoriz, "Other", EmptyFields()),
    ];

    public static ProviderCapabilityDefinition Get(string id) =>
        All.FirstOrDefault(item => Comparer.Equals(item.Id, id))
        ?? All[^1];

    public static string CapabilityForField(string field)
    {
        foreach (var capability in All.Where(item => item.Id is not ProviderCapabilityId.Identity and not ProviderCapabilityId.Other))
        {
            if (capability.FieldKeys.Contains(field))
            {
                return capability.Id;
            }
        }
        return ProviderCapabilityId.Metadata;
    }

    public static string Label(string id) => Get(id).Label;
    public static string Icon(string id) => Get(id).Icon;

    private static IReadOnlySet<string> Fields(params string[] values) => new HashSet<string>(values, Comparer);
    private static IReadOnlySet<string> EmptyFields() => new HashSet<string>(Comparer);
}
