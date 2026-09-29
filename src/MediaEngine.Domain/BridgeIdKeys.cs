namespace MediaEngine.Domain;

/// <summary>
/// Single source of truth for bridge ID key names — the external identifier
/// types stored in the <c>bridge_ids</c> table and used throughout the
/// hydration pipeline to link media to external databases.
/// </summary>
public static class BridgeIdKeys
{
    public const string Isbn = "isbn";
    public const string Isbn13 = "isbn_13";
    public const string Isbn10 = "isbn_10";
    public const string Asin = "asin";
    public const string TmdbId = "tmdb_id";
    public const string TmdbEpisodeId = "tmdb_episode_id";
    public const string ImdbId = "imdb_id";
    public const string TvdbId = "tvdb_id";
    public const string TvdbSeasonId = "tvdb_season_id";
    public const string TvdbEpisodeId = "tvdb_episode_id";
    public const string TvdbPersonId = "tvdb_person_id";
    public const string TmdbPersonId = "tmdb_person_id";
    public const string WikidataQid = "wikidata_qid";
    public const string AppleBooksId = "apple_books_id";
    public const string AudibleId = "audible_id";
    public const string GoodreadsId = "goodreads_id";
    public const string MusicBrainzId = "musicbrainz_id";
    public const string MusicBrainzArtistId = "musicbrainz_artist_id";
    public const string MusicBrainzWorkId = "musicbrainz_work_id";
    public const string MusicBrainzReleaseId = "musicbrainz_release_id";
    public const string MusicBrainzRecordingId = "musicbrainz_recording_id";
    public const string MusicBrainzReleaseGroupId = "musicbrainz_release_group_id";
    public const string Isrc = "isrc";
    public const string ComicVineId = "comic_vine_id";
    public const string ComicVineVolumeId = "comic_vine_volume_id";
    public const string SpotifyId = "spotify_id";
    public const string OpenLibraryId = "open_library_id";
    public const string AppleMusicId = "apple_music_id";
    public const string AppleMusicCollectionId = "apple_music_collection_id";
    public const string AppleArtistId = "apple_artist_id";

    // ── Pseudonym detection (Tuvima.Wikidata patterns) ──────────────────
    // Set on the work's claim list when ReconciliationAdapter detects a pen
    // name relationship. AuthorRealNameQid fires for Pattern 1 (reverse P742
    // — "Richard Bachman" → Stephen King's QID); AuthorPseudonym fires for
    // Pattern 2 (P742 enumeration — Stephen King → ["Richard Bachman", ...]).

    /// <summary>The QID of the real author behind a pen name. Pattern 1.</summary>
    public const string AuthorRealNameQid = "author_real_name_qid";

    /// <summary>A pen name (P742 string value) used by the resolved author. Pattern 2.</summary>
    public const string AuthorPseudonym = "author_pseudonym";

    /// <summary>All known bridge ID keys, for validation and enumeration.</summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Isbn, Isbn13, Isbn10, Asin, TmdbId, TmdbEpisodeId, ImdbId, TvdbId,
        TvdbSeasonId, TvdbEpisodeId, TvdbPersonId, TmdbPersonId, WikidataQid,
        AppleBooksId, AudibleId, GoodreadsId, MusicBrainzId,
        MusicBrainzArtistId, MusicBrainzWorkId, MusicBrainzReleaseId,
        MusicBrainzRecordingId, MusicBrainzReleaseGroupId, Isrc,
        ComicVineId, ComicVineVolumeId, SpotifyId, OpenLibraryId,
        AppleMusicId, AppleMusicCollectionId, AppleArtistId,
        AuthorRealNameQid, AuthorPseudonym
    };
}
