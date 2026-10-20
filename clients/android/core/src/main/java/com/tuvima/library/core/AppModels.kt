package com.tuvima.library.core

/** The cards of one browse page plus what the Dashboard needs to sort and filter it. */
data class BrowsePage(
    val cards: List<DisplayCard>,
    val totalCount: Int,
    val genres: List<String>,
    val creators: List<String>,
    val years: List<String>,
)

/** A work's detail page, reduced to what the app shows. */
data class DetailModel(
    val title: String,
    val subtitle: String?,
    val tagline: String?,
    val description: String?,
    val year: String?,
    val runtime: String?,
    val contentRating: String?,
    val genres: List<String>,
    val artworkUrl: String?,
    val cast: List<DetailCredit>,
    val editions: List<DetailEdition>,
    val progressPercent: Double?,
    /** The show an episode belongs to, when the Engine's answer names it; next-episode features need it. */
    val showId: String? = null,
) {
    /** True when the person has started this work and has not finished it. */
    val isInProgress: Boolean
        get() = progressPercent != null && progressPercent > 0.0 && progressPercent < 99.5

    /** Year, rating and length in one line, in that order. */
    fun factLine(): String = listOfNotNull(year, contentRating, runtime).joinToString(" · ")
}

data class DetailCredit(val name: String, val role: String?)

data class DetailEdition(val id: String, val title: String, val details: String?)

/** One photo or video of the signed-in person's own View. */
data class ViewItem(
    val id: String,
    val mediaKind: String,
    val title: String?,
    val fileName: String?,
    val mimeType: String?,
    val capturedAt: String?,
    val width: Int?,
    val height: Int?,
    val durationSeconds: Double?,
    val byteSize: Long?,
    val favorite: Boolean,
    val locationName: String?,
    val cameraName: String?,
    val description: String?,
) {
    val isVideo: Boolean get() = mediaKind.equals("video", ignoreCase = true)

    /** The title if there is one, otherwise the file name. */
    fun displayTitle(): String = title ?: fileName ?: "Untitled"
}

data class ViewTimelineMonth(val year: Int, val month: Int, val items: List<ViewItem>)

data class ViewTimelinePage(val months: List<ViewTimelineMonth>, val nextCursor: String?, val hasMore: Boolean)

data class ViewItemPage(val items: List<ViewItem>, val nextCursor: String?, val hasMore: Boolean)

data class ViewGallery(
    val id: String,
    val name: String,
    val description: String?,
    val itemCount: Int,
    val coverItemId: String?,
)

data class ViewGalleries(val owned: List<ViewGallery>, val sharedWithYou: List<ViewGallery>)

/** One entry of the Collections catalogue; [groupType] says what it is (show, series, collection, list). */
data class CollectionEntry(
    val id: String,
    val name: String,
    val description: String?,
    val groupType: String,
    val collectionType: String?,
    val systemKey: String?,
    val itemCount: Int,
    val coverUrls: List<String>,
) {
    /** The Wikidata QID when the Engine carries one for this entry, otherwise null. */
    fun universeQid(): String? = systemKey?.trim()?.takeIf { ClientRoutes.isQid(it) }

    /** What the person calls this group, by its group type. */
    fun kindLabel(): String = groupTypeLabel(groupType)
}

data class CollectionsPage(
    val items: List<CollectionEntry>,
    val totalCount: Int,
    val nextCursor: String?,
    val hasMore: Boolean,
)

data class CollectionItem(
    val id: String,
    val workId: String?,
    val title: String,
    val creator: String?,
    val mediaType: String,
    val coverUrl: String?,
    val detailRoute: String?,
) {
    /** The details route's (entity type, id) pair, or a work fallback. */
    fun detailTarget(): Pair<String, String> =
        detailTargetFromRoute(detailRoute) ?: ("work" to (workId ?: id))
}

data class CollectionItemsPage(
    val items: List<CollectionItem>,
    val totalCount: Int,
    val nextCursor: String?,
    val hasMore: Boolean,
)

/**
 * One Series (or show) of a Universe: its numbered run ([shelf], [itemCount] items) and, when the Engine found
 * Universe items that belong to no numbered position, an "Also in this world" row ([alsoInThisWorld]).
 */
data class UniverseShelf(
    val collectionId: String,
    val groupType: String,
    val itemCount: Int,
    val shelf: DisplayShelf,
    val alsoInThisWorld: DisplayShelf? = null,
    val alsoInThisWorldCount: Int = 0,
)

data class UniverseCharacter(val qid: String, val label: String)

/** A person credited across a Universe, with the characters they play there. Read-only. */
data class UniverseCastMember(
    val personId: String,
    val name: String,
    val headshotUrl: String?,
    val characters: List<UniverseCharacter>,
    val workCount: Int,
)

/** One adaptation link: [label] is [relationship] (lowercase, e.g. "based on") [sourceLabel]. Read-only. */
data class UniverseAdaptation(
    val qid: String,
    val label: String,
    val mediaType: String?,
    val relationship: String,
    val sourceQid: String,
    val sourceLabel: String,
)

data class UniverseDetail(
    val qid: String,
    val name: String,
    val description: String?,
    val itemCount: Int,
    val shelves: List<UniverseShelf>,
    val seriesCount: Int = 0,
    val mediaTypes: List<String> = emptyList(),
    val cast: List<UniverseCastMember> = emptyList(),
    val adaptations: List<UniverseAdaptation> = emptyList(),
)

/** One person this paired app may switch to. */
data class DeviceProfile(
    val profileId: String,
    val displayName: String,
    val avatarUrl: String?,
    val isKids: Boolean,
    val requiresPin: Boolean,
    val isActive: Boolean,
)

/** The answer to a profile switch. Every refusal the Engine can give has its own type. */
sealed interface SwitchProfileResult {
    /** The switch worked; the client now holds the new person's tokens. */
    data class Switched(val token: ClientToken) : SwitchProfileResult

    /** HTTP 428: a PIN is needed, or the PIN given was wrong. */
    data class PinRequired(val message: String?) : SwitchProfileResult

    /** HTTP 403: this account has no grant for that person, or the device may not switch. */
    data class NotAllowed(val message: String?) : SwitchProfileResult

    /** HTTP 409: the chosen person is already the one in use. */
    data class AlreadyActive(val message: String?) : SwitchProfileResult

    /** HTTP 429: too many wrong PINs, or too many switches in a minute. */
    data class Locked(val message: String?) : SwitchProfileResult

    /** Anything else, with the Engine's own error code when it gave one. */
    data class Failed(val statusCode: Int, val code: String, val message: String?) : SwitchProfileResult
}

/** The words shown when the Engine refuses a photo request because the device was not given the photo scopes. */
const val PHOTO_ACCESS_DENIED_MESSAGE =
    "Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it"

/** Thrown by the photo calls on HTTP 403 so a screen can show [PHOTO_ACCESS_DENIED_MESSAGE]. */
class PhotoAccessDeniedException : RuntimeException(PHOTO_ACCESS_DENIED_MESSAGE)

/** The three scopes behind "Allow access to my photos and videos". They are never part of the default scopes. */
object PhotoScopes {
    val all: List<String> = listOf("view.personal.read", "view.originals.read", "view.galleries.read")

    /** [baseScopes] as they are, plus the photo scopes only when the person opted in. */
    fun requested(baseScopes: String, includePhotos: Boolean): String {
        if (!includePhotos) return baseScopes
        val existing = baseScopes.split(' ').filter(String::isNotBlank)
        return (existing + all.filter { it !in existing }).joinToString(" ")
    }

    /** True when a token's space-delimited scope string holds the scope that opens the photo timeline. */
    fun grantedIn(tokenScope: String): Boolean = "view.personal.read" in tokenScope.split(' ')
}

/** Turns a switch-profile refusal into its typed result by HTTP status; the error code is only used for `Failed`. */
internal fun classifySwitchFailure(statusCode: Int, code: String?, description: String?): SwitchProfileResult =
    when (statusCode) {
        428 -> SwitchProfileResult.PinRequired(description)
        403 -> SwitchProfileResult.NotAllowed(description)
        409 -> SwitchProfileResult.AlreadyActive(description)
        429 -> SwitchProfileResult.Locked(description)
        else -> SwitchProfileResult.Failed(statusCode, code ?: "request_failed", description)
    }

/**
 * "1 Series", "3 Series", "1 Work", "14 Works", "1 format", "2 formats"; a part whose number is 0 is left out. As on the
 * Dashboard's Universe page, a Universe whose lines are all TV shows says "1 show" / "3 shows" instead of "Series".
 */
fun universeTotalsLine(detail: UniverseDetail): String = listOfNotNull(
    detail.seriesCount.takeIf { it > 0 }?.let { count ->
        val allShows = detail.shelves.isNotEmpty() && detail.shelves.all { it.groupType.equals("show", ignoreCase = true) }
        if (allShows) "$count ${if (count == 1) "show" else "shows"}" else "$count Series"
    },
    detail.itemCount.takeIf { it > 0 }?.let { "$it ${if (it == 1) "Work" else "Works"}" },
    detail.mediaTypes.size.takeIf { it > 0 }?.let { "$it ${if (it == 1) "format" else "formats"}" },
).joinToString(" · ")

/** "as Hero, Sidekick" for a cast member, or null when the Engine named no characters. */
fun castCharactersLine(member: UniverseCastMember): String? =
    member.characters.map { it.label.trim() }.filter { it.isNotEmpty() }
        .takeIf { it.isNotEmpty() }?.joinToString(", ", prefix = "as ")

/** "The Film — Based on The Novel": the relationship's first letter is capitalised. */
fun adaptationLine(adaptation: UniverseAdaptation): String {
    val relationship = adaptation.relationship.trim().replaceFirstChar { it.uppercase() }
    val source = adaptation.sourceLabel.trim()
    return if (relationship.isEmpty() || source.isEmpty()) adaptation.label else "${adaptation.label} — $relationship $source"
}

/** What the screens draw for one Universe shelf: see [universeShelfRows]. */
data class UniverseShelfRows(
    /** The Series name. */
    val title: String,
    /** Kind label and count, e.g. "Series · 4 items" or "Series · Also in this world · 2 items"; never "0 items". */
    val kindLine: String,
    /** The numbered run, or null when it is empty and no row is drawn for it. */
    val run: DisplayShelf?,
    /** The "Also in this world" row, or null when there is none to draw. */
    val also: DisplayShelf?,
    /** The "Also in this world" row's title when [also] is shown. */
    val alsoTitle: String,
    /** How many items "Also in this world" holds (the Engine's count, else the cards given). */
    val alsoCount: Int,
) {
    /** True only when the numbered run has items to draw. */
    val showRun: Boolean get() = run != null

    /** "Also in this world · 2 items". */
    fun alsoHeading(): String = "$alsoTitle · ${itemsText(alsoCount)}"
}

internal fun itemsText(count: Int): String = "$count ${if (count == 1) "item" else "items"}"

/** The rows to draw for a shelf, or null when it has neither a run nor "Also in this world" items (skipped). */
fun universeShelfRows(member: UniverseShelf): UniverseShelfRows? {
    val showRun = member.itemCount > 0 && member.shelf.items.isNotEmpty()
    val also = member.alsoInThisWorld?.takeIf { it.items.isNotEmpty() }
    if (!showRun && also == null) return null
    val alsoTitle = member.alsoInThisWorld?.title?.takeIf { it.isNotBlank() } ?: "Also in this world"
    val kind = groupTypeLabel(member.groupType)
    val alsoCount = member.alsoInThisWorldCount.takeIf { it > 0 } ?: also?.items?.size ?: 0
    val kindLine = if (showRun) "$kind · ${itemsText(member.itemCount)}" else "$kind · $alsoTitle · ${itemsText(alsoCount)}"
    return UniverseShelfRows(member.shelf.title, kindLine, member.shelf.takeIf { showRun }, also, alsoTitle, alsoCount)
}

/** The label for a group type; unknown types read as a collection. */
fun groupTypeLabel(groupType: String): String = when (groupType.lowercase()) {
    "show" -> "Show"
    "series" -> "Series"
    "list" -> "List"
    "universe" -> "Universe"
    else -> "Collection"
}

/**
 * The show id to give the video screen: the details id itself when this is a show (query string and fragment removed, as in
 * `/details/tvshow/{showId}?context=watch&episode={episodeId}`), else the show the episode's own detail answer named.
 * Null when neither is known: the next-episode features then stay hidden and nothing is guessed.
 */
fun playShowId(entityType: String, id: String, detailShowId: String?): String? {
    if (OfflineLogic.isShowEntity(entityType)) return id.substringBefore('?').substringBefore('#').takeIf { it.isNotBlank() }
    return detailShowId?.trim()?.takeIf { it.isNotEmpty() }
}

/** The parent show named by an episode's detail answer: an explicit show id first, then the episode context's show work. */
fun parentShowId(explicitShowId: String?, episodeContextShowWorkId: String?): String? =
    explicitShowId?.trim()?.takeIf { it.isNotEmpty() } ?: episodeContextShowWorkId?.trim()?.takeIf { it.isNotEmpty() }

/** `/details/work/{id}?context=watch` becomes ("work", id); null when the route is not a details route. */
fun detailTargetFromRoute(route: String?): Pair<String, String>? {
    val path = route?.substringBefore('?')?.substringBefore('#') ?: return null
    val parts = path.split('/').filter(String::isNotBlank)
    val index = parts.indexOf("details")
    if (index < 0 || parts.size < index + 3) return null
    return parts[index + 1] to parts[index + 2]
}

/** One row of a photo grid: a month heading, or a line of thumbnails. */
sealed interface TimelineRow {
    data class Header(val year: Int, val month: Int, val label: String) : TimelineRow
    data class Cells(val items: List<ViewItem>) : TimelineRow
}

private val monthNames = listOf(
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
)

fun monthLabel(year: Int, month: Int): String =
    if (month in 1..12) "${monthNames[month - 1]} $year" else "$year"

/**
 * Adds a page to the months already loaded. A month can continue on the next page, so a bucket with the same
 * year and month is merged into the earlier one, keeping item order and dropping repeats.
 */
fun mergeTimelineMonths(
    existing: List<ViewTimelineMonth>,
    incoming: List<ViewTimelineMonth>,
): List<ViewTimelineMonth> {
    val result = existing.toMutableList()
    for (month in incoming) {
        val index = result.indexOfFirst { it.year == month.year && it.month == month.month }
        if (index < 0) {
            result.add(month)
        } else {
            val known = result[index].items.map { it.id }.toSet()
            result[index] = result[index].copy(items = result[index].items + month.items.filter { it.id !in known })
        }
    }
    return result
}

/** Lays months out as heading rows and rows of [columns] thumbnails. */
fun buildTimelineRows(months: List<ViewTimelineMonth>, columns: Int): List<TimelineRow> {
    val width = columns.coerceAtLeast(1)
    return buildList {
        for (month in months) {
            if (month.items.isEmpty()) continue
            add(TimelineRow.Header(month.year, month.month, monthLabel(month.year, month.month)))
            month.items.chunked(width).forEach { add(TimelineRow.Cells(it)) }
        }
    }
}

/** Lays a flat list of photos out as rows of [columns] thumbnails, without month headings. */
fun buildFlatRows(items: List<ViewItem>, columns: Int): List<TimelineRow> =
    items.chunked(columns.coerceAtLeast(1)).map { TimelineRow.Cells(it) }

/** The card's play target: the asset of its play action, otherwise the card's own asset. */
fun DisplayCard.playableAssetId(): String? = actions.firstNotNullOfOrNull { it.assetId } ?: assetId

/** Work id to the file that plays it, for the cards that name both. */
fun mapPlayableAssets(cards: List<DisplayCard>): Map<String, String> = cards.mapNotNull { card ->
    val work = card.workId
    val asset = card.playableAssetId()
    if (work != null && asset != null) work to asset else null
}.toMap()
