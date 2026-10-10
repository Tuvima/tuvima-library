using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Bridge ID to Wikidata P-code mapping and bridge hint extraction.
/// Thread-safe singleton; the P-code map is built lazily from provider config.
/// </summary>
public sealed class BridgeIdHelper
{
    private readonly IConfigurationLoader _configLoader;
    private Dictionary<string, string>? _claimKeyToPCode;
    private readonly Lock _mapLock = new();

    public BridgeIdHelper(IConfigurationLoader configLoader)
    {
        _configLoader = configLoader;
    }

    /// <summary>
    /// Returns the Wikidata P-code for a given claim key (e.g. "isbn_13" → "P212"),
    /// or <c>null</c> if no mapping is found.
    /// </summary>
    public string? GetPCode(string claimKey)
    {
        EnsureMap();
        return _claimKeyToPCode!.TryGetValue(claimKey, out var pCode) ? pCode : null;
    }

    /// <summary>
    /// Returns the claim key for a given Wikidata P-code (e.g. "P212" → "isbn_13"),
    /// or the P-code itself if no mapping is found.
    /// </summary>
    public string GetClaimKey(string pCode)
    {
        var reconConfig = _configLoader
            .LoadConfig<ReconciliationProviderConfig>("providers", "wikidata_reconciliation");

        if (reconConfig?.DataExtension?.PropertyLabels is not null &&
            reconConfig.DataExtension.PropertyLabels.TryGetValue(pCode, out var claimKey))
        {
            return claimKey;
        }

        return pCode;
    }

    /// <summary>
    /// Returns true if the claim key represents a bridge identifier that should be
    /// stored in the bridge_ids table for Stage 2 Wikidata resolution.
    /// </summary>
    public static bool IsBridgeId(string claimKey) => claimKey switch
    {
        BridgeIdKeys.Isbn or BridgeIdKeys.Isbn13 or BridgeIdKeys.Isbn10 => true,
        BridgeIdKeys.Asin => true,
        BridgeIdKeys.AppleBooksId => true,
        BridgeIdKeys.TmdbId => true,
        BridgeIdKeys.TmdbEpisodeId => true,
        // TheTVDB series id (Wikidata P4835): the show-scope target id for TV Stage 2.
        BridgeIdKeys.TvdbId => true,
        BridgeIdKeys.ImdbId => true,
        BridgeIdKeys.AudibleId => true,
        BridgeIdKeys.GoodreadsId => true,
        BridgeIdKeys.MusicBrainzId => true,
        BridgeIdKeys.MusicBrainzArtistId => true,
        BridgeIdKeys.MusicBrainzWorkId => true,
        BridgeIdKeys.MusicBrainzReleaseId => true,
        BridgeIdKeys.MusicBrainzRecordingId => true,
        BridgeIdKeys.MusicBrainzReleaseGroupId => true,
        BridgeIdKeys.Isrc => true,
        BridgeIdKeys.ComicVineId => true,
        BridgeIdKeys.OpenLibraryId => true,
        "gcd_id" => true,

        "apple_music_id" => true,
        BridgeIdKeys.AppleMusicCollectionId => true,
        BridgeIdKeys.AppleArtistId => true,
        _ => false,
    };

    private void EnsureMap()
    {
        if (_claimKeyToPCode is not null)
        {
            return;
        }

        lock (_mapLock)
        {
            if (_claimKeyToPCode is not null)
            {
                return;
            }

            var reconConfig = _configLoader
                .LoadConfig<ReconciliationProviderConfig>("providers", "wikidata_reconciliation");

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (reconConfig?.DataExtension?.PropertyLabels is not null)
            {
                foreach (var kvp in reconConfig.DataExtension.PropertyLabels)
                {
                    map[kvp.Value] = kvp.Key;
                }
            }

            // Aliases
            if (!map.ContainsKey(BridgeIdKeys.Isbn) && map.TryGetValue(BridgeIdKeys.Isbn13, out var isbn13PCode))
            {
                map[BridgeIdKeys.Isbn] = isbn13PCode;
            }

            if (!map.ContainsKey(BridgeIdKeys.TmdbId))
            {
                if (map.TryGetValue("tmdb_movie_id", out var tmdbMoviePCode))
                {
                    map[BridgeIdKeys.TmdbId] = tmdbMoviePCode;
                }
                else if (map.TryGetValue("tmdb_tv_id", out var tmdbTvPCode))
                {
                    map[BridgeIdKeys.TmdbId] = tmdbTvPCode;
                }
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzId))
            {
                if (map.TryGetValue("musicbrainz_release_group_id", out var mbReleaseGroupPCode))
                {
                    map[BridgeIdKeys.MusicBrainzId] = mbReleaseGroupPCode;
                }
                else if (map.TryGetValue("musicbrainz_artist_id", out var mbArtistPCode))
                {
                    map[BridgeIdKeys.MusicBrainzId] = mbArtistPCode;
                }
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzRecordingId))
            {
                map[BridgeIdKeys.MusicBrainzRecordingId] = "P4404";
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzArtistId))
            {
                map[BridgeIdKeys.MusicBrainzArtistId] = "P434";
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzWorkId))
            {
                map[BridgeIdKeys.MusicBrainzWorkId] = "P435";
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzReleaseId))
            {
                map[BridgeIdKeys.MusicBrainzReleaseId] = "P5813";
            }

            if (!map.ContainsKey(BridgeIdKeys.MusicBrainzReleaseGroupId))
            {
                map[BridgeIdKeys.MusicBrainzReleaseGroupId] = "P436";
            }

            if (!map.ContainsKey(BridgeIdKeys.Isrc))
            {
                map[BridgeIdKeys.Isrc] = "P1243";
            }

            if (!map.ContainsKey(BridgeIdKeys.OpenLibraryId))
            {
                if (map.TryGetValue("openlibrary_id", out var openLibraryPCode))
                {
                    map[BridgeIdKeys.OpenLibraryId] = openLibraryPCode;
                }
                else
                {
                    map[BridgeIdKeys.OpenLibraryId] = "P648";
                }
            }

            _claimKeyToPCode = map;
        }
    }
}
