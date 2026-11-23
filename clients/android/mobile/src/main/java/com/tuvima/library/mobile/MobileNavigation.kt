package com.tuvima.library.mobile

import com.tuvima.library.core.CollectionEntry
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.ViewGallery
import com.tuvima.library.core.ViewItem
import com.tuvima.library.core.ViewPerson
import com.tuvima.library.core.detailTargetFromRoute

/** What a Play button asks for. The type decides whether the video screen or the audio service handles it. */
internal data class PlayRequest(
    val assetId: String,
    val entityType: String?,
    val title: String?,
    val subtitle: String?,
    /** The show this episode belongs to, when the screen it came from knows it; next-episode features need it. */
    val showId: String? = null,
    /** The work a video's bookmarks hang on, when the screen it came from knows it (see `PhonePlayerLogic.workIdFor`). */
    val workId: String? = null,
)

/** A full-screen page shown over the tabs. The back button or gesture removes the top one. */
internal sealed interface Overlay {
    data class Detail(val entityType: String, val id: String, val assetId: String?) : Overlay
    data class CollectionOpen(val entry: CollectionEntry) : Overlay
    data class UniverseOpen(val qid: String) : Overlay
    data class GalleryOpen(val gallery: ViewGallery) : Overlay
    data class ViewerOpen(val items: List<ViewItem>, val index: Int) : Overlay

    /** An artist's songs, opened from Listen's Artists tab. */
    data class ArtistOpen(val name: String) : Overlay

    /** One person's photos and videos, opened from View's People tab. */
    data class PersonOpen(val person: ViewPerson) : Overlay
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
    // The route's query and fragment are never part of the id (an episode route is /details/tvshow/{showId}?...&episode={id}).
    card.actions.firstNotNullOfOrNull { action -> action.webUrl?.takeIf { "/details/" in it }?.let(::detailTargetFromRoute) }
        ?.let { return it }
    card.collectionId?.let { return "collection" to it }
    return mobileDetailEntityType(card.mediaType) to (card.workId ?: card.id)
}
