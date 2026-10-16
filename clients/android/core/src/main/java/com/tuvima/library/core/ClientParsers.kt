package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject

// Parsers for the API v1 wire shapes the app screens read. JSON names are the Engine's own: snake_case for the
// photo, Collections and device routes, camelCase for the display and details models. Nothing here guesses at
// a missing field: an absent value becomes null, an empty list, or zero.

internal fun parseViewItem(json: JSONObject): ViewItem {
    val firstFile = json.optJSONArray("files")?.optJSONObject(0)
    return ViewItem(
        id = json.getString("id"),
        mediaKind = json.optString("media_kind", "image"),
        title = json.optionalString("title"),
        fileName = json.optionalString("file_name"),
        mimeType = json.optionalString("mime_type"),
        capturedAt = json.optionalString("captured_at"),
        width = json.optionalInt("width"),
        height = json.optionalInt("height"),
        durationSeconds = json.optionalDouble("duration_seconds"),
        byteSize = firstFile?.optionalLong("byte_size"),
        favorite = json.optBoolean("favorite"),
        locationName = json.optionalString("location_name"),
        cameraName = listOfNotNull(json.optionalString("device_make"), json.optionalString("device_model"))
            .joinToString(" ").takeIf { it.isNotBlank() },
        description = json.optionalString("description"),
    )
}

internal fun parseViewTimelinePage(json: JSONObject): ViewTimelinePage = ViewTimelinePage(
    months = json.optJSONArray("months")?.objects { month ->
        ViewTimelineMonth(
            year = month.optInt("year"),
            month = month.optInt("month"),
            items = month.optJSONArray("items")?.objects(::parseViewItem).orEmpty(),
        )
    }.orEmpty(),
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
)

internal fun parseViewItemPage(json: JSONObject): ViewItemPage = ViewItemPage(
    items = json.optJSONArray("items")?.objects(::parseViewItem).orEmpty(),
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
)

internal fun parseViewGalleries(json: JSONObject): ViewGalleries {
    fun gallery(value: JSONObject) = ViewGallery(
        id = value.getString("id"),
        name = value.optString("name"),
        description = value.optionalString("description"),
        itemCount = value.optInt("item_count"),
        coverItemId = value.optionalString("cover_item_id"),
    )
    return ViewGalleries(
        owned = json.optJSONArray("owned")?.objects { gallery(it) }.orEmpty(),
        sharedWithYou = json.optJSONArray("shared_with_you")?.objects { gallery(it) }.orEmpty(),
    )
}

internal fun parseCollectionEntry(json: JSONObject, absolute: (String?) -> String?): CollectionEntry =
    CollectionEntry(
        id = json.getString("id"),
        name = json.optString("name"),
        description = json.optionalString("description"),
        groupType = json.optString("group_type", "collection"),
        collectionType = json.optionalString("collection_type"),
        systemKey = json.optionalString("system_key"),
        itemCount = json.optInt("item_count"),
        coverUrls = json.optJSONArray("artwork_items")
            ?.objects { absolute(it.optionalString("cover_url")) }
            .orEmpty()
            .filterNotNull(),
    )

internal fun parseCollectionsPage(json: JSONObject, absolute: (String?) -> String?): CollectionsPage =
    CollectionsPage(
        items = json.optJSONArray("items")?.objects { parseCollectionEntry(it, absolute) }.orEmpty(),
        totalCount = json.optInt("total_count"),
        nextCursor = json.optionalString("next_cursor"),
        hasMore = json.optBoolean("has_more"),
    )

internal fun parseCollectionItemsPage(json: JSONObject, absolute: (String?) -> String?): CollectionItemsPage =
    CollectionItemsPage(
        items = json.optJSONArray("items")?.objects { item ->
            CollectionItem(
                id = item.getString("id"),
                workId = item.optionalString("work_id"),
                title = item.optString("title"),
                creator = item.optionalString("creator"),
                mediaType = item.optString("media_type"),
                coverUrl = absolute(item.optionalString("cover_url")),
                detailRoute = item.optionalString("detail_route"),
            )
        }.orEmpty(),
        totalCount = json.optInt("total_count"),
        nextCursor = json.optionalString("next_cursor"),
        hasMore = json.optBoolean("has_more"),
    )

internal fun parseDeviceProfiles(json: JSONObject): List<DeviceProfile> =
    json.optJSONArray("profiles")?.objects { profile ->
        DeviceProfile(
            profileId = profile.getString("profile_id"),
            displayName = profile.optString("display_name"),
            avatarUrl = profile.optionalString("avatar_url"),
            isKids = profile.optBoolean("is_kids"),
            requiresPin = profile.optBoolean("requires_pin"),
            isActive = profile.optBoolean("is_active"),
        )
    }.orEmpty()

internal fun parseDetailModel(json: JSONObject, absolute: (String?) -> String?): DetailModel {
    val facts = json.optJSONObject("facts")
    val artwork = json.optJSONObject("artwork")
    val hero = artwork?.optJSONObject("heroArtwork")
    val actors = facts?.optJSONArray("actors")?.strings().orEmpty().map { DetailCredit(it, null) }
    val credits = json.optJSONArray("previewContributors")?.objects { credit ->
        DetailCredit(
            name = credit.optString("displayName"),
            role = credit.optionalString("characterName") ?: credit.optionalString("primaryRole"),
        )
    }.orEmpty().filter { it.name.isNotBlank() }.distinctBy { it.name }
    return DetailModel(
        title = json.optString("title", "Tuvima Library"),
        subtitle = json.optionalString("subtitle"),
        tagline = json.optionalString("tagline"),
        description = json.optionalString("description"),
        year = facts?.optionalString("year"),
        runtime = facts?.optionalString("runtime") ?: facts?.optionalString("duration"),
        contentRating = facts?.optionalString("contentRating") ?: facts?.optionalString("rating"),
        genres = facts?.optJSONArray("genres")?.strings().orEmpty(),
        artworkUrl = absolute(
            artwork?.optionalString("posterUrl")
                ?: artwork?.optionalString("coverUrl")
                ?: hero?.optionalString("largeUrl")
                ?: hero?.optionalString("url")
                ?: artwork?.optionalString("backdropUrl"),
        ),
        cast = (if (credits.isNotEmpty()) credits else actors).take(24),
        editions = json.optJSONArray("ownedFormats")?.objects { format ->
            DetailEdition(
                id = format.optString("id"),
                title = format.optionalString("editionTitle") ?: format.optString("displayName"),
                details = listOfNotNull(
                    format.optionalString("fileFormat"),
                    format.optionalString("runtime"),
                    format.optionalString("publisher"),
                ).joinToString(" · ").takeIf { it.isNotBlank() },
            )
        }.orEmpty(),
        progressPercent = json.optJSONObject("progress")?.optionalDouble("percent"),
        showId = parentShowId(json.optionalString("showId"), json.optJSONObject("episodeContext")?.optionalString("showWorkId")),
    )
}

internal fun parseDeviceDownloads(json: JSONObject): List<ServerDownloadEntry> =
    json.optJSONArray("downloads")?.objects { entry ->
        ServerDownloadEntry(
            assetId = entry.getString("asset_id"),
            variantId = entry.getString("variant_id"),
            storedBytes = entry.optLong("stored_bytes"),
            storedAtMs = OfflineLogic.parseIsoMillis(entry.optionalString("stored_at")),
            expiresAtMs = OfflineLogic.parseIsoMillis(entry.optionalString("expires_at")),
            kind = OfflineKind.fromWire(entry.optionalString("kind")),
            photoItemId = entry.optionalString("photo_item_id"),
        )
    }.orEmpty()

internal fun parseNextEpisodes(json: JSONObject, absolute: (String?) -> String?): NextEpisodes =
    NextEpisodes(
        showId = json.optString("show_id"),
        afterAssetId = json.optString("after_asset_id"),
        episodes = json.optJSONArray("episodes")?.objects { episode ->
            NextEpisode(
                workId = episode.optionalString("work_id"),
                assetId = episode.getString("asset_id"),
                title = episode.optString("title"),
                seasonNumber = episode.optionalInt("season_number"),
                episodeNumber = episode.optionalInt("episode_number"),
                stillUrl = absolute(episode.optionalString("still_url")),
            )
        }.orEmpty(),
    )

/** The Engine's progress state (`/progress/{assetId}`): the revision and time, plus the position kept in its extra properties. */
internal fun parseServerProgress(json: JSONObject): ServerProgress = ServerProgress(
    revision = json.optLong("revision"),
    progressPct = json.optDouble("progress_pct", 0.0),
    lastAccessedMs = OfflineLogic.parseIsoMillis(json.optionalString("last_accessed")),
    positionSeconds = json.optJSONObject("extended_properties")?.optionalString("position_seconds")?.toDoubleOrNull(),
)

// Playback manifest track lists. Camel-case names from the Engine's PlaybackDtos; a missing or null list is empty.

internal fun parseAudioTracks(array: JSONArray?): List<AudioTrack> = array?.objects { track ->
    AudioTrack(
        index = track.optInt("index"),
        language = track.optionalString("language"),
        codec = track.optionalString("codec"),
        displayName = track.optionalString("displayName"),
        isDefault = track.optBoolean("isDefault"),
        channels = track.optionalInt("channels"),
    )
}.orEmpty()

internal fun parseSubtitleTracks(array: JSONArray?, absolute: (String?) -> String?): List<SubtitleTrack> =
    array?.objects { track ->
        SubtitleTrack(
            index = track.optInt("index"),
            language = track.optionalString("language"),
            codec = track.optionalString("codec"),
            displayName = track.optionalString("displayName"),
            isDefault = track.optBoolean("isDefault"),
            isForced = track.optBoolean("isForced"),
            deliveryUrl = absolute(track.optionalString("deliveryUrl")),
        )
    }.orEmpty()

internal fun parseChapters(array: JSONArray?): List<Chapter> = array?.objects { chapter ->
    Chapter(
        index = chapter.optInt("index"),
        title = chapter.optString("title", ""),
        startSeconds = chapter.optDouble("startSeconds", 0.0),
        endSeconds = chapter.optionalDouble("endSeconds"),
    )
}.orEmpty()

internal fun parsePlaybackSegments(array: JSONArray?): List<PlaybackSegment> = array?.objects { segment ->
    PlaybackSegment(
        kind = segment.optString("kind", ""),
        startSeconds = segment.optDouble("startSeconds", 0.0),
        endSeconds = segment.optionalDouble("endSeconds"),
        isSkippable = segment.optBoolean("isSkippable", true),
        reviewStatus = segment.optionalString("reviewStatus"),
    )
}.orEmpty()

/** Reads the device route's body; any field that is missing or out of range falls back to the safe default. */
internal fun parsePlaybackPreferences(json: JSONObject): PlaybackPreferences {
    val defaults = PlaybackPreferences.Default
    val skipBack = json.optInt("skipBackSeconds", defaults.skipBackSeconds)
    val skipForward = json.optInt("skipForwardSeconds", defaults.skipForwardSeconds)
    val speed = json.optDouble("defaultPlaybackSpeed", defaults.defaultPlaybackSpeed)
    return PlaybackPreferences(
        audioLanguage = json.optionalString("audioLanguage") ?: defaults.audioLanguage,
        subtitleLanguage = json.optionalString("subtitleLanguage") ?: defaults.subtitleLanguage,
        subtitleMode = PlaybackPreferences.modeFrom(json.optionalString("subtitleMode")),
        subtitleSize = json.optionalString("subtitleSize") ?: defaults.subtitleSize,
        subtitleBackground = json.optBoolean("subtitleBackground", defaults.subtitleBackground),
        skipBackSeconds = skipBack.takeIf { it in 1..PlaybackPreferences.MAX_SKIP_SECONDS } ?: defaults.skipBackSeconds,
        skipForwardSeconds = skipForward.takeIf { it in 1..PlaybackPreferences.MAX_SKIP_SECONDS } ?: defaults.skipForwardSeconds,
        autoplayNextEpisode = json.optBoolean("autoplayNextEpisode", defaults.autoplayNextEpisode),
        resumePlayback = json.optBoolean("resumePlayback", defaults.resumePlayback),
        defaultPlaybackSpeed = speed.takeIf { !it.isNaN() && it in PlaybackPreferences.MIN_SPEED..PlaybackPreferences.MAX_SPEED }
            ?: defaults.defaultPlaybackSpeed,
    )
}
