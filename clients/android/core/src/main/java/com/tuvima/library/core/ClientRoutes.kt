package com.tuvima.library.core

import java.net.URLEncoder
import java.nio.charset.StandardCharsets

/**
 * What a browse page asks for. These are the same options the Dashboard's library pages send to
 * `/api/v1/display/browse`: sort, filters, and a window into the result.
 */
data class BrowseQuery(
    val lane: String? = null,
    val mediaType: String? = null,
    val grouping: String? = null,
    val search: String? = null,
    val genres: List<String> = emptyList(),
    val creators: List<String> = emptyList(),
    val years: List<String> = emptyList(),
    val status: String? = null,
    val sort: String? = null,
    val offset: Int = 0,
    val limit: Int = 48,
) {
    /** True when a filter (not just the sort) narrows the result. */
    val hasFilters: Boolean
        get() = genres.isNotEmpty() || creators.isNotEmpty() || years.isNotEmpty() || !status.isNullOrBlank()
}

/**
 * Every API v1 path the app screens use, built in one place so the paths stay inside `/api/v1`
 * and can be checked without a network or a device.
 */
object ClientRoutes {
    private const val API = "/api/v1"

    /** Thumbnail edge lengths the Engine accepts; any other size answers 400. */
    val thumbnailSizes: List<Int> = listOf(160, 320, 640)

    /** Preview edge lengths the Engine accepts. */
    val previewSizes: List<Int> = listOf(1280, 2048)

    private val qidPattern = Regex("^Q[0-9]{1,15}$")

    fun isQid(value: String?): Boolean = value != null && qidPattern.matches(value.trim())

    fun home(includeCatalog: Boolean = false): String =
        "$API/display/home?includeCatalog=$includeCatalog"

    fun continueAll(limit: Int = 24): String =
        "$API/display/continue?limit=${limit.coerceIn(1, 100)}&includeCatalog=false"

    fun group(collectionId: String): String =
        "$API/display/groups/${encode(collectionId)}?includeCatalog=false"

    fun browse(query: BrowseQuery): String {
        val parameters = buildList {
            query.lane?.takeIf(String::isNotBlank)?.let { add("lane=${encode(it)}") }
            query.mediaType?.takeIf(String::isNotBlank)?.let { add("mediaType=${encode(it)}") }
            query.grouping?.takeIf(String::isNotBlank)?.let { add("grouping=${encode(it)}") }
            query.search?.takeIf(String::isNotBlank)?.let { add("search=${encode(it)}") }
            if (query.genres.isNotEmpty()) add("genres=${encode(query.genres.joinToString(","))}")
            if (query.creators.isNotEmpty()) add("creator=${encode(query.creators.joinToString(","))}")
            if (query.years.isNotEmpty()) add("year=${encode(query.years.joinToString(","))}")
            query.status?.takeIf(String::isNotBlank)?.let { add("status=${encode(it)}") }
            query.sort?.takeIf(String::isNotBlank)?.let { add("sort=${encode(it)}") }
            add("offset=${query.offset.coerceAtLeast(0)}")
            add("limit=${query.limit.coerceIn(1, 200)}")
            add("includeCatalog=true")
        }
        return "$API/display/browse?${parameters.joinToString("&")}"
    }

    /** My List: the saved titles of the active profile. GET lists them; `/finished` lists the ones already finished. */
    const val PROFILE_SAVED = "$API/profile-state/saved"
    const val PROFILE_SAVED_FINISHED = "$API/profile-state/saved/finished"

    /** POST: take the named titles off My List; the answer is what was taken (with its saved dates), for Undo. */
    const val PROFILE_SAVED_REMOVE = "$API/profile-state/saved/remove"

    /** POST: save the titles a remove returned again with their original dates. */
    const val PROFILE_SAVED_RESTORE = "$API/profile-state/saved/restore"

    /** PUT saves one title, DELETE removes it (`204`). The kind is the Engine's route name (see [SavedKind.route]). */
    fun profileSaved(kind: SavedKind, id: String): String = "$PROFILE_SAVED/${kind.route}/${encode(id)}"

    /** One page of the My List grid (`personal/my-list`) with the Dashboard's sort and kind filter. */
    fun myListGrid(sort: String? = null, type: String? = null, offset: Int = 0, limit: Int = MyListLogic.PAGE_SIZE): String {
        val parameters = buildList {
            sort?.takeIf(String::isNotBlank)?.let { add("sort=${encode(it)}") }
            type?.takeIf(String::isNotBlank)?.let { add("type=${encode(it)}") }
            add("offset=${offset.coerceAtLeast(0)}")
            add("limit=${limit.coerceIn(1, 100)}")
        }
        return "$API/display/grids/${MyListLogic.LANE}/${MyListLogic.SHELF_KEY}?${parameters.joinToString("&")}"
    }

    /** The newest additions the profile may see, a page at a time by cursor ([type] is watch, read, listen or view). */
    fun recent(type: String? = null, cursor: String? = null, limit: Int = 18): String {
        val parameters = buildList {
            type?.takeIf(String::isNotBlank)?.let { add("type=${encode(it)}") }
            cursor?.takeIf(String::isNotBlank)?.let { add("cursor=${encode(it)}") }
            add("limit=${limit.coerceIn(1, 100)}")
        }
        return "$API/display/recent?${parameters.joinToString("&")}"
    }

    /**
     * One page of a Listen shelf (`albums` or `artists`, built from the Music home). The first page sends neither a cursor nor an
     * offset; later pages follow the cursor the Engine gave, or fall back to the offset when it gave none.
     */
    fun listenShelf(key: String, cursor: String? = null, offset: Int = 0, limit: Int = ListenLogic.SHELF_PAGE): String {
        val parameters = buildList {
            add("lane=${ListenLogic.LANE}")
            add("mediaType=${ListenLogic.MEDIA_TYPE}")
            add("grouping=home")
            if (!cursor.isNullOrBlank()) add("cursor=${encode(cursor)}") else if (offset > 0) add("offset=$offset")
            add("limit=${limit.coerceIn(1, 100)}")
        }
        return "$API/display/shelves/${encode(key)}?${parameters.joinToString("&")}"
    }

    /** Universal search: ranked results in sections (the search overlay). */
    fun search(query: String, limit: Int = SearchLogic.RESULT_LIMIT): String =
        "$API/display/search?q=${encode(query.trim())}&limit=${limit.coerceIn(1, 100)}"

    /** Named or reviewed people in the signed-in person's own photos and videos. */
    fun viewPeople(cursor: String? = null, limit: Int = ViewDiscoveryLogic.PAGE_SIZE): String =
        paged("$API/view/people", cursor, limit.coerceIn(1, 100))

    /** The photos and videos that carry one person's name, newest first. */
    fun viewPersonItems(key: String, cursor: String? = null, limit: Int = 100): String =
        paged("$API/view/people/${encodePathSegment(key)}/items", cursor, limit.coerceIn(1, 200))

    /** Place clusters from the signed-in person's own photos and videos. */
    fun viewPlaces(cursor: String? = null, limit: Int = ViewDiscoveryLogic.PAGE_SIZE): String =
        paged("$API/view/places", cursor, limit.coerceIn(1, 100))

    fun details(entityType: String, id: String): String =
        "$API/details/${encode(entityType)}/${encode(id)}"

    fun encodeJob(assetId: String): String = "$API/playback/${encode(assetId)}/encode"

    fun revokeDevice(deviceId: String): String = "$API/devices/${encode(deviceId)}"

    /** The paired app's own list of what it stores offline: GET lists, POST adds or updates, DELETE `/{variantId}` removes. */
    const val DEVICE_DOWNLOADS = "$API/devices/current/downloads"

    fun deviceDownload(variantId: String): String = "$DEVICE_DOWNLOADS/${encode(variantId)}"

    /** Forgets a kept photo (by its View item id) on the paired app's list: `204`, or `404` when it was not listed. */
    fun devicePhotoDownload(itemId: String): String = "$DEVICE_DOWNLOADS/photos/${encode(itemId)}"

    /** The offline file of a prepared variant (bearer token, `downloads.read`, supports ranges). */
    fun offlineFile(assetId: String, variantId: String): String =
        "$API/playback/${encode(assetId)}/offline/${encode(variantId)}"

    fun progress(assetId: String): String = "$API/progress/${encode(assetId)}"

    /** The owned episodes after the episode file [afterAssetId]; the Engine clamps [count] to 1-10. */
    fun nextEpisodes(showId: String, afterAssetId: String, count: Int = 3): String =
        "$API/details/show/${encode(showId)}/next-episodes?after=${encode(afterAssetId)}&count=${count.coerceIn(1, 10)}"

    // Bookmarks of a title (videos and audiobooks share the player's audiobook-bookmark routes; the Engine decides who sees what).

    /** GET lists the profile's own bookmarks plus the title's Everyone list (each marked isYours and inEveryone); POST adds one of the person's own. */
    fun bookmarks(workId: String): String = "$API/player/audiobooks/${encode(workId)}/bookmarks"

    /** DELETE removes one of the caller's own bookmarks (`204`, or `404` for anyone else's). */
    fun bookmark(bookmarkId: String): String = "$API/player/audiobooks/bookmarks/${encode(bookmarkId)}"

    // Find another (subtitles and lyrics). All four sit under the asset's text-tracks group; a blank language is left out.

    /** Whether a source is switched on for the item (settings only, no source is contacted). */
    fun textTrackSources(assetId: String, kind: TextTrackKind = TextTrackKind.Subtitles): String =
        "$API/stream/${encode(assetId)}/text-tracks/sources?kind=${encode(kind.wire)}"

    /** The list of other results for the item, searched in [language] (the profile's subtitle language). */
    fun textTrackAlternatives(assetId: String, kind: TextTrackKind = TextTrackKind.Subtitles, language: String? = null): String =
        "$API/stream/${encode(assetId)}/text-tracks/alternatives?" +
            queryOf("kind" to kind.wire, "language" to language)

    /** One result read on request: its first lines and, for a subtitle file, the timing check. */
    fun textTrackAlternativePreview(
        assetId: String,
        provider: String,
        sourceId: String,
        kind: TextTrackKind = TextTrackKind.Subtitles,
        language: String? = null,
    ): String =
        "$API/stream/${encode(assetId)}/text-tracks/alternatives/preview?" +
            queryOf("kind" to kind.wire, "provider" to provider, "sourceId" to sourceId, "language" to language)

    /** The asset's lyrics and subtitle tracks (the Lyrics panel picks the preferred lyrics one). */
    fun textTracks(assetId: String): String = "$API/stream/${encode(assetId)}/text-tracks"

    /** One text track's file, served as text (a lyrics file is read as LRC or plain words). */
    fun textTrackFile(assetId: String, trackId: String): String =
        "$API/stream/${encode(assetId)}/text-tracks/${encode(trackId)}"

    /** Use one result (POST). The choice is Library-wide. */
    fun chooseTextTrackAlternative(assetId: String): String =
        "$API/stream/${encode(assetId)}/text-tracks/alternatives/choose"

    const val DEVICE_PROFILES = "$API/devices/current/profiles"
    const val SWITCH_PROFILE = "$API/devices/current/switch-profile"
    const val VIEW_GALLERIES = "$API/view/galleries"

    fun viewTimeline(cursor: String? = null, limit: Int = 100): String =
        paged("$API/view/timeline", cursor, limit.coerceIn(1, 200))

    fun viewGalleryItems(galleryId: String, cursor: String? = null, limit: Int = 100): String =
        paged("$API/view/galleries/${encode(galleryId)}/items", cursor, limit.coerceIn(1, 200))

    fun viewItem(id: String): String = "$API/view/items/${encode(id)}"

    fun viewThumbnail(id: String, size: Int = 320): String =
        "$API/view/items/${encode(id)}/thumbnail?size=${nearestSize(size, thumbnailSizes)}"

    fun viewPreview(id: String, size: Int = 2048): String =
        "$API/view/items/${encode(id)}/preview?size=${nearestSize(size, previewSizes)}"

    fun viewOriginal(id: String): String = "$API/view/items/${encode(id)}/original"

    /** The playback manifest of a personal video, by its View item id (not a catalogue asset id). */
    fun viewPlayback(id: String): String = "$API/view/items/${encode(id)}/playback"

    fun collections(cursor: String? = null, limit: Int = 24): String =
        paged("$API/collections", cursor, limit.coerceIn(1, 250))

    fun collection(id: String): String = "$API/collections/${encode(id)}"

    fun collectionItems(id: String, cursor: String? = null, limit: Int = 24): String =
        paged("$API/collections/${encode(id)}/items", cursor, limit.coerceIn(1, 250))

    /** The Universe route only accepts a Wikidata QID such as `Q4242`; anything else is refused here. */
    fun universe(qid: String): String {
        val value = qid.trim()
        require(isQid(value)) { "A Universe is opened by a Wikidata QID such as Q4242." }
        return "$API/universe/$value"
    }

    /** The smallest allowed size that is at least [requested], or the largest allowed size. */
    fun nearestSize(requested: Int, allowed: List<Int>): Int =
        allowed.firstOrNull { it >= requested } ?: allowed.last()

    private fun queryOf(vararg parameters: Pair<String, String?>): String =
        parameters
            .filter { !it.second.isNullOrBlank() }
            .joinToString("&") { "${it.first}=${encode(it.second!!)}" }

    private fun paged(path: String, cursor: String?, limit: Int): String {
        val parameters = buildList {
            cursor?.takeIf(String::isNotBlank)?.let { add("cursor=${encode(it)}") }
            add("limit=$limit")
        }
        return "$path?${parameters.joinToString("&")}"
    }

    /** For a value that is a path segment but not an id (a person's key can hold spaces): a space is `%20` here, never `+`. */
    internal fun encodePathSegment(value: String): String = encode(value).replace("+", "%20")

    internal fun encode(value: String): String = URLEncoder.encode(value, StandardCharsets.UTF_8.toString())
}
