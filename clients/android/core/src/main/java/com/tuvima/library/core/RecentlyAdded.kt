package com.tuvima.library.core

import org.json.JSONObject

// Recently added (`GET /api/v1/display/recent`): the newest titles the profile may see, a page at a time by cursor. The phone shows the
// catalogue cards; a personal photo or video (a View asset) is left to the View section.

/** One page of recent additions. [availableTypes] (watch, read, listen) come with the first page only and name the filters worth showing. */
data class RecentPage(
    val cards: List<DisplayCard>,
    val nextCursor: String?,
    val hasMore: Boolean,
    val availableTypes: List<String>?,
)

object RecentlyAddedLogic {
    const val TITLE = "Recently added"
    const val EMPTY = "Nothing has been added yet."

    /** The filter words, in the Dashboard's order; View is not offered here because its additions are not catalogue titles. */
    private val typeLabels = listOf("watch" to "Watch", "read" to "Read", "listen" to "Listen")

    fun parsePage(json: JSONObject, absolute: (String?) -> String?): RecentPage {
        val cards = json.optJSONArray("items")?.objects { it }.orEmpty()
            .mapNotNull { item -> item.optJSONObject("catalogue")?.let { parseDisplayCard(it, absolute) } }
        return RecentPage(
            cards = cards,
            nextCursor = json.optionalString("nextCursor"),
            hasMore = json.optBoolean("hasMore", false),
            availableTypes = json.optJSONArray("availableTypes")?.strings(),
        )
    }

    /** The filter chips for the types the Engine says have something, as (value, label) pairs in a fixed order; none when fewer than two. */
    fun typeChips(availableTypes: List<String>?): List<Pair<String, String>> {
        val chips = typeLabels.filter { (value, _) -> availableTypes?.any { it.equals(value, ignoreCase = true) } == true }
        return if (chips.size >= 2) chips else emptyList()
    }

    /** Adds a page to the cards already shown, dropping a title that arrives twice (a cursor can overlap). */
    fun merge(shown: List<DisplayCard>, incoming: List<DisplayCard>): List<DisplayCard> {
        val seen = shown.mapTo(HashSet()) { it.id }
        return shown + incoming.filter { seen.add(it.id) }
    }
}
