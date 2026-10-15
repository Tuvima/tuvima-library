package com.tuvima.library.mobile

import com.tuvima.library.core.CollectionEntry
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.ViewGallery
import com.tuvima.library.core.ViewItem
import org.json.JSONObject

internal data class MobileSearchResult(
    val id: String,
    val entityType: String,
    val title: String,
    val subtitle: String?,
)

/** A full-screen page shown over the tabs. The back button or gesture removes the top one. */
internal sealed interface Overlay {
    data class Detail(val entityType: String, val id: String, val assetId: String?) : Overlay
    data class CollectionOpen(val entry: CollectionEntry) : Overlay
    data class UniverseOpen(val qid: String) : Overlay
    data class GalleryOpen(val gallery: ViewGallery) : Overlay
    data class ViewerOpen(val items: List<ViewItem>, val index: Int) : Overlay
}

internal fun parseMobileSearch(json: JSONObject): List<MobileSearchResult> = buildList {
    val seen = mutableSetOf<String>()
    val sections = json.optJSONArray("sections") ?: return@buildList
    for (sectionIndex in 0 until sections.length()) {
        val results = sections.optJSONObject(sectionIndex)?.optJSONArray("results") ?: continue
        for (resultIndex in 0 until results.length()) {
            val value = results.optJSONObject(resultIndex) ?: continue
            val route = value.optString("detailRoute").split('/').filter(String::isNotBlank)
            val id = route.lastOrNull() ?: value.optString("id")
            val entityType = route.dropLast(1).lastOrNull() ?: value.optString("entityType", "work")
            if (id.isBlank() || !seen.add("$entityType:$id")) continue
            add(MobileSearchResult(id, entityType, value.optString("title"), value.optString("subtitle").takeIf(String::isNotBlank)))
        }
    }
}

internal fun mobileDetailEntityType(mediaType: String): String {
    val value = mediaType.lowercase()
    return when {
        "movie" in value -> "movie"
        "episode" in value -> "tvEpisode"
        "tv" in value -> "tvShow"
        "audiobook" in value -> "audiobook"
        "book" in value -> "book"
        "comic" in value -> "comicIssue"
        "music" in value -> "musicAlbum"
        else -> "work"
    }
}

internal fun mobileCardDetailTarget(card: DisplayCard): Pair<String, String> {
    card.actions.firstNotNullOfOrNull { action ->
        action.webUrl?.takeIf { "/details/" in it }?.split('/')?.filter(String::isNotBlank)?.takeIf { it.size >= 2 }
            ?.let { it[it.size - 2] to it.last() }
    }?.let { return it }
    card.collectionId?.let { return "collection" to it }
    return mobileDetailEntityType(card.mediaType) to (card.workId ?: card.id)
}
