package com.tuvima.library.core

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
    )
}
