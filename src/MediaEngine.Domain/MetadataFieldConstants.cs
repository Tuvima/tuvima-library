using System.Collections.Frozen;

namespace MediaEngine.Domain;

/// <summary>
/// Single source of truth for metadata field key constants used across the Engine.
/// All multi-valued field detection should reference <see cref="MultiValuedKeys"/>
/// instead of maintaining local copies.
/// </summary>
public static class MetadataFieldConstants
{
    /// <summary>
    /// Suffix appended to a multi-valued claim key to form its companion QID key.
    /// Example: "genre" → "genre_qid".
    /// </summary>
    public const string CompanionQidSuffix = "_qid";

    /// <summary>Use <see cref="WellKnownProviders.Wikidata"/> instead.</summary>
    [Obsolete("Use WellKnownProviders.Wikidata instead.")]
    public static readonly Guid WikidataProviderId = WellKnownProviders.Wikidata;

    /// <summary>Use <see cref="WellKnownProviders.AiProvider"/> instead.</summary>
    [Obsolete("Use WellKnownProviders.AiProvider instead.")]
    public static readonly Guid AiProviderId = WellKnownProviders.AiProvider;

    // ── Single-valued claim keys ──────────────────────────────────────────────
    // These are the canonical key names stored in the metadata_claims table.
    // All claim creation and field lookups must use these constants.

    public const string Title = "title";
    public const string Author = "author";
    public const string Year = "year";
    public const string OriginalReleaseYear = "original_release_year";
    public const string OriginalReleaseDate = "original_release_date";
    public const string OriginalPublicationYear = "original_publication_year";
    public const string OriginalPublicationDate = "original_publication_date";
    public const string PublicationYear = "publication_year";
    public const string PublicationDate = "publication_date";
    public const string EditionReleaseYear = "edition_release_year";
    public const string EditionReleaseDate = "edition_release_date";
    public const string Description = "description";
    public const string ShortDescription = "short_description";
    public const string Tagline = "tagline";
    public const string Subtitle = "subtitle";
    public const string Cover = "cover";
    public const string Rating = "rating";
    public const string Series = "series";
    public const string SeriesPosition = "series_position";
    public const string SeriesMembershipSource = "series_membership_source";
    public const string SeriesStartYear = "series_start_year";
    public const string SeriesEndYear = "series_end_year";
    public const string Runtime = "runtime";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string OriginalTitle = "original_title";
    public const string SeasonNumber = "season_number";
    public const string EpisodeNumber = "episode_number";
    public const string TrackNumber = "track_number";
    public const string DiscNumber = "disc_number";
    public const string DiscCount = "disc_count";
    public const string MediaTypeField = "media_type";
    public const string PublisherField = "publisher";
    public const string PageCount = "page_count";
    public const string Volume = "volume";
    public const string Language = "language";
    public const string OriginalLanguage = "original_language";
    public const string DurationField = "duration";
    public const string Franchise = "franchise";
    public const string CustomTags = "custom_tags";
    public const string CoverUrl = "cover_url";
    public const string CoverState = "cover_state";
    public const string CoverSource = "cover_source";
    public const string HeroState = "hero_state";
    public const string ArtworkSettledAt = "artwork_settled_at";
    public const string ArtworkPrimaryHex = "artwork_primary_hex";
    public const string ArtworkSecondaryHex = "artwork_secondary_hex";
    public const string ArtworkAccentHex = "artwork_accent_hex";
    public const string ShowName = "show_name";
    public const string EpisodeTitle = "episode_title";
    public const string EpisodeDescription = "episode_description";
    public const string Network = "network";
    public const string AwardReceived = "award_received";
    public const string AwardNominated = "award_nominated";
    public const string AwardFamily = "award_family";
    public const string NominationFamily = "nomination_family";
    public const string CountryOfOrigin = "country_of_origin";
    public const string ProductionCompany = "production_company";
    public const string RecordLabel = "record_label";
    public const string SetInPeriod = "set_in_period";
    public const string FilmingLocation = "filming_location";
    public const string MainSubject = "main_subject";
    public const string BasedOn = "based_on";
    public const string Biography = "biography";
    public const string QidResolutionMethod = "qid_resolution_method";
    public const string WikidataQidScope = "wikidata_qid_scope";
    public const string IdentityProvider = "identity_provider";
    public const string IdentityProviderItemId = "identity_provider_item_id";
    public const string IdentityRevision = "identity_revision";
    public const string EnrichmentProviders = "enrichment_providers";
    public const string BridgePending = "bridge_pending";
    public const string FictionalUniverse = "fictional_universe";
    public const string Characters = "characters";
    public const string NarrativeLocation = "narrative_location";
    public const string NarrativeEvent = "narrative_event";
    public const string NarrativeObject = "narrative_object";

    // ── Child entity discovery claims ────────────────────────────────────────
    public const string SeasonCount = "season_count";
    public const string EpisodeCount = "episode_count";
    public const string AirDate = "air_date";
    public const string TrackCount = "track_count";
    public const string IssueCount = "issue_count";
    public const string IssueNumber = "issue_number";
    public const string IssueNumberSort = "issue_number_sort";
    public const string IssueTitle = "issue_title";
    public const string IssueDescription = "issue_description";
    public const string IssueSourceUrl = "issue_source_url";
    public const string SequenceTotal = "sequence_total";
    public const string SequenceTotalScope = "sequence_total_scope";
    public const string SequenceFormat = "sequence_format";
    public const string ChildEntitiesJson = "child_entities_json";
    public const string SequenceManifestJson = "sequence_manifest_json";

    // ── Multi-valued claim keys also used in claim creation ──────────────────
    public const string Narrator = "narrator";
    public const string Director = "director";
    public const string Genre = "genre";
    public const string Illustrator = "illustrator";
    /// <summary>Metadata field key for cast/actor entries. Display role name is "Actor".</summary>
    public const string CastMember = "cast_member";
    public const string VoiceActor = "voice_actor";
    public const string GuestStar = "guest_star";
    public const string Composer = "composer";
    public const string Screenwriter = "screenwriter";
    public const string Inker = "inker";
    public const string Colorist = "colorist";
    public const string Letterer = "letterer";
    public const string CoverArtist = "cover_artist";
    public const string Translator = "translator";
    public const string Editor = "editor";

    /// <summary>
    /// Embedded subtitle track languages of a video file, one claim and one array row per
    /// distinct language (ISO 639-1 where the code is recognised).
    /// </summary>
    public const string SubtitleLanguages = "subtitle_languages";

    /// <summary>
    /// Multi-valued field keys that may contain multiple values from Wikidata
    /// or other providers. These keys are decomposed into individual
    /// <c>CanonicalArrayEntry</c> rows in the array repository.
    /// </summary>
    public static IReadOnlySet<string> MultiValuedKeys { get; } = new[]
    {
        // Core multi-valued fields
        "genre",
        "characters",
        "cast_member",
        "voice_actor",
        "narrative_location",
        "narrative_event",
        "narrative_object",
        "main_subject",
        "composer",
        "screenwriter",
        "artist",
        "album_artist",
        "producer",
        "guest_star",
        "author",
        "narrator",
        "director",
        "illustrator",
        "inker",
        "colorist",
        "letterer",
        "cover_artist",
        "translator",
        "editor",
        "nationality",
        "occupation",
        "pseudonym",
        "attributed_to",
        "notable_work",
        "based_on",
        "fictional_universe",
        "first_appearance",
        "enrichment_providers",
        "award_received",
        "award_nominated",
        "award_family",
        "nomination_family",
        "country_of_origin",
        "language",
        "original_language",
        "production_company",
        "publisher",
        "record_label",
        "network",
        "set_in_period",
        "filming_location",

        // Technical track fields
        "subtitle_languages",

        // AI-generated vocabulary fields
        "themes",
        "mood",
        "content_warnings",
        "vibe",

        // Companion QID keys (paired with above)
        "genre_qid",
        "characters_qid",
        "cast_member_qid",
        "voice_actor_qid",
        "narrative_location_qid",
        "narrative_event_qid",
        "narrative_object_qid",
        "main_subject_qid",
        "composer_qid",
        "screenwriter_qid",
        "artist_qid",
        "album_artist_qid",
        "producer_qid",
        "guest_star_qid",
        "author_qid",
        "narrator_qid",
        "director_qid",
        "illustrator_qid",
        "based_on_qid",
        "fictional_universe_qid",
        "award_received_qid",
        "award_nominated_qid",
        "award_family_qid",
        "nomination_family_qid",
        "country_of_origin_qid",
        "language_qid",
        "original_language_qid",
        "production_company_qid",
        "publisher_qid",
        "record_label_qid",
        "network_qid",
        "set_in_period_qid",
        "filming_location_qid",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> if the given claim key is multi-valued.
    /// </summary>
    public static bool IsMultiValued(string key) =>
        MultiValuedKeys.Contains(key);
}
