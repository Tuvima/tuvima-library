package com.tuvima.library.core

import org.json.JSONObject

// View's People and Places on the phone (`GET /api/v1/view/people`, `/people/{key}/items`, `/places`, `/places/{key}/items`). Both lists are the signed-in
// person's own photos and videos only. Pure wire reading and wording; nothing here draws anything.

/** Whether the Engine has anything to show for People or Places, with its own explanation when it does not. */
data class ViewDiscoveryCapability(val state: String, val hasIndexedData: Boolean, val message: String?)

/** A named or reviewed person in the photos. [representativeItemId] is the photo to draw as the face. */
data class ViewPerson(
    val key: String,
    val displayName: String,
    val assetCount: Int,
    val representativeItemId: String?,
)

/** A place the photos cluster around. Latitude and longitude are kept as the Engine gave them. */
data class ViewPlace(
    val key: String,
    val name: String,
    val latitude: Double,
    val longitude: Double,
    val assetCount: Int,
    val representativeItemId: String?,
)

data class ViewPeoplePage(
    val items: List<ViewPerson>,
    val nextCursor: String?,
    val hasMore: Boolean,
    val capability: ViewDiscoveryCapability?,
)

data class ViewPlacesPage(
    val items: List<ViewPlace>,
    val nextCursor: String?,
    val hasMore: Boolean,
    val capability: ViewDiscoveryCapability?,
)

internal fun parseViewCapability(json: JSONObject?): ViewDiscoveryCapability? = json?.let {
    ViewDiscoveryCapability(
        state = it.optString("state"),
        hasIndexedData = it.optBoolean("has_indexed_data"),
        message = it.optionalString("message"),
    )
}

internal fun parseViewPeoplePage(json: JSONObject): ViewPeoplePage = ViewPeoplePage(
    items = json.optJSONArray("items")?.objects { person ->
        ViewPerson(
            key = person.optString("key"),
            displayName = person.optString("display_name"),
            assetCount = person.optInt("asset_count"),
            representativeItemId = person.optionalString("representative_asset_id"),
        )
    }.orEmpty().filter { it.key.isNotBlank() },
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
    capability = parseViewCapability(json.optJSONObject("capability")),
)

internal fun parseViewPlacesPage(json: JSONObject): ViewPlacesPage = ViewPlacesPage(
    items = json.optJSONArray("items")?.objects { place ->
        ViewPlace(
            key = place.optString("key"),
            name = place.optString("name"),
            latitude = place.optDouble("latitude"),
            longitude = place.optDouble("longitude"),
            assetCount = place.optInt("asset_count"),
            representativeItemId = place.optionalString("representative_asset_id"),
        )
    }.orEmpty().filter { it.key.isNotBlank() },
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
    capability = parseViewCapability(json.optJSONObject("capability")),
)

/** The View section's tabs on the phone, in the Dashboard's order. */
enum class ViewTab(val label: String) {
    Timeline("Timeline"),
    Galleries("Galleries"),
    People("People"),
    Places("Places"),
}

object ViewDiscoveryLogic {
    const val PAGE_SIZE = 48
    const val EMPTY_PEOPLE = "No people yet. Names you add or review in your photos show up here."
    const val EMPTY_PLACES = "No places yet. Photos with a location show up here."
    const val NO_ITEMS = "No photos or videos for this person yet."
    const val NO_PLACE_ITEMS = "No photos or videos for this place yet."

    fun tabs(): List<ViewTab> = ViewTab.entries.toList()

    /** "1 photo or video" / "24 photos and videos". */
    fun countText(count: Int): String =
        if (count == 1) "1 photo or video" else "${"%,d".format(java.util.Locale.US, count)} photos and videos"

    /** The name to draw: the person's name, or a plain word for one the Engine sent without a name. */
    fun personName(person: ViewPerson): String = person.displayName.trim().ifEmpty { "Unnamed person" }

    fun placeName(place: ViewPlace): String = place.name.trim().ifEmpty { "Unnamed place" }

    /**
     * The words to show when a list is empty: the Engine's own explanation when it gave one (it says whether processing is on and
     * whether anything is indexed), otherwise the fixed line.
     */
    fun emptyText(capability: ViewDiscoveryCapability?, fallback: String): String =
        capability?.message?.trim()?.takeIf { it.isNotEmpty() } ?: fallback

    /** Adds a page of people to those shown, dropping a person who arrives twice. */
    fun mergePeople(shown: List<ViewPerson>, incoming: List<ViewPerson>): List<ViewPerson> {
        val seen = shown.mapTo(HashSet()) { it.key }
        return shown + incoming.filter { seen.add(it.key) }
    }

    fun mergePlaces(shown: List<ViewPlace>, incoming: List<ViewPlace>): List<ViewPlace> {
        val seen = shown.mapTo(HashSet()) { it.key }
        return shown + incoming.filter { seen.add(it.key) }
    }

    /** The cursor for the next page, or null when the Engine says there is no more. */
    fun cursorFor(page: ViewPeoplePage): String? = page.nextCursor.takeIf { page.hasMore }

    fun cursorFor(page: ViewPlacesPage): String? = page.nextCursor.takeIf { page.hasMore }
}
